using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ComputerUse.TimeTracking.Reports;
using Microsoft.Extensions.Logging;
using ComputerUse.TimeTracking.Services;

namespace ComputerUse.TimeTracking.UI;

/// <summary>
/// The menu bar item and its menu. Mirrors the Windows notification-area menu: status, today's report,
/// pause and resume, settings, logs, reset, and quit.
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ReportCoordinator _reports;
    private readonly AppUsageReportService _usage;
    private readonly AppUsageReportWriter _writer;
    private readonly TimeTrackerWorker _tracker;
    private readonly SettingsService _settings;
    private readonly ILogger<TrayMenu> _logger;
    private readonly Func<Task> _openSettings;
    private readonly Func<Task> _resetLocalData;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _status;
    private readonly NativeMenuItem _pause;
    private readonly NativeMenuItem _resume;
    private readonly DispatcherTimer _refreshTimer;
    private bool _settingsOpen;

    public TrayMenu(IServiceProvider services, Func<Task> openSettings, Func<Task> resetLocalData, Action quit)
    {
        _reports = services.GetRequiredService<ReportCoordinator>();
        _usage = services.GetRequiredService<AppUsageReportService>();
        _writer = services.GetRequiredService<AppUsageReportWriter>();
        _tracker = services.GetRequiredService<TimeTrackerWorker>();
        _settings = services.GetRequiredService<SettingsService>();
        _logger = services.GetRequiredService<ILogger<TrayMenu>>();
        _openSettings = openSettings;
        _resetLocalData = resetLocalData;

        _status = new NativeMenuItem { IsEnabled = false };
        _pause = new NativeMenuItem("Pause tracking") { Menu = BuildPauseMenu() };
        _resume = Item("Resume tracking", Resume);

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem(ProductContext.Current.DisplayName) { IsEnabled = false });
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Generate today's report now", GenerateToday));
        menu.Items.Add(Item("Open today's report", OpenToday));
        menu.Items.Add(Item("Open reports folder", () => Try(() => Shell.OpenFolder(_settings.Current.EffectivePdfReportDirectory))));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_resume);
        menu.Items.Add(Item("Settings…", OpenSettings));
        menu.Items.Add(Item("View logs", () => Try(() => Shell.OpenFolder(AppPaths.Logs))));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Reset local data…", ResetLocalData));
        menu.Items.Add(Item("Quit", quit));

        _icon = new TrayIcon
        {
            Icon = LoadIcon(ProductContext.Current.ActiveIconResourceName),
            ToolTipText = ProductContext.Current.DisplayName,
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => OpenToday();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });

        _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => RefreshState());
        _refreshTimer.Start();
        RefreshState();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    private NativeMenu BuildPauseMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("15 minutes", () => Pause(TimeSpan.FromMinutes(15))));
        menu.Items.Add(Item("1 hour", () => Pause(TimeSpan.FromHours(1))));
        menu.Items.Add(Item("Until I resume", () => Pause(null)));
        return menu;
    }

    private static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    // The icon's tooltip and the status line refresh on a timer; today's total is a database read.
    private void RefreshState()
    {
        DateTimeOffset? pausedUntil = _tracker.PausedUntil;
        bool paused = pausedUntil.HasValue;
        bool storageDegraded = _tracker.IsStorageDegraded;
        string pauseText = pausedUntil == DateTimeOffset.MaxValue
            ? "paused until resumed"
            : $"paused until {pausedUntil?.ToLocalTime():HH:mm}";

        string shortName = ProductContext.Current.ShortName;
        _icon.ToolTipText = storageDegraded
            ? $"{shortName} — storage needs attention"
            : paused ? $"{shortName} — {pauseText}" : $"{shortName} — tracking";
        _pause.IsEnabled = !paused;
        _resume.IsEnabled = paused;

        try
        {
            long seconds = _usage.GetSavedActiveSeconds(_reports.Today) + _tracker.GetLiveBlockSeconds();
            string today = DurationText.Format(seconds);
            _status.Header = storageDegraded ? $"Storage needs attention — {today} today"
                : paused ? $"Paused — {today} today"
                : $"Tracking — {today} today";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read today's total.");
            _status.Header = storageDegraded ? "Storage needs attention" : paused ? "Paused" : "Tracking";
        }
    }

    private async void Pause(TimeSpan? duration)
    {
        try
        {
            await Task.Run(() => _tracker.Pause(duration));
            RefreshState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pausing tracking failed.");
            await MessageWindow.ShowAsync(ProductContext.Current.DisplayName, $"Tracking could not be paused: {ex.Message}");
        }
    }

    private void Resume()
    {
        _tracker.Resume();
        RefreshState();
    }

    private async void GenerateToday()
    {
        try
        {
            ReportResult result = await Task.Run(() => _reports.WriteAsync(_reports.Today, ReportTrigger.OnDemand, CancellationToken.None));
            Try(() => Shell.Open(result.PdfPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "On-demand report failed.");
            await MessageWindow.ShowAsync("The report could not be generated", ex.Message);
        }
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
            GenerateToday();
            return;
        }

        Try(() => Shell.Open(existing));
    }

    private async void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            await _openSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed.");
            await MessageWindow.ShowAsync("Settings could not be saved", ex.Message);
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private async void ResetLocalData()
    {
        try
        {
            await _resetLocalData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resetting local data failed.");
            await MessageWindow.ShowAsync(ProductContext.Current.DisplayName, ex.Message);
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
            _logger.LogError(ex, "Opening a file or folder failed.");
            _ = MessageWindow.ShowAsync(ProductContext.Current.DisplayName, ex.Message);
        }
    }

    private static WindowIcon? LoadIcon(string? resourceName)
    {
        if (resourceName is null) return null;
        using Stream? stream = typeof(TrayMenu).Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : new WindowIcon(stream);
    }
}
