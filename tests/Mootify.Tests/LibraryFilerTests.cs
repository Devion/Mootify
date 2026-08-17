using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>
/// The drop folder. Files land in <c>&lt;root&gt;/import</c> however they arrived and are
/// filed under <c>Artist/Album</c>, or into the catch-all when there is nothing to go on.
///
/// The rules are tested as pure functions and the moving is tested on real files, because
/// the two fail in completely different ways: a rule is wrong everywhere at once, and a move
/// is wrong only on the file that already existed at the destination.
/// </summary>
public sealed class LibraryFilerTests : IAsyncLifetime
{
    private string _root = null!;
    private string _drop = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));
        _drop = Path.Combine(_root, "import");
        Directory.CreateDirectory(_drop);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private LibraryFiler CreateFiler(LibraryOptions? options = null) =>
        new(new StaticOptionsMonitor<LibraryOptions>(options ?? new LibraryOptions { MusicRoot = _root }),
            NullLogger<LibraryFiler>.Instance);

    private string Drop(string relativePath, string? artist = null, string? album = null)
    {
        var full = Path.Combine(_drop, relativePath);
        TestAudio.Write(full, artist, album);
        return full;
    }

    private bool Exists(string relativePath) => File.Exists(Path.Combine(_root, relativePath));

    // ---- the rules -------------------------------------------------------

    [Fact]
    public void Artist_and_album_give_a_two_level_path()
    {
        Assert.Equal(
            Path.Combine("Cowbells", "Bored", "song.mp3"),
            LibraryFiler.Destination("Cowbells", "Bored", "song.mp3", "generic"));
    }

    [Fact]
    public void An_artist_with_no_album_sits_directly_under_the_artist()
    {
        // Best effort: knowing half of it is still worth more than the catch-all.
        Assert.Equal(
            Path.Combine("Cowbells", "song.mp3"),
            LibraryFiler.Destination("Cowbells", null, "song.mp3", "generic"));
    }

    [Fact]
    public void No_artist_means_the_catch_all_even_when_the_album_is_known()
    {
        // An album with no artist has no shelf to stand on, so it goes where a person will
        // look rather than into a tree of orphans.
        Assert.Equal(
            Path.Combine("generic", "song.mp3"),
            LibraryFiler.Destination(null, "Bored", "song.mp3", "generic"));
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("Where Are We? ", "Where Are We_")]
    [InlineData(@"Guns N\ Roses", "Guns N_ Roses")]
    [InlineData("Air.", "Air")]                 // Windows drops the trailing dot silently
    [InlineData("  Spacer  ", "Spacer")]
    public void Tag_text_is_made_safe_for_a_folder_name(string tag, string expected)
    {
        Assert.Equal(expected, LibraryFiler.SafeFolder(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown Artist")]
    [InlineData("unknown")]
    [InlineData("...")]
    public void Nothing_to_go_on_is_not_a_folder_name(string? tag)
    {
        Assert.Null(LibraryFiler.SafeFolder(tag));
    }

    [Fact]
    public void A_reserved_device_name_is_prefixed_rather_than_exiled()
    {
        // A folder called CON can't be opened on Windows, but the band still exists.
        Assert.Equal("_CON", LibraryFiler.SafeFolder("CON"));
        Assert.Equal("_lpt1", LibraryFiler.SafeFolder("lpt1"));

        // Reserved with an extension too, which is the easier one to miss.
        Assert.Equal("_CON.mp3", LibraryFiler.SafeName("CON.mp3"));
    }

    [Fact]
    public void An_absurd_tag_cannot_blow_the_path_limit()
    {
        var folder = LibraryFiler.SafeFolder(new string('x', 400));

        Assert.NotNull(folder);
        Assert.True(folder!.Length <= 96);
    }

    [Fact]
    public void The_file_keeps_its_own_name()
    {
        Assert.Equal("01 - Intro.mp3", LibraryFiler.SafeName("01 - Intro.mp3"));
        Assert.Equal("A_B.flac", LibraryFiler.SafeName("A/B.flac"));
    }

    // ---- moving ----------------------------------------------------------

    [Fact]
    public void A_tagged_file_is_filed_under_artist_and_album()
    {
        Drop("whatever.mp3", artist: "Cowbells", album: "Bored");

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(1, report.Filed);
        Assert.True(Exists(Path.Combine("Cowbells", "Bored", "whatever.mp3")));
        Assert.False(Exists(Path.Combine("import", "whatever.mp3")));
    }

    [Fact]
    public void Only_an_artist_tag_means_straight_under_the_artist()
    {
        Drop("loose.mp3", artist: "Cowbells");

        CreateFiler().FileDropFolder();

        Assert.True(Exists(Path.Combine("Cowbells", "loose.mp3")));
    }

    [Fact]
    public void An_untaggable_file_goes_to_the_catch_all()
    {
        // No tags at all — which is the case the folder exists to absorb, not an error.
        File.WriteAllBytes(Path.Combine(_drop, "mystery.mp3"), new byte[512]);

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(1, report.Unsorted);
        Assert.True(Exists(Path.Combine("generic", "mystery.mp3")));
    }

    [Fact]
    public void The_folder_it_was_dropped_in_stands_in_for_a_missing_album_tag()
    {
        // Somebody dropped a whole album folder in. The tracks know their artist but not their
        // album, and the folder name is the only other thing that could mean one.
        Drop(Path.Combine("OK Computer", "01 - Airbag.mp3"), artist: "Radiohead");

        CreateFiler().FileDropFolder();

        Assert.True(Exists(Path.Combine("Radiohead", "OK Computer", "01 - Airbag.mp3")));
    }

    [Fact]
    public void Artist_and_album_folders_nested_two_deep_are_walked()
    {
        // The realistic drop: a whole Artist/Album tree copied in, not loose files.
        Drop(Path.Combine("Radiohead", "OK Computer", "01 - Airbag.mp3"), artist: "Radiohead", album: "OK Computer");
        Drop(Path.Combine("Radiohead", "Kid A", "01 - Everything.mp3"), artist: "Radiohead", album: "Kid A");
        Drop(Path.Combine("Cowbells", "loose.mp3"), artist: "Cowbells");

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(3, report.Filed);
        Assert.True(Exists(Path.Combine("Radiohead", "OK Computer", "01 - Airbag.mp3")));
        Assert.True(Exists(Path.Combine("Radiohead", "Kid A", "01 - Everything.mp3")));
        Assert.True(Exists(Path.Combine("Cowbells", "loose.mp3")));
    }

    [Fact]
    public void Untagged_files_in_an_artist_album_tree_still_go_somewhere_sensible()
    {
        // No tags at all, which is common for hand-assembled folders. The artist never comes
        // from a folder name, so these land in the catch-all rather than inventing an artist.
        File.WriteAllBytes(Drop(Path.Combine("Radiohead", "OK Computer", "01 - Airbag.mp3")), new byte[512]);

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(1, report.Unsorted);
        Assert.True(Exists(Path.Combine("generic", "01 - Airbag.mp3")));
    }

    [Fact]
    public void A_folder_named_after_the_artist_is_not_mistaken_for_an_album()
    {
        Drop(Path.Combine("Radiohead", "loose.mp3"), artist: "Radiohead");

        CreateFiler().FileDropFolder();

        Assert.True(Exists(Path.Combine("Radiohead", "loose.mp3")));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        // Two different recordings can honestly both be "01 - Intro.mp3". Replacing one with
        // the other would destroy music in order to tidy a folder.
        Directory.CreateDirectory(Path.Combine(_root, "Cowbells", "Bored"));
        File.WriteAllText(Path.Combine(_root, "Cowbells", "Bored", "song.mp3"), "the original");
        Drop("song.mp3", artist: "Cowbells", album: "Bored");

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(1, report.Filed);
        Assert.Equal("the original", File.ReadAllText(Path.Combine(_root, "Cowbells", "Bored", "song.mp3")));
        Assert.True(Exists(Path.Combine("Cowbells", "Bored", "song (2).mp3")));
    }

    [Fact]
    public void Cover_art_travels_with_an_album_that_filed_as_one()
    {
        // AlbumArtService reads an adjacent cover.jpg straight off disk, so leaving it behind
        // is the same as throwing the album's art away.
        Drop(Path.Combine("Bored", "01.mp3"), artist: "Cowbells", album: "Bored");
        Drop(Path.Combine("Bored", "02.mp3"), artist: "Cowbells", album: "Bored");
        File.WriteAllBytes(Path.Combine(_drop, "Bored", "cover.jpg"), new byte[8]);

        CreateFiler().FileDropFolder();

        Assert.True(Exists(Path.Combine("Cowbells", "Bored", "cover.jpg")));
    }

    [Fact]
    public void Cover_art_stays_put_when_the_folder_scattered()
    {
        // Two artists in one folder means the cover belongs to neither.
        Drop(Path.Combine("Mixed", "a.mp3"), artist: "Cowbells");
        Drop(Path.Combine("Mixed", "b.mp3"), artist: "DevionNL");
        File.WriteAllBytes(Path.Combine(_drop, "Mixed", "cover.jpg"), new byte[8]);

        CreateFiler().FileDropFolder();

        Assert.True(File.Exists(Path.Combine(_drop, "Mixed", "cover.jpg")));
    }

    [Fact]
    public void Emptied_subfolders_are_tidied_away_but_the_drop_folder_stays()
    {
        Drop(Path.Combine("Some Album", "01.mp3"), artist: "Cowbells", album: "Bored");

        CreateFiler().FileDropFolder();

        Assert.False(Directory.Exists(Path.Combine(_drop, "Some Album")));
        Assert.True(Directory.Exists(_drop));
    }

    [Fact]
    public void A_file_still_being_written_is_left_for_the_next_pass()
    {
        var path = Drop("copying.mp3", artist: "Cowbells");
        using var holding = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Moved);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Files_that_are_not_music_are_left_alone()
    {
        File.WriteAllText(Path.Combine(_drop, "notes.txt"), "read me");

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(0, report.Total);
        Assert.True(File.Exists(Path.Combine(_drop, "notes.txt")));
    }

    [Fact]
    public void A_format_that_still_needs_converting_is_filed_anyway()
    {
        // The transcode sweep converts it in place, and it should be sitting under its artist
        // by then rather than in the mailbox.
        File.WriteAllBytes(Path.Combine(_drop, "old.wma"), new byte[512]);

        CreateFiler().FileDropFolder();

        Assert.True(Exists(Path.Combine("generic", "old.wma")));
    }

    [Fact]
    public void Turning_it_off_leaves_the_drop_folder_untouched()
    {
        Drop("song.mp3", artist: "Cowbells");

        var report = CreateFiler(new LibraryOptions { MusicRoot = _root, FileImportsOnScan = false })
            .FileDropFolder();

        Assert.Equal(FilingOutcome.Disabled, report.Outcome);
        Assert.True(File.Exists(Path.Combine(_drop, "song.mp3")));
    }

    [Fact]
    public void No_drop_folder_configured_is_not_an_error()
    {
        var filer = CreateFiler(new LibraryOptions { MusicRoot = _root, ImportFolder = "" });

        Assert.Null(filer.DropFolder);
        Assert.Equal(FilingOutcome.Disabled, filer.FileDropFolder().Outcome);
    }

    // ---- telling the four kinds of "nothing happened" apart ---------------

    [Fact]
    public void A_missing_folder_does_not_look_like_an_empty_one()
    {
        // These are the same zero files and completely different problems: one needs a folder
        // created, the other needs nothing at all.
        var filer = CreateFiler(new LibraryOptions { MusicRoot = _root, ImportFolder = "nowhere" });

        Assert.Equal(FilingOutcome.NotFound, filer.FileDropFolder().Outcome);
    }

    [Fact]
    public void An_empty_folder_reports_what_it_walked()
    {
        Directory.CreateDirectory(Path.Combine(_drop, "Radiohead", "OK Computer"));
        File.WriteAllText(Path.Combine(_drop, "Radiohead", "OK Computer", "notes.txt"), "no music here");

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(FilingOutcome.NothingToDo, report.Outcome);

        // The drop folder plus the two it contains — proof the walk went in, rather than
        // silence that could equally mean it never looked.
        Assert.Equal(3, report.Folders);
        Assert.Equal(0, report.Candidates);
    }

    [Fact]
    public void A_pass_that_moved_something_says_how_much_it_looked_at()
    {
        Drop(Path.Combine("Radiohead", "OK Computer", "01 - Airbag.mp3"), artist: "Radiohead", album: "OK Computer");

        var report = CreateFiler().FileDropFolder();

        Assert.Equal(FilingOutcome.Filed, report.Outcome);
        Assert.Equal(3, report.Folders);
        Assert.Equal(1, report.Candidates);
        Assert.Equal(1, report.Filed);
    }

    [Fact]
    public void Nothing_has_run_yet_is_its_own_answer()
    {
        Assert.Null(CreateFiler().LastReport);
    }

    [Fact]
    public void Where_everything_landed_is_reported_back()
    {
        // The scan that follows uses this to tell music that just arrived from music that was
        // always there, which is what stops a request being closed by the wrong album.
        Drop("song.mp3", artist: "Cowbells", album: "Bored");

        var report = CreateFiler().FileDropFolder();

        // Both ends: the destination says what is new, and the source is how a file that was
        // already indexed keeps its Track row — and therefore its playlist entries.
        Assert.Equal(
            [new FiledFile(
                Path.GetFullPath(Path.Combine(_drop, "song.mp3")),
                Path.GetFullPath(Path.Combine(_root, "Cowbells", "Bored", "song.mp3")))],
            report.Paths);
    }
}
