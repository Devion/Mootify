using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Requests;

namespace Mootify.Tests;

/// <summary>
/// The no-migrations stopgap, tested against the one database shape it exists for: an install
/// created by an older <c>EnsureCreated</c> and never touched since. Every other test starts from
/// a fresh schema that already has everything, which is exactly the case the patch doesn't cover —
/// so a broken ALTER here would pass the whole suite and fail on somebody's real library.
/// </summary>
public sealed class SchemaPatchTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MootifyDbContext> _options;

    public SchemaPatchTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MootifyDbContext>().UseSqlite(_connection).Options;

        using var db = new MootifyDbContext(_options);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private MootifyDbContext Db() => new(_options);

    /// <summary>Raw DDL/DML, for rolling the schema back to what an older install looks like.</summary>
    private async Task ExecAsync(string sql)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static Task ApplyAsync(MootifyDbContext db) =>
        SchemaPatch.ApplyAsync(db, NullLogger.Instance);

    /// <summary>A request needs a requester — the foreign key is real even on a patched database.</summary>
    private static async Task<Guid> AddUserAsync(MootifyDbContext db, string name)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            NormalizedName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }

    [Fact]
    public async Task A_column_added_later_reaches_a_database_that_predates_it()
    {
        await ExecAsync("""ALTER TABLE "Users" DROP COLUMN "MustChangePassword" """);

        await using var db = Db();
        await ApplyAsync(db);

        db.Users.Add(new AppUser
        {
            Id = Guid.NewGuid(),
            DisplayName = "devion",
            NormalizedName = "devion",
            MustChangePassword = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.True(await db.Users.Where(u => u.NormalizedName == "devion")
            .Select(u => u.MustChangePassword)
            .SingleAsync());
    }

    [Fact]
    public async Task Existing_rows_survive_the_column_and_default_to_false()
    {
        // The whole point of the stopgap: nobody loses their library over a bool.
        await ExecAsync("""ALTER TABLE "Users" DROP COLUMN "MustChangePassword" """);
        await ExecAsync("""
            INSERT INTO "Users" ("Id", "DisplayName", "NormalizedName", "PasswordHash", "IsAdmin",
                                 "IsBanned", "CreatedAt", "LastSeenAt")
            VALUES ('11111111-1111-1111-1111-111111111111', 'housemate', 'housemate', 'hash', 0, 0, 0, 0)
            """);

        await using var db = Db();
        await ApplyAsync(db);

        var user = await db.Users.SingleAsync(u => u.NormalizedName == "housemate");

        Assert.Equal("housemate", user.DisplayName);
        Assert.False(user.MustChangePassword);
    }

    [Fact]
    public async Task Soulseek_download_identity_reaches_a_database_that_predates_it()
    {
        await ExecAsync("""DROP INDEX "IX_Requests_SoulseekBatchId" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SoulseekBatchId" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SoulseekUsername" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SoulseekFilename" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "OfflineRecoveryAttempts" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "NextOfflineRecoveryAt" """);

        await using var db = Db();
        await ApplyAsync(db);

        var requester = await AddUserAsync(db, "devion");
        var batchId = Guid.NewGuid();

        db.Requests.Add(new Request
        {
            Id = Guid.NewGuid(),
            RequesterId = requester,
            Kind = RequestKind.Track,
            Status = RequestStatus.Searching,
            Query = "Numb",
            ArtistName = "Linkin Park",
            SoulseekBatchId = batchId,
            SoulseekUsername = "peer",
            SoulseekFilename = "music\\Numb.mp3",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var saved = await db.Requests.SingleAsync();

        Assert.Equal(batchId, saved.SoulseekBatchId);
        Assert.Equal("peer", saved.SoulseekUsername);
        Assert.Equal("music\\Numb.mp3", saved.SoulseekFilename);
        Assert.Equal(0, saved.OfflineRecoveryAttempts);
        Assert.Null(saved.NextOfflineRecoveryAt);
    }

    [Fact]
    public async Task Requests_made_before_the_patch_have_no_soulseek_batch()
    {
        await ExecAsync("""DROP INDEX "IX_Requests_SoulseekBatchId" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SoulseekBatchId" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SoulseekUsername" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SoulseekFilename" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "OfflineRecoveryAttempts" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "NextOfflineRecoveryAt" """);
        await ExecAsync("""
            INSERT INTO "Users" ("Id", "DisplayName", "NormalizedName", "PasswordHash", "IsAdmin",
                                 "IsBanned", "MustChangePassword", "CreatedAt", "LastSeenAt")
            VALUES ('33333333-3333-3333-3333-333333333333', 'housemate', 'housemate', 'hash', 0, 0, 0, 0, 0)
            """);
        await ExecAsync("""
            INSERT INTO "Requests" ("Id", "RequesterId", "Kind", "Status", "Query", "ArtistName",
                                    "CreatedAt", "UpdatedAt")
            VALUES ('22222222-2222-2222-2222-222222222222', '33333333-3333-3333-3333-333333333333',
                    0, 1, 'Numb', 'Linkin Park', 0, 0)
            """);

        await using var db = Db();
        await ApplyAsync(db);

        var request = await db.Requests.SingleAsync();

        Assert.Null(request.SoulseekBatchId);
        Assert.Null(request.SoulseekUsername);
        Assert.Null(request.SoulseekFilename);
        Assert.Equal(0, request.OfflineRecoveryAttempts);
        Assert.Null(request.NextOfflineRecoveryAt);
    }

    [Fact]
    public async Task A_table_added_later_reaches_a_database_that_predates_it()
    {
        // Two tables, both with foreign keys and one with a composite index. The DDL has to match
        // what EnsureCreated would have produced or the first query against it fails on a real
        // install and passes in every other test here.
        await ExecAsync("""DROP TABLE "ListeningSessions" """);
        await ExecAsync("""DROP TABLE "Ideas" """);

        await using var db = Db();
        await ApplyAsync(db);

        var userId = await AddUserAsync(db, "devion");

        db.Ideas.Add(new Idea
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Message = "More cowbell",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal("More cowbell", (await db.Ideas.SingleAsync()).Message);

        // The listening session needs a playlist and a track to point at, which is also what
        // proves the three foreign keys in that DDL are real.
        var (playlistId, trackId) = await AddPlaylistWithTrackAsync(db, userId);
        var updatedAt = new DateTimeOffset(2026, 8, 20, 11, 0, 0, TimeSpan.Zero);

        db.ListeningSessions.Add(new ListeningSession
        {
            UserId = userId,
            PlaylistId = playlistId,
            TrackId = trackId,
            PositionSeconds = 42.5,
            IsPlaying = true,
            StartedAt = updatedAt,
            UpdatedAt = updatedAt,
        });
        await db.SaveChangesAsync();

        var session = await db.ListeningSessions.SingleAsync();

        Assert.Equal(42.5, session.PositionSeconds);
        Assert.True(session.IsPlaying);

        // DateTimeOffset is a converted integer, so the range filter every read uses has to work
        // against DDL written by hand rather than by EF.
        Assert.Single(await db.ListeningSessions.Where(x => x.UpdatedAt >= updatedAt).ToListAsync());
    }

    [Fact]
    public async Task Sharing_defaults_to_off_on_a_database_that_predates_it()
    {
        // An upgrade that started broadcasting everybody's playback would be a privacy bug, so
        // the default on an existing row is the thing worth asserting.
        await ExecAsync("""ALTER TABLE "Preferences" DROP COLUMN "ShareListening" """);
        await ExecAsync("""
            INSERT INTO "Users" ("Id", "DisplayName", "NormalizedName", "PasswordHash", "IsAdmin",
                                 "IsBanned", "MustChangePassword", "CreatedAt", "LastSeenAt")
            VALUES ('44444444-4444-4444-4444-444444444444', 'housemate', 'housemate', 'hash', 0, 0, 0, 0, 0)
            """);
        await ExecAsync("""
            INSERT INTO "Preferences" ("UserId", "SoundEnabled", "DuckMusicWhilePlaying", "Volume", "AutoContinue")
            VALUES ('44444444-4444-4444-4444-444444444444', 1, 1, 0.8, 1)
            """);

        await using var db = Db();
        await ApplyAsync(db);

        var preference = await db.Preferences.SingleAsync();

        Assert.False(preference.ShareListening);
        Assert.True(preference.SoundEnabled);
    }

    [Fact]
    public async Task Genre_reaches_a_database_that_predates_it_as_null()
    {
        // Null is the right value on an existing row: it means "we have never read the genre off
        // this file". LibraryScanner's fingerprint version is what makes the next scan go and
        // look — see LibraryScannerTests.
        // Both, in this order: S"""Lite refuses to drop a column an index still refers to. The
        // patch has to put both back, which is what AddedIndexes is for.
        await ExecAsync("""DROP INDEX IF EXISTS "IX_Tracks_Genre" """);
        await ExecAsync("""ALTER TABLE "Tracks" DROP COLUMN "Genre" """);

        await using var db = Db();
        await ApplyAsync(db);

        var artist = new Artist { Id = Guid.NewGuid(), Name = "The Cowbells", SortName = "Cowbells" };
        var album = new Album { Id = Guid.NewGuid(), Title = "Pasture Sounds", ArtistId = artist.Id };

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            Path = "C:/music/cowbells/moo.mp3",
            Title = "Moo Anthem",
            ArtistId = artist.Id,
            AlbumId = album.Id,
            AddedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Null((await db.Tracks.SingleAsync()).Genre);

        // And it has to be writable, since the whole point is the next scan filling it in.
        var track = await db.Tracks.SingleAsync();
        track.Genre = "Grunge";
        await db.SaveChangesAsync();

        Assert.Equal("Grunge", (await db.Tracks.SingleAsync()).Genre);

        // The index too. Suggestions filter on Genre, so a column without one is a column that
        // makes the oldest install the slowest — and nothing else would ever notice.
        var indexes = await db.Database
            .SqlQuery<string>($"""SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_Tracks_Genre' """)
            .ToListAsync();

        Assert.Single(indexes);
    }

    [Fact]
    public async Task Keep_playing_defaults_to_on_for_an_account_that_predates_it()
    {
        // On is safe only because the suggester refuses to run without history: it does nothing
        // at all until it can do something sensible. A default of off would instead mean a
        // setting nobody knew to go and find.
        await ExecAsync("""ALTER TABLE "Preferences" DROP COLUMN "AutoContinue" """);
        await ExecAsync("""
            INSERT INTO "Users" ("Id", "DisplayName", "NormalizedName", "PasswordHash", "IsAdmin",
                                 "IsBanned", "MustChangePassword", "CreatedAt", "LastSeenAt")
            VALUES ('55555555-5555-5555-5555-555555555555', 'housemate', 'housemate', 'hash', 0, 0, 0, 0, 0)
            """);
        await ExecAsync("""
            INSERT INTO "Preferences" ("UserId", "SoundEnabled", "DuckMusicWhilePlaying", "Volume", "ShareListening")
            VALUES ('55555555-5555-5555-5555-555555555555', 1, 1, 0.8, 0)
            """);

        await using var db = Db();
        await ApplyAsync(db);

        Assert.True((await db.Preferences.SingleAsync()).AutoContinue);
    }

    /// <summary>A playlist with one track in it, for the foreign keys a listening session needs.</summary>
    private static async Task<(Guid PlaylistId, Guid TrackId)> AddPlaylistWithTrackAsync(
        MootifyDbContext db, Guid userId)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "The Cowbells", SortName = "Cowbells" };
        var album = new Album { Id = Guid.NewGuid(), Title = "Pasture Sounds", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Path = @"C:\music\cowbells\moo.mp3",
            Title = "Moo Anthem",
            ArtistId = artist.Id,
            AlbumId = album.Id,
            AddedAt = DateTimeOffset.UtcNow,
        };
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Barn Bangers",
            OwnerUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();

        return (playlist.Id, track.Id);
    }

    [Fact]
    public async Task Running_it_on_a_current_database_does_nothing_twice()
    {
        // It runs at every boot, so "already applied" has to be the cheap, silent path.
        await using var db = Db();

        await ApplyAsync(db);
        await ApplyAsync(db);

        Assert.Empty(await db.Users.ToListAsync());
    }
}
