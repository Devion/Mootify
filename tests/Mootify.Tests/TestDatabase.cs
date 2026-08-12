using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Tests;

/// <summary>
/// A real SQLite database, in memory, kept alive by holding the connection open.
/// Deliberately not the InMemory provider: the bugs worth catching here are SQLite
/// translation failures (DateTimeOffset ordering, duration aggregates), and the
/// InMemory provider happily runs queries that the real database rejects.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable, IDbContextFactory<MootifyDbContext>
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MootifyDbContext> _options;

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MootifyDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new MootifyDbContext(_options);
        db.Database.EnsureCreated();
    }

    public MootifyDbContext CreateDbContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    public async Task<AppUser> AddUserAsync(string name)
    {
        await using var db = CreateDbContext();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            NormalizedName = name.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Puts someone in a team directly, bypassing the join policy. For tests where membership
    /// is a precondition rather than the thing under test — TeamServiceTests exercises the
    /// real invite and application paths.
    /// </summary>
    public async Task AddTeamMemberAsync(Guid teamId, Guid userId, TeamRole role = TeamRole.Member)
    {
        await using var db = CreateDbContext();
        db.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<Guid>> AddTracksAsync(int count, string artistName = "The Cowbells")
    {
        await using var db = CreateDbContext();

        var artist = new Artist { Id = Guid.NewGuid(), Name = artistName, SortName = artistName };
        var album = new Album { Id = Guid.NewGuid(), Title = "Pasture Sounds", ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Path = $@"C:\music\{artistName}\track{i}.mp3",
                Title = $"Track {i}",
                ArtistId = artist.Id,
                AlbumId = album.Id,
                TrackNumber = i + 1,
                Duration = TimeSpan.FromMinutes(3),
                AddedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                IsPresent = true,
            };
            db.Tracks.Add(track);
            ids.Add(track.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }
}
