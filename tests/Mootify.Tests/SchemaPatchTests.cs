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
    public async Task The_search_bookkeeping_reaches_a_database_that_predates_it()
    {
        // A nullable timestamp and a counter. The timestamp is the one worth checking, because
        // DateTimeOffset goes into SQLite as a converted integer rather than as text — DDL
        // written to match what EnsureCreated would have produced is the whole job here.
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "LastSearchAt" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SearchAttempts" """);

        await using var db = Db();
        await ApplyAsync(db);

        var requester = await AddUserAsync(db, "devion");
        var searchedAt = new DateTimeOffset(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);

        db.Requests.Add(new Request
        {
            Id = Guid.NewGuid(),
            RequesterId = requester,
            Kind = RequestKind.Track,
            Status = RequestStatus.Searching,
            Query = "Numb",
            ArtistName = "Linkin Park",
            LastSearchAt = searchedAt,
            SearchAttempts = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var saved = await db.Requests.SingleAsync();

        Assert.Equal(searchedAt, saved.LastSearchAt);
        Assert.Equal(3, saved.SearchAttempts);

        // And it has to be orderable, since the reconciler picks least-recently-searched first.
        Assert.Single(await db.Requests.OrderBy(r => r.LastSearchAt).ToListAsync());
    }

    [Fact]
    public async Task Requests_made_before_the_patch_read_as_never_searched()
    {
        // Which is what the reconciler needs them to say: null means "ask Lidarr about this one".
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "LastSearchAt" """);
        await ExecAsync("""ALTER TABLE "Requests" DROP COLUMN "SearchAttempts" """);
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

        Assert.Null(request.LastSearchAt);
        Assert.Equal(0, request.SearchAttempts);
        Assert.True(RequestReconciler.DueForSearch(request.SearchAttempts, request.LastSearchAt, DateTimeOffset.UtcNow));
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
