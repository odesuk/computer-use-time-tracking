using System;
using System.Collections.Generic;
using System.Linq;

namespace ComputerUse.TimeTracking.Providers;

public class EmailProviderFactory
{
    private readonly IEnumerable<IEmailTrackerProvider> _providers;

    public EmailProviderFactory(IEnumerable<IEmailTrackerProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Email metadata for the foreground window. The caller passes the process name and title it
    /// already resolved, so the window is looked up once per tick and title and process always
    /// belong to the same window.
    /// </summary>
    public EmailActivityMetadata? GetActiveEmailActivity(IntPtr hWnd, string processName, string windowTitle)
    {
        if (hWnd == IntPtr.Zero || string.IsNullOrEmpty(processName)) return null;

        try
        {
            var provider = _providers.FirstOrDefault(p => p.CanHandle(processName, windowTitle));
            return provider?.ExtractActivity(hWnd);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
