using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using ComputerUse.TimeTracking.Native;
using ComputerUse.TimeTracking.Tracking;

namespace ComputerUse.TimeTracking.Providers;

public sealed class WindowsActivitySampler : IActivitySampler
{
    private readonly EmailProviderFactory _emailFactory;
    private readonly ConcurrentDictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);

    public WindowsActivitySampler(EmailProviderFactory emailFactory)
    {
        _emailFactory = emailFactory;
    }

    public TimeSpan GetIdleTime() => Win32Native.GetIdleTime();

    public ActivitySample Sample()
    {
        // One foreground lookup and one process lookup per tick; title and process come from the
        // same window handle, so they can't belong to two different windows.
        IntPtr hWnd = Win32Native.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return ActivitySample.Empty;

        string title = Win32Native.GetWindowTitle(hWnd);
        using Process? process = TryGetProcess(hWnd);
        if (process is null) return new ActivitySample(string.Empty, title, null);

        string processName = SafeProcessName(process);
        string displayName = ResolveDisplayName(process, processName);
        EmailActivityMetadata? email = _emailFactory.GetActiveEmailActivity(hWnd, processName, title);
        return new ActivitySample(processName, title, email, displayName);
    }

    private static Process? TryGetProcess(IntPtr hWnd)
    {
        Win32Native.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0) return null;

        try
        {
            return Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            return null;   // exited between the two calls
        }
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    // FileDescription is read once per process name ("Microsoft Word", "Google Chrome").
    private string ResolveDisplayName(Process process, string processName)
    {
        if (string.IsNullOrEmpty(processName)) return string.Empty;
        return _displayNames.GetOrAdd(processName, _ =>
        {
            string? description = null;
            try
            {
                description = process.MainModule?.FileVersionInfo.FileDescription;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Elevated or exited process: fall back to the process name.
            }

            return AppDisplayNames.Resolve(processName, description);
        });
    }
}
