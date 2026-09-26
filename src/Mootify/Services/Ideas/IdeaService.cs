using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Notifications;

namespace Mootify.Services.Ideas;

/// <summary>One row as a page renders it — the author's name resolved, the entity left behind.</summary>
public sealed record IdeaRow(
    Guid Id,
    Guid UserId,
    string AuthorName,
    string Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    string? AdminReply = null)
{
    public bool IsArchived => ArchivedAt is not null;
}

/// <summary>Why a post didn't land. <paramref name="Error"/> is written to be shown to the person.</summary>
public sealed record IdeaPostResult(Guid? Id, string? Error)
{
    public bool Ok => Id is not null;
}

/// <summary>
/// The Ideabox: short notes from anybody with an account to whoever runs the server.
///
/// Three rules, and each of them is here rather than in the page because the page is not the
/// only thing that will ever call this:
///
/// - <b>Text is validated, not escaped.</b> <see cref="IdeaText"/> decides what a message may
///   contain and this is the only place that writes one. See that file for why the rule is
///   "printable ASCII" and not "strip the script tags".
/// - <b>Posting is throttled.</b> Not against an attacker — anyone posting has an account — but
///   against the accident: a stuck key, a double-submitted form, somebody idly testing the box.
///   An admin inbox with 400 rows in it is an admin inbox nobody opens.
/// - <b>Admins are told.</b> A suggestion box that has to be checked on purpose is a suggestion
///   box nobody checks, so a new idea rings the same cowbell everything else does.
/// </summary>
public sealed class IdeaService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    NotificationDispatcher notifications,
    ILogger<IdeaService> log)
{
    /// <summary>Back-to-back posts inside this are the same thought twice, or a stuck button.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many of one person's ideas may be waiting on an admin at once. Archiving clears the
    /// count, so this is a cap on the queue rather than on how much anybody may ever say.
    /// </summary>
    public const int MaxOpenPerUser = 10;

    /// <summary>What the admin's list shows in one go. Older ones are still there, one page back.</summary>
    public const int PageSize = 50;

    public async Task<IdeaPostResult> PostAsync(Guid userId, string? message, CancellationToken ct = default)
    {
        var validated = IdeaText.Validate(message);
        if (!validated.Ok) return new IdeaPostResult(null, validated.Error);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        // Both guards read the same rows, so they are one query. MaxAsync would throw on an
        // account that has never posted, which is most of them.
        var mine = await db.Ideas
            .AsNoTracking()
            .Where(i => i.UserId == userId)
            .Select(i => new { i.CreatedAt, i.ArchivedAt })
            .ToListAsync(ct);

        if (mine.Count > 0 && mine.Max(i => i.CreatedAt) > now - MinimumInterval)
        {
            return new IdeaPostResult(null, "You just posted one. Give it a moment.");
        }

        if (mine.Count(i => i.ArchivedAt is null) >= MaxOpenPerUser)
        {
            return new IdeaPostResult(
                null,
                $"You have {MaxOpenPerUser} ideas still waiting to be read. Let those land first.");
        }

        var idea = new Idea
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Message = validated.Text!,
            CreatedAt = now,
        };

        db.Ideas.Add(idea);
        await db.SaveChangesAsync(ct);

        await NotifyAdminsAsync(db, userId, idea, ct);

        log.LogInformation("{User} posted an idea ({Length} characters)", userId, idea.Message.Length);
        return new IdeaPostResult(idea.Id, null);
    }

    /// <summary>
    /// Every admin, because "the admin" isn't a single account — the first one is created at
    /// setup and any of them can promote another. The author is skipped: an admin leaving
    /// themselves a note doesn't need a cowbell about it.
    /// </summary>
    private async Task NotifyAdminsAsync(MootifyDbContext db, Guid authorId, Idea idea, CancellationToken ct)
    {
        var author = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == authorId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "Somebody";

        var admins = await db.Users
            .AsNoTracking()
            .Where(u => u.IsAdmin && !u.IsBanned && !u.ApprovalPending && u.Id != authorId)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var adminId in admins)
        {
            // The body is the message itself. It has already been through IdeaText, which is the
            // whole reason that runs before anything is stored rather than before anything is
            // shown — this is one of the places nobody would have remembered to escape.
            await notifications.PublishAsync(
                adminId,
                NotificationType.Info,
                $"{author} had an idea",
                Preview(idea.Message),
                url: "/admin/ideas",
                ct: ct);
        }
    }

    /// <summary>First line, clipped. A notification body is a hook, not the whole message.</summary>
    private static string Preview(string message)
    {
        var firstLine = message.Split('\n')[0];
        return firstLine.Length <= 120 ? firstLine : firstLine[..117] + "...";
    }

    /// <summary>Somebody's own ideas, newest first, archived ones included so they can see it landed.</summary>
    public async Task<List<IdeaRow>> GetMineAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Ideas
            .AsNoTracking()
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(PageSize)
            .Select(i => new IdeaRow(
                i.Id, i.UserId, i.User!.DisplayName, i.Message, i.CreatedAt, i.ArchivedAt, i.AdminReply))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The admin inbox. <paramref name="archived"/> switches between the two lists rather than
    /// showing one long one: an archived idea is a decision that has been made, and mixing those
    /// into the list of things still to read makes the list useless.
    /// </summary>
    public async Task<List<IdeaRow>> GetAllAsync(bool archived = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Ideas
            .AsNoTracking()
            .Where(i => archived ? i.ArchivedAt != null : i.ArchivedAt == null)
            .OrderByDescending(i => i.CreatedAt)
            .Take(PageSize)
            .Select(i => new IdeaRow(
                i.Id, i.UserId, i.User!.DisplayName, i.Message, i.CreatedAt, i.ArchivedAt, i.AdminReply))
            .ToListAsync(ct);
    }

    /// <summary>How many are still waiting, for the badge on the admin tab.</summary>
    public async Task<int> CountOpenAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Ideas.CountAsync(i => i.ArchivedAt == null, ct);
    }

    /// <summary>
    /// Marks one as dealt with, or puts it back. Archiving is not deleting — the person who wrote
    /// it can still see it, which is the only feedback this box gives them.
    ///
    /// <paramref name="adminUserId"/> is re-checked against the database rather than taken on
    /// trust from the page's <c>[Authorize]</c> attribute. That attribute decides what is drawn;
    /// this decides what happens, which is the same split every other admin operation here makes.
    /// </summary>
    public async Task<bool> SetArchivedAsync(
        Guid ideaId, bool archived, Guid adminUserId, CancellationToken ct = default, string? reply = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, adminUserId, ct))
        {
            log.LogWarning("{User} tried to file idea {Idea} without being an admin", adminUserId, ideaId);
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == ideaId, ct);
        if (idea is null) return false;

        reply = reply?.Trim();
        if (!string.IsNullOrEmpty(reply) && (reply.Length > 280 || reply.Any(char.IsControl))) return false;
        if (archived && idea.ArchivedAt is not null) return true;
        idea.ArchivedAt = archived ? DateTimeOffset.UtcNow : null;
        Notification? notification = null;
        if (archived && !string.IsNullOrEmpty(reply))
        {
            idea.AdminReply = reply;
            notification = new Notification
            {
                Id = Guid.NewGuid(), UserId = idea.UserId, Type = NotificationType.Info,
                Title = "An admin replied to your idea", Body = reply, Url = "/ideas",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Notifications.Add(notification);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (notification is not null) notifications.Deliver(notification);
        return true;
    }

    /// <summary>
    /// Gone for good. Two people may do this and nobody else: the author, withdrawing something
    /// they regret sending, and an admin, clearing out something that shouldn't be there.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid ideaId, Guid requestedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == ideaId, ct);
        if (idea is null) return false;

        if (idea.UserId != requestedBy && !await IsAdminAsync(db, requestedBy, ct))
        {
            log.LogWarning("{User} tried to delete somebody else's idea {Idea}", requestedBy, ideaId);
            return false;
        }

        db.Ideas.Remove(idea);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// The live answer, not the one in a cookie. A cookie claim is reconciled on every request
    /// (see <c>OnValidatePrincipal</c>), but the rule in this codebase is that the page decides
    /// what to draw and the service decides what to do — so the service asks.
    /// </summary>
    private static Task<bool> IsAdminAsync(MootifyDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin && !u.IsBanned && !u.ApprovalPending, ct);
}
