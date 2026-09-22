using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Import;

namespace Mootify.Services.Library;

/// <summary>What an organize pass is allowed to touch. Both default on; both are one checkbox.</summary>
public sealed record OrganizeSettings(bool TidyNames = true, bool RemoveDuplicates = true);

/// <summary>One line of "here is what would happen", for the admin to read before agreeing to it.</summary>
public sealed record OrganizeSample(string Kind, string From, string To, string Detail);

/// <summary>
/// The dry run. Every number here is something the pass would change, and <see cref="Samples"/>
/// is a capped, readable slice of the same thing — an admin agreeing to "merge 340 artists"
/// without seeing any of the 340 is agreeing to nothing in particular.
/// </summary>
public sealed record OrganizePlan(
    int ArtistsRenamed,
    int ArtistsMerged,
    int AlbumsRenamed,
    int AlbumsMerged,
    int TrackTitlesFixed,
    int DuplicateGroups,
    int DuplicateTracks,
    long DuplicateBytes,
    int EmptyAlbums,
    int EmptyArtists,
    int SampleTotal,
    IReadOnlyList<OrganizeSample> Samples)
{
    public bool HasWork =>
        ArtistsRenamed + ArtistsMerged + AlbumsRenamed + AlbumsMerged + TrackTitlesFixed
        + DuplicateTracks + EmptyAlbums + EmptyArtists > 0;
}

/// <summary>What a pass actually did. Counted as it went, not predicted.</summary>
public sealed record OrganizeReport(
    int ArtistsRenamed,
    int ArtistsMerged,
    int AlbumsRenamed,
    int AlbumsMerged,
    int TrackTitlesFixed,
    int DuplicatesMerged,
    int PlaylistEntriesRepointed,
    int PlaylistEntriesRemoved,
    int FilesQuarantined,
    int FilesFailed,
    long BytesQuarantined,
    int EmptyAlbumsRemoved,
    int EmptyArtistsRemoved,
    TimeSpan Elapsed);

public sealed record OrganizeProgress(
    bool IsRunning,
    string Phase,
    int Done,
    int Total,
    DateTimeOffset? FinishedAt,
    OrganizeReport? Last,
    string? Error)
{
    public static readonly OrganizeProgress Idle = new(false, "", 0, 0, null, null, null);

    public int Percent => Total == 0 ? 0 : (int)(100.0 * Done / Total);
}

