using ComputerUse.TimeTracking.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComputerUse.TimeTracking.Reports;

/// <summary>
/// The only entry point for writing reports. Serialises concurrent requests (tray, scheduler and
/// shutdown can collide), includes the tracker's live block for today, and records each run.
/// </summary>
public sealed class ReportCoordinator
{
    /// <summary>How far back start-up catch-up looks for days without a final report.</summary>
    public const int CatchUpDays = 60;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IActivityCheckpoint _tracker;
    private readonly IDailyReportWriter _writer;
    private readonly IReportRunStore _runs;
    private readonly IActivityCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReportCoordinator> _logger;

    public ReportCoordinator(
        IActivityCheckpoint tracker,
        IDailyReportWriter writer,
        IReportRunStore runs,
        IActivityCalendar calendar,
        TimeProvider timeProvider,
        ILogger<ReportCoordinator>? logger = null)
    {
        _tracker = tracker;
        _writer = writer;
        _runs = runs;
        _calendar = calendar;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<ReportCoordinator>.Instance;
    }

    /// <summary>Raised after each successful write. May run on any thread.</summary>
    public event EventHandler<ReportResult>? ReportWritten;

    public DateOnly Today => LocalDay.Today(_timeProvider);

    public async Task<ReportResult> WriteAsync(DateOnly date, ReportTrigger trigger, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ReportResult result;
        try
        {
            // Always checkpoint: a block that started before midnight belongs partly to yesterday,
            // and yesterday's final report is written only once. Splitting at the last input
            // never double-counts.
            _tracker.Checkpoint();
            DateOnly today = Today;

            bool isFinal = date < today;
            DateTimeOffset generatedAt = _timeProvider.GetLocalNow();
            string pdfPath = await _writer.WriteAsync(date, isFinal, generatedAt, cancellationToken).ConfigureAwait(false);
            _runs.Record(date, trigger, generatedAt, isFinal, pdfPath);
            result = new ReportResult(date, pdfPath, isFinal, trigger);
            _logger.LogInformation("Wrote {Kind} report for {Date} ({Trigger}).", isFinal ? "final" : "partial", date, trigger);
        }
        finally
        {
            _gate.Release();
        }

        ReportWritten?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// Writes a final report for each of the last <see cref="CatchUpDays"/> days that has activity
    /// and no final report yet (the PC was off at midnight, or the app was closed). One failed day
    /// does not stop the others.
    /// </summary>
    public async Task<IReadOnlyList<ReportResult>> CatchUpAsync(ReportTrigger trigger, CancellationToken cancellationToken)
    {
        var written = new List<ReportResult>();
        DateOnly today = Today;
        for (DateOnly date = today.AddDays(-CatchUpDays); date < today; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_runs.HasFinal(date) || !_calendar.HasActivity(date)) continue;
                written.Add(await WriteAsync(date, trigger, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Catch-up report for {Date} failed.", date);
            }
        }

        return written;
    }

    public async Task<int> RunRetentionAsync(Func<int> purge, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return purge(); }
        finally { _gate.Release(); }
    }

    /// <summary>Path of the newest report for a day, if one was written and still exists.</summary>
    public string? FindExistingReport(DateOnly date)
    {
        string? path = _runs.GetLatestPdfPath(date);
        return path != null && File.Exists(path) ? path : null;
    }
}
