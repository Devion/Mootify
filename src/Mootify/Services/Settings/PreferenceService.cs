using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Settings;

/// <summary>
/// Per-account preferences — the things that are true of a person rather than of the deployment
/// (<c>mootify.json</c>) or of the moment (<see cref="SettingsService"/> and <c>AppSetting</c>).
///
/// One place that knows a missing row means the entity's defaults. Every caller was otherwise
/// going to write its own <c>FirstOrDefault ?? true</c>, and the day a default changes only some
/// of them would follow.
/// </summary>
public sealed class PreferenceService(IDbContextFactory<MootifyDbContext> dbFactory)
{
    /// <summary>
    /// Whether the queue tops itself up when a playlist runs out. Defaults to on for an account
    /// that has never set it — safe only because the suggester refuses to run without enough
    /// history, so "on" does nothing at all until it can do something sensible.
    /// </summary>
    public async Task<bool> GetAutoContinueAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var row = await db.Preferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (bool?)p.AutoContinue)
            .FirstOrDefaultAsync(ct);

        // Null both for "no row" and — because of the cast — never for a stored false. A stored
        // false has to survive, which is why this projects a nullable rather than a bool.
        return row ?? new UserPreference().AutoContinue;
    }

    public async Task<bool> SetAutoContinueAsync(Guid userId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var preference = await db.Preferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preference is null)
        {
            preference = new UserPreference { UserId = userId };
            db.Preferences.Add(preference);
        }

        preference.AutoContinue = enabled;
        await db.SaveChangesAsync(ct);

        return enabled;
    }
}
