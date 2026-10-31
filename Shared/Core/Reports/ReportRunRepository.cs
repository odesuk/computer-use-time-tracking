using System.Globalization;
using ComputerUse.TimeTracking.Storage;

namespace ComputerUse.TimeTracking.Reports;

public interface IReportRunStore
{
    void Record(DateOnly date, ReportTrigger trigger, DateTimeOffset generatedAt, bool isFinal, string pdfPath);

    bool HasFinal(DateOnly date);

    bool HasRun(DateOnly date, ReportTrigger trigger);

    /// <summary>The most recent report for a day, or null.</summary>
    string? GetLatestPdfPath(DateOnly date);
}

/// <summary>Records each report run in the <c>ReportRuns</c> table (schema v2).</summary>
public sealed class ReportRunRepository : IReportRunStore
{
    private readonly string? _databasePath;

    public ReportRunRepository()
    {
    }

    public ReportRunRepository(string? databasePath)
    {
        _databasePath = databasePath;
    }

    public void Record(DateOnly date, ReportTrigger trigger, DateTimeOffset generatedAt, bool isFinal, string pdfPath)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ReportRuns (ActivityDate, Trigger, GeneratedAt, IsFinal, PdfPath)
            VALUES ($date, $trigger, $generatedAt, $isFinal, $pdfPath)
            ON CONFLICT(ActivityDate, Trigger) DO UPDATE SET
                GeneratedAt = excluded.GeneratedAt,
                IsFinal = excluded.IsFinal,
                PdfPath = excluded.PdfPath;
        ";
        cmd.Parameters.AddWithValue("$date", FormatDate(date));
        cmd.Parameters.AddWithValue("$trigger", trigger.ToString());
        cmd.Parameters.AddWithValue("$generatedAt", generatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$isFinal", isFinal ? 1 : 0);
        cmd.Parameters.AddWithValue("$pdfPath", pdfPath);
        cmd.ExecuteNonQuery();
    }

    public bool HasFinal(DateOnly date)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM ReportRuns WHERE ActivityDate = $date AND IsFinal = 1);";
        cmd.Parameters.AddWithValue("$date", FormatDate(date));
        return (long)(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    public bool HasRun(DateOnly date, ReportTrigger trigger)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM ReportRuns WHERE ActivityDate = $date AND Trigger = $trigger);";
        cmd.Parameters.AddWithValue("$date", FormatDate(date));
        cmd.Parameters.AddWithValue("$trigger", trigger.ToString());
        return (long)(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    public string? GetLatestPdfPath(DateOnly date)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PdfPath FROM ReportRuns WHERE ActivityDate = $date ORDER BY GeneratedAt DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$date", FormatDate(date));
        return cmd.ExecuteScalar() as string;
    }

    public int PurgeBefore(DateTime cutoffUtc)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ReportRuns WHERE GeneratedAt < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        return cmd.ExecuteNonQuery();
    }

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
