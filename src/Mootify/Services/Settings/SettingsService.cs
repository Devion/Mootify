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
    IOptionsMonitor<AuthOptions> authOptions,
    IOptionsMonitor<RequestOptions> requestOptions)
{
    public const string RegistrationEnabledKey = "auth.registration_enabled";
    public const string MaxOpenRequestsKey = "requests.max_open_per_user";

    /// <summary>The bounds the admin form clamps to, matching <see cref="RequestOptions"/>.</summary>
    public const int MinOpenRequests = 1;
    public const int MaxOpenRequests = 1000;

    public async Task<bool> GetRegistrationEnabledAsync(CancellationToken ct = default)
    {
        var stored = await ReadAsync(RegistrationEnabledKey, ct);

        // Never set: fall back to the configured default rather than guessing.
        return stored is null
            ? authOptions.CurrentValue.AllowRegistration
            : stored == "true";
    }

    public Task SetRegistrationEnabledAsync(bool enabled, CancellationToken ct = default) =>
        WriteAsync(RegistrationEnabledKey, enabled ? "true" : "false", ct);

    /// <summary>
    /// How many requests one person may have in flight. Whatever an admin sets here wins over
    /// <c>Requests:MaxOpenPerUser</c> — the file is the number the install shipped with, this is
    /// the number somebody chose while looking at how full the disk was.
    /// </summary>
    public async Task<int> GetMaxOpenRequestsAsync(CancellationToken ct = default) =>
        await GetMaxOpenRequestsOverrideAsync(ct) ?? requestOptions.CurrentValue.MaxOpenPerUser;

    /// <summary>
    /// The admin's number on its own, or null when nobody has set one and the JSON value is in
    /// force. The admin page needs the difference to say which of the two it is showing.
    /// </summary>
    public async Task<int?> GetMaxOpenRequestsOverrideAsync(CancellationToken ct = default)
    {
        var stored = await ReadAsync(MaxOpenRequestsKey, ct);

        // A row that doesn't parse is a row somebody edited by hand; the file is the safer answer.
        return int.TryParse(stored, out var value) ? Math.Clamp(value, MinOpenRequests, MaxOpenRequests) : null;
    }

    public Task SetMaxOpenRequestsAsync(int max, CancellationToken ct = default) =>
        WriteAsync(
            MaxOpenRequestsKey,
            Math.Clamp(max, MinOpenRequests, MaxOpenRequests).ToString(),
            ct);

    /// <summary>Drops the override so <c>Requests:MaxOpenPerUser</c> is back in charge.</summary>
    public async Task ClearMaxOpenRequestsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == MaxOpenRequestsKey, ct);
        if (setting is null) return;

        db.Settings.Remove(setting);
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Settings
            .AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
    }

    private async Task WriteAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);

        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
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
