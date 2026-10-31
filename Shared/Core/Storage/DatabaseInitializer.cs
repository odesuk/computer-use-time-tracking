using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace ComputerUse.TimeTracking.Storage;

/// <summary>
/// Creates and upgrades the local database. Migrations are versioned with <c>PRAGMA user_version</c>
/// (stored in the database header), so each one runs exactly once per database.
/// </summary>
public static class DatabaseInitializer
{
    // Version 1 is the original CREATE TABLE IF NOT EXISTS script. Databases created before
    // migrations existed have user_version = 0 and these tables; re-running it is a no-op.
    private const string V1Sql = @"
        CREATE TABLE IF NOT EXISTS TimeBlocks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StartTime DATETIME NOT NULL,
            EndTime DATETIME NOT NULL,
            DurationSeconds INTEGER NOT NULL,
            ProcessName TEXT NOT NULL,
            WindowTitle TEXT NOT NULL,
            EmailSubject TEXT NULL,
            EmailSender TEXT NULL,
            EmailRecipients TEXT NULL,
            ActivityCategory TEXT DEFAULT 'General',
            MatchedMatterId TEXT NULL,
            Status TEXT DEFAULT 'Unassigned'
        );

        CREATE TABLE IF NOT EXISTS MatterRules (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MatterId TEXT NOT NULL,
            MatterName TEXT NOT NULL,
            PatternType TEXT NOT NULL,
            Pattern TEXT NOT NULL,
            Priority INTEGER DEFAULT 10
        );

        CREATE TABLE IF NOT EXISTS PendingTimeEntries (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MatterId TEXT NOT NULL,
            ActivityDate DATE NOT NULL,
            DurationHours DECIMAL(4,2) NOT NULL,
            RawSeconds INTEGER NOT NULL,
            SuggestedNarrative TEXT NOT NULL,
            EditedNarrative TEXT NULL,
            IsApproved BOOLEAN DEFAULT 0,
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS DailyActivitySummaries (
            ActivityDate TEXT NOT NULL,
            MatterId TEXT NOT NULL,
            ActivityCategory TEXT NOT NULL,
            ActivityTitle TEXT NOT NULL,
            RawSeconds INTEGER NOT NULL,
            ItemCount INTEGER NOT NULL,
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (ActivityDate, MatterId, ActivityCategory, ActivityTitle)
        );

        CREATE INDEX IF NOT EXISTS IX_TimeBlocks_Status_StartTime
            ON TimeBlocks (Status, StartTime);
    ";

    // Version 2: friendly app names, an index for the daily date-range queries, and a record of
    // report runs (which days have a final report).
    private const string V2Sql = @"
        ALTER TABLE TimeBlocks ADD COLUMN AppDisplayName TEXT NULL;

        CREATE INDEX IF NOT EXISTS IX_TimeBlocks_StartTime_EndTime
            ON TimeBlocks (StartTime, EndTime);

        CREATE TABLE IF NOT EXISTS ReportRuns (
            ActivityDate TEXT NOT NULL,
            Trigger      TEXT NOT NULL,
            GeneratedAt  TEXT NOT NULL,
            IsFinal      INTEGER NOT NULL,
            PdfPath      TEXT NOT NULL,
            PRIMARY KEY (ActivityDate, Trigger)
        );
    ";

    private const string V3Sql = @"
        CREATE INDEX IF NOT EXISTS IX_TimeBlocks_EndTime_StartTime
            ON TimeBlocks (EndTime, StartTime);
        CREATE INDEX IF NOT EXISTS IX_ReportRuns_GeneratedAt
            ON ReportRuns (GeneratedAt);
    ";

    private const string V4Sql = @"
        CREATE TABLE IF NOT EXISTS LocalMatters (
            MatterId TEXT PRIMARY KEY,
            MatterName TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS EmailConversationAssignments (
            ConversationKey TEXT PRIMARY KEY,
            MatterId TEXT NOT NULL,
            AssignedAt TEXT NOT NULL
        );
        UPDATE TimeBlocks
        SET EmailConversationKey = 'email-v1:legacy:' || Id
        WHERE ActivityCategory = 'Email' AND EmailConversationKey IS NULL;
        CREATE INDEX IF NOT EXISTS IX_TimeBlocks_EmailConversationKey
            ON TimeBlocks (EmailConversationKey);
    ";
// Version 5: a client-generated identity makes replay from the durable pending-file idempotent.
private const string V5Sql = @"
ALTER TABLE TimeBlocks ADD COLUMN ClientBlockId TEXT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_TimeBlocks_ClientBlockId
ON TimeBlocks (ClientBlockId);
";

    private static readonly (int Version, string Sql)[] Migrations =
    {
        (1, V1Sql),
        (2, V2Sql),
        (3, V3Sql),
        (4, V4Sql),
(5, V5Sql),
    };

    public static int CurrentVersion => Migrations[^1].Version;

    public static void Initialize(string? databasePath = null)
    {
        string path = Path.GetFullPath(databasePath ?? AppPaths.Database);
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using SqliteConnection connection = TimeTrackerDatabase.Open(path);

        // WAL is persistent: report readers no longer block the tracker's writes.
        Execute(connection, "PRAGMA journal_mode = WAL;");

        long current = GetSchemaVersion(connection);
        if (current > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The activity database is schema version {current}, newer than this app supports ({CurrentVersion}). Install the latest {ProductContext.Current.DisplayName}.");
        }

        foreach ((int version, string sql) in Migrations.Where(migration => migration.Version > current))
        {
            // BEGIN IMMEDIATE takes the write lock; re-read the version inside it, because another
            // process may have just applied it.
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            if (GetSchemaVersion(connection, transaction) >= version) continue;
            if (version == 4)
            {
                ApplyV4Migration(connection, transaction);
            }
            else
            {
                Execute(connection, sql, transaction);
            }
            Execute(connection, $"PRAGMA user_version = {version};", transaction);
            transaction.Commit();
        }
    }

    public static long GetSchemaVersion(string? databasePath = null)
    {
        using SqliteConnection connection = TimeTrackerDatabase.Open(databasePath);
        return GetSchemaVersion(connection);
    }

    private static long GetSchemaVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ApplyV4Migration(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "EmailConversationKey"))
        {
            Execute(connection, "ALTER TABLE TimeBlocks ADD COLUMN EmailConversationKey TEXT NULL;", transaction);
        }

        if (!HasColumn(connection, transaction, "EmailJunkStatus"))
        {
            Execute(connection, "ALTER TABLE TimeBlocks ADD COLUMN EmailJunkStatus INTEGER NOT NULL DEFAULT 0;", transaction);
        }

        Execute(connection, V4Sql, transaction);
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string columnName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TimeBlocks') WHERE name = $columnName COLLATE NOCASE;";
        command.Parameters.AddWithValue("$columnName", columnName);
        return (long)(command.ExecuteScalar() ?? 0L) != 0;
    }
}
