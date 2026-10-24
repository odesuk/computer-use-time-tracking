using System.Security.Cryptography;
using System.Text;
using ComputerUse.TimeTracking.Providers;

namespace ComputerUse.TimeTracking.Tracking;

public static class EmailConversationIdentity
{
    public static string? CreateKey(EmailActivityMetadata? email)
    {
        if (email is null) return null;

        string mailboxKey = Normalize(email.MailboxKey);
        if (mailboxKey.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(email.ConversationId))
        {
            return CreateOpaqueKey("conversation", mailboxKey, Normalize(email.ConversationId));
        }

        if (!string.IsNullOrWhiteSpace(email.EntryId))
        {
            return CreateOpaqueKey("message", mailboxKey, Normalize(email.EntryId));
        }

        return null;
    }

    public static string CreateUnidentifiedBlockKey() => $"email-v1:block:{Guid.NewGuid():N}";

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string CreateOpaqueKey(string kind, params string[] parts)
    {
        string payload = string.Join('\u001e', new[] { kind }.Concat(parts));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"email-v1:{kind}:{Convert.ToHexString(hash)}";
    }
}
