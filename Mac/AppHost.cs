using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Reports;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace ComputerUse.TimeTracking;

/// <summary>The Generic Host for local tracking and reporting. Mirrors the Windows host minus Outlook.</summary>
public static class AppHost
{
    public static IHost Build(string[] hostArgs, SettingsService settings)
    {
        return Host.CreateDefaultBuilder(hostArgs)
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSerilog((_, logger) => ConfigureLogging(logger))
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(settings);
                services.AddSingleton(TimeProvider.System);

                services.AddSingleton<IActivitySampler, MacActivitySampler>();

                services.AddSingleton(_ => new TimeRepository());
                services.AddSingleton<ITimeBlockStore>(sp => sp.GetRequiredService<TimeRepository>());
                services.AddSingleton(_ => new ReportRunRepository());
                services.AddSingleton<IReportRunStore>(sp => sp.GetRequiredService<ReportRunRepository>());

                // One tracker instance: a hosted service that reports and the menu can also call.
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
            })
            .Build();
    }

    /// <summary>
    /// Daily rolling file under the product's data directory. Nothing in the app logs window titles or app
    /// names above Debug, and Debug is not written.
    /// </summary>
    public static void ConfigureLogging(LoggerConfiguration logger) => logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
        .WriteTo.File(Path.Combine(AppPaths.Logs, "tracker-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10_000_000,
            rollOnFileSizeLimit: true,
            shared: true);
}
