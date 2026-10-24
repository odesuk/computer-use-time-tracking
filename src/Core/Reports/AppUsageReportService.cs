using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;

namespace ComputerUse.TimeTracking.Reports;

/// <summary>Which local days have recorded activity.</summary>
public interface IActivityCalendar
{
    bool HasActivity(DateOnly date);
}

/// <summary>Builds the Phase 1 app-usage report for a local day from <see cref="TimeRepository"/>.</summary>
public sealed class AppUsageReportService : IActivityCalendar
{
    // Old databases may hold lock-screen blocks recorded before it was excluded at capture.
    private static readonly string[] AlwaysExcluded = { "LockApp" };

    private readonly TimeRepository _repository;
    private readonly SettingsService _settings;
    private readonly TimeProvider _timeProvider;

    public AppUsageReportService(TimeRepository repository, SettingsService settings, TimeProvider timeProvider)
    {
        _repository = repository;
        _settings = settings;
        _timeProvider = timeProvider;
    }

    public DailyAppUsageReport Create(DateOnly date, DateTimeOffset generatedAt, bool isFinal)
    {
        TimeZoneInfo timeZone = _timeProvider.LocalTimeZone;
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(date, timeZone);
        ClientSetupSettings settings = _settings.Current;

        var options = new AppUsageReportOptions
        {
            IncludeTitles = settings.IncludeWindowTitlesInPdf,
            ExcludedProcessNames = settings.ExcludedProcessNames.Concat(AlwaysExcluded).ToList(),
        };

        return AppUsageReportBuilder.Build(date, timeZone, generatedAt, isFinal,
            _repository.GetAppTitleUsage(start, end), options);
    }

    public bool HasActivity(DateOnly date)
    {
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(date, _timeProvider.LocalTimeZone);
        return _repository.HasActivity(start, end);
    }

    /// <summary>Saved active seconds for a local day (excludes the tracker's live block).</summary>
    public long GetSavedActiveSeconds(DateOnly date)
    {
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(date, _timeProvider.LocalTimeZone);
        return _repository.GetActiveSeconds(start, end);
    }
}
