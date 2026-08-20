using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Recommendations;

/// <summary>
/// One thing this account demonstrably likes, and how much. <paramref name="Weight"/> is a
/// relative number with no unit — only the ordering and the ratios mean anything.
/// <paramref name="Id"/> is the artist it refers to, and null for a genre.
/// </summary>
public sealed record TasteWeight(string Name, double Weight, Guid? Id = null);

/// <summary>
/// What somebody's listening says about them.
///
/// <see cref="IsReady"/> is the important field. Everything downstream refuses to run without it,
/// because a recommender with no history doesn't produce weak suggestions — it produces confident
/// nonsense, and a "Surprise me" that opens with three tracks nobody has ever played is a feature
/// people press once.
/// </summary>
public sealed record TasteProfile(
    IReadOnlyList<TasteWeight> Artists,
    IReadOnlyList<TasteWeight> Genres,
    int TracksHeard,
    int PlaysCounted,
    DateTimeOffset? FirstPlayAt)
{
    public static readonly TasteProfile Empty = new([], [], 0, 0, null);

    /// <summary>
    /// Enough distinct songs to be worth extrapolating from. See
    /// <see cref="TasteService.MinimumTracksHeard"/>.
    /// </summary>
    public bool IsReady => TracksHeard >= TasteService.MinimumTracksHeard;

    /// <summary>How many more distinct songs before suggestions turn on. Zero once ready.</summary>
    public int TracksStillNeeded => Math.Max(0, TasteService.MinimumTracksHeard - TracksHeard);

    /// <summary>The names behind a suggestion, for saying <i>why</i> rather than just what.</summary>
    public IEnumerable<string> TopArtists(int take) => Artists.Take(take).Select(a => a.Name);

    public IEnumerable<string> TopGenres(int take) => Genres.Take(take).Select(g => g.Name);
}

/// <summary>A suggested track, and the reason it was suggested.</summary>
public sealed record Suggestion(
    Guid TrackId,
    string Title,
    string ArtistName,
    Guid AlbumId,
    string AlbumTitle,
    string? Genre,
    long DurationTicks,
    string Reason)
{
    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}

/// <summary>
/// Turns play history into a taste profile, and a taste profile into things to play next.
///
/// <b>The whole design is built around refusing to guess.</b> A household server has libraries
/// where most of the music has never been played by the person asking, so a suggester that always
/// answers will mostly answer with music they own and actively don't listen to — which is worse
/// than not having the feature, because it teaches people the button is bad. So:
/// <see cref="MinimumTracksHeard"/> distinct songs before it says anything at all, and
/// <see cref="SuggestAsync"/> returns an empty list rather than padding with randoms.
///
/// Deliberately simple and explainable — sums over play events, no model, no training. Every
/// suggestion can name the artist or genre it came from, which is both the honest thing to show
/// and the thing that makes a bad suggestion diagnosable.
///
/// Three weightings do the work, and each exists because of a specific way this goes wrong:
///
/// - <b>Completion.</b> A play is weighted by how much of the track was heard, so a song skipped
///   after ten seconds barely counts and one played to the end counts fully. Without it, skipping
///   through an album teaches the profile that you love it.
/// - <b>Recency.</b> Plays decay with a half-life (<see cref="HalfLife"/>), so what somebody is
///   into this month outranks what they wore out last year — without erasing it.
/// - <b>Variety.</b> Suggestions are sampled with weighted randomness and capped per artist
///   (<see cref="MaxPerArtist"/>). Strict top-N scoring returns one artist's discography, which is
///   technically a great recommendation and useless as a playlist.
/// </summary>
public sealed class TasteService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    ILogger<TasteService> log)
{
    /// <summary>
    /// Distinct songs somebody must have actually listened to before this will suggest anything.
    ///
    /// Low enough to reach in one sitting, high enough that it isn't extrapolating a whole library
    /// from one album. Below it every entry point says so in words and offers nothing.
    /// </summary>
    public const int MinimumTracksHeard = 15;

    /// <summary>
    /// A play counts once it is past this much of the track. Anything shorter is a skip, and a
    /// skip is evidence <i>against</i> rather than for.
    /// </summary>
    public const double MinimumCompletion = 0.15;

    /// <summary>How long it takes a play to count for half as much. Taste moves; it doesn't reset.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(45);

