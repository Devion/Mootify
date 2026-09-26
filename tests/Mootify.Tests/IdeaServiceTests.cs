using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Ideas;
using Mootify.Services.Notifications;

namespace Mootify.Tests;

/// <summary>
/// The Ideabox as a service: who may post, who may read, and who may make a row go away.
///
/// <see cref="IdeaTextTests"/> covers what a message may contain. This covers the rest, and in
/// particular the two authorization rules that <c>[Authorize]</c> on a page does <i>not</i>
/// enforce — an admin-only archive and an author-or-admin delete are decided here, because the
/// attribute decides what is drawn and the service decides what happens.
/// </summary>
public sealed class IdeaServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private IdeaService _service = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _service = new IdeaService(
            _db,
            new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance),
            NullLogger<IdeaService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<AppUser> AdminAsync(string name)
    {
        var user = await _db.AddUserAsync(name);

        await using var db = _db.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id);
        row.IsAdmin = true;
        await db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Backdates every one of somebody's ideas, so the 20-second anti-double-post guard doesn't
    /// make a test about the quota into a test about the throttle.
    /// </summary>
    private async Task BackdateAsync(Guid userId)
    {
        await using var db = _db.CreateDbContext();

        foreach (var idea in await db.Ideas.Where(i => i.UserId == userId).ToListAsync())
        {
            idea.CreatedAt = DateTimeOffset.UtcNow - IdeaService.MinimumInterval - TimeSpan.FromMinutes(1);
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> PostAsync(Guid userId, string message)
    {
        var result = await _service.PostAsync(userId, message);
        Assert.True(result.Ok, result.Error);
        return result.Id!.Value;
    }

    // ---- posting ---------------------------------------------------------

    [Fact]
    public async Task An_idea_is_stored_and_the_author_can_see_it()
    {
        var user = await _db.AddUserAsync("devion");
        await PostAsync(user.Id, "More cowbell in the play bar");

        var mine = Assert.Single(await _service.GetMineAsync(user.Id));

        Assert.Equal("More cowbell in the play bar", mine.Message);
        Assert.Equal("devion", mine.AuthorName);
        Assert.False(mine.IsArchived);
    }

    [Fact]
    public async Task A_message_the_validator_refuses_is_not_stored()
    {
        var user = await _db.AddUserAsync("devion");

        // Refused for containing a right-to-left override, per IdeaTextTests.
        var result = await _service.PostAsync(user.Id, "nice" + char.ConvertFromUtf32(0x202E) + "idea");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Empty(await _service.GetMineAsync(user.Id));
    }

    [Fact]
    public async Task Two_posts_in_a_row_are_refused()
    {
        // A stuck key or a double-submitted form, not an attacker — but an inbox full of the
        // same sentence is an inbox nobody opens.
        var user = await _db.AddUserAsync("devion");
        await PostAsync(user.Id, "First thought");

        var second = await _service.PostAsync(user.Id, "Second thought");

        Assert.False(second.Ok);
        Assert.Single(await _service.GetMineAsync(user.Id));
    }

    [Fact]
    public async Task The_queue_of_unread_ideas_per_person_is_capped()
    {
        var user = await _db.AddUserAsync("devion");

        for (var i = 0; i < IdeaService.MaxOpenPerUser; i++)
        {
            await PostAsync(user.Id, $"Idea number {i}");
            await BackdateAsync(user.Id);
        }

        var overflow = await _service.PostAsync(user.Id, "One too many");

        Assert.False(overflow.Ok);
        Assert.Contains(IdeaService.MaxOpenPerUser.ToString(), overflow.Error);
    }

    [Fact]
    public async Task Archiving_frees_the_quota_again()
    {
        // The cap is on what is waiting on an admin, not on how much anybody may ever say.
        var admin = await AdminAsync("mooadmin");
        var user = await _db.AddUserAsync("devion");

        var ids = new List<Guid>();
        for (var i = 0; i < IdeaService.MaxOpenPerUser; i++)
        {
            ids.Add(await PostAsync(user.Id, $"Idea number {i}"));
            await BackdateAsync(user.Id);
        }

        Assert.False((await _service.PostAsync(user.Id, "blocked")).Ok);

        Assert.True(await _service.SetArchivedAsync(ids[0], archived: true, admin.Id));
        await BackdateAsync(user.Id);

        Assert.True((await _service.PostAsync(user.Id, "now there's room")).Ok);
    }

    // ---- who hears about it ----------------------------------------------

    [Fact]
    public async Task Every_admin_is_notified_and_the_author_is_not()
    {
        var one = await AdminAsync("mooadmin");
        var two = await AdminAsync("secondadmin");
        var user = await _db.AddUserAsync("devion");

        await PostAsync(user.Id, "A suggestion box nobody checks is not a suggestion box");

        await using var db = _db.CreateDbContext();

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == one.Id));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == two.Id));
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == user.Id));

        var notification = await db.Notifications.FirstAsync(n => n.UserId == one.Id);
        Assert.Contains("devion", notification.Title);
        Assert.Equal("/admin/ideas", notification.Url);
    }

    [Fact]
    public async Task An_admin_posting_does_not_ring_their_own_cowbell()
    {
        var admin = await AdminAsync("mooadmin");
        await PostAsync(admin.Id, "Note to self");

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == admin.Id));
    }

    [Fact]
    public async Task A_banned_admin_is_not_notified()
    {
        var admin = await AdminAsync("mooadmin");

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Users.FirstAsync(u => u.Id == admin.Id);
            row.IsBanned = true;
            await db.SaveChangesAsync();
        }

        var user = await _db.AddUserAsync("devion");
        await PostAsync(user.Id, "Hello?");

        await using var check = _db.CreateDbContext();
        Assert.Equal(0, await check.Notifications.CountAsync(n => n.UserId == admin.Id));
    }

    // ---- the admin inbox -------------------------------------------------

    [Fact]
    public async Task Waiting_and_filed_are_two_lists()
    {
        var admin = await AdminAsync("mooadmin");
        var user = await _db.AddUserAsync("devion");

        var first = await PostAsync(user.Id, "One");
        await BackdateAsync(user.Id);
        await PostAsync(user.Id, "Two");

        await _service.SetArchivedAsync(first, archived: true, admin.Id);

        Assert.Equal("Two", Assert.Single(await _service.GetAllAsync(archived: false)).Message);
        Assert.Equal("One", Assert.Single(await _service.GetAllAsync(archived: true)).Message);
        Assert.Equal(1, await _service.CountOpenAsync());
    }

    [Fact]
    public async Task Putting_one_back_makes_it_wait_again()
    {
        var admin = await AdminAsync("mooadmin");
        var user = await _db.AddUserAsync("devion");
        var id = await PostAsync(user.Id, "One");

        await _service.SetArchivedAsync(id, archived: true, admin.Id);
        await _service.SetArchivedAsync(id, archived: false, admin.Id);

        Assert.Equal(1, await _service.CountOpenAsync());
    }

    [Fact]
    public async Task The_author_still_sees_an_archived_idea()
    {
        // It is the only feedback this box gives anybody, which is why archiving isn't deleting.
        var admin = await AdminAsync("mooadmin");
        var user = await _db.AddUserAsync("devion");
        var id = await PostAsync(user.Id, "One");

        await _service.SetArchivedAsync(id, archived: true, admin.Id);

        Assert.True(Assert.Single(await _service.GetMineAsync(user.Id)).IsArchived);
    }

    // ---- authorization ---------------------------------------------------

    [Fact]
    public async Task Only_an_admin_can_archive()
    {
        var user = await _db.AddUserAsync("devion");
        var other = await _db.AddUserAsync("nosey");
        var id = await PostAsync(user.Id, "One");

        // Not even the author: filing one means "an admin has dealt with this".
        Assert.False(await _service.SetArchivedAsync(id, archived: true, user.Id));
        Assert.False(await _service.SetArchivedAsync(id, archived: true, other.Id));
        Assert.Equal(1, await _service.CountOpenAsync());
    }

    [Fact]
    public async Task The_author_can_withdraw_their_own()
    {
        var user = await _db.AddUserAsync("devion");
        var id = await PostAsync(user.Id, "Actually never mind");

        Assert.True(await _service.DeleteAsync(id, user.Id));
        Assert.Empty(await _service.GetMineAsync(user.Id));
    }

    [Fact]
    public async Task Somebody_else_cannot_delete_it()
    {
        var user = await _db.AddUserAsync("devion");
        var other = await _db.AddUserAsync("nosey");
        var id = await PostAsync(user.Id, "Mine");

        Assert.False(await _service.DeleteAsync(id, other.Id));
        Assert.Single(await _service.GetMineAsync(user.Id));
    }

    [Fact]
    public async Task An_admin_can_delete_anybody_s()
    {
        var admin = await AdminAsync("mooadmin");
        var user = await _db.AddUserAsync("devion");
        var id = await PostAsync(user.Id, "Something regrettable");

        Assert.True(await _service.DeleteAsync(id, admin.Id));
        Assert.Empty(await _service.GetMineAsync(user.Id));
    }

    [Fact]
    public async Task A_banned_admin_is_not_an_admin_any_more()
    {
        // The check is against the live row, not against a claim minted before the ban.
        var admin = await AdminAsync("mooadmin");
        var user = await _db.AddUserAsync("devion");
        var id = await PostAsync(user.Id, "One");

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Users.FirstAsync(u => u.Id == admin.Id);
            row.IsBanned = true;
            await db.SaveChangesAsync();
        }

        Assert.False(await _service.SetArchivedAsync(id, archived: true, admin.Id));
        Assert.False(await _service.DeleteAsync(id, admin.Id));
    }

    [Fact]
    public async Task Deleting_the_author_takes_their_ideas_with_them()
    {
        var user = await _db.AddUserAsync("devion");
        await PostAsync(user.Id, "One");

        await using (var db = _db.CreateDbContext())
        {
            db.Users.Remove(await db.Users.FirstAsync(u => u.Id == user.Id));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await _service.GetAllAsync());
    }

    [Fact]
    public async Task Filing_with_a_reply_saves_and_notifies_only_the_author_once()
    {
        var author = await _db.AddUserAsync("author");
        var admin = await AdminAsync("admin");
        var id = await PostAsync(author.Id, "More cowbell");
        Assert.False(await _service.SetArchivedAsync(id, true, author.Id, reply: "No permission"));
        Assert.False(await _service.SetArchivedAsync(id, true, admin.Id, reply: new string('x', 281)));
        Assert.True(await _service.SetArchivedAsync(id, true, admin.Id, reply: "Done, enjoy!"));
        Assert.True(await _service.SetArchivedAsync(id, true, admin.Id, reply: "Done, enjoy!"));
        var idea = Assert.Single(await _service.GetMineAsync(author.Id));
        Assert.Equal("Done, enjoy!", idea.AdminReply);
        Assert.True(idea.IsArchived);
        await using var db = _db.CreateDbContext();
        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == author.Id).ToListAsync());
        Assert.Equal("Done, enjoy!", notification.Body);
        Assert.Equal("/ideas", notification.Url);
    }
}
