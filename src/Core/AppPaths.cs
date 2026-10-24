namespace ComputerUse.TimeTracking;

/// <summary>
/// Default per-user locations. Under MSIX, Windows redirects writes to %LOCALAPPDATA% into the
/// package's LocalCache, so paths shown in Explorer must be resolved by the host (see the Windows
/// app's PackagedPaths); code that only reads and writes files can use these as-is.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductContext.Current.DataDirectoryName);

    public static string Database => Path.Combine(DataDirectory, "timekeeper.db");

    public static string Settings => Path.Combine(DataDirectory, "client-settings.json");

    /// <summary>Durable retry records for blocks that could not yet reach the local database.</summary>
    public static string PendingBlocks => Path.Combine(DataDirectory, "pending-blocks.json");

    public static string JsonReports => Path.Combine(DataDirectory, "Reports");

    public static string Logs => Path.Combine(DataDirectory, "logs");

    public static string DefaultPdfReportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProductContext.Current.DefaultReportDirectoryName);
}
