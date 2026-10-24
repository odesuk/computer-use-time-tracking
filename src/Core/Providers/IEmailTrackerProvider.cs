using System;

namespace ComputerUse.TimeTracking.Providers;

public enum EmailJunkStatus
{
    Unknown,
    NotJunk,
    Junk
}

public class EmailActivityMetadata
{
    public string Subject { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string Recipients { get; set; } = string.Empty;
    public string MailboxKey { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public bool IsDraft { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public EmailJunkStatus JunkStatus { get; set; } = EmailJunkStatus.Unknown;
}

public interface IEmailTrackerProvider
{
    bool CanHandle(string processName, string windowTitle);
    EmailActivityMetadata? ExtractActivity(IntPtr hWnd);
}
