using System.IO;
using System.Reflection;
using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinForms = System.Windows.Forms;

namespace ComputerUse.TimeTracking.UI;

/// <summary>
/// The notification-area icon and menu. WinForms NotifyIcon works under the WPF dispatcher's
/// message pump. Every handler runs on the UI thread; report work runs on the thread pool and
/// comes back to the dispatcher for UI.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly Action<string> _requestReset;
    private readonly ReportCoordinator _reports;
    private readonly AppUsageReportService _usage;
    private readonly AppUsageReportWriter _writer;
    private readonly TimeTrackerWorker _tracker;
    private readonly SettingsService _settings;
    private readonly RetentionService _retention;
    private readonly ILogger<TrayIcon> _logger;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _status;
    private readonly WinForms.ToolStripMenuItem _pauseMenu;
    private readonly WinForms.ToolStripMenuItem _resumeItem;
    private readonly System.Drawing.Icon _activeIcon;
    private readonly System.Drawing.Icon _pausedIcon;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private string? _lastReportPath;
    private bool _settingsOpen;
    private bool? _storageWasDegraded;
    private DateTimeOffset? _lastStorageWarning;

    public TrayIcon(System.Windows.Application app, IServiceProvider services, Action<string> requestReset)
    {
        _app = app;
        _requestReset = requestReset;
        _reports = services.GetRequiredService<ReportCoordinator>();
        _usage = services.GetRequiredService<AppUsageReportService>();
        _writer = services.GetRequiredService<AppUsageReportWriter>();
        _tracker = services.GetRequiredService<TimeTrackerWorker>();
        _settings = services.GetRequiredService<SettingsService>();
        _retention = services.GetRequiredService<RetentionService>();
        _logger = services.GetRequiredService<ILogger<TrayIcon>>();

        _activeIcon = LoadIcon(ProductContext.Current.ActiveIconResourceName) ?? System.Drawing.SystemIcons.Application;
        _pausedIcon = LoadIcon(ProductContext.Current.PausedIconResourceName) ?? _activeIcon;

        _status = new WinForms.ToolStripMenuItem { Enabled = false };
        _resumeItem = new WinForms.ToolStripMenuItem("Resume tracking", null, (_, _) => Resume());
        _pauseMenu = new WinForms.ToolStripMenuItem("Pause tracking");
        _pauseMenu.DropDownItems.Add("15 minutes", null, (_, _) => Pause(TimeSpan.FromMinutes(15)));
        _pauseMenu.DropDownItems.Add("1 hour", null, (_, _) => Pause(TimeSpan.FromHours(1)));
        _pauseMenu.DropDownItems.Add("Until I resume", null, (_, _) => Pause(null));

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem(ProductContext.Current.DisplayName) { Enabled = false, Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Generate today's report now", null, (_, _) => GenerateToday(openWhenDone: true));
        menu.Items.Add("Open today's report", null, (_, _) => OpenToday());
        menu.Items.Add("Open reports folder", null, (_, _) => Try(() => Shell.OpenFolder(_settings.Current.EffectivePdfReportDirectory)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(_resumeItem);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("View logs", null, (_, _) => Try(() => Shell.OpenFolder(_settings.Current.EffectivePdfReportDirectory)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Reset local data…", null, (_, _) => ResetLocalData());
        menu.Items.Add("Exit", null, (_, _) => _app.Shutdown());
        menu.Opening += (_, _) => RefreshState(includeTotals: true);

        _icon = new WinForms.NotifyIcon
        {
            Icon = _activeIcon,
            Text = ProductContext.Current.DisplayName,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenToday();
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_lastReportPath != null) Try(() => Shell.Open(_lastReportPath));
        };

        // Keeps the icon and tooltip right when a timed pause runs out.
        _refreshTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(30), System.Windows.Threading.DispatcherPriority.Background, (_, _) => RefreshState(includeTotals: false), app.Dispatcher);
        _refreshTimer.Start();
        RefreshState(includeTotals: false);
    }

    /// <summary>A second launch signalled this instance.</summary>
    public void ShowActivated() =>
        _icon.ShowBalloonTip(4000, ProductContext.Current.DisplayName, "Already running. Right-click the tray icon for reports, pause and settings.", WinForms.ToolTipIcon.Info);

    public void ShowSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            ClientSetupSettings? chosen = SettingsWindow.ShowDialog(_settings.Current, firstRun: false);
            if (chosen is null) return;

            // Keep a pause set while the window was open.
            int previousRetention = _settings.Current.EffectiveRetentionDays;
            chosen.PausedUntil = _settings.Current.PausedUntil;
            _settings.Save(chosen);

            // Folder, report time, titles and exclusions are read on each use. A shorter retention
            // period is applied now rather than at the next daily purge.
            if (chosen.EffectiveRetentionDays < previousRetention)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _retention.Purge();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Retention purge after a settings change failed.");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed.");
            ShowError("Settings could not be saved", ex.Message);
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void ResetLocalData()
    {
        WinForms.DialogResult result = WinForms.MessageBox.Show(
            $"This permanently deletes {ProductContext.Current.ShortName} tracking data, settings, pending data, CSV and JSON reports, and logs. " +
            $"It also deletes {ProductContext.Current.ReportFilePrefix} PDF reports in the configured report folder.\n\n" +
            "The application will close and must be launched again to complete setup.\n\nContinue?",
            ProductContext.Current.DisplayName,
            WinForms.MessageBoxButtons.YesNo,
            WinForms.MessageBoxIcon.Warning,
            WinForms.MessageBoxDefaultButton.Button2);
        if (result != WinForms.DialogResult.Yes) return;

        _requestReset(_settings.Current.EffectivePdfReportDirectory);
        _app.Shutdown();
    }

    private void GenerateToday(bool openWhenDone)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                ReportResult result = await _reports.WriteAsync(_reports.Today, ReportTrigger.OnDemand, CancellationToken.None)
                    .ConfigureAwait(false);
                await _app.Dispatcher.InvokeAsync(() =>
                {
                    _lastReportPath = result.PdfPath;
                    if (openWhenDone)
                    {
                        Try(() => Shell.Open(result.PdfPath));
                    }
                    else
                    {
                        _icon.ShowBalloonTip(3000, "Report ready", result.PdfPath, WinForms.ToolTipIcon.Info);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On-demand report failed.");
                await _app.Dispatcher.InvokeAsync(() => ShowError("The report could not be generated", ex.Message));
            }
        });
    }

    // Opens the latest report for today, generating one first if none exists yet.
    private void OpenToday()
    {
        string? existing = _reports.FindExistingReport(_reports.Today);
        if (existing is null)
        {
            string expected = _writer.GetDefaultPdfPath(_reports.Today);
            if (File.Exists(expected)) existing = expected;
        }

        if (existing is null)
        {
            GenerateToday(openWhenDone: true);
            return;
        }

        Try(() => Shell.Open(existing));
    }

    private async void Pause(TimeSpan? duration)
    {
        try
        {
            await Task.Run(() => _tracker.Pause(duration));
            RefreshState(includeTotals: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pausing tracking failed.");
            ShowError("Tracking could not be paused", ex.Message);
        }
    }

    private void Resume()
    {
        _tracker.Resume();
        RefreshState(includeTotals: false);
    }

    // The icon and tooltip refresh on a timer; today's total (a database read) only when the menu opens.
    private void RefreshState(bool includeTotals)
    {
        DateTimeOffset? pausedUntil = _tracker.PausedUntil;
        bool paused = pausedUntil.HasValue;
        bool storageDegraded = _tracker.IsStorageDegraded;
        string pauseText = pausedUntil == DateTimeOffset.MaxValue
            ? "paused until resumed"
            : $"paused until {pausedUntil?.ToLocalTime():HH:mm}";

        _icon.Icon = paused ? _pausedIcon : _activeIcon;
        // NotifyIcon.Text is limited to 127 characters.
        string shortName = ProductContext.Current.ShortName;
        _icon.Text = storageDegraded ? $"{shortName} — storage needs attention" : paused ? $"{shortName} — {pauseText}" : $"{shortName} — tracking";
        _pauseMenu.Visible = !paused;
        _resumeItem.Visible = paused;
        if (_storageWasDegraded != storageDegraded)
        {
            bool? previousStorageState = _storageWasDegraded;
            _storageWasDegraded = storageDegraded;
            if (storageDegraded)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!_lastStorageWarning.HasValue || now - _lastStorageWarning.Value >= TimeSpan.FromMinutes(5))
                {
                    _lastStorageWarning = now;
                    _icon.ShowBalloonTip(5000, ProductContext.Current.DisplayName, "Some activity could not be saved. Retrying; check available disk space or open logs.", WinForms.ToolTipIcon.Warning);
                }
            }
            else if (previousStorageState.HasValue)
            {
                _icon.ShowBalloonTip(3000, ProductContext.Current.DisplayName, "Local time storage recovered.", WinForms.ToolTipIcon.Info);
            }
        }
        if (!includeTotals) return;

        try
        {
            long seconds = _usage.GetSavedActiveSeconds(_reports.Today) + _tracker.GetLiveBlockSeconds();
            string today = DurationText.Format(seconds);
            _status.Text = storageDegraded ? $"Storage needs attention — {today} today" : paused ? $"Paused — {today} today" : $"Tracking — {today} today";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read today's total.");
            _status.Text = storageDegraded ? "Storage needs attention" : paused ? "Paused" : "Tracking";
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray action failed.");
            ShowError("That didn't work", ex.Message);
        }
    }

    private void ShowError(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.Error);

    public void Dispose()
    {
        _refreshTimer.Stop();
        _icon.Visible = false;   // otherwise a ghost icon stays until the user hovers over it
        _icon.Dispose();
        foreach (System.Drawing.Icon icon in new[] { _activeIcon, _pausedIcon }.Distinct())
        {
            if (!ReferenceEquals(icon, System.Drawing.SystemIcons.Application)) icon.Dispose();
        }
    }

    private static System.Drawing.Icon? LoadIcon(string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName)) return null;
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(TrayIcon).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }
}
