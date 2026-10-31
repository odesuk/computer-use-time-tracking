using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class TimeRepositoryEmailMatterTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly TimeRepository _repository;

    public TimeRepositoryEmailMatterTests()
    {
        DatabaseInitializer.Initialize(_databasePath);
        _repository = new TimeRepository(_databasePath);
    }

    [Fact]
    public void AssignEmailConversationToMatter_UpdatesExistingAndFutureBlocks()
    {
        _repository.SaveMatter(new LocalMatter("MAT-101", "Matter One"));
        DateTime date = DateTime.Today;
        _repository.SaveTimeBlock(CreateEmailBlock("conversation-a", "RE: Motion review", date, 10, 10));
        _repository.AssignEmailConversationToMatter("conversation-a", "MAT-101");
        _repository.SaveTimeBlock(CreateEmailBlock("conversation-a", "RE: Motion review", date, 20, 10));

        var group = Assert.Single(_repository.GetDailyActivityGroups(date));

        Assert.Equal("MAT-101", group.MatterId);
        Assert.Equal(20, group.RawSeconds);
        Assert.Empty(_repository.GetUnassignedEmailConversations());
    }

    [Fact]
    public void AssignEmailConversationToMatter_DoesNotAssignUnknownJunkStatusBlocks()
    {
        _repository.SaveMatter(new LocalMatter("MAT-101", "Matter One"));
        DateTime date = DateTime.Today;
        _repository.SaveTimeBlock(CreateEmailBlock(
            "conversation-a", "Motion review", date, 10, 10, EmailJunkStatus.Unknown));

        _repository.AssignEmailConversationToMatter("conversation-a", "MAT-101");
        _repository.SaveTimeBlock(CreateEmailBlock(
            "conversation-a", "Motion review", date, 30, 10, EmailJunkStatus.Unknown));

        var groups = _repository.GetDailyActivityGroups(date);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.MatterId));
        Assert.Empty(_repository.GetUnassignedEmailConversations());
    }

    [Fact]
    public void GetUnassignedEmailConversations_ExcludesUnknownJunkStatus()
    {
        DateTime date = DateTime.Today;
        _repository.SaveTimeBlock(CreateEmailBlock(
            "unknown-conversation", "Unclassified message", date, 10, 10, EmailJunkStatus.Unknown));
        _repository.SaveTimeBlock(CreateEmailBlock(
            "normal-conversation", "Confirmed message", date, 30, 10, EmailJunkStatus.NotJunk));

        var pending = Assert.Single(_repository.GetUnassignedEmailConversations());

        Assert.Equal("normal-conversation", pending.ConversationKey);
        Assert.Equal("Confirmed message", pending.Subject);
    }

    [Fact]
    public void AssignEmailConversationToMatter_RejectsUnknownMatter()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            _repository.AssignEmailConversationToMatter("conversation-a", "missing-matter"));
    }

    [Fact]
    public void GetDailyActivityGroups_KeepsDifferentConversationsSeparate_WhenSubjectsMatch()
    {
        DateTime date = DateTime.Today;
        _repository.SaveTimeBlock(CreateEmailBlock("conversation-a", "RE: Motion review", date, 10, 10));
        _repository.SaveTimeBlock(CreateEmailBlock("conversation-b", "RE: Motion review", date, 30, 10));

        List<DailyActivityGroup> groups = _repository.GetDailyActivityGroups(date);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Equal("RE: Motion review", group.ActivityTitle));
        Assert.Equal(new long[] { 10, 10 }, groups.Select(group => group.RawSeconds).Order().ToArray());
    }

    [Fact]
    public void GetDailyActivityGroups_RollsUpReplySubjectsWithinTheSameConversation()
    {
        DateTime date = DateTime.Today;
        _repository.SaveTimeBlock(CreateEmailBlock("conversation-a", "Motion review", date, 10, 10));
        _repository.SaveTimeBlock(CreateEmailBlock("conversation-a", "RE: Motion review", date, 30, 20));

        var group = Assert.Single(_repository.GetDailyActivityGroups(date));

        Assert.Equal(30, group.RawSeconds);
        Assert.Equal(2, group.ItemCount);
    }

    [Fact]
    public void JunkEmail_IsExcludedFromUnassignedQueueAndDailySummary()
    {
        DateTime date = DateTime.Today;
        _repository.SaveTimeBlock(CreateEmailBlock(
            "junk-conversation", "Spam offer", date, 10, 10, EmailJunkStatus.Junk));
        _repository.SaveTimeBlock(CreateEmailBlock(
            "normal-conversation", "Client update", date, 30, 10, EmailJunkStatus.NotJunk));

        var pending = Assert.Single(_repository.GetUnassignedEmailConversations());
        var group = Assert.Single(_repository.GetDailyActivityGroups(date));

        Assert.Equal("normal-conversation", pending.ConversationKey);
        Assert.Equal("Client update", group.ActivityTitle);
    }

    [Fact]
    public void Initialize_MigratesLegacyEmailRowsWithoutLosingData_AndIsIdempotent()
    {
        string legacyPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE TimeBlocks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        StartTime DATETIME NOT NULL,
                        EndTime DATETIME NOT NULL,
                        DurationSeconds INTEGER NOT NULL,
                        ProcessName TEXT NOT NULL,
                        WindowTitle TEXT NOT NULL,
                        EmailSubject TEXT NULL,
                        EmailSender TEXT NULL,
                        EmailRecipients TEXT NULL,
                        ActivityCategory TEXT DEFAULT 'General',
                        MatchedMatterId TEXT NULL,
                        Status TEXT DEFAULT 'Unassigned'
                    );
                    INSERT INTO TimeBlocks
                        (StartTime, EndTime, DurationSeconds, ProcessName, WindowTitle, EmailSubject, ActivityCategory)
                    VALUES
                        ('2026-01-01T10:00:00Z', '2026-01-01T10:01:00Z', 60, 'outlook', 'Legacy email', 'Legacy email', 'Email');
                ";
                command.ExecuteNonQuery();
            }

            DatabaseInitializer.Initialize(legacyPath);
            DatabaseInitializer.Initialize(legacyPath);
            var repository = new TimeRepository(legacyPath);
            var conversation = Assert.Single(repository.GetUnassignedEmailConversations());

            Assert.Equal("Legacy email", conversation.Subject);
            Assert.StartsWith("email-v1:legacy:1", conversation.ConversationKey);
            Assert.Equal(60, conversation.RawSeconds);
            Assert.True(conversation.IsUnidentifiedBlock);
            Assert.False(conversation.IsMessageIdentity);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    private static TimeBlockRecord CreateEmailBlock(
        string conversationKey,
        string subject,
        DateTime date,
        int startSecond,
        int durationSeconds,
        EmailJunkStatus junkStatus = EmailJunkStatus.Unknown)
    {
        DateTime start = DateTime.SpecifyKind(date.Date.AddHours(10).AddSeconds(startSecond), DateTimeKind.Local);
        return new TimeBlockRecord
        {
            StartTime = start,
            EndTime = start.AddSeconds(durationSeconds),
            DurationSeconds = durationSeconds,
            ProcessName = "outlook",
            WindowTitle = subject,
            EmailSubject = subject,
            EmailSender = "client@example.com",
            EmailRecipients = "counsel@example.com",
            EmailConversationKey = conversationKey,
            EmailJunkStatus = junkStatus,
            ActivityCategory = "Email"
        };
    }
}
