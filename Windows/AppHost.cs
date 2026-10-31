using System.IO;
using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.Tracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace ComputerUse.TimeTracking;

/// <summary>The Generic Host for local tracking and reporting.</summary>
public static class AppHost
{
    public static IHost Build(
        string[] hostArgs,
        SettingsService settings,
        Action<HostBuilderContext, IServiceCollection, SettingsService>? configureServices = null)
    {
        return Host.CreateDefaultBuilder(hostArgs)
            // A startup task launches with System32 as the working directory; read appsettings.json
            // from the app folder instead.
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSerilog((_, logger) => ConfigureLogging(logger, settings.Current.EffectivePdfReportDirectory))
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(settings);
                services.AddSingleton(TimeProvider.System);

                services.AddSingleton<IEmailTrackerProvider, ClassicOutlookComProvider>();
                services.AddSingleton<IEmailTrackerProvider, NewOutlookUIAutomationProvider>();
                services.AddSingleton<EmailProviderFactory>();
                services.AddSingleton<IActivitySampler, WindowsActivitySampler>();

                services.AddSingleton(_ => new TimeRepository());
                services.AddSingleton<ITimeBlockStore>(sp => sp.GetRequiredService<TimeRepository>());
                services.AddSingleton(_ => new ReportRunRepository());
                services.AddSingleton<IReportRunStore>(sp => sp.GetRequiredService<ReportRunRepository>());

                // One tracker instance: a hosted service that reports and the tray can also call.
                services.AddSingleton<TimeTrackerWorker>();
                services.AddSingleton<IActivityCheckpoint>(sp => sp.GetRequiredService<TimeTrackerWorker>());
                services.AddHostedService(sp => sp.GetRequiredService<TimeTrackerWorker>());

                services.AddSingleton<AppUsageReportService>();
                services.AddSingleton<IActivityCalendar>(sp => sp.GetRequiredService<AppUsageReportService>());
                services.AddSingleton<AppUsagePdfWriter>();
                services.AddSingleton(sp => new AppUsageReportWriter(
                    sp.GetRequiredService<AppUsageReportService>(),
                    sp.GetRequiredService<AppUsagePdfWriter>(),
                    sp.GetRequiredService<SettingsService>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppUsageReportWriter>>()));
                services.AddSingleton<IDailyReportWriter>(sp => sp.GetRequiredService<AppUsageReportWriter>());
                services.AddSingleton<ReportCoordinator>();
                services.AddSingleton<RetentionService>();
                services.AddHostedService<ReportScheduler>();
                configureServices?.Invoke(context, services, settings);
            })
            .Build();
    }

    /// <summary>
    /// Daily rolling file beside the PDF reports. Nothing in the app logs window
    /// titles, email subjects, senders or recipients above Debug, and Debug is not written.
    /// </summary>
    public static void ConfigureLogging(LoggerConfiguration logger, string reportDirectory) => logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
        .WriteTo.File(Path.Combine(reportDirectory, "tracker-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10_000_000,
            rollOnFileSizeLimit: true,
            shared: true);
}
