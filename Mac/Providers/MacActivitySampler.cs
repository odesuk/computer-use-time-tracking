using ComputerUse.TimeTracking.Native;
using ComputerUse.TimeTracking.Tracking;

namespace ComputerUse.TimeTracking.Providers;

/// <summary>
/// Foreground application from NSWorkspace, its focused window title through Accessibility, and idle time
/// from the Quartz event source. Mail is not split out on macOS yet, so it is tracked by window title like any
/// other app.
/// </summary>
public sealed class MacActivitySampler : IActivitySampler
{
    public TimeSpan GetIdleTime() => MacNative.SecondsSinceLastInput();

    public ActivitySample Sample()
    {
        // The sampler runs on a thread-pool thread with no autorelease pool; push one so the Foundation
        // objects returned by each lookup are released at the end of every tick.
        IntPtr pool = MacNative.PushAutoreleasePool();
        try
        {
            MacNative.ForegroundApp? app = MacNative.FrontmostApplication();
            if (app is null || app.Name.Length == 0) return ActivitySample.Empty;

            // The app's localized name is the grouping key, so the same app always lands in one report row.
            string title = MacNative.FocusedWindowTitle(app.ProcessId);
            return new ActivitySample(app.Name, title, null, app.Name);
        }
        finally
        {
            MacNative.PopAutoreleasePool(pool);
        }
    }
}