    /// <summary>
    /// Nothing older than this is read at all. A bound on the query as much as on the taste — a
    /// household server accumulates play events for years and none of them are about today.
    /// </summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(365);

    /// <summary>
    /// Songs by one artist in a single batch of suggestions. Without a cap, the best-scoring
    /// answer to "what should I play" is forty tracks by whoever you played most, which is a
    /// correct answer to a question nobody asked.
    /// </summary>
    public const int MaxPerArtist = 2;

    /// <summary>
    /// Recently played tracks are held back from suggestions — the point is something to listen
    /// to, not the song that just finished.
    /// </summary>
    public static readonly TimeSpan RepeatCooldown = TimeSpan.FromDays(14);

    /// <summary>How much of the score comes from the artist versus the genre.</summary>
    private const double ArtistWeight = 1.0;
    private const double GenreWeight = 0.6;

    /// <summary>
    /// A floor under every candidate's score, so a library's unheard corners stay reachable.
    /// Without it the pool is only ever artists already in the profile, and "Surprise me" can
    /// never surprise — it just replays your own taste back at you.
    /// </summary>
    private const double ExplorationFloor = 0.08;

    /// <summary>How much of the profile is used to build the candidate query.</summary>
    private const int ProfileArtists = 40;
    private const int ProfileGenres = 15;

    /// <summary>
    /// Ceiling on how many rows a suggestion pass pulls into memory. The scoring is per-row and
    /// in C#, so this is what stops "Surprise me" from materialising a 40,000-track library every
    /// time somebody presses it.
    /// </summary>
    private const int CandidateCap = 2_000;

    /// <summary>
    /// How many tracks outside the profile come along for the ride, taken from a random offset so
    /// the same corners of the library aren't offered every time. This is the only way somebody
    /// hears an artist they have never played.
    /// </summary>
    private const int ExplorationSample = 150;

    /// <summary>What a scoring pass needs about a track, and nothing else.</summary>
    private sealed record Candidate(
        Guid Id,
        string Title,
        Guid ArtistId,
        string ArtistName,
        Guid AlbumId,
        string AlbumTitle,
        string? Genre,
        long DurationTicks);

    // ---- the profile -----------------------------------------------------

    public async Task<TasteProfile> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var since = now - History;

