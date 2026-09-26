using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Auth;
using Mootify.Services.Settings;

namespace Mootify.Tests;

public sealed class AccountServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private AccountService _accounts = null!;
    private SettingsService _settings = null!;
    private SetupState _setup = null!;
    private readonly AuthOptions _options = new() { AdminUsername = "mooadmin", MinPasswordLength = 8 };

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        var monitor = new StaticOptionsMonitor<AuthOptions>(_options);
        _settings = new SettingsService(_db, monitor, new StaticOptionsMonitor<RequestOptions>(new RequestOptions()));
        _setup = new SetupState();
        _accounts = new AccountService(_db, monitor, _settings, _setup, NullLogger<AccountService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private Task<AuthResult> SetupAdminAsync(string password = "hunter22") =>
        _accounts.CompleteSetupAsync("mooadmin", password, password);

    // ---- first run -------------------------------------------------------

    [Fact]
    public async Task First_run_creates_the_admin_account()
    {
        var result = await SetupAdminAsync();

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Principal!.IsInRole(MootifyAuth.AdminRole));
        Assert.True(_setup.IsComplete);

        await using var db = _db.CreateDbContext();
        var user = await db.Users.SingleAsync();
        Assert.True(user.IsAdmin);
        Assert.NotEqual("hunter22", user.PasswordHash);   // stored hashed, never in the clear
    }

    [Fact]
    public async Task The_first_account_has_to_use_the_configured_admin_name()
    {
        var result = await _accounts.CompleteSetupAsync("someoneelse", "hunter22", "hunter22");

        Assert.False(result.Succeeded);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Setup_refuses_once_anybody_exists()
    {
        // Otherwise /setup stays open forever as a way to mint an admin.
        await SetupAdminAsync();

        var second = await _accounts.CompleteSetupAsync("mooadmin", "another11", "another11");

        Assert.False(second.Succeeded);
        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync());
    }

    // ---- registration ----------------------------------------------------

    [Fact]
    public async Task Registered_users_are_not_admins()
    {
        await SetupAdminAsync();

        var result = await _accounts.RegisterAsync("housemate", "password1", "password1");

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Principal);
        await using var db = _db.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.NormalizedName == "housemate");
        Assert.False(user.IsAdmin);
        Assert.True(user.ApprovalPending);
        Assert.False((await _accounts.SignInAsync("housemate", "password1")).Succeeded);
    }

    [Fact]
    public async Task Registration_can_be_closed()
    {
        await SetupAdminAsync();
        await _settings.SetRegistrationEnabledAsync(false);

        var result = await _accounts.RegisterAsync("housemate", "password1", "password1");

        Assert.False(result.Succeeded);

        await _settings.SetRegistrationEnabledAsync(true);
        Assert.True((await _accounts.RegisterAsync("housemate", "password1", "password1")).Succeeded);
    }

    [Fact]
    public async Task Usernames_are_unique_regardless_of_case()
    {
        await SetupAdminAsync();
        await _accounts.RegisterAsync("Housemate", "password1", "password1");

        var duplicate = await _accounts.RegisterAsync("  HOUSEMATE ", "password1", "password1");

        Assert.False(duplicate.Succeeded);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public async Task Silly_usernames_are_refused(string username)
    {
        await SetupAdminAsync();
        Assert.False((await _accounts.RegisterAsync(username, "password1", "password1")).Succeeded);
    }

    [Fact]
    public async Task Short_and_mismatched_passwords_are_refused()
    {
        await SetupAdminAsync();

        Assert.False((await _accounts.RegisterAsync("a", "short", "short")).Succeeded);
        Assert.False((await _accounts.RegisterAsync("a", "password1", "password2")).Succeeded);
    }

    // ---- sign in ---------------------------------------------------------

    [Fact]
    public async Task Sign_in_works_and_wrong_credentials_do_not()
    {
        await SetupAdminAsync("hunter22");

        Assert.True((await _accounts.SignInAsync("mooadmin", "hunter22")).Succeeded);
        Assert.True((await _accounts.SignInAsync("MOOADMIN", "hunter22")).Succeeded);
        Assert.False((await _accounts.SignInAsync("mooadmin", "wrong")).Succeeded);
        Assert.False((await _accounts.SignInAsync("nobody", "hunter22")).Succeeded);
    }

    [Fact]
    public async Task A_wrong_password_and_a_missing_account_say_the_same_thing()
    {
        // Different wording would let anyone enumerate who has an account here.
        await SetupAdminAsync();

        var wrongPassword = await _accounts.SignInAsync("mooadmin", "nope");
        var noSuchUser = await _accounts.SignInAsync("ghost", "nope");

        Assert.Equal(wrongPassword.Error, noSuchUser.Error);
    }

    [Fact]
    public async Task A_banned_user_cannot_sign_in_and_is_told_why()
    {
        await SetupAdminAsync();
        await _accounts.RegisterAsync("housemate", "password1", "password1");

        await using (var db = _db.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(u => u.NormalizedName == "housemate");
            user.IsBanned = true;
            user.BanReason = "kept adding polka";
            await db.SaveChangesAsync();
        }

        var result = await _accounts.SignInAsync("housemate", "password1");

        Assert.False(result.Succeeded);
        Assert.Contains("polka", result.Error);
    }

    // ---- passwords -------------------------------------------------------

    [Fact]
    public async Task Changing_your_password_needs_the_current_one()
    {
        var setup = await SetupAdminAsync("hunter22");
        var userId = Guid.Parse(setup.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);

        Assert.False((await _accounts.ChangePasswordAsync(userId, "wrong", "newpass11", "newpass11")).Ok);
        Assert.True((await _accounts.ChangePasswordAsync(userId, "hunter22", "newpass11", "newpass11")).Ok);

        Assert.True((await _accounts.SignInAsync("mooadmin", "newpass11")).Succeeded);
        Assert.False((await _accounts.SignInAsync("mooadmin", "hunter22")).Succeeded);
    }

    [Fact]
    public async Task An_admin_can_reset_somebody_elses_password_but_a_normal_user_cannot()
    {
        // There's no email here, so somebody has to be able to rescue a locked-out account.
        var setup = await SetupAdminAsync();
        var adminId = Guid.Parse(setup.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var registration = await _accounts.RegisterAsync("housemate", "password1", "password1");
        var userId = await ApproveHousemateAsync();

        Assert.False((await _accounts.AdminSetPasswordAsync(userId, adminId, "hacked11", "hacked11")).Ok);
        Assert.True((await _accounts.AdminSetPasswordAsync(adminId, userId, "rescued1", "rescued1")).Ok);

        Assert.True((await _accounts.SignInAsync("housemate", "rescued1")).Succeeded);
    }

    // ---- one-time passwords ----------------------------------------------

    [Fact]
    public async Task What_an_admin_sets_is_a_one_time_password()
    {
        // Two people know it the moment it's spoken, so it buys exactly one sign-in. The claim
        // is what every gate downstream reads.
        var (adminId, userId) = await AdminAndHousemateAsync();

        Assert.True((await _accounts.AdminSetPasswordAsync(adminId, userId, "rescued1", "rescued1")).Ok);
        Assert.True(await MustChangeAsync(userId));

        var signIn = await _accounts.SignInAsync("housemate", "rescued1");

        Assert.True(signIn.Succeeded, signIn.Error);
        Assert.True(MootifyAuth.MustChangePassword(signIn.Principal));
    }

    [Fact]
    public async Task Choosing_your_own_password_lifts_the_flag()
    {
        var (adminId, userId) = await AdminAndHousemateAsync();
        await _accounts.AdminSetPasswordAsync(adminId, userId, "rescued1", "rescued1");

        Assert.True((await _accounts.ChangePasswordAsync(userId, "rescued1", "myownpw1", "myownpw1")).Ok);
        Assert.False(await MustChangeAsync(userId));

        var signIn = await _accounts.SignInAsync("housemate", "myownpw1");

        Assert.True(signIn.Succeeded, signIn.Error);
        Assert.False(MootifyAuth.MustChangePassword(signIn.Principal));
    }

    [Fact]
    public async Task The_one_time_password_cant_be_kept_as_the_new_one()
    {
        // Otherwise the forced change is a form to click through and the password an admin
        // knows stays live.
        var (adminId, userId) = await AdminAndHousemateAsync();
        await _accounts.AdminSetPasswordAsync(adminId, userId, "rescued1", "rescued1");

        var (ok, error) = await _accounts.ChangePasswordAsync(userId, "rescued1", "rescued1", "rescued1");

        Assert.False(ok);
        Assert.Contains("already have", error);
        Assert.True(await MustChangeAsync(userId));
    }

    [Fact]
    public async Task Resetting_a_password_signs_the_phones_out_too()
    {
        // A live device token is credentials the reset was meant to retire, and it would sail
        // straight past the change screen the website puts up.
        var (adminId, userId) = await AdminAndHousemateAsync();

        var tokens = new ApiTokenService(
            _db, new StaticOptionsMonitor<ApiOptions>(new ApiOptions()), NullLogger<ApiTokenService>.Instance);
        var issued = await tokens.IssueAsync(userId, "Pixel 8");

        Assert.NotNull(await tokens.ValidateAsync(issued.Secret));

        await _accounts.AdminSetPasswordAsync(adminId, userId, "rescued1", "rescued1");

        Assert.Null(await tokens.ValidateAsync(issued.Secret));
    }

    [Fact]
    public async Task An_admin_cant_hand_themselves_a_one_time_password()
    {
        // It would lock them into the change screen to solve a problem they don't have.
        var (adminId, _) = await AdminAndHousemateAsync();

        var (ok, error) = await _accounts.AdminSetPasswordAsync(adminId, adminId, "newpass1", "newpass1");

        Assert.False(ok);
        Assert.Contains("account page", error);
        Assert.False(await MustChangeAsync(adminId));
    }

    private async Task<(Guid AdminId, Guid UserId)> AdminAndHousemateAsync()
    {
        var setup = await SetupAdminAsync();
        var registration = await _accounts.RegisterAsync("housemate", "password1", "password1");

        return (Guid.Parse(setup.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!),
                await ApproveHousemateAsync());
    }

    private async Task<bool> MustChangeAsync(Guid userId)
    {
        await using var db = _db.CreateDbContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.MustChangePassword).SingleAsync();
    }
    private async Task<Guid> ApproveHousemateAsync()
    {
        await using var db = _db.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.NormalizedName == "housemate");
        var admin = await db.Users.SingleAsync(u => u.IsAdmin);
        var service = new Mootify.Services.Admin.AdminService(_db, NullLogger<Mootify.Services.Admin.AdminService>.Instance);
        Assert.False((await service.ApproveAsync(user.Id, user.Id)).Ok);
        Assert.True((await service.ApproveAsync(admin.Id, user.Id)).Ok);
        return user.Id;
    }

    [Fact]
    public async Task Site_activity_updates_last_seen_and_is_throttled()
    {
        await SetupAdminAsync();
        await using var db = _db.CreateDbContext();
        var user = await db.Users.SingleAsync();
        user.LastSeenAt = DateTimeOffset.UtcNow.AddDays(-10);
        await db.SaveChangesAsync();
        await _accounts.TouchLastSeenAsync(user.Id);
        await db.Entry(user).ReloadAsync();
        var seen = user.LastSeenAt;
        Assert.True(seen > DateTimeOffset.UtcNow.AddMinutes(-1));
        await _accounts.TouchLastSeenAsync(user.Id);
        await db.Entry(user).ReloadAsync();
        Assert.Equal(seen, user.LastSeenAt);
    }
}
