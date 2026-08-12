using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Settings;

namespace Mootify.Services.Auth;

public sealed record AuthResult(bool Succeeded, ClaimsPrincipal? Principal, string? Error)
{
    public static AuthResult Fail(string error) => new(false, null, error);
    public static AuthResult Ok(ClaimsPrincipal principal) => new(true, principal, null);
}

/// <summary>
/// Usernames and passwords: registration, sign-in, and the first-run admin account.
/// Password hashing is <see cref="PasswordHasher{TUser}"/> (PBKDF2, salted, iteration count
/// maintained by ASP.NET Core) rather than anything hand-rolled.
/// </summary>
public sealed class AccountService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    IOptionsMonitor<AuthOptions> options,
    SettingsService settings,
    SetupState setupState,
    ILogger<AccountService> log)
{
    private readonly PasswordHasher<AppUser> _hasher = new();

    // ---- sign in ---------------------------------------------------------

    public async Task<AuthResult> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var normalized = Normalize(username);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedName == normalized, ct);

        if (user is null)
        {
            // Hash anyway so a missing account doesn't return measurably faster than a wrong
            // password, which would let anyone enumerate usernames.
            _hasher.HashPassword(new AppUser(), password ?? "");
            log.LogWarning("Failed sign-in for unknown user {Username}", normalized);
            return AuthResult.Fail("Wrong username or password.");
        }

        var verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (verification == PasswordVerificationResult.Failed)
        {
            log.LogWarning("Failed sign-in for {Username}", normalized);
            return AuthResult.Fail("Wrong username or password.");
        }

        if (user.IsBanned)
        {
            log.LogWarning("Banned user {Username} tried to sign in", normalized);
            return AuthResult.Fail(
                string.IsNullOrWhiteSpace(user.BanReason)
                    ? "That account is banned."
                    : $"That account is banned: {user.BanReason}");
        }

        // The hasher tells us when its parameters have moved on. Take the free upgrade.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password!);
        }

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return AuthResult.Ok(BuildPrincipal(user));
    }

    // ---- registration ----------------------------------------------------

    public async Task<AuthResult> RegisterAsync(
        string username, string password, string confirmPassword, CancellationToken ct = default)
    {
        if (!await settings.GetRegistrationEnabledAsync(ct))
        {
            return AuthResult.Fail("Registration is closed. Ask an admin for an account.");
        }

        return await CreateAsync(username, password, confirmPassword, isAdmin: false, ct);
    }

    /// <summary>
    /// The first account on an empty database. Refuses once anybody exists, so this can't be
    /// used later to mint an admin.
    /// </summary>
    public async Task<AuthResult> CompleteSetupAsync(
        string username, string password, string confirmPassword, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Users.AnyAsync(ct))
            {
                return AuthResult.Fail("Setup has already been done.");
            }
        }

        var expected = options.CurrentValue.AdminUsername;
        if (!string.Equals(Normalize(username), Normalize(expected), StringComparison.Ordinal))
        {
            return AuthResult.Fail($"The first account has to be called {expected}.");
        }

        var result = await CreateAsync(username, password, confirmPassword, isAdmin: true, ct);

        if (result.Succeeded)
        {
            setupState.MarkComplete();
            log.LogWarning("First-run setup complete — admin account {Username} created", expected);
        }

        return result;
    }

    private async Task<AuthResult> CreateAsync(
        string username, string password, string confirmPassword, bool isAdmin, CancellationToken ct)
    {
        var name = (username ?? "").Trim();
        var normalized = Normalize(name);

        if (normalized.Length is 0 or > 64)
        {
            return AuthResult.Fail("Pick a username between 1 and 64 characters.");
        }

        if (!normalized.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return AuthResult.Fail("Usernames can use letters, numbers, dot, dash and underscore.");
        }

        if (password != confirmPassword)
        {
            return AuthResult.Fail("Those passwords don't match.");
        }

        var minimum = options.CurrentValue.MinPasswordLength;
        if ((password ?? "").Length < minimum)
        {
            return AuthResult.Fail($"Passwords need at least {minimum} characters.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Users.AnyAsync(u => u.NormalizedName == normalized, ct))
        {
            return AuthResult.Fail("That username is taken.");
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            NormalizedName = normalized,
            IsAdmin = isAdmin,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        user.PasswordHash = _hasher.HashPassword(user, password!);
        user.Preference = new UserPreference { UserId = user.Id };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Created {Kind} account {Username}", isAdmin ? "admin" : "user", name);
        return AuthResult.Ok(BuildPrincipal(user));
    }

    // ---- passwords -------------------------------------------------------

    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, string confirmPassword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return (false, "No such account.");

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            return (false, "That's not your current password.");
        }

        return await SetPasswordCoreAsync(db, user, newPassword, confirmPassword, ct);
    }

    /// <summary>
    /// Admin override — no current password required. There's no email here, so somebody has
    /// to be able to rescue a locked-out account.
    /// </summary>
    public async Task<(bool Ok, string? Error)> AdminSetPasswordAsync(
        Guid actingAdminId, Guid targetUserId, string newPassword, string confirmPassword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingAdminId, ct)) return (false, "Admins only.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (user is null) return (false, "No such account.");

        var result = await SetPasswordCoreAsync(db, user, newPassword, confirmPassword, ct);
        if (result.Ok)
        {
            log.LogWarning("Admin {Admin} reset the password for {User}", actingAdminId, user.DisplayName);
        }

        return result;
    }

    private async Task<(bool Ok, string? Error)> SetPasswordCoreAsync(
        MootifyDbContext db, AppUser user, string newPassword, string confirmPassword, CancellationToken ct)
    {
        if (newPassword != confirmPassword) return (false, "Those passwords don't match.");

        var minimum = options.CurrentValue.MinPasswordLength;
        if ((newPassword ?? "").Length < minimum)
        {
            return (false, $"Passwords need at least {minimum} characters.");
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword!);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    // ---- helpers ---------------------------------------------------------

    public static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static ClaimsPrincipal BuildPrincipal(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, MootifyAuth.UserRole),
        };

        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, MootifyAuth.AdminRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, MootifyAuth.Scheme));
    }

    private static Task<bool> IsAdminAsync(MootifyDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin && !u.IsBanned, ct);
}

public static class MootifyAuth
{
    public const string Scheme = "Mootify";
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";
    public const string SetupPath = "/setup";
    public const string RegisterPath = "/register";

    public const string UserRole = "user";
    public const string AdminRole = "admin";

    /// <summary>Authorization policy name for everything under /admin.</summary>
    public const string AdminPolicy = "RequireAdmin";
}
