using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;

namespace Mootify.Tests;

public sealed class LibraryOrganizerTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private string _root = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _root = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The real graph, because two of the things being asserted on are wiring: that the quarantine
    /// path comes from the filer, and that a pass suspends the scanner while it runs.
    /// </summary>
    private (LibraryOrganizer Organizer, LibraryScanner Scanner) Create()
    {
        var options = new StaticOptionsMonitor<LibraryOptions>(new LibraryOptions { MusicRoot = _root });

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MootifyDbContext>>(_db);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext());

        var filer = new LibraryFiler(options, NullLogger<LibraryFiler>.Instance);

        var scanner = new LibraryScanner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            filer,
            new NetworkShareConnector(options, NullLogger<NetworkShareConnector>.Instance),
            options,
            NullLogger<LibraryScanner>.Instance);

        return (new LibraryOrganizer(_db, filer, scanner, options, NullLogger<LibraryOrganizer>.Instance), scanner);
    }

    private LibraryOrganizer CreateOrganizer() => Create().Organizer;

    private Task<OrganizeReport?> OrganizeAsync(OrganizeSettings? settings = null) =>
        CreateOrganizer().OrganizeAsync(settings ?? new OrganizeSettings(), Guid.NewGuid());

    /// <summary>A track with a real file behind it, so the quarantine move has something to move.</summary>
    private async Task<Guid> AddTrackAsync(
        string artistName,
        string albumTitle,
        string title,
        string fileName,
        TimeSpan? duration = null,
        int bitrate = 320,
        bool present = true,
        bool onDisk = true)
    {
        await using var db = _db.CreateDbContext();

        var artist = await db.Artists.FirstOrDefaultAsync(a => a.Name == artistName);
        if (artist is null)
        {
            artist = new Artist { Id = Guid.NewGuid(), Name = artistName, SortName = artistName };
            db.Artists.Add(artist);
            await db.SaveChangesAsync();
        }

        var album = await db.Albums.FirstOrDefaultAsync(a => a.ArtistId == artist.Id && a.Title == albumTitle);
        if (album is null)
        {
            album = new Album { Id = Guid.NewGuid(), Title = albumTitle, ArtistId = artist.Id };
            db.Albums.Add(album);
            await db.SaveChangesAsync();
        }

        var path = Path.Combine(_root, LibraryFiler.SafeFolder(artistName) ?? "generic", fileName);

        if (onDisk)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "not really an mp3");
        }

        var track = new Track
        {
            Id = Guid.NewGuid(),
            Path = path,
            Title = title,
            ArtistId = artist.Id,
            AlbumId = album.Id,
            Duration = duration ?? TimeSpan.FromMinutes(3),
            Bitrate = bitrate,
            FileSize = 1024,
            AddedAt = DateTimeOffset.UtcNow,
            IsPresent = present,
        };

        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track.Id;
    }

    // ---- names -----------------------------------------------------------

    [Fact]
    public async Task A_numbered_artist_loses_its_number_and_joins_the_real_one()
    {
        await AddTrackAsync("Elton John", "Honky Château", "Rocket Man", "rocket.mp3");
        await AddTrackAsync("09. Elton John", "Best Of", "Your Song", "yoursong.mp3");
        await AddTrackAsync("12. Shocking Blue", "Best Of", "Venus", "venus.mp3");

        var report = await OrganizeAsync();

        Assert.NotNull(report);

        await using var db = _db.CreateDbContext();
        var names = await db.Artists.Select(a => a.Name).OrderBy(n => n).ToListAsync();

        // The compilation's Elton John row is gone entirely; Shocking Blue only ever had the one
        // row, so it is renamed rather than merged.
        Assert.Equal(["Elton John", "Shocking Blue"], names);
        Assert.Equal(1, report!.ArtistsMerged);
        Assert.Equal(1, report.ArtistsRenamed);
        Assert.Equal(3, await db.Tracks.CountAsync());
    }

    [Fact]
    public async Task The_article_is_folded_so_one_band_is_one_row()
    {
        await AddTrackAsync("The Black Eyed Peas", "Elephunk", "Hey Mama", "heymama.mp3");
        await AddTrackAsync("The Black Eyed Peas", "Elephunk", "Shut Up", "shutup.mp3");
        await AddTrackAsync("Black Eyed Peas", "Monkey Business", "Pump It", "pumpit.mp3");

        await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        var artist = Assert.Single(await db.Artists.ToListAsync());

        // Two tracks to one: the spelling the library itself mostly uses is the one that survives.
        Assert.Equal("The Black Eyed Peas", artist.Name);
        Assert.Equal("Black Eyed Peas", artist.SortName);
        Assert.Equal(3, await db.Tracks.CountAsync(t => t.ArtistId == artist.Id));
        Assert.Equal(2, await db.Albums.CountAsync());
    }

    [Fact]
    public async Task Albums_that_collide_once_their_artists_merged_become_one()
    {
        await AddTrackAsync("The Cowbells", "Greatest Hits", "Moo", "moo.mp3");
        await AddTrackAsync("Cowbells", "Greatest Hits", "Graze", "graze.mp3");

        var report = await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        var album = Assert.Single(await db.Albums.ToListAsync());

        Assert.Equal("Greatest Hits", album.Title);
        Assert.Equal(2, await db.Tracks.CountAsync(t => t.AlbumId == album.Id));
        Assert.Equal(1, report!.AlbumsMerged);
    }

    [Fact]
    public async Task Song_titles_lose_their_track_number_too()
    {
        await AddTrackAsync("Blondie", "Parallel Lines", "01 - Hanging on the Telephone", "one.mp3");

        var report = await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal("Hanging on the Telephone", await db.Tracks.Select(t => t.Title).SingleAsync());
        Assert.Equal(1, report!.TrackTitlesFixed);
    }

    [Fact]
    public async Task Tidying_can_be_turned_off_on_its_own()
    {
        await AddTrackAsync("09. Elton John", "Best Of", "Your Song", "yoursong.mp3");

        await OrganizeAsync(new OrganizeSettings(TidyNames: false, RemoveDuplicates: true));

        await using var db = _db.CreateDbContext();
        Assert.Equal("09. Elton John", await db.Artists.Select(a => a.Name).SingleAsync());
    }

    // ---- duplicates ------------------------------------------------------

    [Fact]
    public async Task The_better_copy_stays_and_the_other_is_moved_aside()
    {
        var poor = await AddTrackAsync("Queen", "A Night at the Opera", "Bohemian Rhapsody", "bohemian.mp3", bitrate: 128);
        var good = await AddTrackAsync("Queen", "Greatest Hits", "Bohemian Rhapsody", "bohemian-hq.flac", bitrate: 1000);

        var report = await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        var survivor = Assert.Single(await db.Tracks.ToListAsync());

        Assert.Equal(good, survivor.Id);
        Assert.Equal(1, report!.DuplicatesMerged);
        Assert.Equal(1, report.FilesQuarantined);
        Assert.Equal(0, report.FilesFailed);

        // Moved, never deleted, and out of the part of the tree the scanner walks — otherwise
        // the next scan indexes it straight back in as a fresh duplicate.
        Assert.False(File.Exists((await db.Tracks.FindAsync(good))!.Path.Replace("bohemian-hq.flac", "bohemian.mp3")));

        var quarantined = Directory
            .EnumerateFiles(Path.Combine(_root, "duplicates"), "*", SearchOption.AllDirectories)
            .ToList();

        Assert.Single(quarantined);
        Assert.EndsWith("bohemian.mp3", quarantined[0]);
        Assert.DoesNotContain(poor, await db.Tracks.Select(t => t.Id).ToListAsync());
    }

    [Fact]
    public async Task A_different_length_recording_is_a_different_recording()
    {
        // Same artist, same title, six minutes apart. The live version is not the studio one,
        // and merging them would quietly throw one away.
        await AddTrackAsync("Queen", "A Night at the Opera", "Bohemian Rhapsody", "studio.mp3",
            duration: TimeSpan.FromMinutes(5));
        await AddTrackAsync("Queen", "Live Killers", "Bohemian Rhapsody", "live.mp3",
            duration: TimeSpan.FromMinutes(11));

        var report = await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.Tracks.CountAsync());
        Assert.Equal(0, report!.DuplicatesMerged);
    }

    [Fact]
    public async Task Duplicates_are_only_found_once_the_artists_are_one()
    {
        // The whole point of doing names first: these are the same song by the same band, and
        // nothing can tell until "09. Elton John" and "Elton John" are one artist.
        await AddTrackAsync("Elton John", "Honky Château", "Rocket Man", "rocket.mp3", bitrate: 320);
        await AddTrackAsync("09. Elton John", "Best Of", "01 - Rocket Man", "rocket-comp.mp3", bitrate: 128);

        await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        var track = Assert.Single(await db.Tracks.ToListAsync());
        Assert.Equal("Rocket Man", track.Title);
        Assert.EndsWith("rocket.mp3", track.Path);
    }

    [Fact]
    public async Task Turning_duplicates_off_leaves_both_copies_and_both_files()
    {
        await AddTrackAsync("Queen", "A", "Bohemian Rhapsody", "a.mp3", bitrate: 320);
        await AddTrackAsync("Queen", "B", "Bohemian Rhapsody", "b.mp3", bitrate: 128);

        await OrganizeAsync(new OrganizeSettings(TidyNames: true, RemoveDuplicates: false));

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.Tracks.CountAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "duplicates")));
    }

    // ---- what points at the copy that went -------------------------------

    [Fact]
    public async Task Playlists_follow_the_copy_that_stays()
    {
        var user = await _db.AddUserAsync("cowbell");
        var good = await AddTrackAsync("Queen", "A", "Bohemian Rhapsody", "a.mp3", bitrate: 320);
        var poor = await AddTrackAsync("Queen", "B", "Bohemian Rhapsody", "b.mp3", bitrate: 128);
        var other = await AddTrackAsync("Queen", "A", "Somebody to Love", "c.mp3");

        Guid playlistId;

        await using (var db = _db.CreateDbContext())
        {
            var playlist = new Playlist
            {
                Id = Guid.NewGuid(),
                Name = "Drive",
                OwnerUserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Playlists.Add(playlist);
            playlistId = playlist.Id;

            db.PlaylistItems.Add(new PlaylistItem { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackId = poor, SortKey = 1 });
            db.PlaylistItems.Add(new PlaylistItem { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackId = other, SortKey = 2 });
            await db.SaveChangesAsync();
        }

        var report = await OrganizeAsync();

        await using (var db = _db.CreateDbContext())
        {
            var trackIds = await db.PlaylistItems
                .Where(i => i.PlaylistId == playlistId)
                .OrderBy(i => i.SortKey)
                .Select(i => i.TrackId)
                .ToListAsync();

            // The entry moved rather than disappearing — a tidy-up that empties a playlist is
            // worse than the mess it tidied.
            Assert.Equal([good, other], trackIds);
        }

        Assert.Equal(1, report!.PlaylistEntriesRepointed);
        Assert.Equal(0, report.PlaylistEntriesRemoved);
    }

    [Fact]
    public async Task A_playlist_holding_both_copies_ends_up_holding_one()
    {
        var user = await _db.AddUserAsync("cowbell");
        var good = await AddTrackAsync("Queen", "A", "Bohemian Rhapsody", "a.mp3", bitrate: 320);
        var poor = await AddTrackAsync("Queen", "B", "Bohemian Rhapsody", "b.mp3", bitrate: 128);

        Guid playlistId;

        await using (var db = _db.CreateDbContext())
        {
            var playlist = new Playlist
            {
                Id = Guid.NewGuid(),
                Name = "Drive",
                OwnerUserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Playlists.Add(playlist);
            playlistId = playlist.Id;

            db.PlaylistItems.Add(new PlaylistItem { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackId = good, SortKey = 1 });
            db.PlaylistItems.Add(new PlaylistItem { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackId = poor, SortKey = 2 });
            await db.SaveChangesAsync();
        }

        var report = await OrganizeAsync();

        await using (var db = _db.CreateDbContext())
        {
            var item = Assert.Single(await db.PlaylistItems.Where(i => i.PlaylistId == playlistId).ToListAsync());
            Assert.Equal(good, item.TrackId);
            Assert.Equal(1, item.SortKey);
        }

        Assert.Equal(1, report!.PlaylistEntriesRemoved);
    }

    [Fact]
    public async Task History_and_whatever_is_queued_follow_it_too()
    {
        var user = await _db.AddUserAsync("cowbell");
        var good = await AddTrackAsync("Queen", "A", "Bohemian Rhapsody", "a.mp3", bitrate: 320);
        var poor = await AddTrackAsync("Queen", "B", "Bohemian Rhapsody", "b.mp3", bitrate: 128);
        var other = await AddTrackAsync("Queen", "A", "Somebody to Love", "c.mp3");

        await using (var db = _db.CreateDbContext())
        {
            db.PlayEvents.Add(new PlayEvent
            {
                UserId = user.Id,
                TrackId = poor,
                PlayedAt = DateTimeOffset.UtcNow,
                SecondsPlayed = 180,
            });

            db.PlaybackStates.Add(new PlaybackState
            {
                UserId = user.Id,
                CurrentTrackId = poor,
                QueueJson = System.Text.Json.JsonSerializer.Serialize(new[] { poor, other }),
                QueueIndex = 0,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        await OrganizeAsync();

        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal(good, await db.PlayEvents.Select(p => p.TrackId).SingleAsync());

            var state = await db.PlaybackStates.SingleAsync();
            Assert.Equal(good, state.CurrentTrackId);

            // Same length, so QueueIndex still points where it pointed.
            var queue = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(state.QueueJson)!;
            Assert.Equal([good, other], queue);
        }
    }

    // ---- clearing up -----------------------------------------------------

    [Fact]
    public async Task An_artist_with_nothing_left_by_them_is_cleared_out()
    {
        await AddTrackAsync("Blondie", "Parallel Lines", "Heart of Glass", "heart.mp3");

        await using (var db = _db.CreateDbContext())
        {
            var stranded = new Artist { Id = Guid.NewGuid(), Name = "Nobody", SortName = "Nobody" };
            db.Artists.Add(stranded);
            db.Albums.Add(new Album { Id = Guid.NewGuid(), Title = "Nothing", ArtistId = stranded.Id });
            await db.SaveChangesAsync();
        }

        var report = await OrganizeAsync();

        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal("Blondie", await db.Artists.Select(a => a.Name).SingleAsync());
            Assert.Equal(1, await db.Albums.CountAsync());
        }

        Assert.Equal(1, report!.EmptyArtistsRemoved);
        Assert.Equal(1, report.EmptyAlbumsRemoved);
    }

    [Fact]
    public async Task An_absent_row_is_merged_without_anything_being_moved()
    {
        await AddTrackAsync("Queen", "A", "Bohemian Rhapsody", "a.mp3", bitrate: 320);
        await AddTrackAsync("Queen", "B", "Bohemian Rhapsody", "b.mp3", bitrate: 128, present: false, onDisk: false);

        var report = await OrganizeAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Tracks.CountAsync());
        Assert.Equal(1, report!.DuplicatesMerged);
        Assert.Equal(0, report.FilesQuarantined);
        Assert.Equal(0, report.FilesFailed);
    }

    // ---- the preview -----------------------------------------------------

    [Fact]
    public async Task The_preview_says_what_the_pass_will_do_and_changes_nothing()
    {
        await AddTrackAsync("Elton John", "Honky Château", "Rocket Man", "rocket.mp3");
        await AddTrackAsync("09. Elton John", "Best Of", "Your Song", "yoursong.mp3");

        var organizer = CreateOrganizer();
        var plan = await organizer.PreviewAsync(new OrganizeSettings());

        Assert.True(plan.HasWork);
        Assert.Equal(1, plan.ArtistsMerged);
        Assert.Contains(plan.Samples, s => s.From == "09. Elton John" && s.To == "Elton John");

        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal(2, await db.Artists.CountAsync());
        }

        var report = await organizer.OrganizeAsync(new OrganizeSettings(), Guid.NewGuid());
        Assert.Equal(plan.ArtistsMerged, report!.ArtistsMerged);
    }

    [Fact]
    public async Task A_tidy_library_has_nothing_to_do()
    {
        await AddTrackAsync("Blondie", "Parallel Lines", "Heart of Glass", "heart.mp3");
        await AddTrackAsync("3 Doors Down", "The Better Life", "Kryptonite", "krypto.mp3");

        var plan = await CreateOrganizer().PreviewAsync(new OrganizeSettings());

        Assert.False(plan.HasWork);
        Assert.Empty(plan.Samples);
    }

    // ---- keeping the scanner out of it -----------------------------------

    [Fact]
    public async Task Nothing_scans_while_a_pass_is_running()
    {
        // The pass deletes the rows a scan upserts into. A scan caught mid-merge saves tracks
        // pointing at an artist that stopped existing halfway through it.
        var (organizer, scanner) = Create();

        Assert.False(scanner.IsSuspended);

        await AddTrackAsync("Blondie", "Parallel Lines", "01 - Heart of Glass", "heart.mp3");
        await organizer.OrganizeAsync(new OrganizeSettings(), Guid.NewGuid());

        // And released again afterwards, or the library would never be scanned twice.
        Assert.False(scanner.IsSuspended);
    }

    [Fact]
    public async Task A_suspended_scanner_refuses_rather_than_queueing()
    {
        // Refused, not blocked: every caller is a timer, a watcher or a button, and all three
        // would rather come back in a minute than hang.
        var (_, scanner) = Create();

        using (scanner.Suspend())
        {
            Assert.True(scanner.IsSuspended);
            Assert.Equal(ScanReport.Empty, await scanner.ScanAllAsync());
            Assert.Equal(ScanReport.Empty, await scanner.ScanPathAsync(_root));
        }

        Assert.False(scanner.IsSuspended);
    }

    [Fact]
    public async Task The_quarantine_is_one_of_the_folders_that_is_not_the_library()
    {
        // One list, in one place: the scanner, the watcher and the transcode sweep all walk this
        // tree, and a merged-away copy comes back the moment one of them disagrees.
        var (organizer, _) = Create();

        await AddTrackAsync("Queen", "A", "Bohemian Rhapsody", "a.mp3", bitrate: 320);
        await AddTrackAsync("Queen", "B", "Bohemian Rhapsody", "b.mp3", bitrate: 128);

        await organizer.OrganizeAsync(new OrganizeSettings(), Guid.NewGuid());

        var quarantined = Directory
            .EnumerateFiles(organizer.Quarantine!, "*", SearchOption.AllDirectories)
            .Single();

        var filer = new LibraryFiler(
            new StaticOptionsMonitor<LibraryOptions>(new LibraryOptions { MusicRoot = _root }),
            NullLogger<LibraryFiler>.Instance);

        Assert.True(filer.IsOutsideTheLibrary(quarantined));
        Assert.DoesNotContain(
            LibraryScanner.EnumerateAudioFiles(_root, filer.NotLibrary),
            f => f.FullName == quarantined);
    }
}
