using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class ReportCoordinatorTests
{
    private static readonly TimeZoneInfo Zone = TestBlocks.NewYork;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 18, 30, 0, TimeSpan.Zero), Zone);
    private readonly CountingCheckpoint _tracker = new();
    private readonly FakeWriter _writer = new();
    private readonly InMemoryRuns _runs = new();
    private readonly FakeCalendar _calendar = new();

    private ReportCoordinator Coordinator() => new(_tracker, _writer, _runs, _calendar, _clock);

    private DateOnly Today => LocalDay.Today(_clock);

    [Fact]
    public async Task Today_IsPartial_AndCheckpointsOnce()
    {
        ReportResult result = await Coordinator().WriteAsync(Today, ReportTrigger.OnDemand, CancellationToken.None);

        Assert.False(result.IsFinal);
        Assert.Equal(1, _tracker.Count);
        Assert.True(_runs.HasRun(Today, ReportTrigger.OnDemand));
        Assert.False(_runs.HasFinal(Today));
    }

    [Fact]
    public async Task PastDate_IsFinal_AndStillCheckpoints()
    {
        // A block still running across midnight must reach yesterday's final report.
        ReportResult result = await Coordinator().WriteAsync(Today.AddDays(-1), ReportTrigger.DayRollover, CancellationToken.None);

        Assert.True(result.IsFinal);
        Assert.Equal(1, _tracker.Count);
        Assert.True(_runs.HasFinal(Today.AddDays(-1)));
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerialised()
    {
        _writer.Delay = TimeSpan.FromMilliseconds(30);
        ReportCoordinator coordinator = Coordinator();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => coordinator.WriteAsync(Today, ReportTrigger.OnDemand, CancellationToken.None))));

        Assert.Equal(8, _writer.Calls);
        Assert.Equal(1, _writer.MaxConcurrency);
    }

    [Fact]
    public async Task CatchUp_WritesDaysWithActivityAndNoFinal_AndContinuesAfterFailure()
    {
        DateOnly alreadyFinal = Today.AddDays(-1);
        DateOnly failing = Today.AddDays(-2);
        DateOnly missing = Today.AddDays(-3);
        DateOnly tooOld = Today.AddDays(-(ReportCoordinator.CatchUpDays + 1));
        _calendar.Days.UnionWith(new[] { alreadyFinal, failing, missing, tooOld, Today });
        _runs.Record(alreadyFinal, ReportTrigger.OnClose, _clock.GetLocalNow(), isFinal: true, "x.pdf");
        _writer.FailFor.Add(failing);

        IReadOnlyList<ReportResult> written = await Coordinator().CatchUpAsync(ReportTrigger.CatchUp, CancellationToken.None);

        ReportResult result = Assert.Single(written);
        Assert.Equal(missing, result.Date);
        Assert.True(result.IsFinal);
        Assert.Equal(ReportTrigger.CatchUp, result.Trigger);
        Assert.DoesNotContain(_writer.Dates, date => date == tooOld || date == Today || date == alreadyFinal);
    }

    [Fact]
    public async Task ReportWritten_IsRaised()
    {
        ReportCoordinator coordinator = Coordinator();
        ReportResult? raised = null;
        coordinator.ReportWritten += (_, result) => raised = result;

        await coordinator.WriteAsync(Today, ReportTrigger.OnClose, CancellationToken.None);

        Assert.Equal(ReportTrigger.OnClose, raised?.Trigger);
    }

    [Fact]
    public void Today_UsesLocalTimeZone()
    {
        // 02:30 UTC on Oct 21 is still Oct 20 in New York.
        _clock.Set(new DateTimeOffset(2026, 10, 21, 2, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 10, 20), Coordinator().Today);
    }

    [Theory]
    [InlineData("17:59", "18:00", false, false)]
    [InlineData("18:00", "18:00", false, true)]
    [InlineData("19:00", "18:00", false, true)]   // started after the due time: write now
    [InlineData("19:00", "18:00", true, false)]   // already ran today
    [InlineData("00:05", "18:00", false, false)]
    public void ShouldRunEndOfDay(string now, string due, bool alreadyRan, bool expected)
    {
        DateTime localNow = new DateOnly(2026, 10, 20).ToDateTime(TimeOnly.Parse(now));
        Assert.Equal(expected, ReportScheduler.ShouldRunEndOfDay(localNow, TimeOnly.Parse(due), alreadyRan));
    }

    private sealed class CountingCheckpoint : IActivityCheckpoint
    {
        public int Count;
        public void Checkpoint() => Interlocked.Increment(ref Count);
    }

    private sealed class FakeCalendar : IActivityCalendar
    {
        public HashSet<DateOnly> Days { get; } = new();
        public bool HasActivity(DateOnly date) => Days.Contains(date);
    }

    private sealed class FakeWriter : IDailyReportWriter
    {
        private int _active;
        public TimeSpan Delay { get; set; }
        public int Calls;
        public int MaxConcurrency;
        public HashSet<DateOnly> FailFor { get; } = new();
        public List<DateOnly> Dates { get; } = new();

        public async Task<string> WriteAsync(DateOnly date, bool isFinal, DateTimeOffset generatedAt, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            MaxConcurrency = Math.Max(MaxConcurrency, active);
            try
            {
                Interlocked.Increment(ref Calls);
                lock (Dates) Dates.Add(date);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                if (FailFor.Contains(date)) throw new IOException("disk full");
                return $"{date:yyyy-MM-dd}.pdf";
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class InMemoryRuns : IReportRunStore
    {
        private readonly List<(DateOnly Date, ReportTrigger Trigger, bool IsFinal, string Path)> _runs = new();

        public void Record(DateOnly date, ReportTrigger trigger, DateTimeOffset generatedAt, bool isFinal, string pdfPath)
        {
            lock (_runs) _runs.Add((date, trigger, isFinal, pdfPath));
        }

        public bool HasFinal(DateOnly date) => _runs.Any(run => run.Date == date && run.IsFinal);
        public bool HasRun(DateOnly date, ReportTrigger trigger) => _runs.Any(run => run.Date == date && run.Trigger == trigger);
        public string? GetLatestPdfPath(DateOnly date) => _runs.LastOrDefault(run => run.Date == date).Path;
    }
}
