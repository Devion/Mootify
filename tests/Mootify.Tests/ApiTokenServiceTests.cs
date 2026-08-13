using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Auth;

namespace Mootify.Tests;

/// <summary>
/// The Android app's credential. Everything here is about a token stopping working at the moment
/// it should — revoked, expired, pruned, or belonging to somebody who has since been banned. A
/// token that outlives one of those is a device nobody can take away.
/// </summary>
public sealed class ApiTokenServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private ApiOptions _options = null!;
    private ApiTokenService _tokens = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _options = new ApiOptions();
        _tokens = new ApiTokenService(
            _db,
            new StaticOptionsMonitor<ApiOptions>(_options),
            NullLogger<ApiTokenService>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task An_issued_token_identifies_its_owner()
    {
        var user = await _db.AddUserAsync("devion");

        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");
        var identity = await _tokens.ValidateAsync(issued.Secret);

        Assert.NotNull(identity);
        Assert.Equal(user.Id, identity.UserId);
        Assert.Equal("devion", identity.DisplayName);
        Assert.Equal(issued.TokenId, identity.TokenId);
        Assert.False(identity.IsAdmin);
    }

    [Fact]
    public async Task The_secret_is_never_stored()
    {
        // If the table held the secret, a database that leaks would be a fleet of phones that
        // leaks with it. Only a hash of it goes in.
        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");

        await using var db = _db.CreateDbContext();
        var stored = await db.ApiTokens.AsNoTracking().SingleAsync();

        Assert.NotEqual(issued.Secret, stored.TokenHash);
        Assert.DoesNotContain(stored.TokenHash, issued.Secret);
        Assert.Equal(ApiTokenService.Hash(issued.Secret), stored.TokenHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("moo_not-a-real-token")]
    public async Task Nonsense_is_not_a_token(string candidate)
    {
        await _db.AddUserAsync("devion");
        Assert.Null(await _tokens.ValidateAsync(candidate));
    }

    [Fact]
    public async Task Null_is_not_a_token()
    {
        Assert.Null(await _tokens.ValidateAsync(null));
    }

    [Fact]
    public async Task Revoking_stops_the_device()
    {
        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Old phone");

        Assert.True(await _tokens.RevokeAsync(issued.TokenId, user.Id));
        Assert.Null(await _tokens.ValidateAsync(issued.Secret));
    }

    [Fact]
    public async Task Only_the_owner_can_revoke()
    {
        // Ids travel in URLs. Without the ownership check, "revoke device" would be a way to
        // sign anybody else out.
        var owner = await _db.AddUserAsync("devion");
        var stranger = await _db.AddUserAsync("someone-else");
        var issued = await _tokens.IssueAsync(owner.Id, "Pixel 8");

        Assert.False(await _tokens.RevokeAsync(issued.TokenId, stranger.Id));
        Assert.NotNull(await _tokens.ValidateAsync(issued.Secret));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.ApiTokens.SingleAsync();
            row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        Assert.Null(await _tokens.ValidateAsync(issued.Secret));
    }

    [Fact]
    public async Task A_zero_lifetime_means_no_expiry()
    {
        // The car stereo case: a token that expires is a password prompt at 70mph.
        _options.TokenLifetime = TimeSpan.Zero;

        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Head unit");

        Assert.Null(issued.ExpiresAt);
        Assert.NotNull(await _tokens.ValidateAsync(issued.Secret));
    }

    [Fact]
    public async Task Banning_someone_stops_their_phone_too()
    {
        // The cookie path re-checks this on every request; the token path has to as well, or a
        // ban would only apply to whichever device happened to be a browser.
        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Users.SingleAsync(u => u.Id == user.Id);
            row.IsBanned = true;
            await db.SaveChangesAsync();
        }

        Assert.Null(await _tokens.ValidateAsync(issued.Secret));
    }

    [Fact]
    public async Task Admin_rights_come_through()
    {
        var user = await _db.AddUserAsync("mooadmin");

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Users.SingleAsync(u => u.Id == user.Id);
            row.IsAdmin = true;
            await db.SaveChangesAsync();
        }

        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");
        var identity = await _tokens.ValidateAsync(issued.Secret);

        Assert.NotNull(identity);
        Assert.True(identity.IsAdmin);
    }

    [Fact]
    public async Task Reinstalling_over_and_over_does_not_grow_the_table()
    {
        // Every sign-in mints a row. Without pruning, a phone that reinstalls weekly leaves a
        // permanent trail of working credentials.
        _options.MaxTokensPerUser = 2;

        var user = await _db.AddUserAsync("devion");
        var first = await _tokens.IssueAsync(user.Id, "Install 1");
        var second = await _tokens.IssueAsync(user.Id, "Install 2");
        var third = await _tokens.IssueAsync(user.Id, "Install 3");

        Assert.Null(await _tokens.ValidateAsync(first.Secret));
        Assert.NotNull(await _tokens.ValidateAsync(second.Secret));
        Assert.NotNull(await _tokens.ValidateAsync(third.Secret));

        var listed = await _tokens.ListAsync(user.Id);
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task Revoke_all_clears_every_device()
    {
        var user = await _db.AddUserAsync("devion");
        var phone = await _tokens.IssueAsync(user.Id, "Phone");
        var tablet = await _tokens.IssueAsync(user.Id, "Tablet");

        Assert.Equal(2, await _tokens.RevokeAllAsync(user.Id));
        Assert.Null(await _tokens.ValidateAsync(phone.Secret));
        Assert.Null(await _tokens.ValidateAsync(tablet.Secret));
        Assert.Empty(await _tokens.ListAsync(user.Id));
    }

    [Fact]
    public async Task Another_persons_devices_are_not_listed()
    {
        var mine = await _db.AddUserAsync("devion");
        var theirs = await _db.AddUserAsync("housemate");

        await _tokens.IssueAsync(mine.Id, "My phone");
        await _tokens.IssueAsync(theirs.Id, "Their phone");

        var listed = await _tokens.ListAsync(mine.Id);

        Assert.Single(listed);
        Assert.Equal("My phone", listed[0].DeviceName);
    }

    [Fact]
    public async Task The_current_device_is_flagged_in_the_list()
    {
        var user = await _db.AddUserAsync("devion");
        var phone = await _tokens.IssueAsync(user.Id, "Phone");
        await _tokens.IssueAsync(user.Id, "Tablet");

        var listed = await _tokens.ListAsync(user.Id, phone.TokenId);

        Assert.True(listed.Single(t => t.DeviceName == "Phone").IsCurrent);
        Assert.False(listed.Single(t => t.DeviceName == "Tablet").IsCurrent);
    }

    [Fact]
    public async Task Last_used_is_stamped_when_it_has_gone_stale()
    {
        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");

        var backdated = DateTimeOffset.UtcNow - ApiTokenService.LastUsedResolution - TimeSpan.FromMinutes(1);
        await SetLastUsedAsync(backdated);

        await _tokens.ValidateAsync(issued.Secret);

        Assert.True(await ReadLastUsedAsync() > backdated);
    }

    [Fact]
    public async Task Seeking_through_a_song_does_not_write_to_the_database()
    {
        // A car seeking through an album is hundreds of range requests. Stamping LastUsedAt on
        // each one would turn playback into a write load on the file serving the website.
        var user = await _db.AddUserAsync("devion");
        var issued = await _tokens.IssueAsync(user.Id, "Pixel 8");

        var recent = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
        await SetLastUsedAsync(recent);

        for (var i = 0; i < 5; i++)
        {
            await _tokens.ValidateAsync(issued.Secret);
        }

        Assert.Equal(recent.ToUnixTimeMilliseconds(), (await ReadLastUsedAsync()).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_blank_device_name_still_reads_as_something()
    {
        var user = await _db.AddUserAsync("devion");
        await _tokens.IssueAsync(user.Id, "   ");

        Assert.Equal("Unknown device", (await _tokens.ListAsync(user.Id))[0].DeviceName);
    }

    private async Task SetLastUsedAsync(DateTimeOffset when)
    {
        await using var db = _db.CreateDbContext();
        var row = await db.ApiTokens.SingleAsync();
        row.LastUsedAt = when;
        await db.SaveChangesAsync();
    }

    private async Task<DateTimeOffset> ReadLastUsedAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.ApiTokens.AsNoTracking().Select(t => t.LastUsedAt).SingleAsync();
    }
}
