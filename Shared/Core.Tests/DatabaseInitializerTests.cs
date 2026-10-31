using ComputerUse.TimeTracking.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public void FreshDatabase_IsMigratedToCurrentVersion_InWalMode()
    {
        using var workspace = new TempWorkspace();

        Assert.Equal(DatabaseInitializer.CurrentVersion, DatabaseInitializer.GetSchemaVersion(workspace.DatabasePath));
        Assert.Equal(5, DatabaseInitializer.CurrentVersion);
        using SqliteConnection connection = TimeTrackerDatabase.Open(workspace.DatabasePath);
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('TimeBlocks') WHERE name = 'AppDisplayName';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ReportRuns';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'LocalMatters';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('TimeBlocks') WHERE name = 'EmailConversationKey';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_TimeBlocks_StartTime_EndTime';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('TimeBlocks') WHERE name = 'ClientBlockId';"));
    }

    [Fact]
    public void Version1Database_IsUpgraded_AndKeepsRows()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        CreateVersion1Database(workspace.DatabasePath);

        DatabaseInitializer.Initialize(workspace.DatabasePath);

        Assert.Equal(5L, DatabaseInitializer.GetSchemaVersion(workspace.DatabasePath));
        using SqliteConnection connection = TimeTrackerDatabase.Open(workspace.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM TimeBlocks;"));
        Assert.Equal("Brief.docx", Scalar(connection, "SELECT WindowTitle FROM TimeBlocks;"));
        Assert.Equal(DBNull.Value, Scalar(connection, "SELECT AppDisplayName FROM TimeBlocks;"));
    }

    [Fact]
    public void RunningTwice_IsANoOp()
    {
        using var workspace = new TempWorkspace();
        workspace.Repository.SaveTimeBlock(TestBlocks.Block(new DateTime(2026, 10, 20, 9, 0, 0), TimeSpan.FromMinutes(5), "WINWORD", "A"));

        DatabaseInitializer.Initialize(workspace.DatabasePath);

        Assert.Equal(5L, DatabaseInitializer.GetSchemaVersion(workspace.DatabasePath));
        using SqliteConnection connection = TimeTrackerDatabase.Open(workspace.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM TimeBlocks;"));
    }

    [Fact]
    public void NewerDatabase_IsRejected()
    {
        using var workspace = new TempWorkspace();
        using (SqliteConnection connection = TimeTrackerDatabase.Open(workspace.DatabasePath))
        {
            Scalar(connection, "PRAGMA user_version = 99;");
        }

        Assert.Throws<InvalidOperationException>(() => DatabaseInitializer.Initialize(workspace.DatabasePath));
    }

    // The schema and a row exactly as the pre-migration app (user_version 0) left them.
    private static void CreateVersion1Database(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE TimeBlocks (
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
            CREATE TABLE DailyActivitySummaries (
                ActivityDate TEXT NOT NULL, MatterId TEXT NOT NULL, ActivityCategory TEXT NOT NULL,
                ActivityTitle TEXT NOT NULL, RawSeconds INTEGER NOT NULL, ItemCount INTEGER NOT NULL,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (ActivityDate, MatterId, ActivityCategory, ActivityTitle));
            INSERT INTO TimeBlocks (StartTime, EndTime, DurationSeconds, ProcessName, WindowTitle, ActivityCategory, Status)
            VALUES ('2026-10-19T14:00:00.0000000Z', '2026-10-19T14:10:00.0000000Z', 600, 'WINWORD', 'Brief.docx', 'Document', 'Unassigned');
        ";
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
