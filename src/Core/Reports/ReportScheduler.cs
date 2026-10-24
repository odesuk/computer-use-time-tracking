using ComputerUse.TimeTracking.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ComputerUse.TimeTracking.Reports;

/// <summary>
/// Catch-up at start, end-of-day report, day rollover, and daily retention. Ticks once a minute
/// against wall-clock time instead of sleeping until a due time: long delays drift across sleep,
/// hibernate, and clock or time-zone changes.
/// </summary>
public sealed class ReportScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly ReportCoordinator _reports;
    private readonly IReportRunStore _runs;
    private readonly IActivityCalendar _calendar;
    private readonly SettingsService _settings;
    private readonly RetentionService _retention;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReportScheduler> _logger;

    private DateOnly? _lastSeen;
    private DateOnly? _lastRetention;

    public ReportScheduler(
        ReportCoordinator reports,
        IReportRunStore runs,
        IActivityCalendar calendar,
        SettingsService settings,
        RetentionService retention,
        TimeProvider timeProvider,
        ILogger<ReportScheduler> logger)
    {
        _reports = reports;
        _runs = runs;
        _calendar = calendar;
        _settings = settings;
        _retention = retention;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// True once the configured time has passed today and no end-of-day report was written yet.
    /// Starting the app after the due time writes it straight away; the next day's rollover then
    /// replaces it with the final report.
    /// </summary>
    public static bool ShouldRunEndOfDay(DateTime localNow, TimeOnly dueTime, bool alreadyRan) =>
        !alreadyRan && TimeOnly.FromDateTime(localNow) >= dueTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _timeProvider);
        try
        {
            do
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTime now = _timeProvider.GetLocalNow().DateTime;
            DateOnly today = DateOnly.FromDateTime(now);

            if (_lastSeen != today)
            {
                // First tick after start, or midnight passed (possibly after days asleep).
                ReportTrigger trigger = _lastSeen is null ? ReportTrigger.CatchUp : ReportTrigger.DayRollover;
                await _reports.CatchUpAsync(trigger, cancellationToken).ConfigureAwait(false);
                _lastSeen = today;
            }

            // After catch-up, so days that still need a final report are not purged first.
            if (_lastRetention != today)
            {
                if (await RunRetentionAsync(cancellationToken).ConfigureAwait(false))
                {
                    _lastRetention = today;
                }
            }

            // No PDF for a day without activity (weekend, PC left on but unused).
            if (ShouldRunEndOfDay(now, _settings.Current.EndOfDayReportTime, _runs.HasRun(today, ReportTrigger.EndOfDay)) &&
                _calendar.HasActivity(today))
            {
                await _reports.WriteAsync(today, ReportTrigger.EndOfDay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled report failed.");
        }
    }

    private async Task<bool> RunRetentionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _reports.RunRetentionAsync(_retention.Purge, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention purge failed.");
            return false;
        }
    }
}