/// <summary>
/// Repairs names the scanner has already stored, and collapses the same song stored twice.
///
/// <b>Why this exists.</b> The artist on a <see cref="Track"/> comes from the file's tag and from
/// nowhere else, so a library full of "09. Elton John" and "12. Shocking Blue" is a library full
/// of files whose <i>tags</i> say that — most often a compilation ripped with the track's position
/// written into the artist field. Every one of those is a separate <see cref="Artist"/> row with
/// one album and one song under it, which is what turns a browsable library into a wall of
/// near-identical names. The same wall gets built a second way, by "The Black Eyed Peas" and
/// "Black Eyed Peas" being two rows a long way apart in the list.
///
/// <see cref="LibraryNaming"/> owns the rule for reading those names; this owns applying it to
/// what is already in the database, and the scanner applies the same rule to everything that
/// arrives afterwards, so the repair holds instead of being undone by the next pass.
///
/// <b>Three things are deliberate.</b>
///
/// A pass is <i>previewed</i> before it is run (<see cref="PreviewAsync"/>), because merging
/// artists is not undoable and the interesting failure — two bands that were never the same band
/// — is only ever visible as a pair of names an admin can read.
///
/// A duplicate <i>file</i> is moved into <c>&lt;MusicRoot&gt;/duplicates</c>, never deleted, and
/// the move is what makes the merge stick: delete only the row and the next scan finds the file
/// still sitting there and indexes it straight back in as a brand-new duplicate. If the move
/// fails the row is kept, so the database still describes what is on the disk.
///
/// Everything that pointed at a merged-away track is repointed rather than dropped — playlist
/// entries, play history, whatever somebody is in the middle of listening to. The whole operation
/// is worthless if it tidies the library by emptying a playlist.
/// </summary>
public sealed class LibraryOrganizer(
    IDbContextFactory<MootifyDbContext> dbFactory,
    LibraryFiler filer,
    LibraryScanner scanner,
    IOptionsMonitor<LibraryOptions> options,
    ILogger<LibraryOrganizer> log)
{
    /// <summary>One pass at a time. Two of these racing would merge each other's survivors away.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Two recordings of one song are the same recording when they agree to within this. Live and
    /// studio versions share a title and an artist and are not the same file; they very rarely
    /// share a running time. The same number <see cref="PlaylistImportService"/> uses as its
    /// tiebreaker, for the same reason.
    /// </summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Enough of the plan to judge it by, not so much that the page can't be read.</summary>
    private const int MaxSamples = 300;

    public OrganizeProgress Progress { get; private set; } = OrganizeProgress.Idle;

    public event Action? Changed;

    /// <summary>
    /// Where a duplicate copy goes. The path lives on <see cref="LibraryFiler"/> next to the drop
    /// folder, because they are the same idea — a folder inside the music root that is not the
    /// library — and everything that walks the tree has to agree about both. A quarantine the
    /// scanner walks is not a quarantine.
    /// </summary>
    public string? Quarantine => filer.DuplicatesFolder;

    public void Cancel() => _cts?.Cancel();

    // ---- preview ---------------------------------------------------------

    public async Task<OrganizePlan> PreviewAsync(OrganizeSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return Summarise(Plan(await ReadAsync(db, ct), settings));
    }

    // ---- running ---------------------------------------------------------

    /// <summary>
    /// Runs the pass in the background and returns immediately: the caller is a Blazor circuit
    /// and this walks the whole library. False means one is already running.
    /// </summary>
    public bool Start(OrganizeSettings settings, Guid actingUserId)
    {
        if (!_gate.Wait(0)) return false;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Flip the flag here rather than on the background thread, so the page that just pressed
        // the button redraws as running instead of briefly offering it again.
        Update(new OrganizeProgress(true, "Reading the library", 0, 0, null, Progress.Last, null));

        _ = Task.Run(async () =>
        {
            try
            {
                var report = await RunAsync(settings, actingUserId, token);
                Update(Progress with { Last = report, Error = null });
            }
            catch (OperationCanceledException)
            {
                log.LogInformation("Organize cancelled");
                Update(Progress with { Error = "Stopped part-way. Whatever had already been done is done." });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Organize failed");
                Update(Progress with { Error = ex.Message });
            }
            finally
            {
                Finish();
                _gate.Release();
            }
        }, CancellationToken.None);

        return true;
    }

    /// <summary>
    /// The same pass, awaited rather than watched. Null means one was already running. Failures
    /// come back as exceptions here — <see cref="Start"/> turns them into
    /// <see cref="OrganizeProgress.Error"/> because nobody is holding its task.
    /// </summary>
    public async Task<OrganizeReport?> OrganizeAsync(
        OrganizeSettings settings, Guid actingUserId, CancellationToken ct = default)
    {
        if (!_gate.Wait(0)) return null;

        try
        {
            var report = await RunAsync(settings, actingUserId, ct);
            Update(Progress with { Last = report, Error = null });
            return report;
        }
        finally
        {
            Finish();
            _gate.Release();
        }
    }

    private void Finish() =>
        Update(Progress with { IsRunning = false, Phase = "", FinishedAt = DateTimeOffset.UtcNow });

    private async Task<OrganizeReport> RunAsync(
        OrganizeSettings settings, Guid actingUserId, CancellationToken ct)
    {
        log.LogWarning("Admin {Admin} started an organize pass ({Settings})", actingUserId, settings);

        var startedAt = DateTimeOffset.UtcNow;
        Update(new OrganizeProgress(true, "Reading the library", 0, 0, null, Progress.Last, null));

        // Nothing may scan while this runs. The pass deletes the rows a scan upserts into, and a
        // scan caught mid-merge saves tracks pointing at an artist that stopped existing halfway.
        using var suspended = scanner.Suspend();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var work = Plan(await ReadAsync(db, ct), settings);

        var albumsMerged = await MergeAlbumsAsync(db, work.Albums, ct);
        var artistsMerged = await MergeArtistsAsync(db, work.Artists, ct);
        var artistsRenamed = await RenameArtistsAsync(db, work.Artists, ct);
        var albumsRenamed = await RenameAlbumsAsync(db, work.Albums, ct);
        var titlesFixed = await ApplyTitlesAsync(db, work.Titles, ct);
        var duplicates = await ApplyDuplicatesAsync(db, work.Duplicates, ct);
        var (emptyAlbums, emptyArtists) = await RemoveEmptyAsync(db, ct);

        var report = new OrganizeReport(
            artistsRenamed,
            artistsMerged,
            albumsRenamed,
            albumsMerged,
            titlesFixed,
            duplicates.Merged,
            duplicates.Repointed,
            duplicates.RemovedEntries,
            duplicates.Quarantined,
            duplicates.Failed,
            duplicates.Bytes,
            emptyAlbums,
            emptyArtists,
            DateTimeOffset.UtcNow - startedAt);

        log.LogInformation(
            "Organize finished in {Elapsed:0.0}s: {Renamed} artist(s) renamed, {Merged} merged, "
            + "{Titles} title(s) fixed, {Dupes} duplicate(s) merged, {Files} file(s) quarantined",
            report.Elapsed.TotalSeconds, artistsRenamed, artistsMerged, titlesFixed,
            duplicates.Merged, duplicates.Quarantined);

        return report;
    }

    // ---- the plan --------------------------------------------------------

    private sealed record ArtistRow(Guid Id, string Name, string? MusicBrainzId);

    private sealed record AlbumRow(Guid Id, Guid ArtistId, string Title);

    private sealed record TrackRow(
        Guid Id, Guid ArtistId, Guid AlbumId, string Title, long DurationTicks,
        int Bitrate, long FileSize, string Path, bool IsPresent, DateTimeOffset AddedAt);

    private sealed record Snapshot(List<ArtistRow> Artists, List<AlbumRow> Albums, List<TrackRow> Tracks);

    private sealed record ArtistOp(Guid SurvivorId, string Name, string OldName, List<Guid> LoserIds);

    private sealed record AlbumOp(Guid SurvivorId, string Title, string OldTitle, List<Guid> LoserIds);

    private sealed record TitleOp(Guid TrackId, string OldTitle, string NewTitle);

    private sealed record DupLoser(Guid Id, string Path, long Bytes, bool IsPresent);

    private sealed record DupOp(Guid SurvivorId, string Label, string SurvivorPath, List<DupLoser> Losers);

    private sealed record Work(
        List<ArtistOp> Artists,
        List<AlbumOp> Albums,
        List<TitleOp> Titles,
        List<DupOp> Duplicates,
        int EmptyAlbums,
        int EmptyArtists);

    private static async Task<Snapshot> ReadAsync(MootifyDbContext db, CancellationToken ct) =>
        new(
            await db.Artists.AsNoTracking()
                .Select(a => new ArtistRow(a.Id, a.Name, a.MusicBrainzId)).ToListAsync(ct),
            await db.Albums.AsNoTracking()
                .Select(a => new AlbumRow(a.Id, a.ArtistId, a.Title)).ToListAsync(ct),
            // Projected rather than materialised as entities: this is every track in the library,
            // and the plan wants ten columns of each, not the graph hanging off it.
            await db.Tracks.AsNoTracking()
                .Select(t => new TrackRow(
                    t.Id, t.ArtistId, t.AlbumId, t.Title, t.DurationTicks,
                    t.Bitrate, t.FileSize, t.Path, t.IsPresent, t.AddedAt)).ToListAsync(ct));

    /// <summary>
    /// Works out every change from a read-only snapshot, so the preview and the pass itself are
    /// one decision made twice rather than two implementations free to drift apart. Nothing here
    /// writes, and nothing here touches the disk.
    /// </summary>
    private static Work Plan(Snapshot snapshot, OrganizeSettings settings)
    {
        var tracksPerArtist = snapshot.Tracks
            .GroupBy(t => t.ArtistId)
            .ToDictionary(g => g.Key, g => g.Count());

        var tracksPerAlbum = snapshot.Tracks
            .GroupBy(t => t.AlbumId)
            .ToDictionary(g => g.Key, g => g.Count());

        var artistOps = new List<ArtistOp>();
        var artistMap = new Dictionary<Guid, Guid>();

        if (settings.TidyNames)
        {
            foreach (var group in snapshot.Artists.GroupBy(a => LibraryNaming.ArtistKey(a.Name)))
            {
                // Whichever spelling the library itself mostly uses wins — the majority is the
                // only evidence available about which one is the artist's real name. The name is
                // the tiebreaker rather than the id, so the pass an admin agreed to is the pass
                // they were shown.
                var survivor = group
                    .OrderByDescending(a => tracksPerArtist.GetValueOrDefault(a.Id))
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .First();

                var name = Clean(survivor.Name);
                var losers = group.Where(a => a.Id != survivor.Id).Select(a => a.Id).ToList();

                if (losers.Count == 0 && name == survivor.Name) continue;

                artistOps.Add(new ArtistOp(survivor.Id, name, survivor.Name, losers));
                foreach (var loser in losers) artistMap[loser] = survivor.Id;
            }
        }

        Guid ResolveArtist(Guid id) => artistMap.GetValueOrDefault(id, id);

        // Albums are folded far more conservatively than artists: the track-number prefix and
        // case, nothing else. An album title isn't repeated across a library the way an artist
        // name is, so there is no bloat here to justify the risk of merging "Live" into "Live at
        // Leeds". This pass still has to run whenever artists merged, because two artists that
        // became one can each own a "Greatest Hits" and (ArtistId, Title) is unique.
        var albumOps = new List<AlbumOp>();
        var albumMap = new Dictionary<Guid, Guid>();

        foreach (var group in snapshot.Albums.GroupBy(a => (
                     Artist: ResolveArtist(a.ArtistId),
                     Title: (settings.TidyNames ? Clean(a.Title) : a.Title).ToLowerInvariant())))
        {
            var survivor = group
                .OrderByDescending(a => tracksPerAlbum.GetValueOrDefault(a.Id))
                .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                .First();

            var title = settings.TidyNames ? Clean(survivor.Title) : survivor.Title;
            var losers = group.Where(a => a.Id != survivor.Id).Select(a => a.Id).ToList();

            if (losers.Count == 0 && title == survivor.Title) continue;

            albumOps.Add(new AlbumOp(survivor.Id, title, survivor.Title, losers));
            foreach (var loser in losers) albumMap[loser] = survivor.Id;
        }

        Guid ResolveAlbum(Guid id) => albumMap.GetValueOrDefault(id, id);

        var titleOps = new List<TitleOp>();

        if (settings.TidyNames)
        {
            foreach (var track in snapshot.Tracks)
            {
                var title = Clean(track.Title);
                if (title != track.Title) titleOps.Add(new TitleOp(track.Id, track.Title, title));
            }
        }

        var artistNames = snapshot.Artists.ToDictionary(a => a.Id, a => a.Name);
        var dupOps = new List<DupOp>();
        var merged = new HashSet<Guid>();

        if (settings.RemoveDuplicates)
        {
            // Normalize is the import matcher's rule for "same song", reused rather than
            // reinvented — two opinions about that is how half a library ends up hidden.
            var groups = snapshot.Tracks.GroupBy(t => (
                Artist: ResolveArtist(t.ArtistId),
                Title: PlaylistImportService.Normalize(Clean(t.Title))));

            foreach (var group in groups)
            {
                if (group.Key.Title.Length == 0) continue;

                foreach (var cluster in ClusterByDuration(group))
                {
                    if (cluster.Count < 2) continue;

                    // Present beats absent, then the better copy: bitrate carries FLAC above MP3
                    // without this needing to know about formats, and size settles two files of
                    // the same nominal bitrate. AddedAt and the id last, so the answer is the
                    // same every run.
                    var survivor = cluster
                        .OrderByDescending(t => t.IsPresent)
                        .ThenByDescending(t => t.Bitrate)
                        .ThenByDescending(t => t.FileSize)
                        .ThenBy(t => t.AddedAt)
                        .ThenBy(t => t.Id)
                        .First();

                    var losers = cluster
                        .Where(t => t.Id != survivor.Id)
                        .Select(t => new DupLoser(t.Id, t.Path, t.FileSize, t.IsPresent))
                        .ToList();

                    var label = $"{artistNames.GetValueOrDefault(group.Key.Artist, "?")} — {Clean(survivor.Title)}";
                    dupOps.Add(new DupOp(survivor.Id, label, survivor.Path, losers));
                    foreach (var loser in losers) merged.Add(loser.Id);
                }
            }
        }

        // What is left with nothing in it once the merges have happened — including rows that
        // were already debris before this pass started. Absent tracks count as occupants: the
        // row is still there and playlists can still point at it.
        var surviving = snapshot.Tracks.Where(t => !merged.Contains(t.Id)).ToList();
        var liveAlbums = surviving.Select(t => ResolveAlbum(t.AlbumId)).ToHashSet();
        var liveArtists = surviving.Select(t => ResolveArtist(t.ArtistId)).ToHashSet();

        var emptyAlbums = snapshot.Albums.Count(a => !albumMap.ContainsKey(a.Id) && !liveAlbums.Contains(a.Id));
        var emptyArtists = snapshot.Artists.Count(a => !artistMap.ContainsKey(a.Id) && !liveArtists.Contains(a.Id));

        return new Work(artistOps, albumOps, titleOps, dupOps, emptyAlbums, emptyArtists);
    }

    /// <summary>
    /// Never returns nothing. A name that is only a track number ("09.") has no artist in it to
    /// recover, and an empty name is worse than a silly one.
    /// </summary>
    private static string Clean(string name)
    {
        var cleaned = LibraryNaming.StripIndexPrefix(name);
        return cleaned.Length > 0 ? cleaned : name;
    }

    /// <summary>
    /// Splits same-title-same-artist tracks into runs that agree on running time. Greedy over the
    /// sorted list and anchored on the first member of each run, so a chain of tracks each four
    /// seconds longer than the last can't walk a three-minute song into a six-minute one.
    /// </summary>
    private static List<List<TrackRow>> ClusterByDuration(IEnumerable<TrackRow> tracks)
    {
        var clusters = new List<List<TrackRow>>();
        var tolerance = DurationTolerance.Ticks;

        foreach (var track in tracks.OrderBy(t => t.DurationTicks))
        {
            var current = clusters.Count > 0 ? clusters[^1] : null;

            if (current is not null && track.DurationTicks - current[0].DurationTicks <= tolerance)
            {
                current.Add(track);
            }
            else
            {
                clusters.Add([track]);
            }
        }

        return clusters;
    }

    private static OrganizePlan Summarise(Work work)
    {
        var samples = new List<OrganizeSample>();
        var total = 0;

        void Sample(string kind, string from, string to, string detail)
        {
            total++;
            if (samples.Count < MaxSamples) samples.Add(new OrganizeSample(kind, from, to, detail));
        }

        // Merges first and biggest first: those are the ones worth reading before agreeing.
        foreach (var op in work.Artists.OrderByDescending(a => a.LoserIds.Count).ThenBy(a => a.Name))
        {
            Sample(
                op.LoserIds.Count > 0 ? "Merge artist" : "Rename artist",
                op.OldName,
                op.Name,
                op.LoserIds.Count > 0 ? $"{op.LoserIds.Count + 1} rows become one" : "");
        }

        foreach (var op in work.Albums.OrderByDescending(a => a.LoserIds.Count).ThenBy(a => a.Title))
        {
            Sample(
                op.LoserIds.Count > 0 ? "Merge album" : "Rename album",
                op.OldTitle,
                op.Title,
                op.LoserIds.Count > 0 ? $"{op.LoserIds.Count + 1} rows become one" : "");
        }

        foreach (var op in work.Duplicates)
        {
            Sample("Duplicate", op.Label, Path.GetFileName(op.SurvivorPath), $"{op.Losers.Count} copy(s) moved aside");
        }

        foreach (var op in work.Titles)
        {
            Sample("Rename song", op.OldTitle, op.NewTitle, "");
        }

        return new OrganizePlan(
            work.Artists.Count(a => a.LoserIds.Count == 0),
            work.Artists.Sum(a => a.LoserIds.Count),
            work.Albums.Count(a => a.LoserIds.Count == 0),
            work.Albums.Sum(a => a.LoserIds.Count),
            work.Titles.Count,
            work.Duplicates.Count,
            work.Duplicates.Sum(d => d.Losers.Count),
            work.Duplicates.Sum(d => d.Losers.Sum(l => l.Bytes)),
            work.EmptyAlbums,
            work.EmptyArtists,
            total,
            samples);
    }

    // ---- applying --------------------------------------------------------

    // Merge before rename, and albums before artists. The order is one long argument with two
    // unique indexes and two cascading foreign keys, and every step of it was a crash first.
    //
    // (Albums.ArtistId, Title) is unique, so moving Cowbells' "Greatest Hits" onto The Cowbells
    // while The Cowbells still has its own "Greatest Hits" fails — collapsing the albums first is
    // what leaves at most one album per title under each artist, which is what makes the move
    // safe. Artists.Name is unique for the reason renaming comes last: "09. Elton John" cannot
    // become "Elton John" while the real Elton John is still a row. And nothing is deleted before
    // what hangs off it has moved, because both foreign keys cascade — a loser deleted early
    // takes its albums and tracks with it.

    private async Task<int> MergeAlbumsAsync(MootifyDbContext db, List<AlbumOp> ops, CancellationToken ct)
    {
        var merging = ops.Where(o => o.LoserIds.Count > 0).ToList();
        if (merging.Count == 0) return 0;

        Update(Progress with { Phase = "Merging albums", Done = 0, Total = merging.Count });

        var merged = 0;
        var done = 0;

        foreach (var chunk in merging.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();

            var owner = chunk
                .SelectMany(o => o.LoserIds.Select(l => (Loser: l, o.SurvivorId)))
                .ToDictionary(x => x.Loser, x => x.SurvivorId);

            var loserIds = owner.Keys.ToList();
            var survivorIds = chunk.Select(o => o.SurvivorId).ToList();

            foreach (var track in await db.Tracks.Where(t => loserIds.Contains(t.AlbumId)).ToListAsync(ct))
            {
                track.AlbumId = owner[track.AlbumId];
            }

            await db.SaveChangesAsync(ct);

            var losers = await db.Albums.Where(a => loserIds.Contains(a.Id)).ToListAsync(ct);
            var survivors = await db.Albums.Where(a => survivorIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

            foreach (var loser in losers)
            {
                if (!survivors.TryGetValue(owner[loser.Id], out var survivor)) continue;

                // A year or a cover found on the copy being merged away belongs to the album, not
                // to the row that happened to be holding it.
                survivor.Year ??= loser.Year;
                survivor.MusicBrainzId ??= loser.MusicBrainzId;
                survivor.CoverPath ??= loser.CoverPath;
            }

            db.Albums.RemoveRange(losers);
            await db.SaveChangesAsync(ct);

            merged += losers.Count;
            done += chunk.Length;
            Update(Progress with { Done = done });
        }

        return merged;
    }

    private async Task<int> MergeArtistsAsync(MootifyDbContext db, List<ArtistOp> ops, CancellationToken ct)
    {
        var merging = ops.Where(o => o.LoserIds.Count > 0).ToList();
        if (merging.Count == 0) return 0;

        Update(Progress with { Phase = "Merging artists", Done = 0, Total = merging.Count });

        var merged = 0;
        var done = 0;

        foreach (var chunk in merging.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();

            var owner = chunk
                .SelectMany(o => o.LoserIds.Select(l => (Loser: l, o.SurvivorId)))
                .ToDictionary(x => x.Loser, x => x.SurvivorId);

            var loserIds = owner.Keys.ToList();
            var survivorIds = chunk.Select(o => o.SurvivorId).ToList();

            foreach (var track in await db.Tracks.Where(t => loserIds.Contains(t.ArtistId)).ToListAsync(ct))
            {
                track.ArtistId = owner[track.ArtistId];
            }

            // Safe now and not a moment earlier: the albums that would have collided under the
            // new owner were merged away in the pass above.
            foreach (var album in await db.Albums.Where(a => loserIds.Contains(a.ArtistId)).ToListAsync(ct))
            {
                album.ArtistId = owner[album.ArtistId];
            }

            await db.SaveChangesAsync(ct);

            var losers = await db.Artists.Where(a => loserIds.Contains(a.Id)).ToListAsync(ct);
            var survivors = await db.Artists.Where(a => survivorIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

            foreach (var loser in losers)
            {
                // The merged-away row may be the only one that ever carried a MusicBrainz id,
                // which is the strongest metadata join key — preserve it while reorganising.
                if (loser.MusicBrainzId is { } mbid
                    && survivors.TryGetValue(owner[loser.Id], out var survivor)
                    && survivor.MusicBrainzId is null)
                {
                    survivor.MusicBrainzId = mbid;
                }
            }

            db.Artists.RemoveRange(losers);
            await db.SaveChangesAsync(ct);

            merged += losers.Count;
            done += chunk.Length;
            Update(Progress with { Done = done });
        }

        return merged;
    }

    /// <summary>
    /// Last, once every row holding the wanted name has gone. <see cref="Artist.SortName"/> is
    /// rewritten even when the name itself didn't change: an artist that has just absorbed
    /// "The Black Eyed Peas" keeps its own name and still has to start sorting under B.
    /// </summary>
    private async Task<int> RenameArtistsAsync(MootifyDbContext db, List<ArtistOp> ops, CancellationToken ct)
    {
        if (ops.Count == 0) return 0;

        Update(Progress with { Phase = "Tidying artist names", Done = 0, Total = ops.Count });

        var renamed = 0;
        var done = 0;

        foreach (var chunk in ops.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();

            var wanted = chunk.ToDictionary(o => o.SurvivorId);
            var ids = wanted.Keys.ToList();

            foreach (var artist in await db.Artists.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
            {
                var op = wanted[artist.Id];

                if (artist.Name != op.Name)
                {
                    artist.Name = op.Name;

                    // A merge is counted as a merge, not also as a rename.
                    if (op.LoserIds.Count == 0) renamed++;
                }

                artist.SortName = LibraryNaming.SortName(artist.Name);
            }

            await db.SaveChangesAsync(ct);

            done += chunk.Length;
            Update(Progress with { Done = done });
        }

        return renamed;
    }

    private async Task<int> RenameAlbumsAsync(MootifyDbContext db, List<AlbumOp> ops, CancellationToken ct)
    {
        if (ops.Count == 0) return 0;

        Update(Progress with { Phase = "Tidying album titles", Done = 0, Total = ops.Count });

        var renamed = 0;
        var done = 0;

        foreach (var chunk in ops.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();

            var wanted = chunk.ToDictionary(o => o.SurvivorId);
            var ids = wanted.Keys.ToList();

            foreach (var album in await db.Albums.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
            {
                var op = wanted[album.Id];
                if (album.Title == op.Title) continue;

                album.Title = op.Title;
                if (op.LoserIds.Count == 0) renamed++;
            }

            await db.SaveChangesAsync(ct);

            done += chunk.Length;
            Update(Progress with { Done = done });
        }

        return renamed;
    }

    private async Task<int> ApplyTitlesAsync(MootifyDbContext db, List<TitleOp> ops, CancellationToken ct)
    {
        if (ops.Count == 0) return 0;

        Update(Progress with { Phase = "Tidying song titles", Done = 0, Total = ops.Count });

        var fixedUp = 0;
        var done = 0;

        foreach (var chunk in ops.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();

            var wanted = chunk.ToDictionary(o => o.TrackId, o => o.NewTitle);
            var ids = wanted.Keys.ToList();

            foreach (var track in await db.Tracks.Where(t => ids.Contains(t.Id)).ToListAsync(ct))
            {
                track.Title = wanted[track.Id];
                fixedUp++;
            }

            await db.SaveChangesAsync(ct);

            done += chunk.Length;
            Update(Progress with { Done = done });
        }

        return fixedUp;
    }

    private sealed record DuplicateOutcome(
        int Merged, int Repointed, int RemovedEntries, int Quarantined, int Failed, long Bytes);

    private async Task<DuplicateOutcome> ApplyDuplicatesAsync(
        MootifyDbContext db, List<DupOp> ops, CancellationToken ct)
    {
        if (ops.Count == 0) return new DuplicateOutcome(0, 0, 0, 0, 0, 0);

        var map = new Dictionary<Guid, Guid>();
        foreach (var op in ops)
        {
            foreach (var loser in op.Losers) map[loser.Id] = op.SurvivorId;
        }

        // Everything that points at a copy about to go has to point at the one that stays first.
        // A cascade delete would take playlist entries and broadcasts with it.
        Update(Progress with { Phase = "Pointing playlists at the copy that stays", Done = 0, Total = map.Count });

        var (repointed, removedEntries) = await RepointPlaylistsAsync(db, map, ct);
        await RepointHistoryAsync(db, map, ct);

        // Only now the files: one has to leave the library before its row does, or the next scan
        // finds it sitting where it always was and indexes it back in as a fresh duplicate.
        Update(Progress with { Phase = "Moving duplicate files aside", Done = 0, Total = map.Count });

        var quarantined = 0;
        var failed = 0;
        var bytes = 0L;
        var deletable = new List<Guid>();
        var done = 0;

        foreach (var op in ops)
        {
            foreach (var loser in op.Losers)
            {
                ct.ThrowIfCancellationRequested();

                if (!loser.IsPresent || !File.Exists(loser.Path))
                {
                    // Nothing on disk to move: the row already describes a file that is gone.
                    deletable.Add(loser.Id);
                }
                else if (MoveAside(loser.Path))
                {
                    deletable.Add(loser.Id);
                    quarantined++;
                    bytes += loser.Bytes;
                }
                else
                {
                    // Keep the row. Deleting it while the file stays put leaves the database
                    // describing a library that isn't there, and the next scan would add the file
                    // back anyway — as a stranger with a new id and no history.
                    failed++;
                }

                Update(Progress with { Done = ++done });
            }
        }

        var merged = 0;

        foreach (var chunk in deletable.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var ids = chunk.ToList();
            merged += await db.Tracks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct);
        }

        return new DuplicateOutcome(merged, repointed, removedEntries, quarantined, failed, bytes);
    }

    /// <summary>
    /// Moves every playlist entry onto the surviving track, and drops the one that would
    /// otherwise become the same song listed twice. Adding a song twice by hand is deliberate and
    /// left alone everywhere else; these two entries were different tracks until a moment ago,
    /// and leaving both is the tidy-up failing to tidy.
    /// </summary>
    private static async Task<(int Repointed, int Removed)> RepointPlaylistsAsync(
        MootifyDbContext db, Dictionary<Guid, Guid> map, CancellationToken ct)
    {
        var repointed = 0;
        var removed = 0;

        foreach (var chunk in map.Keys.Chunk(400))
        {
            ct.ThrowIfCancellationRequested();

            var loserIds = chunk.ToList();
            var items = await db.PlaylistItems.Where(i => loserIds.Contains(i.TrackId)).ToListAsync(ct);
            if (items.Count == 0) continue;

            var playlistIds = items.Select(i => i.PlaylistId).Distinct().ToList();

            var occupied = await db.PlaylistItems
                .Where(i => playlistIds.Contains(i.PlaylistId) && !loserIds.Contains(i.TrackId))
                .Select(i => new { i.PlaylistId, i.TrackId, i.RequestId })
                .ToListAsync(ct);

            var taken = occupied.Select(i => (i.PlaylistId, i.TrackId, i.RequestId)).ToHashSet();

            // Earliest position wins, so the song keeps the place it was first put in.
            foreach (var item in items.OrderBy(i => i.SortKey))
            {
                var survivor = map[item.TrackId];

                if (!taken.Add((item.PlaylistId, survivor, item.RequestId)))
                {
                    db.PlaylistItems.Remove(item);
                    removed++;
                    continue;
                }

                item.TrackId = survivor;
                repointed++;
            }

            await db.SaveChangesAsync(ct);
        }

        return (repointed, removed);
    }

    /// <summary>
    /// Play history, whoever is broadcasting, and whatever anybody has queued up. A merge that
    /// drops these quietly rewrites what people have listened to and stops the car resuming what
    /// was playing in the kitchen.
    /// </summary>
    private static async Task RepointHistoryAsync(
        MootifyDbContext db, Dictionary<Guid, Guid> map, CancellationToken ct)
    {
        foreach (var chunk in map.Keys.Chunk(400))
        {
            ct.ThrowIfCancellationRequested();

            var loserIds = chunk.ToList();

            foreach (var play in await db.PlayEvents.Where(p => loserIds.Contains(p.TrackId)).ToListAsync(ct))
            {
                play.TrackId = map[play.TrackId];
            }

            foreach (var session in await db.ListeningSessions.Where(s => loserIds.Contains(s.TrackId)).ToListAsync(ct))
            {
                session.TrackId = map[session.TrackId];
            }

            await db.SaveChangesAsync(ct);
        }

        // One row per person, so this is small enough to walk whole — and the queue is a JSON
        // column, which no amount of SQL is going to rewrite.
        var touched = false;

        foreach (var state in await db.PlaybackStates.ToListAsync(ct))
        {
            if (state.CurrentTrackId is { } current && map.TryGetValue(current, out var survivor))
            {
                state.CurrentTrackId = survivor;
                touched = true;
            }

            if (RemapQueue(state.QueueJson, map) is { } queue)
            {
                // Same length, so QueueIndex still means what it meant. A queue with one song
                // twice in it is a far smaller problem than a queue whose position has shifted.
                state.QueueJson = queue;
                touched = true;
            }
        }

        if (touched) await db.SaveChangesAsync(ct);
    }

    private static string? RemapQueue(string json, Dictionary<Guid, Guid> map)
    {
        try
        {
            if (JsonSerializer.Deserialize<List<Guid>>(json) is not { Count: > 0 } queue) return null;
            if (!queue.Any(map.ContainsKey)) return null;

            return JsonSerializer.Serialize(queue.Select(id => map.GetValueOrDefault(id, id)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves a duplicate into the quarantine folder, keeping the path it had under the music root
    /// so somebody can tell which copy this was. Never overwrites — <see cref="LibraryFiler.Unique"/>
    /// is the same collision rule the filer uses, because two duplicates can honestly both be
    /// called <c>01 - Intro.mp3</c>.
    /// </summary>
    private bool MoveAside(string path)
    {
        var opts = options.CurrentValue;
        var quarantine = filer.DuplicatesFolder;

        if (quarantine is null)
        {
            log.LogWarning("Nowhere to move duplicates to: Library:DuplicatesFolder is blank");
            return false;
        }

        try
        {
            var relative = Path.GetRelativePath(opts.MusicRoot, path);

            // A track outside the music root has no shape worth preserving, and no "..\.." is
            // getting built into a destination path.
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                relative = Path.GetFileName(path);
            }

            var target = Path.Combine(quarantine, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (LibraryFiler.Unique(target) is not { } free) return false;

            File.Move(path, free);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not move duplicate {Path} aside", path);
            return false;
        }
    }

    /// <summary>
    /// Albums with nothing on them and artists with nothing by them. Recomputed against the
    /// database rather than taken from the plan, because by now the plan is several saves old —
    /// and because debris that was already there before this pass is worth clearing too. Albums
    /// first: an artist whose only album has just gone is only empty afterwards.
    /// </summary>
    private async Task<(int Albums, int Artists)> RemoveEmptyAsync(MootifyDbContext db, CancellationToken ct)
    {
        Update(Progress with { Phase = "Clearing out empty artists and albums", Done = 0, Total = 0 });

        var albums = await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
        var artists = await db.Artists.Where(a => !a.Tracks.Any() && !a.Albums.Any()).ExecuteDeleteAsync(ct);

        return (albums, artists);
    }

    private void Update(OrganizeProgress progress)
    {
        Progress = progress;
        Changed?.Invoke();
    }
}
