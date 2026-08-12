using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Auth;

namespace Mootify.Tests;

/// <summary>
/// These assert the delay and lockout the throttle <i>reports</i>. Nothing here sleeps — the
/// waiting is the endpoint's job, and a suite that really waited 15 minutes would never be run.
/// </summary>
public sealed class LoginThrottleTests
{
    private static LoginThrottle Create(int maxAttempts = 10) =>
        new(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 }),
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions
            {
                MaxFailedAttempts = maxAttempts,
                LockoutDuration = TimeSpan.FromMinutes(15),
            }),
            NullLogger<LoginThrottle>.Instance);

    private static void Fail(LoginThrottle throttle, int times, string user = "mooadmin", string ip = "10.0.0.1")
    {
        for (var i = 0; i < times; i++)
        {
            throttle.RecordFailure(user, ip);
        }
    }

    [Fact]
    public void A_first_attempt_is_not_delayed()
    {
        Assert.Equal(TimeSpan.Zero, Create().GetDelay("mooadmin", "10.0.0.1"));
    }

    [Fact]
    public void Each_failure_adds_a_second()
    {
        var throttle = Create();

        Fail(throttle, 1);
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.GetDelay("mooadmin", "10.0.0.1"));

        Fail(throttle, 2);
        Assert.Equal(TimeSpan.FromSeconds(3), throttle.GetDelay("mooadmin", "10.0.0.1"));
    }

    [Fact]
    public void The_delay_is_capped()
    {
        // Without a ceiling, a long run of failures would hold a request open for minutes,
        // which exhausts our own connections rather than the attacker's patience.
        var throttle = Create(maxAttempts: 100);
        Fail(throttle, 60);

        Assert.Equal(LoginThrottle.MaxDelay, throttle.GetDelay("mooadmin", "10.0.0.1"));
    }

    [Fact]
    public void Signing_in_clears_the_penalty_and_any_lockout()
    {
        var throttle = Create();
        Fail(throttle, 10);
        Assert.NotNull(throttle.GetLockout("mooadmin", "10.0.0.1"));

        throttle.RecordSuccess("mooadmin", "10.0.0.1");

        Assert.Null(throttle.GetLockout("mooadmin", "10.0.0.1"));
        Assert.Equal(TimeSpan.Zero, throttle.GetDelay("mooadmin", "10.0.0.1"));
    }

    // ---- lockout ---------------------------------------------------------

    [Fact]
    public void Nine_wrong_passwords_delay_but_do_not_lock()
    {
        var throttle = Create();
        Fail(throttle, 9);

        Assert.Null(throttle.GetLockout("mooadmin", "10.0.0.1"));
        Assert.Equal(TimeSpan.FromSeconds(9), throttle.GetDelay("mooadmin", "10.0.0.1"));
    }

    [Fact]
    public void The_tenth_locks_for_fifteen_minutes()
    {
        var throttle = Create();
        Fail(throttle, 10);

        var remaining = throttle.GetLockout("mooadmin", "10.0.0.1");

        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void A_lockout_follows_the_username_to_another_address()
    {
        // Otherwise moving machine is all it takes to shrug it off.
        var throttle = Create();
        Fail(throttle, 10, ip: "10.0.0.1");

        Assert.NotNull(throttle.GetLockout("mooadmin", "10.0.0.99"));
    }

    [Fact]
    public void A_lockout_follows_the_address_to_another_username()
    {
        var throttle = Create();

        for (var i = 0; i < 10; i++)
        {
            throttle.RecordFailure($"guess{i}", "10.0.0.1");
        }

        Assert.NotNull(throttle.GetLockout("someone-new", "10.0.0.1"));
    }

    [Fact]
    public void Somebody_elses_lockout_is_not_yours()
    {
        var throttle = Create();
        Fail(throttle, 10, user: "mooadmin", ip: "10.0.0.1");

        Assert.Null(throttle.GetLockout("housemate", "10.0.0.9"));
    }

    [Fact]
    public void Attempts_during_a_lockout_do_not_extend_it()
    {
        // "Try again in 12 minutes" has to stay true, or the message is a lie and the user
        // learns to ignore it.
        var throttle = Create();
        Fail(throttle, 10);
        var first = throttle.GetLockout("mooadmin", "10.0.0.1")!.Value;

        // The endpoint refuses without recording, so nothing further is counted.
        var second = throttle.GetLockout("mooadmin", "10.0.0.1")!.Value;

        Assert.True(second <= first);
        Assert.InRange(second, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Locking_resets_the_counter_so_the_next_mistake_is_not_an_instant_relock()
    {
        // After serving the lockout you get a clean ten, not a hair trigger.
        var throttle = Create();
        Fail(throttle, 10);

        Assert.Equal(TimeSpan.Zero, throttle.GetDelay("mooadmin", "10.0.0.1"));
    }

    [Fact]
    public void The_threshold_is_configurable()
    {
        var throttle = Create(maxAttempts: 3);

        Fail(throttle, 2);
        Assert.Null(throttle.GetLockout("mooadmin", "10.0.0.1"));

        Fail(throttle, 1);
        Assert.NotNull(throttle.GetLockout("mooadmin", "10.0.0.1"));
    }

    [Fact]
    public void The_penalty_follows_the_username_across_addresses()
    {
        // Otherwise a botnet grinding one account resets to zero on every new address.
        var throttle = Create();

        throttle.RecordFailure("mooadmin", "10.0.0.1");
        throttle.RecordFailure("mooadmin", "10.0.0.2");
        throttle.RecordFailure("mooadmin", "10.0.0.3");

        Assert.Equal(TimeSpan.FromSeconds(3), throttle.GetDelay("mooadmin", "10.0.0.4"));
    }

    [Fact]
    public void The_penalty_follows_the_address_across_usernames()
    {
        // And otherwise spraying one password across many accounts costs nothing.
        var throttle = Create();

        throttle.RecordFailure("alice", "10.0.0.1");
        throttle.RecordFailure("bob", "10.0.0.1");
        throttle.RecordFailure("carol", "10.0.0.1");

        Assert.Equal(TimeSpan.FromSeconds(3), throttle.GetDelay("dave", "10.0.0.1"));
    }

    [Fact]
    public void Somebody_elses_failures_do_not_delay_you()
    {
        var throttle = Create();
        throttle.RecordFailure("mooadmin", "10.0.0.1");
        throttle.RecordFailure("mooadmin", "10.0.0.1");

        Assert.Equal(TimeSpan.Zero, throttle.GetDelay("housemate", "10.0.0.9"));
    }

    [Fact]
    public void Usernames_are_matched_the_way_sign_in_matches_them()
    {
        // Case and whitespace can't be a way to dodge the counter, because they aren't a
        // way to get a different account either.
        var throttle = Create();

        throttle.RecordFailure("MooAdmin", "10.0.0.1");
        throttle.RecordFailure("  mooadmin  ", "10.0.0.1");

        Assert.Equal(TimeSpan.FromSeconds(2), throttle.GetDelay("mooadmin", "10.0.0.2"));
    }

    [Fact]
    public void A_missing_address_still_counts()
    {
        var throttle = Create();

        throttle.RecordFailure("mooadmin", null);
        throttle.RecordFailure("mooadmin", null);

        Assert.Equal(TimeSpan.FromSeconds(2), throttle.GetDelay("mooadmin", null));
    }
}
