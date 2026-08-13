using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;

namespace Mootify.Services.Auth;

public sealed record IssuedToken(string Secret, Guid TokenId, DateTimeOffset? ExpiresAt);

/// <summary>What a validated token resolves to. Deliberately not an <see cref="AppUser"/>:
/// the handler wants three fields, not a tracked entity.</summary>
public sealed record ApiIdentity(Guid UserId, string DisplayName, bool IsAdmin, Guid TokenId);

public sealed record ApiTokenInfo(
    Guid Id,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset? ExpiresAt,
    bool IsCurrent);

/// <summary>
/// Device tokens for clients that can't hold a cookie. The website keeps its cookie; the
/// Android app gets one of these, and both end up as the same <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// shape so every service below the endpoint layer is unaware of which door was used.
///
/// Issuing is deliberately routed through <see cref="AccountService.SignInAsync"/> by the
/// endpoint rather than duplicated here — the throttle, the ban check and the rehash-on-login
/// all have to apply to a phone exactly as they do to a browser.
/// </summary>
public sealed class ApiTokenService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    IOptionsMonitor<ApiOptions> options,
    ILogger<ApiTokenService> log)
{
    /// <summary>Bytes of entropy in a token secret. 32 is well past guessable.</summary>
    private const int SecretBytes = 32;

    /// <summary>
    /// How stale <see cref="ApiToken.LastUsedAt"/> is allowed to get. Seeking through an album
    /// is hundreds of range requests; writing a timestamp for each would turn playback into a
    /// write load on a SQLite file that is also serving the website.
    /// </summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Prefix on the wire. Purely so a leaked string is recognisable as a Mootify credential
    /// in a log or a bug report — it carries no information and isn't hashed differently.
    /// </summary>
    public const string Prefix = "moo_";

    public async Task<IssuedToken> IssueAsync(
        Guid userId, string? deviceName, CancellationToken ct = default)
    {
        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var now = DateTimeOffset.UtcNow;
        var lifetime = options.CurrentValue.TokenLifetime;

        var token = new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(secret),
            DeviceName = CleanDeviceName(deviceName),
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = lifetime > TimeSpan.Zero ? now + lifetime : null,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);

        await PruneAsync(db, userId, ct);

        log.LogInformation("Issued API token {TokenId} to {UserId} for {Device}",
            token.Id, userId, token.DeviceName);

        return new IssuedToken(secret, token.Id, token.ExpiresAt);
    }

    /// <summary>
    /// Resolves a secret to whoever holds it, or null. Re-checks the ban flag here as well as in
    /// the cookie's OnValidatePrincipal: a banned account has to stop working on the phone at the
    /// same moment it stops working in the browser, and the phone never revalidates a cookie.
    /// </summary>
    public async Task<ApiIdentity?> ValidateAsync(string? secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;

        var hash = Hash(secret);
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var row = await db.ApiTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => new
            {
                t.Id,
                t.UserId,
                t.ExpiresAt,
                t.RevokedAt,
                t.LastUsedAt,
                DisplayName = t.User!.DisplayName,
                t.User!.IsAdmin,
                t.User!.IsBanned,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;
        if (row.RevokedAt is not null) return null;
        if (row.ExpiresAt is { } expiry && expiry <= now) return null;
        if (row.IsBanned) return null;

        // Cheap enough to skip most of the time — see LastUsedResolution.
        if (now - row.LastUsedAt > LastUsedResolution)
        {
            await db.ApiTokens
                .Where(t => t.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        return new ApiIdentity(row.UserId, row.DisplayName, row.IsAdmin, row.Id);
    }

    public async Task<List<ApiTokenInfo>> ListAsync(
        Guid userId, Guid? currentTokenId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.ApiTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.LastUsedAt)
            .Select(t => new { t.Id, t.DeviceName, t.CreatedAt, t.LastUsedAt, t.ExpiresAt })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new ApiTokenInfo(
                r.Id, r.DeviceName, r.CreatedAt, r.LastUsedAt, r.ExpiresAt, r.Id == currentTokenId))
        ];
    }

    /// <summary>
    /// Revokes one device. Scoped to the owner: an id is a guessable-enough handle that
    /// "revoke by id" without an ownership check would let anyone sign out anyone.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid tokenId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var affected = await db.ApiTokens
            .Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);

        if (affected > 0)
        {
            log.LogInformation("Revoked API token {TokenId} for {UserId}", tokenId, userId);
        }

        return affected > 0;
    }

    /// <summary>Every device, for a stolen-phone-shaped panic or a password change.</summary>
    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// Keeps the newest <see cref="ApiOptions.MaxTokensPerUser"/> active tokens and revokes the
    /// rest. Re-installing the app is the common case, and each install mints a row.
    /// </summary>
    private async Task PruneAsync(MootifyDbContext db, Guid userId, CancellationToken ct)
    {
        var max = options.CurrentValue.MaxTokensPerUser;

        var stale = await db.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(max)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        await db.ApiTokens
            .Where(t => stale.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);

        log.LogInformation("Pruned {Count} old API tokens for {UserId}", stale.Count, userId);
    }

    /// <summary>
    /// Plain SHA-256. The input is 32 bytes from a CSPRNG, so there is no dictionary to run and
    /// nothing for a slow hash to buy; it is checked on every single request, including every
    /// range request of every stream.
    /// </summary>
    internal static string Hash(string secret) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CleanDeviceName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "Unknown device";
        return trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }
}