        // Joined to the track so a play can be weighted by how much of it was heard, and
        // projected — this is a read over one user's history and wants none of the entities.
        var plays = await db.PlayEvents
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.PlayedAt >= since)
            .Join(
                db.Tracks.AsNoTracking(),
                p => p.TrackId,
                t => t.Id,
                (p, t) => new
                {
                    p.TrackId,
                    p.PlayedAt,
                    p.SecondsPlayed,
                    t.DurationTicks,
                    t.ArtistId,
                    ArtistName = t.Artist!.Name,
                    t.Genre,
                })
            .ToListAsync(ct);

        if (plays.Count == 0) return TasteProfile.Empty;

        var artists = new Dictionary<Guid, (string Name, double Weight)>();
        var genres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var heard = new HashSet<Guid>();
        var counted = 0;

        foreach (var play in plays)
        {
            var completion = Completion(play.SecondsPlayed, play.DurationTicks);
            if (completion < MinimumCompletion) continue;

            var weight = completion * Decay(now - play.PlayedAt);

            heard.Add(play.TrackId);
            counted++;

            var current = artists.GetValueOrDefault(play.ArtistId);
            artists[play.ArtistId] = (play.ArtistName, current.Weight + weight);

            if (NormaliseGenre(play.Genre) is { } genre)
            {
                genres[genre] = genres.GetValueOrDefault(genre) + weight;
            }
        }

        return new TasteProfile(
            [
                .. artists
                    .OrderByDescending(kv => kv.Value.Weight)
                    .ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new TasteWeight(kv.Value.Name, kv.Value.Weight, kv.Key))
            ],
            [
                .. genres
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new TasteWeight(kv.Key, kv.Value))
            ],
            heard.Count,
            counted,
            plays.Min(p => p.PlayedAt));
    }

    /// <summary>
    /// How much of the track was heard, as 0..1. A track with no known duration — which happens
    /// when tags are unreadable — counts as a full listen rather than being thrown away, because
    /// the alternative is silently ignoring somebody's untagged music.
    /// </summary>
    internal static double Completion(double secondsPlayed, long durationTicks)
    {
        if (durationTicks <= 0) return 1;

        var seconds = TimeSpan.FromTicks(durationTicks).TotalSeconds;
        return Math.Clamp(secondsPlayed / seconds, 0, 1);
    }

    /// <summary>Exponential decay on <see cref="HalfLife"/>. 1 now, 0.5 one half-life ago.</summary>
    internal static double Decay(TimeSpan age)
    {
        if (age <= TimeSpan.Zero) return 1;
        return Math.Pow(0.5, age.TotalDays / HalfLife.TotalDays);
    }

    /// <summary>
    /// Genre tags are free text and inconsistently cased — "Rock", "rock" and " Rock " are one
    /// genre. Numeric ID3v1 leftovers like "(17)" are dropped: they mean something, but not
    /// anything this can act on.
    /// </summary>
    internal static string? NormaliseGenre(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')')) return null;
        if (trimmed.All(char.IsDigit)) return null;

        return trimmed;
    }

    // ---- suggestions -----------------------------------------------------

    /// <summary>
    /// Things to play next. Empty when the profile isn't ready — see the class comment; that is
    /// the feature, not a gap in it.
    ///
    /// <paramref name="exclude"/> is whatever is already queued or already in the playlist being
    /// added to, so a batch doesn't suggest what the caller already has.
    /// <paramref name="seed"/> makes a batch reproducible for tests; null means genuinely random.
    /// </summary>
    public async Task<List<Suggestion>> SuggestAsync(
        Guid userId,
        int count,
        IReadOnlyCollection<Guid>? exclude = null,
        int? seed = null,
        CancellationToken ct = default)
    {
        if (count <= 0) return [];

        var profile = await GetProfileAsync(userId, ct);
        if (!profile.IsReady)
        {
            log.LogDebug(
                "No suggestions for {User}: {Heard} of {Needed} tracks heard",
                userId, profile.TracksHeard, MinimumTracksHeard);
            return [];
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var blocked = await BlockedAsync(db, userId, exclude, ct);
        var random = seed is { } s ? new Random(s) : new Random();

        var candidates = await CandidatesAsync(db, profile, random, ct);
        if (candidates.Count == 0) return [];

        var artistWeights = profile.Artists
            .Where(a => a.Id is not null)
            .ToDictionary(a => a.Id!.Value, a => a.Weight);

        var genreWeights = profile.Genres.ToDictionary(
            g => g.Name, g => g.Weight, StringComparer.OrdinalIgnoreCase);

        // Normalised against the strongest signal in the profile, so the artist and genre halves
        // are comparable rather than whichever happens to have more plays behind it.
        var topArtist = artistWeights.Count == 0 ? 1 : artistWeights.Values.Max();
        var topGenre = genreWeights.Count == 0 ? 1 : genreWeights.Values.Max();

        var scored = new List<(double Score, string Reason, Candidate Row)>(candidates.Count);

        foreach (var row in candidates)
        {
            if (blocked.Contains(row.Id)) continue;

            var artist = artistWeights.GetValueOrDefault(row.ArtistId) / topArtist;
            var genreName = NormaliseGenre(row.Genre);
            var genre = genreName is null ? 0 : genreWeights.GetValueOrDefault(genreName) / topGenre;

            scored.Add((
                (artist * ArtistWeight) + (genre * GenreWeight) + ExplorationFloor,
                Reason(artist, genre, row.ArtistName, genreName),
                row));
        }

        return scored.Count == 0 ? [] : Sample(scored, count, random);
    }

    /// <summary>
    /// What must not be suggested: what the caller already has, and anything heard inside
    /// <see cref="RepeatCooldown"/>. The cooldown is held out rather than scored down, because
    /// "play me something" never means the song that just finished, however well it matches.
    /// </summary>
    private static async Task<HashSet<Guid>> BlockedAsync(
        MootifyDbContext db, Guid userId, IReadOnlyCollection<Guid>? exclude, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - RepeatCooldown;

        var recent = await db.PlayEvents
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.PlayedAt >= since)
            .Select(p => p.TrackId)
            .Distinct()
            .ToListAsync(ct);

        var blocked = new HashSet<Guid>(recent);
        if (exclude is not null) blocked.UnionWith(exclude);

        return blocked;
    }

    /// <summary>
    /// The pool to score: everything by an artist or in a genre this account plays, plus a random
    /// slice of everything else.
    ///
    /// Filtered in SQL and capped, because the scoring is per-row in C# and the alternative is
    /// pulling a whole library into memory every time somebody presses a button. The random slice
    /// is what stops the pool being a closed loop of artists already in the profile — without it
    /// nobody ever hears something new, which is the one thing "Surprise me" is for.
    /// </summary>
    private static async Task<List<Candidate>> CandidatesAsync(
        MootifyDbContext db, TasteProfile profile, Random random, CancellationToken ct)
    {
        var artistIds = profile.Artists
            .Where(a => a.Id is not null)
            .Take(ProfileArtists)
            .Select(a => a.Id!.Value)
            .ToList();

        // Lowercased both sides: genre tags are free text and SQLite's IN is case-sensitive, so
        // "Rock" and "rock" would otherwise be two genres in the database and one in the profile.
        var genreNames = profile.Genres
            .Take(ProfileGenres)
            .Select(g => g.Name.ToLowerInvariant())
            .ToList();

        var present = db.Tracks.AsNoTracking().Where(t => t.IsPresent);

        var matching = await Shape(present.Where(t =>
                artistIds.Contains(t.ArtistId)
                || (t.Genre != null && genreNames.Contains(t.Genre.ToLower()))))
            .Take(CandidateCap)
            .ToListAsync(ct);

        // A random offset rather than a random sort: SQLite can do ORDER BY random(), EF can't
        // express it, and skipping into an ordered page is both translatable and cheap.
        var total = await present.CountAsync(ct);
        var offset = total <= ExplorationSample ? 0 : random.Next(total - ExplorationSample);

        var exploration = await Shape(present.OrderBy(t => t.Id).Skip(offset))
            .Take(ExplorationSample)
            .ToListAsync(ct);

        // Distinct because the slice can overlap the profile match.
        return [.. matching.Concat(exploration).DistinctBy(c => c.Id)];
    }

    private static IQueryable<Candidate> Shape(IQueryable<Track> query) =>
        query.Select(t => new Candidate(
            t.Id,
            t.Title,
            t.ArtistId,
            t.Artist!.Name,
            t.AlbumId,
            t.Album!.Title,
            t.Genre,
            t.DurationTicks));

    /// <summary>
    /// Why this track. Said in the user's terms — "because you play Nirvana" — because a
    /// suggestion nobody can account for is one nobody trusts, and because it makes a bad batch
    /// diagnosable instead of mysterious.
    /// </summary>
    private static string Reason(double artist, double genre, string artistName, string? genreName)
    {
        if (artist > 0 && genre > 0 && genreName is not null) return $"You play {artistName} and {genreName}";
        if (artist > 0) return $"You play {artistName}";
        if (genre > 0 && genreName is not null) return $"You play a lot of {genreName}";

        return "Something new from your library";
    }

    /// <summary>
    /// Weighted sampling without replacement, capped per artist.
    ///
    /// Not top-N: the same button pressed twice has to give a different answer, or "Surprise me"
    /// is a static list with a misleading name. Weighting means the good matches still come up
    /// far more often; the randomness is what makes it a shuffle of the likely rather than a
    /// ranking of it.
    ///
    /// The sort key is the standard exponential-jump trick — ordering by <c>-ln(u)/w</c> draws the
    /// whole list in weighted order in one pass, instead of re-scanning the pool for every pick.
    /// </summary>
    private static List<Suggestion> Sample(
        List<(double Score, string Reason, Candidate Row)> scored, int count, Random random)
    {
        var perArtist = new Dictionary<Guid, int>();
        var picked = new List<Suggestion>(count);

        var ordered = scored
            .OrderBy(item => -Math.Log(1 - random.NextDouble()) / Math.Max(item.Score, 1e-9))
            .ToList();

        foreach (var (_, reason, row) in ordered)
        {
            if (picked.Count >= count) break;
            if (perArtist.GetValueOrDefault(row.ArtistId) >= MaxPerArtist) continue;

            perArtist[row.ArtistId] = perArtist.GetValueOrDefault(row.ArtistId) + 1;

            picked.Add(new Suggestion(
                row.Id,
                row.Title,
                row.ArtistName,
                row.AlbumId,
                row.AlbumTitle,
                row.Genre,
                row.DurationTicks,
                reason));
        }

        return picked;
    }
}
