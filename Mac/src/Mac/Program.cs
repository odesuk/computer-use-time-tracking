using Avalonia;
using Serilog;

namespace ComputerUse.TimeTracking;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ProductContext.Configure(ProductIdentity.Standalone with
        {
            ActiveIconResourceName = "ComputerUse.TimeTracking.AppIcon.png",
        });

        CommandLineOptions options = CommandLineOptions.Parse(args);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        using SingleInstanceGuard instance = SingleInstanceGuard.Acquire(Path.Combine(AppPaths.DataDirectory, "instance.lock"));
        if (!instance.IsPrimary)
        {
            // Settings changed by a second process would not reach the running instance, so it is left to the menu.
            Console.Error.WriteLine($"{ProductContext.Current.DisplayName} is already running. Use its menu bar icon to open Settings or reports.");
            return 0;
        }

        Log.Logger = TrackerApplication.CreateBootstrapLogger();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; the tracker is stopping.");

        try
        {
            var tracker = new TrackerApplication(options);
            return AppBuilder.Configure(() => new App(tracker))
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(options.HostArgs);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{ProductName} failed to start.", ProductContext.Current.DisplayName);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
