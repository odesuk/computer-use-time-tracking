using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Storage;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class AppUsageReportBuilderTests
{
    private static readonly DateOnly Day = new(2026, 10, 20);
    private static readonly DateTime Noon = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TitlesBeyondTheLimit_FoldIntoOther()
    {
        var rows = Enumerable.Range(1, 8)
            .Select(i => Row("WINWORD", $"Doc {i}.docx", seconds: 100 * i))
            .ToList();

        AppUsage app = Assert.Single(Build(rows, new AppUsageReportOptions { MaxTitlesPerApp = 5 }).Apps);

        Assert.Equal(new[] { "Doc 8.docx", "Doc 7.docx", "Doc 6.docx", "Doc 5.docx", "Doc 4.docx" }, app.TopTitles.Select(t => t.Title));
        Assert.Equal(3, app.OtherTitlesCount);
        Assert.Equal(600, app.OtherTitlesSeconds);
        Assert.Equal(3600, app.Seconds);
    }

    [Fact]
    public void RawTitlesFromOlderRows_MergeAfterNormalisation()
    {
        AppUsage app = Assert.Single(Build(new[]
        {
            Row("chrome", "(1) Inbox - Gmail - Google Chrome", 60),
            Row("chrome", "(7) Inbox - Gmail - Google Chrome", 60),
            Row("chrome", "Inbox - Gmail", 60),
        }).Apps);

        TitleUsage title = Assert.Single(app.TopTitles);
        Assert.Equal(("Inbox - Gmail", 180L), (title.Title, title.Seconds));
    }

    [Fact]
    public void SmallApps_FoldIntoOther_WhenAtLeastTwo()
    {
        DailyAppUsageReport report = Build(new[]
        {
            Row("WINWORD", "A", 3 * 3600),
            Row("chrome", "B", 3600),
            Row("calc", "C", 60),       // under 2 minutes
            Row("notepad", "D", 90),    // under 2 minutes
        });

        Assert.Equal(new[] { "WINWORD", "chrome" }, report.Apps.Select(app => app.ProcessName));
        Assert.NotNull(report.OtherApps);
        Assert.Equal(2, report.OtherApps.Count);
        Assert.Equal(150, report.OtherApps.Seconds);
        Assert.Equal(4, report.AppCount);
        Assert.Equal(1.0, report.Apps.Sum(app => app.Share) + report.OtherApps.Share, precision: 6);
    }

    [Fact]
    public void SingleSmallApp_IsNotFolded()
    {
        DailyAppUsageReport report = Build(new[] { Row("WINWORD", "A", 3600), Row("calc", "C", 60) });

        Assert.Equal(2, report.Apps.Count);
        Assert.Null(report.OtherApps);
    }

    [Fact]
    public void TitlesOff_LeavesAppTotalsOnly()
    {
        DailyAppUsageReport report = Build(new[] { Row("WINWORD", "Secret.docx", 600) },
            new AppUsageReportOptions { IncludeTitles = false });

        AppUsage app = Assert.Single(report.Apps);
        Assert.Empty(app.TopTitles);
        Assert.Equal(0, app.OtherTitlesCount);
        Assert.False(report.IncludesTitles);
    }

    [Fact]
    public void DisplayName_FallsBackToOverridesThenProcessName()
    {
        DailyAppUsageReport report = Build(new[]
        {
            Row("olk", "Inbox", 600),
            Row("customapp", "Thing", 500),
            Row("WINWORD", "A", 400, displayName: "Microsoft Word for Microsoft 365"),
        });

        Assert.Equal(new[] { "Outlook (new)", "customapp", "Microsoft Word" }, report.Apps.Select(app => app.DisplayName));
    }

    [Fact]
    public void UntitledTime_GetsALabel()
    {
        AppUsage app = Assert.Single(Build(new[] { Row("explorer", "", 600) }).Apps);
        Assert.Equal("(untitled window)", Assert.Single(app.TopTitles).Title);
    }

    private static DailyAppUsageReport Build(IEnumerable<AppTitleUsageRow> rows, AppUsageReportOptions? options = null) =>
        AppUsageReportBuilder.Build(Day, TimeZoneInfo.Utc, new DateTimeOffset(Noon), isFinal: false, rows, options);

    private static AppTitleUsageRow Row(string process, string title, long seconds, string? displayName = null) =>
        new(process, displayName, title, seconds, Noon, Noon.AddSeconds(seconds), 1);
}
