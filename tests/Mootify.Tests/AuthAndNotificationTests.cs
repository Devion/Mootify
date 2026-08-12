using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mootify.Data;
using Mootify.Services.Notifications;

namespace Mootify.Tests;

public sealed class NotificationDispatcherTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private NotificationDispatcher _dispatcher = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _dispatcher = new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Notification_is_persisted_before_it_is_pushed()
    {
        // The person who was offline when their album landed is exactly the person who has
        // been waiting for it — a push-only bell would lose them.
        var user = await _db.AddUserAsync("devion");
        Notification? pushed = null;
        _dispatcher.Received += n => pushed = n;

        await _dispatcher.PublishAsync(user.Id, NotificationType.RequestAvailable, "Ready", "3 tracks");

        Assert.NotNull(pushed);

        await using var db = _db.CreateDbContext();
        var stored = await db.Notifications.SingleAsync();
        Assert.Equal("Ready", stored.Title);
        Assert.Null(stored.ReadAt);
        Assert.Equal(1, await _dispatcher.GetUnreadCountAsync(user.Id));
    }

    [Fact]
    public async Task A_dead_subscriber_does_not_lose_the_notification()
    {
        var user = await _db.AddUserAsync("devion");
        _dispatcher.Received += _ => throw new InvalidOperationException("circuit is gone");

        await _dispatcher.PublishAsync(user.Id, NotificationType.RequestAvailable, "Ready", "3 tracks");

        Assert.Equal(1, await _dispatcher.GetUnreadCountAsync(user.Id));
    }

    [Fact]
    public async Task Marking_read_clears_the_badge()
    {
        var user = await _db.AddUserAsync("devion");
        await _dispatcher.PublishAsync(user.Id, NotificationType.RequestAvailable, "One", "");
        await _dispatcher.PublishAsync(user.Id, NotificationType.RequestAvailable, "Two", "");

        Assert.Equal(2, await _dispatcher.GetUnreadCountAsync(user.Id));

        await _dispatcher.MarkAllReadAsync(user.Id);

        Assert.Equal(0, await _dispatcher.GetUnreadCountAsync(user.Id));
    }

    [Fact]
    public async Task Recent_notifications_come_back_newest_first()
    {
        // Regression: SQLite cannot ORDER BY a DateTimeOffset stored as TEXT. The model
        // converts them to a sortable binary form so the bell isn't in random order.
        var user = await _db.AddUserAsync("devion");
        await _dispatcher.PublishAsync(user.Id, NotificationType.Info, "First", "");
        await Task.Delay(10);
        await _dispatcher.PublishAsync(user.Id, NotificationType.Info, "Second", "");

        var recent = await _dispatcher.GetRecentAsync(user.Id);

        Assert.Equal(["Second", "First"], recent.Select(n => n.Title));
    }

    [Fact]
    public async Task Notifications_are_scoped_to_their_user()
    {
        var mine = await _db.AddUserAsync("devion");
        var theirs = await _db.AddUserAsync("someone-else");

        await _dispatcher.PublishAsync(theirs.Id, NotificationType.Info, "Not yours", "");

        Assert.Equal(0, await _dispatcher.GetUnreadCountAsync(mine.Id));
        Assert.Empty(await _dispatcher.GetRecentAsync(mine.Id));
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
