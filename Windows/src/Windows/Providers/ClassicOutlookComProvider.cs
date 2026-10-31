using System;
using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ComputerUse.TimeTracking.Providers;

public class ClassicOutlookComProvider : IEmailTrackerProvider
{
    // Resolved once: Type.GetTypeFromProgID reads the registry.
    private static readonly Lazy<Guid?> OutlookClassId = new(() => Type.GetTypeFromProgID("Outlook.Application")?.GUID);

    public bool CanHandle(string processName, string windowTitle)
    {
        return processName.Equals("outlook", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the open or selected mail item. Every COM object touched here is released before
    /// returning; otherwise a runtime-callable wrapper piles up every poll while Outlook is in
    /// front and keeps Outlook items and handles alive for hours.
    /// </summary>
    public EmailActivityMetadata? ExtractActivity(IntPtr hWnd)
    {
        if (OutlookClassId.Value is not Guid classId) return null;

        object? active = null, window = null, item = null, selection = null;
        try
        {
            if (GetActiveObject(ref classId, IntPtr.Zero, out active) < 0) return null;
            if (active is not Outlook.Application application) return null;

            window = application.ActiveWindow();
            if (window is Outlook.Inspector inspector)
            {
                item = inspector.CurrentItem;
                return item is Outlook.MailItem mail ? FromMail(mail, selected: false) : null;
            }

            if (window is Outlook.Explorer explorer)
            {
                Outlook.Selection currentSelection = explorer.Selection;
                selection = currentSelection;
                if (currentSelection.Count == 0) return null;
                item = currentSelection[1];
                return item is Outlook.MailItem mail ? FromMail(mail, selected: true) : null;
            }

            return null;
        }
        catch (COMException)
        {
            return null;   // Outlook busy, a modal dialog is open, or Outlook is closing
        }
        catch (InvalidCastException)
        {
            return null;
        }
        finally
        {
            Release(item);
            Release(selection);
            Release(window);
            Release(active);
        }
    }

    // Copies plain strings out so nothing COM-backed escapes. mail.To is a display-name list with
    // client names; it stays local-only.
    private static EmailActivityMetadata FromMail(Outlook.MailItem mail, bool selected)
    {
        bool sent = mail.Sent;
        return new EmailActivityMetadata
        {
            Subject = mail.Subject ?? string.Empty,
            SenderEmail = mail.SenderEmailAddress ?? string.Empty,
            Recipients = mail.To ?? string.Empty,
            IsDraft = !selected && !sent,
            ActionType = selected || sent ? "Reading" : "Composing",
            EntryId = mail.EntryID ?? string.Empty
        };
    }

    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }

    [DllImport("oleaut32.dll", EntryPoint = "GetActiveObject", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object activeObject);
}
