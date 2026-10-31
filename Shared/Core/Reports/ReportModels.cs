using System.Text.Json.Serialization;

namespace ComputerUse.TimeTracking.Reports;

[JsonConverter(typeof(JsonStringEnumConverter<ReportTrigger>))]
public enum ReportTrigger
{
    /// <summary>Tray → "Generate today's report now".</summary>
    OnDemand,

    /// <summary>The configured end-of-day time, if the app is running.</summary>
    EndOfDay,

    /// <summary>Exit, sign-out, or shutdown.</summary>
    OnClose,

    /// <summary>First scheduler tick on a new local date.</summary>
    DayRollover,

    /// <summary>App start: recent days with activity and no final report.</summary>
    CatchUp,
}

/// <param name="IsFinal">True when written after the day ended; the report will not change.</param>
public sealed record ReportResult(DateOnly Date, string PdfPath, bool IsFinal, ReportTrigger Trigger);

/// <summary>One day's active time by application: the model behind the PDF and report sidecars.</summary>
public sealed record DailyAppUsageReport(
    DateOnly Date,
    string TimeZoneId,
    string TimeZoneName,
    DateTimeOffset GeneratedAt,
    bool IsFinal,
    long ActiveSeconds,
    DateTimeOffset? FirstActivity,
    DateTimeOffset? LastActivity,
    int AppCount,
    IReadOnlyList<AppUsage> Apps,
    OtherAppsUsage? OtherApps,
    bool IncludesTitles,
    int IdleThresholdMinutes);

/// <param name="Share">Fraction of the day's active time, 0..1.</param>
public sealed record AppUsage(
    string ProcessName,
    string DisplayName,
    long Seconds,
    double Share,
    IReadOnlyList<TitleUsage> TopTitles,
    long OtherTitlesSeconds,
    int OtherTitlesCount);

/// <summary>Small apps folded into one "Other (n apps)" line.</summary>
public sealed record OtherAppsUsage(int Count, long Seconds, double Share, IReadOnlyList<string> DisplayNames);

public sealed record TitleUsage(string Title, long Seconds);
