using Microsoft.Data.Sqlite;

namespace ComputerUse.TimeTracking.Storage;

/// <summary>The one place that builds the SQLite connection string.</summary>
public static class TimeTrackerDatabase
{
    public static string ConnectionString(string? databasePath = null) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath ?? AppPaths.Database),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        // Seconds to retry SQLITE_BUSY before failing a command.
        DefaultTimeout = 10,
    }.ToString();

    public static SqliteConnection Open(string? databasePath = null)
    {
        var connection = new SqliteConnection(ConnectionString(databasePath));
        connection.Open();
        return connection;
    }
}
