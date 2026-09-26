using System.Text;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Services.Transcoding;

namespace Mootify.Services.Library;

/// <summary>One file's journey. Both ends matter — see <see cref="FilingReport.Paths"/>.</summary>
public sealed record FiledFile(string From, string To);

/// <summary>
/// Why a pass did what it did. These are four different situations that all produce zero
/// filed files, and telling them apart is the difference between "it's working, there was
/// nothing to do" and an hour spent wondering whether the feature is on.
/// </summary>
public enum FilingOutcome
{
    /// <summary><c>Library:FileImportsOnScan</c> is off, or <c>Library:ImportFolder</c> is blank.</summary>
    Disabled,

    /// <summary>The folder isn't there. Not the same as it being empty, and not the same as it being wrong.</summary>
    NotFound,

    /// <summary>Walked it and found no music in it.</summary>
    NothingToDo,

    /// <summary>Walked it and moved something.</summary>
    Filed,
}

/// <summary>
/// <paramref name="Filed"/> landed under an artist, <paramref name="Unsorted"/> in the
/// catch-all folder, <paramref name="Skipped"/> was still being written and is somebody
/// else's problem for another ten seconds.
///
/// <paramref name="Paths"/> is what the scan that follows works from, and it needs both ends.
/// The destination tells music that just arrived from music that was always there — the
/// difference between completing the request somebody made and completing one at random. The
/// source is how an already-indexed file keeps its `Track` row through the move instead of
/// going absent and coming back as a stranger, which would empty every playlist it was in.
/// </summary>
public sealed record FilingReport(
    FilingOutcome Outcome,
    int Folders,
    int Candidates,
    int Filed,
    int Unsorted,
    int Skipped,
    int Failed,
    IReadOnlyList<FiledFile> Paths)
{
    public static FilingReport Disabled => new(FilingOutcome.Disabled, 0, 0, 0, 0, 0, 0, []);

    public static FilingReport NotFound => new(FilingOutcome.NotFound, 0, 0, 0, 0, 0, 0, []);

    /// <summary>Walked it, nothing here.</summary>
    public static FilingReport Nothing => new(FilingOutcome.NothingToDo, 0, 0, 0, 0, 0, 0, []);

    public int Moved => Filed + Unsorted;
    public int Total => Filed + Unsorted + Skipped + Failed;
}

