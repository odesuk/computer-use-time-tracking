namespace ComputerUse.TimeTracking.Tracking;

/// <summary>
/// Friendly names for process names. Platforms resolve a description (Windows: the executable's
/// FileDescription) and pass it here; names that come out poorly are overridden.
/// </summary>
public static class AppDisplayNames
{
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["olk"] = "Outlook (new)",
        ["OUTLOOK"] = "Outlook",
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["firefox"] = "Mozilla Firefox",
        ["WINWORD"] = "Microsoft Word",
        ["EXCEL"] = "Microsoft Excel",
        ["POWERPNT"] = "Microsoft PowerPoint",
        ["ONENOTE"] = "Microsoft OneNote",
        ["ms-teams"] = "Microsoft Teams",
        ["Teams"] = "Microsoft Teams",
        ["Acrobat"] = "Adobe Acrobat",
        ["AcroRd32"] = "Adobe Acrobat Reader",
        ["Code"] = "Visual Studio Code",
        ["explorer"] = "File Explorer",
        ["Zoom"] = "Zoom",
        ["notepad"] = "Notepad",
    };

    /// <param name="processName">Process name as sampled.</param>
    /// <param name="description">Platform-supplied description, if any.</param>
    public static string Resolve(string processName, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return string.Empty;
        if (Overrides.TryGetValue(processName, out string? overridden)) return overridden;
        return string.IsNullOrWhiteSpace(description) ? processName : description.Trim();
    }
}
