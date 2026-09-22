using Microsoft.EntityFrameworkCore;

namespace Mootify.Data;

/// <summary>
/// The stopgap for having no migrations. <c>EnsureCreated</c> creates the whole schema on an
/// empty file and then does <i>nothing</i> forever after — so a new entity means an existing
/// install starts throwing "no such table" on the first query that touches it, and the documented
/// fix is to delete the database.
///
/// Deleting the database was an acceptable answer while this was one developer's toy. It stopped
/// being one the moment there were accounts, playlists and a merged 600-row import in there, and
/// adding the Android app's token table shouldn't cost anybody their library.
///
/// So: additive, idempotent DDL for tables and columns added after the fact, run at boot. It is
/// deliberately not a migration system — there is no version table, no down path, and no support
/// for <i>changing</i> an existing column, only for appending a new nullable-or-defaulted one.
/// <b>Anything beyond that needs real EF migrations, and this file should go away when they
/// arrive.</b>
/// </summary>
public static class SchemaPatch
{
    /// <summary>
    /// Each entry is a table added after the initial <c>EnsureCreated</c>, with DDL matching what
    /// EF would have generated for it. Column types follow the provider's own mapping: Guid is
    /// TEXT, and DateTimeOffset is INTEGER because of the sortable-binary converter in
    /// <see cref="MootifyDbContext.ConfigureConventions"/>.
    /// </summary>
    private static readonly (string Table, string[] Statements)[] AddedTables =
    [
        ("ApiTokens",
        [
            """
            CREATE TABLE IF NOT EXISTS "ApiTokens" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ApiTokens" PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "TokenHash" TEXT NOT NULL,
                "DeviceName" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "LastUsedAt" INTEGER NOT NULL,
                "ExpiresAt" INTEGER NULL,
                "RevokedAt" INTEGER NULL,
                CONSTRAINT "FK_ApiTokens_Users_UserId" FOREIGN KEY ("UserId")
                    REFERENCES "Users" ("Id") ON DELETE CASCADE
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApiTokens_TokenHash" ON "ApiTokens" ("TokenHash")""",
            """CREATE INDEX IF NOT EXISTS "IX_ApiTokens_UserId" ON "ApiTokens" ("UserId")""",
        ]),

        ("ListeningSessions",
        [
            // UserId is the primary key, not just a foreign one: one person broadcasts one thing.
            """
            CREATE TABLE IF NOT EXISTS "ListeningSessions" (
                "UserId" TEXT NOT NULL CONSTRAINT "PK_ListeningSessions" PRIMARY KEY,
                "PlaylistId" TEXT NOT NULL,
                "TrackId" TEXT NOT NULL,
                "PositionSeconds" REAL NOT NULL,
                "IsPlaying" INTEGER NOT NULL,
                "StartedAt" INTEGER NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                CONSTRAINT "FK_ListeningSessions_Users_UserId" FOREIGN KEY ("UserId")
                    REFERENCES "Users" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ListeningSessions_Playlists_PlaylistId" FOREIGN KEY ("PlaylistId")
                    REFERENCES "Playlists" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ListeningSessions_Tracks_TrackId" FOREIGN KEY ("TrackId")
                    REFERENCES "Tracks" ("Id") ON DELETE CASCADE
            )
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ListeningSessions_PlaylistId_UpdatedAt" ON "ListeningSessions" ("PlaylistId", "UpdatedAt")""",
            """CREATE INDEX IF NOT EXISTS "IX_ListeningSessions_PlaylistId" ON "ListeningSessions" ("PlaylistId")""",
            """CREATE INDEX IF NOT EXISTS "IX_ListeningSessions_TrackId" ON "ListeningSessions" ("TrackId")""",
        ]),

        ("Ideas",
        [
            """
            CREATE TABLE IF NOT EXISTS "Ideas" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Ideas" PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "ArchivedAt" INTEGER NULL,
                CONSTRAINT "FK_Ideas_Users_UserId" FOREIGN KEY ("UserId")
                    REFERENCES "Users" ("Id") ON DELETE CASCADE
            )
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Ideas_ArchivedAt_CreatedAt" ON "Ideas" ("ArchivedAt", "CreatedAt")""",
            """CREATE INDEX IF NOT EXISTS "IX_Ideas_UserId_CreatedAt" ON "Ideas" ("UserId", "CreatedAt")""",
        ]),
    ];

