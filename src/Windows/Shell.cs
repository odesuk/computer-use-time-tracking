using System.IO;
using System.Diagnostics;

namespace ComputerUse.TimeTracking;

internal static class Shell
{
    /// <summary>Opens a file or folder with its default handler (PDF viewer, Explorer).</summary>
    public static void Open(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            using Process? _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        throw new FileNotFoundException("The file or folder does not exist.", path);
    }

    /// <summary>Opens the folder and creates it first if needed.</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Open(path);
    }
}
