using Windows.ApplicationModel;

namespace ComputerUse.TimeTracking;

/// <summary>The MSIX "start at sign-in" task (TaskId in Installer/Package.appxmanifest.template).</summary>
internal static class StartupTaskService
{
    public static string TaskId => ProductContext.Current.StartupTaskId;

    public enum State
    {
        /// <summary>Not running from the MSIX package (development build).</summary>
        Unavailable,
        Enabled,
        Disabled,
        DisabledByUser,
        DisabledByPolicy,
    }

    public static async Task<State> GetStateAsync()
    {
        if (!PackagedPaths.IsPackaged) return State.Unavailable;
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);
            return Map(task.State);
        }
        catch (Exception)
        {
            return State.Unavailable;
        }
    }

    /// <summary>Turns the task on or off. Windows may refuse (disabled by the user in Task Manager, or by policy).</summary>
    public static async Task<State> SetEnabledAsync(bool enabled)
    {
        if (!PackagedPaths.IsPackaged) return State.Unavailable;
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);
            if (enabled)
            {
                return Map(await task.RequestEnableAsync().AsTask().ConfigureAwait(false));
            }

            task.Disable();
            return Map(task.State);
        }
        catch (Exception)
        {
            return State.Unavailable;
        }
    }

    public static string Describe(State state) => state switch
    {
        State.Enabled => "On",
        State.Disabled => "Off",
        State.DisabledByUser => "Off — turned off in Task Manager → Startup apps; turn it back on there",
        State.DisabledByPolicy => "Off — blocked by your organisation's policy",
        _ => "Not available (the app is not installed from its package)",
    };

    private static State Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => State.Enabled,
        StartupTaskState.DisabledByUser => State.DisabledByUser,
        StartupTaskState.DisabledByPolicy => State.DisabledByPolicy,
        _ => State.Disabled,
    };
}
