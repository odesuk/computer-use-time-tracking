using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.Tracking;

namespace ComputerUse.TimeTracking.Reports;

/// <summary>Options that shape a report. Defaults match the Phase 1 design (doc 03).</summary>
public sealed record AppUsageReportOptions
{
    public bool IncludeTitles { get; init; } = true;

    /// <summary>Titles listed per app before the rest fold into "+ n other titles".</summary>
    public int MaxTitlesPerApp { get; init; } = 5;

    /// <summary>Apps under this share of active time fold into "Other".</summary>
    public double FoldShareThreshold { get; init; } = 0.01;

    /// <summary>Apps under this many seconds fold into "Other".</summary>
    public long FoldSecondsThreshold { get; init; } = 120;

    public IReadOnlyCollection<string> ExcludedProcessNames { get; init; } = Array.Empty<string>();

    public int IdleThresholdMinutes { get; init; } = 3;
}

/// <summary>Pure aggregation from query rows to the report model; no I/O, so it is unit-tested directly.</summary>
public static class AppUsageReportBuilder
{
    public static DailyAppUsageReport Build(
        DateOnly date,
        TimeZoneInfo timeZone,
        DateTimeOffset generatedAt,
        bool isFinal,
        IEnumerable<AppTitleUsageRow> rows,
        AppUsageReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(rows);
        options ??= new AppUsageReportOptions();
        var excluded = new HashSet<string>(
            options.ExcludedProcessNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        List<AppTitleUsageRow> usable = rows
            .Where(row => row.Seconds > 0 && !string.IsNullOrWhiteSpace(row.ProcessName) && !excluded.Contains(row.ProcessName))
            .ToList();

        long activeSeconds = usable.Sum(row => row.Seconds);
        int maxTitles = Math.Max(0, options.MaxTitlesPerApp);

        List<AppUsage> apps = usable
            .GroupBy(row => row.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                long seconds = group.Sum(row => row.Seconds);
                string? description = group
                    .Select(row => row.DisplayName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                string displayName = AppDisplayNames.Resolve(group.Key, description);

                List<TitleUsage> titles = options.IncludeTitles
                    ? group
                        .GroupBy(row => DisplayTitle(row.Title), StringComparer.Ordinal)
                        .Select(titleGroup => new TitleUsage(titleGroup.Key, titleGroup.Sum(row => row.Seconds)))
                        .OrderByDescending(title => title.Seconds)
                        .ThenBy(title => title.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
                    : new List<TitleUsage>();

                List<TitleUsage> top = titles.Take(maxTitles).ToList();
                List<TitleUsage> rest = titles.Skip(maxTitles).ToList();
                return new AppUsage(
                    group.Key,
                    displayName,
                    seconds,
                    Share(seconds, activeSeconds),
                    top,
                    rest.Sum(title => title.Seconds),
                    rest.Count);
            })
            .OrderByDescending(app => app.Seconds)
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        int appCount = apps.Count;
        List<AppUsage> small = apps
            .Where(app => app.Seconds < options.FoldSecondsThreshold || app.Share < options.FoldShareThreshold)
            .ToList();

        // Folding a single app into "Other (1 app)" hides its name for nothing.
        OtherAppsUsage? other = null;
        if (small.Count >= 2)
        {
            long otherSeconds = small.Sum(app => app.Seconds);
            other = new OtherAppsUsage(small.Count, otherSeconds, Share(otherSeconds, activeSeconds),
                small.Select(app => app.DisplayName).ToList());
            apps = apps.Except(small).ToList();
        }

        DateTimeOffset? first = usable.Count == 0 ? null : ToLocal(usable.Min(row => row.FirstSeenUtc), timeZone);
        DateTimeOffset? last = usable.Count == 0 ? null : ToLocal(usable.Max(row => row.LastSeenUtc), timeZone);

        return new DailyAppUsageReport(
            date,
            timeZone.Id,
            timeZone.IsDaylightSavingTime(date.ToDateTime(new TimeOnly(12, 0))) ? timeZone.DaylightName : timeZone.StandardName,
            generatedAt,
            isFinal,
            activeSeconds,
            first,
            last,
            appCount,
            apps,
            other,
            options.IncludeTitles,
            options.IdleThresholdMinutes);
    }

    // Rows written before normalisation existed still carry raw titles; normalise on read so they
    // merge with newer rows. Untitled time keeps a visible label instead of an empty line.
    private static string DisplayTitle(string title)
    {
        string normalized = WindowTitleNormalizer.Normalize(title);
        return normalized.Length == 0 ? "(untitled window)" : normalized;
    }

    private static double Share(long part, long total) => total <= 0 ? 0 : (double)part / total;

    private static DateTimeOffset ToLocal(DateTime utc, TimeZoneInfo timeZone)
    {
        DateTime asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(asUtc), timeZone);
    }
}
