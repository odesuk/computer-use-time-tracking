using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Storage;

namespace ComputerUse.TimeTracking.Tracking;

/// <summary>What the user is looking at right now, as reported by the platform.</summary>
/// <param name="ProcessName">Stable process identifier (e.g. <c>WINWORD</c>); the grouping key in reports.</param>
/// <param name="WindowTitle">Raw window title. The tracker normalises it before comparing or storing.</param>
/// <param name="Email">Email metadata when the foreground window is a mail item.</param>
/// <param name="DisplayName">Friendly app name (e.g. "Microsoft Word"); null when unknown.</param>
public sealed record ActivitySample(
    string ProcessName,
    string WindowTitle,
    EmailActivityMetadata? Email,
    string? DisplayName = null)
{
    public static ActivitySample Empty { get; } = new(string.Empty, string.Empty, null);
}

/// <summary>Platform-specific source of foreground activity and input idle time.</summary>
public interface IActivitySampler
{
    /// <summary>Time since the last keyboard or mouse input.</summary>
    TimeSpan GetIdleTime();

    ActivitySample Sample();
}

public interface ITimeBlockStore
{
    void SaveTimeBlock(TimeBlockRecord block);
}
