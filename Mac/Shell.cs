using System.Diagnostics;

namespace ComputerUse.TimeTracking;

internal static class Shell
{
    /// <summary>Opens a file or folder with its default handler (PDF viewer, Finder).</summary>
    public static void Open(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new FileNotFoundException("The file or folder does not exist.", path);
        }

        Launch(path);
    }

    /// <summary>Opens the folder and creates it first if needed.</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Open(path);
    }

    /// <summary>Opens a system settings pane through its URL, such as the Accessibility privacy page.</summary>
    public static void OpenUrl(string url) => Launch(url);

    private static void Launch(string target)
    {
        using Process? _ = Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { target } });
    }
}
