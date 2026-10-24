using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Tracking;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class EmailConversationIdentityTests
{
    [Fact]
    public void CreateKey_UsesMailboxAndConversationId_WhenAvailable()
    {
        var first = new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            ConversationId = "conversation-123",
            Subject = "RE: Matter review",
            SenderEmail = "client@example.com",
            Recipients = "counsel@example.com"
        };
        var reply = new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            ConversationId = "conversation-123",
            Subject = "Matter review - updated",
            SenderEmail = "counsel@example.com",
            Recipients = "client@example.com"
        };

        Assert.Equal(EmailConversationIdentity.CreateKey(first), EmailConversationIdentity.CreateKey(reply));
    }

    [Fact]
    public void CreateKey_UsesEntryId_WhenConversationIdIsUnavailable()
    {
        var email = new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            EntryId = "message-123",
            Subject = "Matter review"
        };

        Assert.StartsWith("email-v1:message:", EmailConversationIdentity.CreateKey(email));
    }

    [Fact]
    public void CreateKey_DoesNotGroupBySubjectOrParticipants()
    {
        var firstMessage = new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            Subject = "Matter review",
            SenderEmail = "client@example.com",
            Recipients = "counsel@example.com",
            EntryId = "message-1"
        };
        var secondMessage = new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            Subject = "RE: Matter review",
            SenderEmail = "counsel@example.com",
            Recipients = "client@example.com",
            EntryId = "message-2"
        };

        Assert.NotEqual(
            EmailConversationIdentity.CreateKey(firstMessage),
            EmailConversationIdentity.CreateKey(secondMessage));
    }

    [Fact]
    public void CreateKey_ReturnsNull_WhenNoProviderIssuedIdentityExists()
    {
        var email = new EmailActivityMetadata
        {
            Subject = "Matter review",
            SenderEmail = "client@example.com",
            Recipients = "counsel@example.com"
        };

        Assert.Null(EmailConversationIdentity.CreateKey(email));
    }

    [Fact]
    public void CreateKey_ReturnsNull_WhenMailboxScopeIsMissing()
    {
        var email = new EmailActivityMetadata
        {
            ConversationId = "conversation-123",
            EntryId = "message-123"
        };

        Assert.Null(EmailConversationIdentity.CreateKey(email));
    }
}
