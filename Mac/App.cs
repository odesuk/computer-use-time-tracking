using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ComputerUse.TimeTracking;

/// <summary>Avalonia application. It runs on the main thread and has no main window: the app lives in the menu bar.</summary>
internal sealed class App(TrackerApplication tracker) : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing a Settings window must not end the app; only the menu's Quit does.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            tracker.Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
