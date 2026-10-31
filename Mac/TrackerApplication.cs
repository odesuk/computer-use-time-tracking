using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ComputerUse.TimeTracking.Native;
using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ComputerUse.TimeTracking;

/// <summary>
/// Startup, shutdown, and the tracker host for the macOS app. Avalonia owns the main thread (menu bar item and
/// Settings); the Generic Host runs the tracker and scheduler on the thread pool.
/// </summary>
internal sealed class TrackerApplication(CommandLineOptions options)
{
    private static readonly TimeSpan SessionEndingReportWait = TimeSpan.FromSeconds(4);

    private readonly object _sessionEndingGate = new();
    private DateTime _lastSessionEndingReport = DateTime.MinValue;
    private IHost? _host;
    private SettingsService? _settings;
    private TimeTrackerWorker? _tracker;
    private ReportCoordinator? _reports;
    private IActivityCalendar? _calendar;
    private TrayMenu? _tray;
    private DispatcherTimer? _screenLockWatch;
    private bool _screenLocked;
    private bool _stopped;

    public static Serilog.ILogger CreateBootstrapLogger()
    {
        var configuration = new LoggerConfiguration();
        AppHost.ConfigureLogging(configuration);
        return configuration.CreateLogger();
    }

    /// <summary>Runs the startup sequence. Called once from <see cref="App"/> when Avalonia is ready.</summary>
    public async void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await StartCoreAsync(desktop);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{ProductName} failed to start.", ProductContext.Current.DisplayName);
            await MessageWindow.ShowAsync(
                ProductContext.Current.DisplayName,
                $"{ProductContext.Current.DisplayName} could not start:\n\n{ex.Message}");
            desktop.Shutdown(1);
        }
    }

    private async Task StartCoreAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _settings = new SettingsService(new ClientConfigurationStore());
        ClientSetupSettings current = _settings.Current;
        if (options.Setup || !current.SetupCompleted)
        {
            ClientSetupSettings? chosen = await SettingsWindow.ShowAsync(current, firstRun: !current.SetupCompleted);
            if (chosen is null && !current.SetupCompleted)
            {
                desktop.Shutdown();
                return;
            }

            if (chosen is not null) _settings.Save(chosen);
        }

        DatabaseInitializer.Initialize();

        _host = AppHost.Build(options.HostArgs, _settings);
        await _host.StartAsync();
        LogStartup(_host.Services, _settings);

        _tracker = _host.Services.GetRequiredService<TimeTrackerWorker>();
        _reports = _host.Services.GetRequiredService<ReportCoordinator>();
        _calendar = _host.Services.GetRequiredService<IActivityCalendar>();

        _tray = new TrayMenu(
            _host.Services,
            openSettings: OpenSettingsAsync,
            resetLocalData: () => ResetLocalDataAsync(desktop),
            quit: () => desktop.Shutdown());

        // The lock screen is not a recordable window, so tracking pauses while it is up. Polling is cheap,
        // and a change is applied once.
        _screenLockWatch = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => SyncScreenLock());
        _screenLockWatch.Start();

        desktop.ShutdownRequested += (_, _) => OnSessionEnding();
        desktop.Exit += (_, _) => Task.Run(() => StopAsync(writeCloseReport: true)).GetAwaiter().GetResult();
    }

    private async Task OpenSettingsAsync()
    {
        ClientSetupSettings? chosen = await SettingsWindow.ShowAsync(_settings!.Current, firstRun: false);
        if (chosen is null) return;

        // Keep a pause set while the window was open.
        chosen.PausedUntil = _settings.Current.PausedUntil;
        _settings.Save(chosen);
    }

    private async Task ResetLocalDataAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        string name = ProductContext.Current.DisplayName;
        bool confirmed = await MessageWindow.ConfirmAsync(
            name,
            $"This permanently deletes {ProductContext.Current.ShortName} tracking data, settings, pending data, JSON reports, and logs. " +
            $"It also deletes {ProductContext.Current.ReportFilePrefix} PDF reports in the configured report folder.\n\n" +
            "The application will close and must be launched again to complete setup.\n\nContinue?",
            confirmText: "Reset");
        if (!confirmed) return;

        string reportDirectory = _settings!.Current.EffectivePdfReportDirectory;
        await StopAsync(writeCloseReport: false);

        if (!LocalStateReset.TryDelete(reportDirectory, out string? error))
        {
            await MessageWindow.ShowAsync(name, $"Some local {ProductContext.Current.ShortName} data could not be deleted:\n\n{error}");
            desktop.Shutdown(1);
            return;
        }

        await MessageWindow.ShowAsync(name, $"{ProductContext.Current.ShortName} local data was reset. Launch the app again to complete setup.");
        desktop.Shutdown();
    }

    private void SyncScreenLock()
    {
        if (_tracker is null) return;

        bool locked = MacNative.IsScreenLocked();
        if (locked == _screenLocked) return;

        _screenLocked = locked;
        _tracker.SetSessionLocked(locked);
    }

    /// <summary>
    /// Logout or shutdown. The OS may end the process a few seconds later, so save the live block and today's
    /// report first. Anything lost is covered by the next start's catch-up.
    /// </summary>
    private void OnSessionEnding()
    {
        // The event can arrive more than once for a single logout; write once.
        lock (_sessionEndingGate)
        {
            if (DateTime.UtcNow - _lastSessionEndingReport < TimeSpan.FromSeconds(30)) return;
            _lastSessionEndingReport = DateTime.UtcNow;
        }

        try
        {
            _tracker?.Checkpoint();
            Task.Run(WriteCloseReportAsync).Wait(SessionEndingReportWait);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Report on sign-out failed.");
        }
    }

    private async Task StopAsync(bool writeCloseReport)
    {
        if (_stopped || _host is null) return;
        _stopped = true;

        _screenLockWatch?.Stop();
        _tray?.Dispose();
        await _host.StopAsync(TimeSpan.FromSeconds(10));   // the tracker saves its block
        if (writeCloseReport) await WriteCloseReportAsync();
        _host.Dispose();
    }

    private async Task WriteCloseReportAsync()
    {
        if (_reports is null || _calendar is null) return;

        try
        {
            DateOnly today = _reports.Today;
            if (!_calendar.HasActivity(today)) return;
            await _reports.WriteAsync(today, ReportTrigger.OnClose, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Report on close failed.");
        }
    }

    private static void LogStartup(IServiceProvider services, SettingsService settings)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TrackerApplication).FullName!);
        logger.LogInformation(
            "{ProductName} {Version} started. Packaged: {Packaged}. Data: {DataDirectory}. Reports: {ReportDirectory}. Schema: v{Schema}.",
            ProductContext.Current.DisplayName,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version,
            LoginItem.PackagedExecutable() is not null,
            AppPaths.DataDirectory,
            settings.Current.EffectivePdfReportDirectory,
            DatabaseInitializer.GetSchemaVersion());
    }
}
