using System.IO;
namespace ComputerUse.TimeTracking;

/// <summary>
/// Paths to show in Explorer. Under MSIX, writes to %LOCALAPPDATA% are redirected into the
/// package's LocalCache; Explorer runs outside the package and doesn't see the redirected view,
/// so folders opened for the user must be the real location.
/// </summary>
internal static class PackagedPaths
{
    private static readonly Lazy<bool> PackagedLazy = new(DetectPackaged);

    public static bool IsPackaged => PackagedLazy.Value;

    public static string DataDirectory => Real(AppPaths.DataDirectory);

    public static string Logs => Real(AppPaths.Logs);

    private static string Real(string path)
    {
        if (!IsPackaged) return path;
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase)) return path;

            string relative = Path.GetRelativePath(localAppData, path);
            string localCache = global::Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
            return Path.Combine(localCache, "Local", relative);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool DetectPackaged()
    {
        try
        {
            return global::Windows.ApplicationModel.Package.Current is not null;
        }
        catch (Exception)
        {
            // InvalidOperationException (or a COMException) when running without package identity.
            return false;
        }
    }
}
