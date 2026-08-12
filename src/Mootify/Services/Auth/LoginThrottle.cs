using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Auth;

/// <summary>
/// Two layers over the sign-in endpoint.
///
/// <b>Delay</b>: one second per prior failed attempt, so the fourth wait is three seconds and
/// guessing by hand stops being worth anybody's evening.
///
/// <b>Lockout</b>: at <see cref="AuthOptions.MaxFailedAttempts"/> failures the door shuts for
/// <see cref="AuthOptions.LockoutDuration"/> and no password is checked at all.
///
/// Three rules that matter more than the arithmetic:
///
/// 1. The wait happens <b>before</b> the password is checked, and applies to a correct
///    password too. Delaying only failures would make a fast response mean "that was right" —
///    handing an attacker the answer through timing instead of the response body.
/// 2. Counts are kept per username <i>and</i> per address, whichever is worse. Per username
///    alone does nothing against spraying one common password across many accounts; per
///    address alone does nothing against a botnet grinding one account.
/// 3. A lockout expires at a fixed time set when it trips. Retrying during it neither extends
///    it nor counts, so "try again in 12 minutes" stays true.
/// </summary>
public sealed class LoginThrottle(
    IMemoryCache cache,
    IOptionsMonitor<AuthOptions> options,
    ILogger<LoginThrottle> log)
{
    /// <summary>Added per prior failure.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling on the delay. Without one, a run of failures would hold a request open for
    /// minutes — a way to exhaust our own connections, not a defence. In practice the lockout
    /// arrives first; this only matters if the threshold is configured very high.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a failure is remembered when no lockout has tripped. Sliding, so a persistent
    /// attacker never gets a fresh start, but somebody who fumbled at lunch isn't still being
    /// punished at dinner.
    /// </summary>
    private static readonly TimeSpan CountWindow = TimeSpan.FromMinutes(15);

    /// <summary>How much longer this username or address is shut out, if at all.</summary>
    public TimeSpan? GetLockout(string? username, string? ip)
    {
        var until = Latest(LockKey(UserKey(username)), LockKey(IpKey(ip)));
        if (until is not { } expiry) return null;

        var remaining = expiry - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public TimeSpan GetDelay(string? username, string? ip)
    {
        var failures = Math.Max(Count(UserKey(username)), Count(IpKey(ip)));
        if (failures == 0) return TimeSpan.Zero;

        var delay = Step * failures;
        return delay > MaxDelay ? MaxDelay : delay;
    }

    public void RecordFailure(string? username, string? ip)
    {
        var opts = options.CurrentValue;

        var forUser = Increment(UserKey(username), opts);
        var forIp = Increment(IpKey(ip), opts);
        var worst = Math.Max(forUser, forIp);

        if (worst >= opts.MaxFailedAttempts)
        {
            log.LogWarning(
                "Locking out {Username} / {Ip} for {Minutes} minutes after {Failures} failed sign-ins",
                AccountService.Normalize(username), ip ?? "unknown",
                opts.LockoutDuration.TotalMinutes, worst);
        }
        else if (worst >= 5)
        {
            log.LogWarning(
                "{Failures} failed sign-ins for {Username} from {Ip} — now delaying {Delay}s",
                worst, AccountService.Normalize(username), ip ?? "unknown",
                GetDelay(username, ip).TotalSeconds);
        }
    }

    /// <summary>Signing in successfully clears the slate for that username and address.</summary>
    public void RecordSuccess(string? username, string? ip)
    {
        foreach (var key in new[] { UserKey(username), IpKey(ip) })
        {
            cache.Remove(key);
            cache.Remove(LockKey(key));
        }
    }

    private int Count(string key) => cache.TryGetValue(key, out int value) ? value : 0;

    private int Increment(string key, AuthOptions opts)
    {
        var value = Count(key) + 1;

        if (value >= opts.MaxFailedAttempts)
        {
            // Lock, then wipe the counter. When the lockout expires they get a clean ten
            // rather than being re-locked by their very next mistake.
            cache.Set(LockKey(key), DateTimeOffset.UtcNow + opts.LockoutDuration, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = opts.LockoutDuration,
                Size = 1,
            });

            cache.Remove(key);
            return value;
        }

        // Sliding rather than absolute: each new attempt pushes the expiry out, so hammering
        // the endpoint keeps the penalty alive instead of resetting it.
        cache.Set(key, value, new MemoryCacheEntryOptions
        {
            SlidingExpiration = CountWindow,
            Size = 1,
        });

        return value;
    }

    private DateTimeOffset? Latest(params string[] keys)
    {
        DateTimeOffset? latest = null;

        foreach (var key in keys)
        {
            if (cache.TryGetValue(key, out DateTimeOffset until) && (latest is null || until > latest))
            {
                latest = until;
            }
        }

        return latest;
    }

    // Namespaced so a username can never collide with an address.
    private static string UserKey(string? username) => $"login:user:{AccountService.Normalize(username)}";

    private static string IpKey(string? ip) => $"login:ip:{ip ?? "unknown"}";

    private static string LockKey(string key) => $"lock:{key}";
}
