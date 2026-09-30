using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Playback;

public sealed record SharedQueueSnapshot(Guid Id, Guid HostId, Guid PlaylistId, string HostName,
    IReadOnlyList<QueueEntry> Entries, int CurrentIndex, bool IsPlaying);

/// <summary>Live web-player sessions. The host circuit owns playback; guests only contribute tracks.</summary>
public sealed class SharedQueueService(IDbContextFactory<MootifyDbContext> dbFactory)
{
    private sealed class Session(SharedQueueSnapshot snapshot, Func<IReadOnlyList<Guid>, bool, Task> add)
    {
        public SharedQueueSnapshot Snapshot = snapshot;
        public readonly Func<IReadOnlyList<Guid>, bool, Task> Add = add;
        public readonly SemaphoreSlim Gate = new(1);
        public DateTimeOffset Updated = DateTimeOffset.UtcNow;
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private static bool IsLive(Session s) => DateTimeOffset.UtcNow - s.Updated < TimeSpan.FromSeconds(30);

    private async Task<bool> CanAccessAsync(SharedQueueSnapshot s, Guid viewer)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Playlists.AnyAsync(p => p.Id == s.PlaylistId && p.TeamId != null
            && db.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == viewer)
            && db.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == s.HostId)
            && db.Preferences.Any(pref => pref.UserId == s.HostId && pref.ShareListening));
    }

    public async Task<Guid?> StartAsync(Guid host, Guid playlist, IReadOnlyList<QueueEntry> entries,
        int currentIndex, bool playing, Func<IReadOnlyList<Guid>, bool, Task> add)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var name = await db.Users.Where(u => u.Id == host).Select(u => u.DisplayName).FirstAsync();
        var snapshot = new SharedQueueSnapshot(Guid.NewGuid(), host, playlist, name, entries, currentIndex, playing);
        if (!await CanAccessAsync(snapshot, host)) return null;
        lock (_lock)
        {
            foreach (var id in _sessions.Where(p => !IsLive(p.Value) || p.Value.Snapshot.HostId == host).Select(p => p.Key).ToList())
                _sessions.Remove(id);
            _sessions.Add(snapshot.Id, new Session(snapshot, add));
        }
        return snapshot.Id;
    }

    public void Publish(Guid id, IReadOnlyList<QueueEntry> entries, int currentIndex, bool playing)
    {
        lock (_lock)
            if (_sessions.TryGetValue(id, out var session))
            {
                session.Snapshot = session.Snapshot with { Entries = entries, CurrentIndex = currentIndex, IsPlaying = playing };
                session.Updated = DateTimeOffset.UtcNow;
            }
    }

    public void Stop(Guid id) { lock (_lock) _sessions.Remove(id); }

    public async Task<SharedQueueSnapshot?> GetAsync(Guid id, Guid viewer)
    {
        Session? session;
        lock (_lock) session = _sessions.GetValueOrDefault(id);
        if (session is null || !await CanAccessAsync(session.Snapshot, viewer)) return null;
        lock (_lock)
            return _sessions.GetValueOrDefault(id) == session && IsLive(session) ? session.Snapshot : null;
    }

    public async Task<SharedQueueSnapshot?> FindAsync(Guid host, Guid playlist, Guid viewer)
    {
        Guid? id;
        lock (_lock) id = _sessions.Values.FirstOrDefault(s => s.Snapshot.HostId == host && s.Snapshot.PlaylistId == playlist)?.Snapshot.Id;
        return id is { } value ? await GetAsync(value, viewer) : null;
    }

    public async Task<bool> AddAsync(Guid id, Guid viewer, IReadOnlyList<Guid> tracks, bool next)
    {
        Session? session;
        lock (_lock) session = _sessions.GetValueOrDefault(id);
        if (session is null || tracks.Count == 0) return false;
        await session.Gate.WaitAsync();
        try
        {
            if (await GetAsync(id, viewer) is null) return false;
            await session.Add(tracks, next);
            return true;
        }
        finally { session.Gate.Release(); }
    }
}
