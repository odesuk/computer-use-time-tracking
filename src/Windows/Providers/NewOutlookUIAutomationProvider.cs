using System;
using System.Windows.Automation;

namespace ComputerUse.TimeTracking.Providers;

public class NewOutlookUIAutomationProvider : IEmailTrackerProvider
{
    public bool CanHandle(string processName, string windowTitle)
    {
        return processName.Equals("olk", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("thunderbird", StringComparison.OrdinalIgnoreCase);
    }

    public EmailActivityMetadata? ExtractActivity(IntPtr hWnd)
    {
        try
        {
            var windowElement = AutomationElement.FromHandle(hWnd);
            if (windowElement == null) return null;

            string windowTitle = windowElement.Current.Name ?? string.Empty;

            // Calendar, People, To Do, etc. are not email; categorise them by app like anything else.
            if (IsNonMailView(windowTitle)) return null;

            return new EmailActivityMetadata
            {
                Subject = windowTitle,
                SenderEmail = string.Empty,
                Recipients = string.Empty,
                IsDraft = windowTitle.Contains("Draft", StringComparison.OrdinalIgnoreCase),
                ActionType = windowTitle.Contains("Compose", StringComparison.OrdinalIgnoreCase) ? "Composing" : "Reading",
                EntryId = string.Empty
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly string[] NonMailViews = { "Calendar", "People", "Contacts", "To Do", "Tasks", "Settings", "Files", "Notes" };

    internal static bool IsNonMailView(string windowTitle)
    {
        foreach (string view in NonMailViews)
        {
            if (windowTitle.Equals(view, StringComparison.OrdinalIgnoreCase) ||
                windowTitle.StartsWith(view + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
