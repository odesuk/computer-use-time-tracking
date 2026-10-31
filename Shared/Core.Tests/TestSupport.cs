using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.Tracking;
using Microsoft.Data.Sqlite;

namespace ComputerUse.TimeTracking.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset now, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone ?? TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

internal sealed class FakeSampler : IActivitySampler
{
    public ActivitySample Current { get; set; } = ActivitySample.Empty;
    public TimeSpan Idle { get; set; } = TimeSpan.Zero;
    public int SampleCount { get; private set; }
    public TimeSpan GetIdleTime() => Idle;

    public ActivitySample Sample()
    {
        SampleCount++;
        return Current;
    }
}

internal sealed class RecordingStore : ITimeBlockStore
{
    public List<TimeBlockRecord> Blocks { get; } = new();
    public int FailuresRemaining { get; set; }

    public void SaveTimeBlock(TimeBlockRecord block)
    {
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new IOException("disk full");
        }

        Blocks.Add(block);
    }
}

/// <summary>A temp folder holding a migrated database and a settings file; deleted on dispose.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(bool initializeDatabase = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "computer-use-time-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DatabasePath = Path.Combine(Directory, "timekeeper.db");
        SettingsPath = Path.Combine(Directory, "client-settings.json");
        ReportDirectory = Path.Combine(Directory, "pdf");
        JsonDirectory = Path.Combine(Directory, "json");
        if (initializeDatabase) DatabaseInitializer.Initialize(DatabasePath);
    }

    public string Directory { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
    public string ReportDirectory { get; }
    public string JsonDirectory { get; }

    public TimeRepository Repository => new(DatabasePath);

    public SettingsService CreateSettings(Action<ClientSetupSettings>? configure = null)
    {
        var store = new ClientConfigurationStore(SettingsPath);
        var settings = new ClientSetupSettings { PdfReportDirectory = ReportDirectory };
        configure?.Invoke(settings);
        store.Save(settings);
        return new SettingsService(store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class TestBlocks
{
    public static TimeBlockRecord Block(
        DateTime startUtc,
        TimeSpan duration,
        string process,
        string title,
        string? emailSubject = null,
        string? displayName = null) => new()
    {
        StartTime = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
        EndTime = DateTime.SpecifyKind(startUtc + duration, DateTimeKind.Utc),
        DurationSeconds = (int)duration.TotalSeconds,
        ProcessName = process,
        AppDisplayName = displayName,
        WindowTitle = title,
        EmailSubject = emailSubject,
        ActivityCategory = emailSubject is null ? "Document" : "Email",
    };

    public static TimeZoneInfo NewYork => TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
}