/// <summary>
/// Empties the drop folder inside the music root, filing what it finds under
/// <c>Artist/Album</c>.
///
/// It runs at the start of every scan and before the scanner enumerates, so a dropped file is
/// indexed once, at the path it will keep. The other order indexes it twice — once where it
/// landed and once where it went — and the first row goes absent on the following pass,
/// taking any playlist entry made in between with it. The drop folder is therefore excluded
/// from indexing outright: what's left in it after a pass is a file still being copied, and a
/// half-written MP3 has a plausible size and unreadable tags, which is exactly the shape of a
/// Track row nobody wants.
///
/// Best effort, and the effort has a floor: it never overwrites, never deletes, and never
/// guesses an artist. A wrong guess is a file nobody will find again, whereas the catch-all
/// folder is a list somebody can work through.
/// </summary>
public sealed class LibraryFiler(
    IOptionsMonitor<LibraryOptions> options,
    ILogger<LibraryFiler> log)
{
    /// <summary>Null until a scan has actually run one, which is itself worth being able to see.</summary>
    public FilingReport? LastReport { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>
    /// The drop folder's full path, or null when it isn't configured. Also what the scanner
    /// excludes from indexing.
    /// </summary>
    public string? DropFolder => Inside(options.CurrentValue.ImportFolder);

    /// <summary>
    /// Where <see cref="LibraryOrganizer"/> puts a copy of a song it merged away, or null when
    /// <c>Library:DuplicatesFolder</c> is blank.
    /// </summary>
    public string? DuplicatesFolder => Inside(options.CurrentValue.DuplicatesFolder);

    /// <summary>
    /// The folders inside the music root that are <i>not</i> the library: the mailbox, whose
    /// contents are still being copied, the quarantine, whose contents were deliberately
    /// taken out, and the storage cache, whose copies must never become library tracks. One list in one place, because three different walkers over the same tree with
    /// two different opinions about it is precisely how a merged-away duplicate finds its way
    /// back in — see <see cref="LibraryScanner.EnumerateAudioFiles"/> and
    /// <see cref="Transcoding.LibraryTranscodeService.FindConvertible"/>.
    /// </summary>
    public string? CacheFolder => string.IsNullOrWhiteSpace(options.CurrentValue.MusicRoot)
        ? null : LibraryCachePaths.Root(options.CurrentValue);

    public string?[] NotLibrary => [DropFolder, DuplicatesFolder, CacheFolder];

    /// <summary>Resolves a configured folder name against the music root. Null if either is blank.</summary>
    private string? Inside(string? folder)
    {
        var root = options.CurrentValue.MusicRoot;

        return string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(folder)
            ? null
            : Path.GetFullPath(Path.Combine(root, folder.Trim('/', '\\')));
    }

    /// <summary>
    /// Whether a path sits inside one of <see cref="NotLibrary"/>. The trailing separator
    /// matters: without it a folder called "duplicates-old" would match "duplicates".
    /// </summary>
    public bool IsOutsideTheLibrary(string path) =>
        NotLibrary.Any(folder => folder is not null && Contains(folder, path));

    internal static bool Contains(string folder, string path) =>
        path.StartsWith(
            folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public FilingReport FileDropFolder(CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var drop = DropFolder;

        // Every exit below records a report and says something. A pass that returns silently is
        // indistinguishable from a feature that isn't running, and that ambiguity costs more to
        // untangle than the logging costs to emit.
        if (!opts.FileImportsOnScan || drop is null)
        {
            return Record(FilingReport.Disabled, drop);
        }

        if (!Directory.Exists(drop))
        {
            log.LogWarning(
                "Import folder {Drop} does not exist or is not reachable — nothing will be filed. " +
                "It is Library:ImportFolder ({Folder}) resolved against Library:MusicRoot ({Root}).",
                drop, opts.ImportFolder, opts.MusicRoot);

            return Record(FilingReport.NotFound, drop);
        }

        int filed = 0, unsorted = 0, skipped = 0, failed = 0, folders = 0, candidates = 0;
        var paths = new List<FiledFile>();

        try
        {
            // Bottom-up, so a nested album folder is empty by the time the sweep below reaches it.
            foreach (var folder in EnumerateFoldersDeepestFirst(drop))
            {
                ct.ThrowIfCancellationRequested();
                folders++;

                var outcome = FileOneFolder(folder, opts, ct);

                candidates += outcome.Candidates;
                filed += outcome.Filed;
                unsorted += outcome.Unsorted;
                skipped += outcome.Skipped;
                failed += outcome.Failed;
                paths.AddRange(outcome.Paths);
            }

            RemoveEmptyFolders(drop);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A drop folder that can't be read must not take the scan down with it — the
            // library itself is still perfectly scannable.
            log.LogWarning(ex, "Could not file the drop folder at {Drop}", drop);
        }

        var result = new FilingReport(
            paths.Count > 0 ? FilingOutcome.Filed : FilingOutcome.NothingToDo,
            folders, candidates, filed, unsorted, skipped, failed, paths);

        return Record(result, drop);
    }

    private FilingReport Record(FilingReport report, string? drop)
    {
        LastReport = report;
        LastRunAt = DateTimeOffset.UtcNow;

        // Folder and candidate counts, always — "walked 14 folders and found no music" and
        // "walked nothing" are the two answers worth being able to tell apart, and neither is
        // visible from the outside otherwise.
        if (report.Outcome is FilingOutcome.Filed or FilingOutcome.NothingToDo)
        {
            log.LogInformation(
                "Import folder {Drop}: walked {Folders} folder(s), found {Candidates} music file(s), " +
                "filed {Filed} by artist, {Unsorted} unsorted, {Skipped} still in use, {Failed} failed",
                drop, report.Folders, report.Candidates,
                report.Filed, report.Unsorted, report.Skipped, report.Failed);
        }

        return report;
    }

    /// <summary>
    /// One folder at a time, because a folder is the unit that carries information: every music
    /// file in it going to the same place is what makes it safe to take the cover art along.
    /// </summary>
    private FilingReport FileOneFolder(string folder, LibraryOptions opts, CancellationToken ct)
    {
        int filed = 0, unsorted = 0, skipped = 0, failed = 0;
        var paths = new List<FiledFile>();

        // Materialised before anything moves — enumerating a directory while emptying it is
        // undefined enough to skip files on some filesystems.
        var music = new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(f => IsMusic(f.Name))
            .ToList();

        if (music.Count == 0) return FilingReport.Nothing;

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in music)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsFinishedCopying(file))
            {
                skipped++;
                log.LogDebug("{File} is still open elsewhere; leaving it for the next pass", file.FullName);
                continue;
            }

            // Captured before the move, because FileInfo.MoveTo rewrites FullName in place.
            var from = file.FullName;

            var (artist, album) = ReadTags(file);

            // The album falls back to the folder the file was dropped in, on the same reasoning
            // the scanner already uses: a folder that isn't just the artist's name, holding
            // tracks, is an album. The artist never falls back — a folder called "New stuff"
            // would become an artist in the library, and that is worse than unsorted.
            album ??= AlbumFromFolder(file, artist);

            var relative = Destination(artist, album, file.Name, opts.UnsortedFolder);
            var target = Path.Combine(opts.MusicRoot, relative);

            if (TryMove(file, target) is { } landed)
            {
                paths.Add(new FiledFile(Path.GetFullPath(from), landed));
                destinations.Add(Path.GetDirectoryName(landed)!);

                // SafeFolder, not the raw tag: a file tagged literally "Unknown Artist" lands in
                // the catch-all, and counting it as filed would make the admin panel disagree
                // with the folder somebody is looking at.
                if (SafeFolder(artist) is null) unsorted++; else filed++;
            }
            else
            {
                failed++;
            }
        }

        // Only when the whole folder agreed on one destination — a mixed bag of singles has no
        // single album for a cover to belong to, and AlbumArtService reads cover.jpg from the
        // track's own folder, so putting it anywhere else is the same as losing it.
        if (destinations.Count == 1)
        {
            MoveSidecars(folder, destinations.Single());
        }

        return new FilingReport(
            FilingOutcome.NothingToDo, Folders: 1, music.Count, filed, unsorted, skipped, failed, paths);
    }

    // ---- the rules -------------------------------------------------------

    /// <summary>
    /// Where one file belongs, relative to the music root. Pure, because these rules are the
    /// part worth arguing about and a rule you can only check by moving somebody's music is a
    /// rule nobody checks.
    /// </summary>
    public static string Destination(string? artist, string? album, string fileName, string unsortedFolder)
    {
        var safeName = SafeName(fileName) ?? "track";
        var artistFolder = SafeFolder(artist);

        if (artistFolder is null)
        {
            // No artist means no shelf to put it on, and an album with no artist is the same
            // case — one flat folder somebody can work through beats a tree of orphans.
            return Path.Combine(SafeFolder(unsortedFolder) ?? "generic", safeName);
        }

        var albumFolder = SafeFolder(album);

        return albumFolder is null
            ? Path.Combine(artistFolder, safeName)
            : Path.Combine(artistFolder, albumFolder, safeName);
    }

    /// <summary>
    /// Reserved on Windows, and the rest of the library has to stay readable from there even
    /// when the server is Linux — <see cref="Path.GetInvalidFileNameChars"/> would let a colon
    /// through on Linux and produce a share nobody can open from a PC.
    /// </summary>
    private const string InvalidCharacters = @"<>:""/\|?*";

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Longer than any real artist or album, short enough that nesting can't hit MAX_PATH.</summary>
    private const int MaxComponentLength = 96;

    /// <summary>
    /// A tag is free text; a folder name is not. Windows refuses the reserved characters
    /// outright and *silently drops* trailing dots and spaces, which is the nastier half:
    /// "Air." and "Air" become one folder on Windows and two on Linux, so the same library
    /// splits in half depending on which machine wrote it.
    /// </summary>
    public static string? SafeFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Tags carry these often enough to matter, and both mean "nobody filled this in".
        if (name.Trim() is var trimmed &&
            (trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("unknown artist", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("unknown album", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return Sanitize(trimmed);
    }

    /// <summary>The file keeps its own name — only the characters a filesystem refuses change.</summary>
    public static string? SafeName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        // Replace the separators *before* splitting off the extension: otherwise
        // GetFileNameWithoutExtension reads what's left of one as a path and silently drops
        // everything in front of it, so "A/B.flac" comes back as "B.flac".
        var replaced = ReplaceInvalid(fileName);
        var stem = Sanitize(Path.GetFileNameWithoutExtension(replaced));

        return stem is null ? null : stem + Path.GetExtension(replaced);
    }

    private static string ReplaceInvalid(string value)
    {
        var cleaned = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            cleaned.Append(char.IsControl(ch) || InvalidCharacters.Contains(ch) ? '_' : ch);
        }

        return cleaned.ToString();
    }

    private static string? Sanitize(string value)
    {
        // Trim both ends of both, because Windows drops trailing dots and spaces and would
        // otherwise collide two names that are distinct everywhere else.
        var result = ReplaceInvalid(value).Trim().TrimEnd(' ', '.');
        if (result.Length > MaxComponentLength)
        {
            result = result[..MaxComponentLength].TrimEnd(' ', '.');
        }

        if (result.Length == 0) return null;

        // "AUX" is unopenable on Windows rather than merely odd, extension or no extension.
        // Prefixing keeps the file filed under something recognisable instead of exiling a
        // real band to the catch-all.
        return ReservedNames.Contains(result, StringComparer.OrdinalIgnoreCase) ? "_" + result : result;
    }

    // ---- reading the file ------------------------------------------------

    private (string? Artist, string? Album) ReadTags(FileInfo file)
    {
        try
        {
            using var tag = TagLib.File.Create(file.FullName);
            return (LibraryScanner.ReadArtist(tag), tag.Tag.Album);
        }
        catch (Exception ex)
        {
            // Unreadable tags is the case this whole folder exists to handle, not an error.
            log.LogDebug(ex, "No usable tags on {File}", file.FullName);
            return (null, null);
        }
    }

    /// <summary>
    /// The folder a file was dropped in, when it can mean an album: not the drop folder itself
    /// and not just the artist's name repeated.
    /// </summary>
    private string? AlbumFromFolder(FileInfo file, string? artist)
    {
        var folder = file.Directory;
        var drop = DropFolder;

        if (folder is null || drop is null || SamePath(folder.FullName, drop)) return null;

        return artist is not null && folder.Name.Equals(artist.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : folder.Name;
    }

    /// <summary>
    /// Everything worth filing: what the library indexes plus what the transcode sweep can
    /// rescue. Filing an OGG is still worth doing — the sweep converts it in place, and it
    /// should be sitting under its artist by then rather than in the mailbox.
    /// </summary>
    public static readonly string[] MusicExtensions =
        [.. LibraryScanner.IndexedExtensions, .. Transcoder.ConvertibleExtensions];

    public static bool IsMusic(string path) =>
        MusicExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SidecarExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>
    /// A file halfway through a copy has a plausible size and no readable tags, so it would be
    /// filed under the catch-all and stay there. Asking for it exclusively is the question
    /// actually worth asking: if the writer still holds it we get nothing, and the next pass
    /// finds it finished.
    /// </summary>
    private static bool IsFinishedCopying(FileInfo file)
    {
        try
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- moving ----------------------------------------------------------

    /// <summary>Where the file ended up, or null if it didn't move.</summary>
    private string? TryMove(FileInfo source, string target)
    {
        try
        {
            var folder = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(folder)) return null;

            Directory.CreateDirectory(folder);

            var free = Unique(target);
            if (free is null) return null;

            source.MoveTo(free);
            log.LogDebug("Filed {Source} as {Target}", source.Name, free);

            // Fully resolved, because the request matcher compares it against the path the
            // scanner enumerated and a configured music root can be relative.
            return Path.GetFullPath(free);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not file {File}", source.FullName);
            return null;
        }
    }

    /// <summary>
    /// Never overwrite. Two files can legitimately both be "01 - Intro.mp3" for the same album,
    /// and a filer that replaces one with the other has destroyed music in order to tidy up.
    ///
    /// Public because the drop folder is no longer the only way a file arrives — an upload
    /// (<see cref="TrackUploadService"/>) lands in exactly the same folders and has to answer
    /// the collision exactly the same way. Null means it gave up looking for a free name.
    /// </summary>
    public static string? Unique(string path)
    {
        if (!File.Exists(path)) return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < 200; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Cover art travels with the album. AlbumArtService reads an adjacent cover.jpg off disk,
    /// so leaving it behind in the drop folder is the same as throwing it away.
    /// </summary>
    private void MoveSidecars(string folder, string destination)
    {
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (!SidecarExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(destination, SafeName(file.Name) ?? file.Name);

            // Only if the album doesn't already have art — the copy already there is the one
            // the library has been serving, and it isn't ours to replace.
            if (File.Exists(target)) continue;

            try
            {
                file.MoveTo(target);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not move the sidecar {File}", file.FullName);
            }
        }
    }

    // ---- walking ---------------------------------------------------------

    private static IEnumerable<string> EnumerateFoldersDeepestFirst(string root) =>
        Directory
            .EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            })
            .OrderByDescending(d => d.Length)
            .Append(root);

    /// <summary>Tidies up after itself, but never removes the drop folder — it's the mailbox.</summary>
    private void RemoveEmptyFolders(string root)
    {
        foreach (var folder in EnumerateFoldersDeepestFirst(root))
        {
            if (SamePath(folder, root)) continue;

            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not remove the empty folder {Folder}", folder);
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
