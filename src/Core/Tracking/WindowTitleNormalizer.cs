using System.Text.RegularExpressions;

namespace ComputerUse.TimeTracking.Tracking;

/// <summary>
/// Removes title noise (unread counters, unsaved markers, app-name suffixes, browser profile names)
/// so that one activity stays one block and one report line. Applied at capture time, before the
/// "has the window changed" comparison, and again when reports read older rows.
/// </summary>
public static partial class WindowTitleNormalizer
{
    // App-name suffixes, longest first within a family so " - Adobe Acrobat Reader (64-bit)" wins
    // over a shorter match. Extend from real titles seen on pilot machines.
    private static readonly string[] AppSuffixes =
    {
        " - Google Chrome",
        " - Microsoft Edge",
        " - Mozilla Firefox",
        " - Brave",
        " - Opera",
        " - Word",
        " - Excel",
        " - PowerPoint",
        " - OneNote",
        " - Outlook",
        " | Microsoft Teams",
        " - Microsoft Teams",
        " - Visual Studio Code",
        " - Adobe Acrobat Reader (64-bit)",
        " - Adobe Acrobat Pro (64-bit)",
        " - Adobe Acrobat Reader",
        " - Adobe Acrobat Pro",
        " - Adobe Acrobat",
        " - Notepad",
        " - File Explorer",
    };

    // Chromium browsers put the profile name between the page title and the browser name:
    // "Inbox - Work - Microsoft Edge". Only stripped when the browser suffix was present.
    private static readonly string[] BrowserSuffixes =
    {
        " - Google Chrome", " - Microsoft Edge", " - Brave", " - Opera",
    };

    [GeneratedRegex(@"^\(\d+\+?\)\s*")]
    private static partial Regex LeadingCounter();                  // "(3) Inbox", "(99+) Inbox"

    [GeneratedRegex(@"^[●•*]\s*")]
    private static partial Regex LeadingMarker();                   // unsaved-change markers

    [GeneratedRegex(@"\s*\*$")]
    private static partial Regex TrailingMarker();                  // "Notes.txt*"

    [GeneratedRegex(@"\s+and \d+ more pages?(?= - |$)")]
    private static partial Regex EdgeMorePages();                   // Edge tab groups: "... and 3 more pages"

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();

    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // Edge writes "Microsoft​ Edge"; drop zero-width characters before matching.
        string t = title
            .Replace("​", string.Empty)
            .Replace("‌", string.Empty)
            .Replace("‍", string.Empty)
            .Replace("﻿", string.Empty)
            .Trim();
        t = RepeatedWhitespace().Replace(t, " ");
        t = LeadingCounter().Replace(t, string.Empty);
        t = LeadingMarker().Replace(t, string.Empty);
        t = EdgeMorePages().Replace(t, string.Empty);

        foreach (string suffix in AppSuffixes)
        {
            if (!t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || t.Length == suffix.Length) continue;

            t = t[..^suffix.Length];
            if (Array.IndexOf(BrowserSuffixes, suffix) >= 0) t = StripBrowserProfile(t);
            break;
        }

        t = TrailingMarker().Replace(t, string.Empty);
        return t.Trim();
    }

    // Profile names are short single words ("Work", "Personal", "Profile 1"). Only the last " - "
    // segment is considered, and only when something meaningful remains in front of it.
    private static string StripBrowserProfile(string t)
    {
        int separator = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0) return t;

        string segment = t[(separator + 3)..].Trim();
        bool looksLikeProfile = segment.Length is > 0 and <= 20 &&
                                (KnownProfileNames.Contains(segment) || segment.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase));
        return looksLikeProfile ? t[..separator] : t;
    }

    private static readonly HashSet<string> KnownProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Work", "Personal", "Default", "Home", "School", "Guest", "InPrivate", "Incognito",
    };
}
