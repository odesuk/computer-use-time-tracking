using System.Security;

namespace ComputerUse.TimeTracking;

/// <summary>
/// "Start when I sign in" through a per-user LaunchAgent. Only the packaged .app can register itself: a
/// development run would register the dotnet host instead of the tracker.
/// </summary>
internal static class LoginItem
{
    public const string Label = "com.computeruse.timetracking";

    public static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static bool IsEnabled => File.Exists(PlistPath);

    /// <summary>The running executable inside an .app bundle, or null when running from a development build.</summary>
    public static string? PackagedExecutable()
    {
        string? path = Environment.ProcessPath;
        return path is not null && path.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) ? path : null;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return;
        }

        string executable = PackagedExecutable()
            ?? throw new InvalidOperationException("Start at sign-in is available in the packaged app only.");
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, BuildPlist(Label, executable));
    }

    // Takes effect at the next sign-in. Launching it now would start a second copy of the running tracker.
    public static string BuildPlist(string label, string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Escape(label)}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{Escape(executable)}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>LimitLoadToSessionType</key>
          <string>Aqua</string>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>
        """ + "\n";

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
