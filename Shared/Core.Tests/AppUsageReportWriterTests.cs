using System.Text;
using System.Text.Json;
using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class AppUsageReportWriterTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero), TestBlocks.NewYork);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task LargeDay_ProducesMultiPagePdf_AndJsonSidecar()
    {
        // 60 apps x 5 titles, with long titles that must wrap.
        DateTime start = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        for (int app = 0; app < 60; app++)
        {
            for (int title = 0; title < 5; title++)
            {
                string longTitle = $"Smith v. Jones - Motion to Dismiss draft {title} for matter {app} with a long title that needs wrapping across the page width - https://example.com/a/very/long/path/without/spaces/{app}/{title}";
                _workspace.Repository.SaveTimeBlock(TestBlocks.Block(start, TimeSpan.FromSeconds(200 + (app * 7) + title), $"app{app:00}", longTitle));
                start = start.AddMinutes(1);
            }
        }

        SettingsService settings = _workspace.CreateSettings();
        AppUsageReportWriter writer = CreateWriter(settings);
        DateOnly day = new(2026, 10, 20);

        string pdfPath = await writer.WriteAsync(day, isFinal: false, _clock.GetLocalNow(), CancellationToken.None);

        Assert.Equal(Path.Combine(_workspace.ReportDirectory, "Computer Use Activity 2026-10-20.pdf"), pdfPath);
        string pdf = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdfPath));
        Assert.StartsWith("%PDF-", pdf);
        Assert.True(CountPages(pdf) > 2, $"expected several pages, got {CountPages(pdf)}");

        string jsonPath = Path.Combine(_workspace.JsonDirectory, "2026-10-20.json");
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal("2026-10-20", json.RootElement.GetProperty("date").GetString());
        Assert.False(json.RootElement.GetProperty("isFinal").GetBoolean());
        Assert.Equal(60, json.RootElement.GetProperty("appCount").GetInt32());
        string csv = await File.ReadAllTextAsync(Path.Combine(_workspace.JsonDirectory, "2026-10-20.csv"));
        Assert.StartsWith("Date,ProcessName,DisplayName,Title,Seconds,FirstSeenUtc,LastSeenUtc,ItemCount", csv);
        Assert.Contains("app00", csv);
    }

    [Fact]
    public async Task DetailedCsv_ExcludesConfiguredAndLockScreenProcesses()
    {
        DateTime start = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        _workspace.Repository.SaveTimeBlock(TestBlocks.Block(start, TimeSpan.FromMinutes(5), "WINWORD", "Brief"));
        _workspace.Repository.SaveTimeBlock(TestBlocks.Block(start.AddMinutes(6), TimeSpan.FromMinutes(5), "WhatsApp", "Personal"));
        _workspace.Repository.SaveTimeBlock(TestBlocks.Block(start.AddMinutes(12), TimeSpan.FromMinutes(5), "LockApp", "Lock screen"));

        SettingsService settings = _workspace.CreateSettings(s => s.ExcludedProcessNames = new List<string> { "WhatsApp" });
        await CreateWriter(settings).WriteAsync(new DateOnly(2026, 10, 20), false, _clock.GetLocalNow(), CancellationToken.None);

        string csv = await File.ReadAllTextAsync(Path.Combine(_workspace.JsonDirectory, "2026-10-20.csv"));
        Assert.Contains("WINWORD", csv);
        Assert.DoesNotContain("WhatsApp", csv);
        Assert.DoesNotContain("LockApp", csv);
    }

    [Fact]
    public void DetailedCsv_PrefixesSpreadsheetFormulas()
    {
        string csv = AppUsageReportWriter.BuildCsv(new DateOnly(2026, 10, 20), new[]
        {
            new AppTitleUsageRow("excel", "Excel", "=1+1", 60,
                new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 20, 12, 1, 0, DateTimeKind.Utc), 1),
        });
        Assert.Contains(",'=1+1,", csv);
    }

    [Theory]
    [InlineData("2026-10-20.csv")]
    [InlineData("2026-10-20.csv.tmp")]
    [InlineData("2026-10-20.json")]
    public void Retention_RecognizesGeneratedReportSidecars(string name) =>
        Assert.True(RetentionService.IsGeneratedSidecarName(name));

    [Fact]
    public async Task EmptyDay_AndTitlesOff_StillRender()
    {
        AppUsageReportWriter writer = CreateWriter(_workspace.CreateSettings(s => s.IncludeWindowTitlesInPdf = false));

        string pdfPath = await writer.WriteAsync(new DateOnly(2026, 10, 19), isFinal: true, _clock.GetLocalNow(), CancellationToken.None);

        Assert.True(new FileInfo(pdfPath).Length > 1000);
    }

    [Fact]
    public void FallbackPath_IsTimeStamped_AndUnique()
    {
        string pdfPath = Path.Combine(_workspace.Directory, "Computer Use Activity 2026-10-20.pdf");
        var at = new DateTimeOffset(2026, 10, 20, 17, 46, 12, TimeSpan.Zero);

        string first = AppUsageReportWriter.FallbackPath(pdfPath, at);
        Assert.EndsWith("Computer Use Activity 2026-10-20 (17-46).pdf", first);

        File.WriteAllText(first, "taken");
        Assert.EndsWith("Computer Use Activity 2026-10-20 (17-46-12).pdf", AppUsageReportWriter.FallbackPath(pdfPath, at));
    }

    [Fact]
    public async Task Coordinator_RecordsRuns_InTheDatabase()
    {
        SettingsService settings = _workspace.CreateSettings();
        var service = new AppUsageReportService(_workspace.Repository, settings, _clock);
        var runs = new ReportRunRepository(_workspace.DatabasePath);
        var coordinator = new ReportCoordinator(new NoCheckpoint(), CreateWriter(settings), runs, service, _clock);
        DateOnly yesterday = coordinator.Today.AddDays(-1);

        ReportResult result = await coordinator.WriteAsync(yesterday, ReportTrigger.CatchUp, CancellationToken.None);

        Assert.True(runs.HasFinal(yesterday));
        Assert.True(runs.HasRun(yesterday, ReportTrigger.CatchUp));
        Assert.False(runs.HasRun(yesterday, ReportTrigger.EndOfDay));
        Assert.Equal(result.PdfPath, coordinator.FindExistingReport(yesterday));
        Assert.Null(coordinator.FindExistingReport(coordinator.Today));
    }

    [Fact]
    public void Retention_PurgesOldBlocksAndRuns()
    {
        var runs = new ReportRunRepository(_workspace.DatabasePath);
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime expired = now.AddDays(-(ClientSetupSettings.RawActivityRetentionLimitDays + 1));
        _workspace.Repository.SaveTimeBlock(TestBlocks.Block(expired, TimeSpan.FromMinutes(5), "WINWORD", "Old"));
        _workspace.Repository.SaveTimeBlock(TestBlocks.Block(now.AddDays(-2), TimeSpan.FromMinutes(5), "WINWORD", "Recent"));
        runs.Record(DateOnly.FromDateTime(expired), ReportTrigger.CatchUp, new DateTimeOffset(expired), true, "old.pdf");

        int purged = new RetentionService(_workspace.Repository, runs, _clock).Purge();

        Assert.Equal(1, purged);
        Assert.False(runs.HasFinal(DateOnly.FromDateTime(expired)));
        Assert.True(_workspace.Repository.HasActivity(now.AddDays(-3), now));
    }

    private AppUsageReportWriter CreateWriter(SettingsService settings) => new(
        new AppUsageReportService(_workspace.Repository, settings, _clock),
        new AppUsagePdfWriter(),
        settings,
        NullLogger<AppUsageReportWriter>.Instance,
        _workspace.JsonDirectory);

    private static int CountPages(string pdf) =>
        System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page[^s]").Count;

    private sealed class NoCheckpoint : IActivityCheckpoint
    {
        public void Checkpoint()
        {
        }
    }
}
