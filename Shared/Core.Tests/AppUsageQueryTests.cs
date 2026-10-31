using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Storage;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

/// <summary>Repository queries + builder against a real SQLite file, in a DST time zone.</summary>
public sealed class AppUsageQueryTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TestBlocks.NewYork;
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void BlocksInsideTheDay_AreSummedPerApp()
    {
        DateOnly day = new(2026, 10, 20);
        Save(Local(day, 9, 0), TimeSpan.FromMinutes(50), "WINWORD", "Brief.docx - Word", displayName: "Microsoft Word");
        Save(Local(day, 10, 0), TimeSpan.FromMinutes(10), "WINWORD", "● Brief.docx - Word");
        Save(Local(day, 11, 0), TimeSpan.FromMinutes(20), "chrome", "Docket - Google Chrome");

        DailyAppUsageReport report = Build(day);

        Assert.Equal(80 * 60, report.ActiveSeconds);
        Assert.Collection(report.Apps,
            word =>
            {
                Assert.Equal("Microsoft Word", word.DisplayName);
                Assert.Equal(60 * 60, word.Seconds);
                TitleUsage title = Assert.Single(word.TopTitles);
                Assert.Equal(("Brief.docx", 3600L), (title.Title, title.Seconds));
            },
            chrome => Assert.Equal(("Google Chrome", 1200L), (chrome.DisplayName, chrome.Seconds)));
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(report.FirstActivity!.Value.DateTime));
        Assert.Equal(new TimeOnly(11, 20), TimeOnly.FromDateTime(report.LastActivity!.Value.DateTime));
    }

    [Fact]
    public void BlockCrossingMidnight_IsSplitBetweenDays()
    {
        DateOnly day = new(2026, 10, 20);
        Save(Local(day, 23, 30), TimeSpan.FromHours(1), "WINWORD", "Late.docx");

        Assert.Equal(30 * 60, Build(day).ActiveSeconds);
        Assert.Equal(30 * 60, Build(day.AddDays(1)).ActiveSeconds);
    }

    [Fact]
    public void SpringForwardDay_Has23Hours_AndCountsLateEveningActivity()
    {
        DateOnly day = new(2026, 3, 8);
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(day, Zone);
        Assert.Equal(TimeSpan.FromHours(23), end - start);

        Save(Local(day, 23, 0), TimeSpan.FromMinutes(59), "WINWORD", "A");
        Save(Local(day, 1, 30), TimeSpan.FromHours(1), "WINWORD", "B");   // 01:30 EST -> 03:30 EDT

        Assert.Equal((59 + 60) * 60, Build(day).ActiveSeconds);
        Assert.Equal(0, Build(day.AddDays(1)).ActiveSeconds);
    }

    [Fact]
    public void FallBackDay_Has25Hours_AndCountsTheRepeatedHourOnce()
    {
        DateOnly day = new(2026, 11, 1);
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(day, Zone);
        Assert.Equal(TimeSpan.FromHours(25), end - start);

        // 00:30 EDT for three real hours spans the repeated 01:00-02:00 hour.
        Save(Local(day, 0, 30), TimeSpan.FromHours(3), "WINWORD", "A");
        Save(Local(day, 23, 30), TimeSpan.FromMinutes(29), "WINWORD", "B");

        Assert.Equal((180 + 29) * 60, Build(day).ActiveSeconds);
    }

    [Fact]
    public void ExcludedProcess_IsLeftOutOfTotals()
    {
        DateOnly day = new(2026, 10, 20);
        Save(Local(day, 9, 0), TimeSpan.FromMinutes(30), "WINWORD", "A");
        Save(Local(day, 10, 0), TimeSpan.FromMinutes(30), "WhatsApp", "Family");
        Save(Local(day, 11, 0), TimeSpan.FromMinutes(5), "LockApp", "Lock screen");

        DailyAppUsageReport report = Build(day, new AppUsageReportOptions { ExcludedProcessNames = new[] { "whatsapp", "LockApp" } });

        Assert.Equal(30 * 60, report.ActiveSeconds);
        Assert.Equal("WINWORD", Assert.Single(report.Apps).ProcessName);
    }

    [Fact]
    public void EmptyDay_HasNoApps()
    {
        DailyAppUsageReport report = Build(new DateOnly(2026, 10, 20));

        Assert.Equal(0, report.ActiveSeconds);
        Assert.Empty(report.Apps);
        Assert.Null(report.OtherApps);
        Assert.Null(report.FirstActivity);
    }

    [Fact]
    public void EmailSubject_IsUsedAsTitle()
    {
        DateOnly day = new(2026, 10, 20);
        Save(Local(day, 9, 0), TimeSpan.FromMinutes(10), "OUTLOOK", "Inbox - jane@firm.com - Outlook", emailSubject: "RE: Deposition schedule");

        TitleUsage title = Assert.Single(Assert.Single(Build(day).Apps).TopTitles);
        Assert.Equal("RE: Deposition schedule", title.Title);
    }

    [Fact]
    public void RepositoryHelpers_ActiveSecondsHasActivityAndPurge()
    {
        DateOnly day = new(2026, 10, 20);
        Save(Local(day, 9, 0), TimeSpan.FromMinutes(10), "WINWORD", "A");
        Save(Local(day.AddDays(-100), 9, 0), TimeSpan.FromMinutes(10), "WINWORD", "Old");
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(day, Zone);

        Assert.Equal(600, _workspace.Repository.GetActiveSeconds(start, end));
        Assert.True(_workspace.Repository.HasActivity(start, end));
        Assert.False(_workspace.Repository.HasActivity(start.AddDays(-1), start));

        Assert.Equal(1, _workspace.Repository.PurgeRawActivityBefore(start.AddDays(-90)));
        Assert.True(_workspace.Repository.HasActivity(start, end));
    }

    private DailyAppUsageReport Build(DateOnly day, AppUsageReportOptions? options = null)
    {
        (DateTime start, DateTime end) = LocalDay.ToUtcRange(day, Zone);
        return AppUsageReportBuilder.Build(day, Zone, DateTimeOffset.UtcNow, isFinal: true,
            _workspace.Repository.GetAppTitleUsage(start, end), options);
    }

    private void Save(DateTime utcStart, TimeSpan duration, string process, string title, string? emailSubject = null, string? displayName = null) =>
        _workspace.Repository.SaveTimeBlock(TestBlocks.Block(utcStart, duration, process, title, emailSubject, displayName));

    private static DateTime Local(DateOnly day, int hour, int minute)
    {
        DateTime local = day.ToDateTime(new TimeOnly(hour, minute));
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
}
