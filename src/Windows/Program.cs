using System.Reflection;
using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ComputerUse.TimeTracking;

/// <summary>
/// One STA main thread owns the WPF <see cref="System.Windows.Application"/> and its dispatcher
/// (tray, Settings, dialogs). The Generic Host runs the tracker and scheduler on the thread pool.
/// Main stays synchronous so UI is always created on the STA thread.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args) => TrackerApplication.Run(args, ProductIdentity.Standalone);
}

/// <summary>Reusable Windows host used by the standalone executable and explicitly compiled product hosts.</summary>
public static class TrackerApplication
{

    private static readonly object SessionEndingGate = new();
    private static DateTime _lastSessionEndingReport = DateTime.MinValue;

    [STAThread]
    public static int Run(
        string[] args,
        ProductIdentity identity,
        Action<HostBuilderContext, IServiceCollection, SettingsService>? configureServices = null)
    {
        ProductContext.Configure(identity);
        CommandLineOptions options = CommandLineOptions.Parse(args);
        using SingleInstanceGuard instance = SingleInstanceGuard.Acquire(identity.SingleInstanceName);
        if (!instance.IsPrimary)
        {
            // Settings changed by a second process would not reach the running instance, so it opens them.
            SingleInstanceGuard.SignalPrimary(
                options.Setup ? ActivationRequest.OpenSettings : ActivationRequest.Show,
                identity.SingleInstanceName);
            return 0;
        }

        var settings = new SettingsService(new ClientConfigurationStore());
        Log.Logger = CreateBootstrapLogger(settings);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; the tracker is stopping.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };

        try
        {
            int exitCode = RunTracker(options, instance, settings, configureServices, out string? resetPdfReportDirectory);
            if (exitCode != 0 || resetPdfReportDirectory is null) return exitCode;

            Log.CloseAndFlush();
            if (!LocalStateReset.TryDelete(resetPdfReportDirectory, out string? error))
            {
                System.Windows.MessageBox.Show($"Some local {identity.ShortName} data could not be deleted:\n\n{error}", identity.DisplayName,
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return 1;
            }

            System.Windows.MessageBox.Show($"{identity.ShortName} local data was reset. Launch the app again to complete setup.", identity.DisplayName,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{ProductName} failed to start.", identity.DisplayName);
            System.Windows.MessageBox.Show($"{identity.DisplayName} could not start:\n\n{ex.Message}", identity.DisplayName,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int RunTracker(
        CommandLineOptions options,
        SingleInstanceGuard instance,
        SettingsService settings,
        Action<HostBuilderContext, IServiceCollection, SettingsService>? configureServices,
        out string? resetPdfReportDirectory)
    {
        resetPdfReportDirectory = null;
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            // A failing menu action must not take tracking down with it.
            Log.Error(e.Exception, "Unhandled UI exception.");
            e.Handled = true;
        };

        ClientSetupSettings current = settings.Current;
        if (options.Setup || !current.SetupCompleted)
        {
            ClientSetupSettings? chosen = SettingsWindow.ShowDialog(current, firstRun: !current.SetupCompleted);
            if (chosen is null && !current.SetupCompleted) return 0;
            if (chosen is not null) settings.Save(chosen);
        }

        DatabaseInitializer.Initialize();

        using IHost host = AppHost.Build(options.HostArgs, settings, configureServices);
        host.Start();
        LogStartup(host.Services, settings);

        var tracker = host.Services.GetRequiredService<TimeTrackerWorker>();
        var reports = host.Services.GetRequiredService<ReportCoordinator>();
        var calendar = host.Services.GetRequiredService<IActivityCalendar>();

        string? requestedResetPdfReportDirectory = null;
        using var tray = new TrayIcon(app, host.Services, directory => requestedResetPdfReportDirectory = directory);
        instance.ListenForActivation(request => app.Dispatcher.InvokeAsync(() =>
        {
            if (request == ActivationRequest.OpenSettings) tray.ShowSettings();
            else tray.ShowActivated();
        }));

        using var systemEvents = new SystemEventsBridge(tracker, () => OnSessionEnding(tracker, reports, calendar));
        app.SessionEnding += (_, _) => OnSessionEnding(tracker, reports, calendar);

        app.Run();   // returns after the tray's Exit calls app.Shutdown()

        // The dispatcher is gone; run async shutdown off this thread so no continuation tries to
        // post back to a dead DispatcherSynchronizationContext.
        Task.Run(async () =>
        {
            await host.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);   // tracker saves its block
        }).GetAwaiter().GetResult();

        if (requestedResetPdfReportDirectory is not null)
        {
            resetPdfReportDirectory = requestedResetPdfReportDirectory;
            return 0;
        }

        Task.Run(() => WriteCloseReportAsync(reports, calendar)).GetAwaiter().GetResult();

        return 0;
    }

    /// <summary>
    /// Sign-out or shutdown (WM_QUERYENDSESSION). Windows terminates the process a few seconds
    /// later, so save the live block and today's report first. Anything lost is covered by the
    /// next start's catch-up, which writes yesterday's final report.
    /// </summary>
    private static void OnSessionEnding(TimeTrackerWorker tracker, ReportCoordinator reports, IActivityCalendar calendar)
    {
        // WPF and SystemEvents both report the same session end; write once. A cancelled sign-out
        // followed by a real one later still writes again.
        lock (SessionEndingGate)
        {
            if (DateTime.UtcNow - _lastSessionEndingReport < TimeSpan.FromSeconds(30)) return;
            _lastSessionEndingReport = DateTime.UtcNow;
        }

        try
        {
            tracker.Checkpoint();
            Task.Run(() => WriteCloseReportAsync(reports, calendar)).Wait(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Report on sign-out failed.");
        }
    }

    private static async Task WriteCloseReportAsync(ReportCoordinator reports, IActivityCalendar calendar)
    {
        try
        {
            DateOnly today = reports.Today;
            if (!calendar.HasActivity(today)) return;
            await reports.WriteAsync(today, ReportTrigger.OnClose, CancellationToken.None).ConfigureAwait(false);
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
            Assembly.GetEntryAssembly()?.GetName().Version,
            PackagedPaths.IsPackaged,
            PackagedPaths.DataDirectory,
            settings.Current.EffectivePdfReportDirectory,
            DatabaseInitializer.GetSchemaVersion());
    }

    private static Serilog.ILogger CreateBootstrapLogger(SettingsService settings)
    {
        var configuration = new LoggerConfiguration();
        AppHost.ConfigureLogging(configuration, settings.Current.EffectivePdfReportDirectory);
        return configuration.CreateLogger();
    }

}

/// <summary>
/// Lock, unlock, suspend, and session-end notifications. <see cref="SystemEvents"/> handlers are
/// static, so they are removed on dispose or they leak.
/// </summary>
internal sealed class SystemEventsBridge : IDisposable
{
    private readonly TimeTrackerWorker _tracker;
    private readonly Action _onSessionEnding;

    public SystemEventsBridge(TimeTrackerWorker tracker, Action onSessionEnding)
    {
        _tracker = tracker;
        _onSessionEnding = onSessionEnding;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _tracker.SetSessionLocked(true);
                break;
            // A reconnect to a session that is still locked shows the lock screen, which is never
            // recorded, so treating connect as unlock can't record a locked session.
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _tracker.SetSessionLocked(false);
                break;
        }
    }

    // Sleep is also caught by the polling-gap check; this makes it immediate.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _tracker.EndActivityNow("suspend");
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => _onSessionEnding();
}
