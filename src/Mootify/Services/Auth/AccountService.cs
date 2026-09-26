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

        if (user.ApprovalPending) return AuthResult.Fail("Your account is waiting for admin approval.");

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
            ApprovalPending = !isAdmin,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        user.PasswordHash = _hasher.HashPassword(user, password!);
        user.Preference = new UserPreference { UserId = user.Id };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Created {Kind} account {Username}", isAdmin ? "admin" : "user", name);
        return isAdmin ? AuthResult.Ok(BuildPrincipal(user)) : new AuthResult(true, null, null);
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

        // The whole point of a one-time password is that it stops working. Typing it back in as
        // the new one would satisfy the gate and change nothing, so this is refused for every
        // password change rather than only the forced one — nobody means to do it deliberately.
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, newPassword ?? "") != PasswordVerificationResult.Failed)
        {
            return (false, "That's the password you already have. Pick a different one.");
        }

        return await SetPasswordCoreAsync(db, user, newPassword!, confirmPassword, mustChangeNext: false, ct);
    }

    /// <summary>
    /// Admin override — no current password required. There's no email here, so somebody has
    /// to be able to rescue a locked-out account.
    ///
    /// What the admin types is a <i>one-time</i> password: it's a secret two people now know, so
    /// the account is flagged and can do nothing but choose a new one at the next sign-in. Any
    /// device tokens go with it, because a phone holding a live token would otherwise sail past
    /// the gate on credentials the reset was meant to retire.
    /// </summary>
    public async Task<(bool Ok, string? Error)> AdminSetPasswordAsync(
        Guid actingAdminId, Guid targetUserId, string newPassword, string confirmPassword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingAdminId, ct)) return (false, "Admins only.");

        // Resetting your own would lock you into the change screen to solve a problem you don't
        // have — you know the password, you just typed it.
        if (actingAdminId == targetUserId)
        {
            return (false, "Change your own password from your account page.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (user is null) return (false, "No such account.");

        var result = await SetPasswordCoreAsync(db, user, newPassword, confirmPassword, mustChangeNext: true, ct);
        if (result.Ok)
        {
            log.LogWarning(
                "Admin {Admin} reset the password for {User} — they must change it at next sign-in",
                actingAdminId, user.DisplayName);
        }

        return result;
    }

    private async Task<(bool Ok, string? Error)> SetPasswordCoreAsync(
        MootifyDbContext db, AppUser user, string newPassword, string confirmPassword, bool mustChangeNext,
        CancellationToken ct)
    {
        if (newPassword != confirmPassword) return (false, "Those passwords don't match.");

        var minimum = options.CurrentValue.MinPasswordLength;
        if ((newPassword ?? "").Length < minimum)
        {
            return (false, $"Passwords need at least {minimum} characters.");
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword!);
        user.MustChangePassword = mustChangeNext;
        await db.SaveChangesAsync(ct);

        if (mustChangeNext)
        {
            var userId = user.Id;
            await db.ApiTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
        }

        return (true, null);
    }

    public async Task TouchLastSeenAsync(Guid userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-5);
        await db.Users.Where(u => u.Id == userId && !u.IsBanned && !u.ApprovalPending && u.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now));
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

        // Carried in the cookie so the gate costs nothing per request, and reconciled by
        // OnValidatePrincipal — which is what lifts it, since the page that clears the flag
        // runs in a circuit and can't rewrite a cookie.
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(MootifyAuth.MustChangePasswordClaim, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, MootifyAuth.Scheme));
    }

    private static Task<bool> IsAdminAsync(MootifyDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin && !u.IsBanned && !u.ApprovalPending, ct);
}

public static class MootifyAuth
{
    public const string Scheme = "Mootify";
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";
    public const string SetupPath = "/setup";
    public const string RegisterPath = "/register";

    /// <summary>Bearer tokens for the Android app. See <see cref="ApiTokenAuthenticationHandler"/>.</summary>
    public const string ApiScheme = "MootifyApi";

    public const string UserRole = "user";
    public const string AdminRole = "admin";

    /// <summary>Authorization policy name for everything under /admin.</summary>
    public const string AdminPolicy = "RequireAdmin";

    /// <summary>
    /// Token-only. Cookies are excluded on purpose: a cookie-authenticated POST to /api is a
    /// CSRF target, and the API disables antiforgery because token clients can't mint a token.
    /// </summary>
    public const string ApiPolicy = "RequireApiToken";

    /// <summary>
    /// Streaming accepts either door — the website's cookie and the app's token both have to be
    /// able to fetch audio, and it's a GET, so there's nothing for a forged one to achieve.
    /// </summary>
    public const string MediaPolicy = "AllowCookieOrToken";

    /// <summary>Which device token a request arrived on, so it can list and revoke itself.</summary>
    public const string TokenIdClaim = "mootify:token";

    /// <summary>
    /// An admin reset this account to a one-time password. Only ever on a cookie principal:
    /// the reset revokes every device token and the API refuses to issue a new one while the
    /// flag is set, so a token holder can't be in this state.
    /// </summary>
    public const string MustChangePasswordClaim = "mootify:mustchangepw";

    public const string ChangePasswordPath = "/password";

    public static bool MustChangePassword(ClaimsPrincipal? principal) =>
        principal?.HasClaim(MustChangePasswordClaim, "true") == true;

    /// <summary>
    /// Same claim shape the cookie path builds, so <see cref="Playlists.PlaylistService"/> and
    /// friends can't tell a phone from a browser.
    /// </summary>
    public static ClaimsPrincipal BuildApiPrincipal(ApiIdentity identity)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
            new(ClaimTypes.Name, identity.DisplayName),
            new(ClaimTypes.Role, UserRole),
            new(TokenIdClaim, identity.TokenId.ToString()),
        };

        if (identity.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiScheme));
    }
}
