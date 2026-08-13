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
/// So: additive, idempotent DDL for tables added after the fact, run at boot. It is deliberately
/// not a migration system — there is no version table, no down path, and no support for changing
/// an existing column. <b>The next schema change that isn't a brand-new table needs real EF
/// migrations, and this file should go away when they arrive.</b>
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
    ];

    public static async Task ApplyAsync(MootifyDbContext db, ILogger log, CancellationToken ct = default)
    {
        foreach (var (table, statements) in AddedTables)
        {
            if (await ExistsAsync(db, table, ct)) continue;

            foreach (var statement in statements)
            {
                await db.Database.ExecuteSqlRawAsync(statement, ct);
            }

            log.LogWarning(
                "Added the {Table} table to an existing database. This is the no-migrations " +
                "stopgap in SchemaPatch, not a migration — see the comment there.", table);
        }
    }

    private static async Task<bool> ExistsAsync(MootifyDbContext db, string table, CancellationToken ct)
    {
        // sqlite_master rather than a probe query: a failed SELECT would already have logged an
        // error by the time we caught it.
        var found = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = {table}")
            .ToListAsync(ct);

        return found.Count > 0;
    }
}
