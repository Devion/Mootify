using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Notifications;

/// <summary>
/// Persist first, then push. Push-only would mean the person who was offline when their
/// album landed never finds out — and that's exactly the person who's been waiting for it.
///
/// In-process events rather than a SignalR hub: every Blazor Server circuit lives in this
/// process, so a singleton event bus delivers the same thing without the plumbing. Swap this
/// for a hub the day Mootify runs on more than one node.
/// </summary>
public sealed class NotificationDispatcher(
    IDbContextFactory<MootifyDbContext> dbFactory,
    ILogger<NotificationDispatcher> log)
{
    /// <summary>Raised on the publishing thread. Handlers must not block.</summary>
    public event Action<Notification>? Received;

    public async Task PublishAsync(
        Guid userId,
        NotificationType type,
        string title,
        string body,
        string? url = null,
        Guid? requestId = null,
        CancellationToken ct = default)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            Body = body,
            Url = url,
            RequestId = requestId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(ct);
        }

        log.LogInformation("Notified {UserId}: {Title}", userId, title);

        try
        {
            Received?.Invoke(notification);
        }
        catch (Exception ex)
        {
            // A dead circuit must not fail the publish — the row is already saved,
            // and the bell will pick it up on reconnect.
            log.LogWarning(ex, "A notification subscriber threw");
        }
    }

    public async Task<List<Notification>> GetRecentAsync(Guid userId, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Notifications
            .Where(n => n.Id == notificationId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// Same, scoped to the owner. The API takes the notification id from a client that could
    /// have made it up, so "mark read" has to be a statement about your own bell.
    /// </summary>
    public async Task<bool> MarkReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);

        return affected > 0;
    }
}
