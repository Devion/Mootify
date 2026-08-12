using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;

namespace Mootify.Services.Settings;

/// <summary>
/// Settings an admin flips at runtime, stored in the database rather than mootify.json.
/// The file stays for facts about the deployment; this is for facts about right now.
/// </summary>
public sealed class SettingsService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    IOptionsMonitor<AuthOptions> authOptions)
{
    public const string RegistrationEnabledKey = "auth.registration_enabled";

    public async Task<bool> GetRegistrationEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var stored = await db.Settings
            .AsNoTracking()
            .Where(s => s.Key == RegistrationEnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // Never set: fall back to the configured default rather than guessing.
        return stored is null
            ? authOptions.CurrentValue.AllowRegistration
            : stored == "true";
    }

    public async Task SetRegistrationEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == RegistrationEnabledKey, ct);

        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = RegistrationEnabledKey, Value = enabled ? "true" : "false" });
        }
        else
        {
            setting.Value = enabled ? "true" : "false";
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Whether the first-run admin account exists. A singleton so the check is one bool on the
/// hot path instead of a database round trip on every request.
/// </summary>
public sealed class SetupState
{
    private volatile bool _complete;

    public bool IsComplete => _complete;

    public void MarkComplete() => _complete = true;

    public void MarkIncomplete() => _complete = false;
}