    /// <summary>
    /// Columns appended to a table that already exists. SQLite's <c>ADD COLUMN</c> only rewrites
    /// the header, so this stays cheap on a big table — but it also means the DDL has to carry a
    /// default for every existing row, which is why every entry here is NOT NULL DEFAULT or
    /// nullable. There is no <c>IF NOT EXISTS</c> for columns, hence the pragma check.
    /// </summary>
    private static readonly (string Table, string Column, string Ddl)[] AddedColumns =
    [
        ("Users", "MustChangePassword",
            """ALTER TABLE "Users" ADD COLUMN "MustChangePassword" INTEGER NOT NULL DEFAULT 0"""),

        ("Requests", "SoulseekBatchId",
            """ALTER TABLE "Requests" ADD COLUMN "SoulseekBatchId" TEXT NULL"""),
        ("Requests", "SoulseekUsername",
            """ALTER TABLE "Requests" ADD COLUMN "SoulseekUsername" TEXT NULL"""),
        ("Requests", "SoulseekFilename",
            """ALTER TABLE "Requests" ADD COLUMN "SoulseekFilename" TEXT NULL"""),

        // 0 is the default the entity carries too: nobody's listening is shared until they say so,
        // and an upgrade that started broadcasting everyone's playback would be a privacy bug.
        ("Preferences", "ShareListening",
            """ALTER TABLE "Preferences" ADD COLUMN "ShareListening" INTEGER NOT NULL DEFAULT 0"""),

        // 1, matching the entity: it only does anything once there is listening history to work
        // from, so defaulting it on can't surprise anybody with music they didn't ask for.
        ("Preferences", "AutoContinue",
            """ALTER TABLE "Preferences" ADD COLUMN "AutoContinue" INTEGER NOT NULL DEFAULT 1"""),

        // Null on every existing row, which is exactly right: it means "we have never read the
        // genre off this file". LibraryScanner.FingerprintVersion is what makes the next scan go
        // and look, rather than skipping every file as unchanged forever.
        ("Tracks", "Genre",
            """ALTER TABLE "Tracks" ADD COLUMN "Genre" TEXT NULL"""),
    ];


    /// <summary>
    /// Indexes on a table that already exists.
    ///
    /// A separate list from <see cref="AddedColumns"/> because adding a column does not add the
    /// index EF would have created alongside it — so an upgraded database ended up with
    /// <c>Tracks.Genre</c> and no index on it, while a fresh one had both. That difference is
    /// invisible until the install that has been running for a year is the slow one.
    ///
    /// <c>CREATE INDEX IF NOT EXISTS</c> is idempotent by itself, so unlike the lists above this
    /// needs no existence check of its own — only that the table is there to index.
    /// </summary>
    private static readonly (string Table, string Name, string Ddl)[] AddedIndexes =
    [
        ("Requests", "IX_Requests_SoulseekBatchId",
            """CREATE INDEX IF NOT EXISTS "IX_Requests_SoulseekBatchId" ON "Requests" ("SoulseekBatchId")"""),
        ("Tracks", "IX_Tracks_Genre",
            """CREATE INDEX IF NOT EXISTS "IX_Tracks_Genre" ON "Tracks" ("Genre")"""),
    ];

    public static async Task ApplyAsync(MootifyDbContext db, ILogger log, CancellationToken ct = default)
    {
        foreach (var (table, statements) in AddedTables)
        {
            if (await TableExistsAsync(db, table, ct)) continue;

            foreach (var statement in statements)
            {
                await db.Database.ExecuteSqlRawAsync(statement, ct);
            }

            log.LogWarning(
                "Added the {Table} table to an existing database. This is the no-migrations " +
                "stopgap in SchemaPatch, not a migration — see the comment there.", table);
        }

        foreach (var (table, column, ddl) in AddedColumns)
        {
            // A table this patch just created already has its columns; one that doesn't exist at
            // all would make ALTER TABLE throw rather than no-op.
            if (!await TableExistsAsync(db, table, ct)) continue;
            if (await ColumnExistsAsync(db, table, column, ct)) continue;

            await db.Database.ExecuteSqlRawAsync(ddl, ct);

            log.LogWarning(
                "Added the {Table}.{Column} column to an existing database. This is the " +
                "no-migrations stopgap in SchemaPatch, not a migration — see the comment there.",
                table, column);
        }

        // After the columns, since an index is on one of them.
        foreach (var (table, name, ddl) in AddedIndexes)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;

            await db.Database.ExecuteSqlRawAsync(ddl, ct);
            log.LogDebug("Ensured the {Index} index exists", name);
        }
    }

    private static async Task<bool> TableExistsAsync(MootifyDbContext db, string table, CancellationToken ct)
    {
        // sqlite_master rather than a probe query: a failed SELECT would already have logged an
        // error by the time we caught it.
        var found = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = {table}")
            .ToListAsync(ct);

        return found.Count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        MootifyDbContext db, string table, string column, CancellationToken ct)
    {
        var found = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table}) WHERE name = {column}")
            .ToListAsync(ct);

        return found.Count > 0;
    }
}
