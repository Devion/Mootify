using Mootify.Configuration;
using Mootify.Services.Settings;

namespace Mootify.Tests;

/// <summary>
/// The point of these settings is that the database beats mootify.json, so what's worth testing
/// is the handover: which of the two answers when, and that clearing gives the file its job back.
/// </summary>
public sealed class SettingsServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private SettingsService _settings = null!;
    private readonly RequestOptions _requests = new() { MaxOpenPerUser = 5 };

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _settings = new SettingsService(
            _db,
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()),
            new StaticOptionsMonitor<RequestOptions>(_requests));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Unset_falls_back_to_the_configured_limit()
    {
        Assert.Equal(5, await _settings.GetMaxOpenRequestsAsync());
        Assert.Null(await _settings.GetMaxOpenRequestsOverrideAsync());
    }

    [Fact]
    public async Task An_admin_limit_overrides_the_configured_one()
    {
        await _settings.SetMaxOpenRequestsAsync(20);

        Assert.Equal(20, await _settings.GetMaxOpenRequestsAsync());
        Assert.Equal(20, await _settings.GetMaxOpenRequestsOverrideAsync());
    }

    [Fact]
    public async Task Saving_twice_updates_rather_than_duplicating()
    {
        await _settings.SetMaxOpenRequestsAsync(20);
        await _settings.SetMaxOpenRequestsAsync(7);

        Assert.Equal(7, await _settings.GetMaxOpenRequestsAsync());

        await using var db = _db.CreateDbContext();
        Assert.Single(db.Settings.Where(s => s.Key == SettingsService.MaxOpenRequestsKey));
    }

    [Fact]
    public async Task Clearing_hands_the_limit_back_to_the_file()
    {
        await _settings.SetMaxOpenRequestsAsync(20);
        await _settings.ClearMaxOpenRequestsAsync();

        Assert.Null(await _settings.GetMaxOpenRequestsOverrideAsync());
        Assert.Equal(5, await _settings.GetMaxOpenRequestsAsync());
    }

    [Fact]
    public async Task Clearing_a_limit_that_was_never_set_is_a_no_op()
    {
        await _settings.ClearMaxOpenRequestsAsync();

        Assert.Equal(5, await _settings.GetMaxOpenRequestsAsync());
    }

    [Theory]
    [InlineData(0, SettingsService.MinOpenRequests)]
    [InlineData(-4, SettingsService.MinOpenRequests)]
    [InlineData(99999, SettingsService.MaxOpenRequests)]
    public async Task Out_of_range_limits_are_clamped(int typed, int expected)
    {
        await _settings.SetMaxOpenRequestsAsync(typed);

        Assert.Equal(expected, await _settings.GetMaxOpenRequestsAsync());
    }

    [Fact]
    public async Task A_limit_edited_into_nonsense_by_hand_falls_back_to_the_file()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Settings.Add(new Mootify.Data.AppSetting
            {
                Key = SettingsService.MaxOpenRequestsKey,
                Value = "lots",
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _settings.GetMaxOpenRequestsOverrideAsync());
        Assert.Equal(5, await _settings.GetMaxOpenRequestsAsync());
    }

    [Fact]
    public async Task Registration_still_falls_back_and_overrides_the_same_way()
    {
        Assert.True(await _settings.GetRegistrationEnabledAsync());

        await _settings.SetRegistrationEnabledAsync(false);
        Assert.False(await _settings.GetRegistrationEnabledAsync());

        await _settings.SetRegistrationEnabledAsync(true);
        Assert.True(await _settings.GetRegistrationEnabledAsync());
    }
}
