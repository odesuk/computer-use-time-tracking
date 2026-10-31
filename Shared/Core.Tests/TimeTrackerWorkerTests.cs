using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.Tracking;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class TimeTrackerWorkerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly FakeSampler _sampler = new();
    private readonly RecordingStore _store = new();
    private readonly TempWorkspace _pendingWorkspace = new(initializeDatabase: false);
    private readonly TimeTrackerWorker _worker;

    public TimeTrackerWorkerTests()
    {
        _worker = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store, _clock,
            pendingBlocksPath: Path.Combine(_pendingWorkspace.Directory, "pending-blocks.json"));
    }

    [Fact]
    public async Task StopAsync_CompletesExecutionSuccessfully()
    {
        await _worker.StartAsync(CancellationToken.None);

        await _worker.StopAsync(CancellationToken.None);

        Task execution = _worker.ExecuteTask!;
        Assert.True(execution.IsCompletedSuccessfully);
    }

    [Fact]
    public void WindowChange_SavesPreviousBlock()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(60));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal("WINWORD", block.ProcessName);
        Assert.Equal("Brief.docx", block.WindowTitle);
        Assert.Equal(60, block.DurationSeconds);
        Assert.Equal("Document", block.ActivityCategory);
    }

    [Fact]
    public void SingleShortSwitch_IsSavedUnderItsActualProcess()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(4));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal("WINWORD", block.ProcessName);
        Assert.Equal(4, block.DurationSeconds);
    }

    [Fact]
    public void Idle_EndsBlockAtLastInput()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        PollFor(TimeSpan.FromSeconds(60));
        PollFor(TimeSpan.FromMinutes(4), idleSinceStart: true);

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal(60, block.DurationSeconds);
    }

    [Fact]
    public void SuspendGap_EndsBlockAtLastPoll_WhenIdleCounterDoesNotSpanSleep()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(30));
        _worker.Poll();

        // The machine sleeps for an hour; the user's first input after waking resets idle to zero.
        Advance(TimeSpan.FromHours(1));
        _sampler.Idle = TimeSpan.Zero;
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal(30, block.DurationSeconds);
    }

    [Fact]
    public void SuspendGap_UsesLastInputWhenEarlierThanLastPoll()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        PollFor(TimeSpan.FromSeconds(30));
        PollFor(TimeSpan.FromSeconds(60), idleSinceStart: true);

        // Windows' idle counter keeps running across sleep.
        Advance(TimeSpan.FromHours(1));
        _sampler.Idle = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(60);
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal(30, block.DurationSeconds);
    }

    [Fact]
    public void EmailActivity_IsCategorisedAsEmail()
    {
        var email = new EmailActivityMetadata
        {
            Subject = "RE: Deposition schedule",
            SenderEmail = "a@b.com",
            MailboxKey = "mailbox-a",
            ConversationId = "conversation-123",
            JunkStatus = EmailJunkStatus.Junk
        };
        _sampler.Current = new ActivitySample("outlook", "Inbox - Outlook", email);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal("Email", block.ActivityCategory);
        Assert.Equal("RE: Deposition schedule", block.EmailSubject);
        Assert.Equal(EmailConversationIdentity.CreateKey(email), block.EmailConversationKey);
        Assert.Equal(EmailJunkStatus.Junk, block.EmailJunkStatus);
    }

    [Fact]
    public void EntryIdChange_SavesSeparateEmailBlocks()
    {
        _sampler.Current = new ActivitySample("outlook", "Matter review", new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            ConversationId = "conversation-123",
            EntryId = "message-1",
            Subject = "Matter review"
        });
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("outlook", "Matter review", new EmailActivityMetadata
        {
            MailboxKey = "mailbox-a",
            ConversationId = "conversation-123",
            EntryId = "message-2",
            Subject = "Matter review"
        });
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        _worker.Poll();

        Assert.Equal(2, _store.Blocks.Count);
        Assert.All(_store.Blocks, block => Assert.Equal("Email", block.ActivityCategory));
        Assert.All(_store.Blocks, block => Assert.StartsWith("email-v1:conversation:", block.EmailConversationKey));
        Assert.Equal(_store.Blocks[0].EmailConversationKey, _store.Blocks[1].EmailConversationKey);
    }

    [Fact]
    public void MissingMailboxScope_UsesSeparateBlockKeysForSeparateItems()
    {
        _sampler.Current = new ActivitySample("olk", "Matter review", new EmailActivityMetadata
        {
            ConversationId = "conversation-123",
            Subject = "Matter review"
        });
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("olk", "Matter review (2)", new EmailActivityMetadata
        {
            ConversationId = "conversation-456",
            Subject = "Matter review"
        });
        _worker.Poll();

        Assert.Single(_store.Blocks);
        Assert.StartsWith("email-v1:block:", _store.Blocks[0].EmailConversationKey);
    }

    [Fact]
    public void TitleChurn_DoesNotSplitBlock()
    {
        _sampler.Current = new ActivitySample("chrome", "(1) Inbox - Gmail - Google Chrome", null);
        PollFor(TimeSpan.FromSeconds(30));
        _sampler.Current = new ActivitySample("chrome", "(2) Inbox - Gmail - Google Chrome", null);
        PollFor(TimeSpan.FromSeconds(30));
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx - Word", null);
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal("Inbox - Gmail", block.WindowTitle);
        Assert.Equal(60, block.DurationSeconds);
    }

    [Fact]
    public void ShortSwitch_IsSavedUnderItsActualProcess()
    {
        // A(60 s) -> B(3 s) -> C(60 s) keeps each interval with its actual foreground app.
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(60));
        _sampler.Current = new ActivitySample("explorer", "Downloads", null);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(3));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        _worker.Poll();
        Advance(TimeSpan.FromSeconds(60));
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        _worker.Poll();

        Assert.Collection(_store.Blocks,
            a => Assert.Equal(("WINWORD", 60), (a.ProcessName, a.DurationSeconds)),
            b => Assert.Equal(("explorer", 3), (b.ProcessName, b.DurationSeconds)),
            c => Assert.Equal(("chrome", 60), (c.ProcessName, c.DurationSeconds)));
        Assert.Equal(_store.Blocks[0].EndTime, _store.Blocks[1].StartTime);
        Assert.Equal(_store.Blocks[1].EndTime, _store.Blocks[2].StartTime);
    }

    [Fact]
    public void ShortSwitch_BeforeIdle_IsSavedUnderItsActualProcess()
    {
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        PollFor(TimeSpan.FromSeconds(60));
        _sampler.Current = new ActivitySample("explorer", "Downloads", null);
        PollFor(TimeSpan.FromSeconds(2));
        PollFor(TimeSpan.FromMinutes(4), idleSinceStart: true);
        _sampler.Idle = TimeSpan.Zero;
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        PollFor(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        _worker.Poll();

        Assert.Collection(_store.Blocks,
            a => Assert.Equal(("WINWORD", 60), (a.ProcessName, a.DurationSeconds)),
            b => Assert.Equal(("explorer", 2), (b.ProcessName, b.DurationSeconds)),
            c => Assert.Equal(("chrome", 20), (c.ProcessName, c.DurationSeconds)));
    }

    [Fact]
    public void EmailWithEntryId_SubjectEditsDoNotSplit_AndFinalSubjectIsStored()
    {
        _sampler.Current = new ActivitySample("outlook", "Untitled - Message",
            new EmailActivityMetadata { Subject = "Re: Mot", EntryId = "ABC" });
        PollFor(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("outlook", "Untitled - Message",
            new EmailActivityMetadata { Subject = "Re: Motion to dismiss", EntryId = "ABC" });
        PollFor(TimeSpan.FromSeconds(20));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        _worker.Poll();

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal(40, block.DurationSeconds);
        Assert.Equal("Re: Motion to dismiss", block.EmailSubject);
    }

    [Fact]
    public void DisplayName_IsStoredWithBlock()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null, "Microsoft Word");
        PollFor(TimeSpan.FromSeconds(10));
        _worker.Stop();

        Assert.Equal("Microsoft Word", Assert.Single(_store.Blocks).AppDisplayName);
    }

    [Fact]
    public void Checkpoint_SplitsLiveBlockIntoAdjacentBlocks()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        PollFor(TimeSpan.FromSeconds(40));
        _worker.Checkpoint();
        Assert.Equal(40, Assert.Single(_store.Blocks).DurationSeconds);

        PollFor(TimeSpan.FromSeconds(20));
        _worker.Stop();

        Assert.Equal(2, _store.Blocks.Count);
        Assert.Equal(_store.Blocks[0].EndTime, _store.Blocks[1].StartTime);
        Assert.Equal(60, _store.Blocks.Sum(block => block.DurationSeconds));
    }

    [Fact]
    public void Poll_CheckpointsLiveBlockEveryMinute()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);

        PollFor(TimeSpan.FromSeconds(62));

        TimeBlockRecord block = Assert.Single(_store.Blocks);
        Assert.Equal(60, block.DurationSeconds);
        Assert.Equal("WINWORD", block.ProcessName);
    }

    [Fact]
    public void Checkpoint_SplitsAtLastInput_SoIdleTimeIsNotRecorded()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        PollFor(TimeSpan.FromSeconds(30));
        PollFor(TimeSpan.FromSeconds(60), idleSinceStart: true);
        _worker.Checkpoint();
        PollFor(TimeSpan.FromMinutes(3), idleSinceStart: true);

        Assert.Equal(30, Assert.Single(_store.Blocks).DurationSeconds);
    }

    [Fact]
    public void Checkpoint_DoesNotSaveUntitledBlock()
    {
        _sampler.Current = new ActivitySample("explorer", "", null);
        PollFor(TimeSpan.FromSeconds(30));
        _worker.Checkpoint();

        Assert.Empty(_store.Blocks);
    }

    [Fact]
    public void Checkpoint_AfterMissedSleep_EndsAtLastPoll()
    {
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        PollFor(TimeSpan.FromSeconds(30));

        // Slept for an hour with no suspend notification; a report runs before the next poll.
        Advance(TimeSpan.FromHours(1));
        _worker.Checkpoint();

        Assert.Equal(30, Assert.Single(_store.Blocks).DurationSeconds);
    }

    [Fact]
    public void Checkpoint_AcrossMidnight_SavesTheEveningPart()
    {
        _clock.Set(new DateTimeOffset(2026, 10, 20, 23, 50, 0, TimeSpan.Zero));
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);
        PollFor(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(20));
        _worker.Checkpoint();

        Assert.Equal(620, _store.Blocks.Sum(block => block.DurationSeconds));
    }

    [Fact]
    public void Checkpoint_WithoutLiveBlock_DoesNothing()
    {
        _worker.Checkpoint();
        Assert.Empty(_store.Blocks);
    }

    [Fact]
    public void FailedSave_IsRetriedOnNextPoll()
    {
        _store.FailuresRemaining = 1;
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        PollFor(TimeSpan.FromSeconds(10));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        PollFor(TimeSpan.FromSeconds(10));
        Assert.Equal("WINWORD", Assert.Single(_store.Blocks).ProcessName);

        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        _worker.Poll();

        Assert.Equal(new[] { "WINWORD", "chrome" }, _store.Blocks.Select(block => block.ProcessName));
    }

    [Fact]
    public void FailedSave_IsRecoveredAfterRestart()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        var failingStore = new RecordingStore { FailuresRemaining = 1 };
        var first = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, failingStore, _clock, pendingBlocksPath: pendingPath);
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);

        first.Poll();
        Advance(TimeSpan.FromSeconds(10));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        first.Poll();

        Assert.True(first.IsStorageDegraded);
        Assert.True(File.Exists(pendingPath));

        var restarted = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, failingStore, _clock, pendingBlocksPath: pendingPath);
        restarted.Poll();

        Assert.Single(failingStore.Blocks);
        Assert.False(restarted.IsStorageDegraded);
        Assert.Empty(new PendingBlockSpool(pendingPath).Load());
    }

    [Fact]
    public void ReplayAfterDatabaseCommit_DoesNotDuplicateBlock()
    {
        using var workspace = new TempWorkspace();
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        var store = new CommitThenThrowStore(workspace.Repository);
        var first = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, store, _clock, pendingBlocksPath: pendingPath);
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);

        first.Poll();
        Advance(TimeSpan.FromSeconds(10));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        first.Poll();

        Assert.True(first.IsStorageDegraded);
        var restarted = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, workspace.Repository, _clock, pendingBlocksPath: pendingPath);
        restarted.Poll();

        using var connection = TimeTrackerDatabase.Open(workspace.DatabasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TimeBlocks;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
        Assert.Empty(new PendingBlockSpool(pendingPath).Load());
    }

    [Fact]
    public void UnreadablePendingFile_IsNotOverwritten()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        File.WriteAllText(pendingPath, "{ not valid json");
        var worker = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store, _clock, pendingBlocksPath: pendingPath);
        _sampler.Current = new ActivitySample("WINWORD", "Brief.docx", null);

        worker.Poll();
        Advance(TimeSpan.FromSeconds(10));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        worker.Poll();

        Assert.True(worker.IsStorageDegraded);
        Assert.Equal("{ not valid json", File.ReadAllText(pendingPath));
        Assert.Single(_store.Blocks);
    }

    [Fact]
    public void Restart_DropsPendingBlocksPastRawRetention()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        DateTime end = Start.UtcDateTime.AddDays(-ClientSetupSettings.RawActivityRetentionLimitDays - 1);
        new PendingBlockSpool(pendingPath).Write(new[]
        {
            new TimeBlockRecord
            {
                ClientBlockId = Guid.NewGuid(),
                StartTime = end.AddMinutes(-1),
                EndTime = end,
                DurationSeconds = 60,
                ProcessName = "WINWORD",
                WindowTitle = "Old brief",
                ActivityCategory = "Document"
            }
        });
        var worker = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store, _clock, pendingBlocksPath: pendingPath);

        worker.Poll();

        Assert.Empty(_store.Blocks);
        Assert.Empty(new PendingBlockSpool(pendingPath).Load());
    }

    [Fact]
    public void CompleteTemporaryPendingFile_ReplacesPreviousSnapshot()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        var spool = new PendingBlockSpool(pendingPath);
        var previous = new TimeBlockRecord
        {
            ClientBlockId = Guid.NewGuid(),
            StartTime = Start.UtcDateTime,
            EndTime = Start.UtcDateTime.AddSeconds(10),
            DurationSeconds = 10,
            ProcessName = "WINWORD",
            WindowTitle = "Brief.docx"
        };
        var replacement = new TimeBlockRecord
        {
            ClientBlockId = Guid.NewGuid(),
            StartTime = Start.UtcDateTime.AddSeconds(10),
            EndTime = Start.UtcDateTime.AddSeconds(20),
            DurationSeconds = 10,
            ProcessName = "WINWORD",
            WindowTitle = "Replacement.docx"
        };
        spool.Write(new[] { previous });
        string replacementPath = Path.Combine(workspace.Directory, "replacement.json");
        new PendingBlockSpool(replacementPath).Write(new[] { replacement });
        File.Copy(replacementPath, pendingPath + ".tmp");

        TimeBlockRecord recovered = Assert.Single(spool.Load());

        Assert.Equal(replacement.ClientBlockId, recovered.ClientBlockId);
        Assert.True(File.Exists(pendingPath));
        Assert.False(File.Exists(pendingPath + ".tmp"));
    }

    [Fact]
    public void IncompleteTemporaryPendingFile_PreservesPreviousSnapshot()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        var spool = new PendingBlockSpool(pendingPath);
        var previous = new TimeBlockRecord
        {
            ClientBlockId = Guid.NewGuid(),
            StartTime = Start.UtcDateTime,
            EndTime = Start.UtcDateTime.AddSeconds(10),
            DurationSeconds = 10,
            ProcessName = "WINWORD",
            WindowTitle = "Brief.docx"
        };
        spool.Write(new[] { previous });
        File.WriteAllText(pendingPath + ".tmp", "[null]");

        TimeBlockRecord recovered = Assert.Single(spool.Load());

        Assert.Equal(previous.ClientBlockId, recovered.ClientBlockId);
        Assert.False(File.Exists(pendingPath + ".tmp"));

        File.WriteAllText(pendingPath + ".tmp", "[{}]");
        recovered = Assert.Single(spool.Load());

        Assert.Equal(previous.ClientBlockId, recovered.ClientBlockId);
        Assert.False(File.Exists(pendingPath + ".tmp"));
    }

    [Fact]
    public void FailedSaves_KeepMoreThanOneHundredPendingBlocks()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        string pendingPath = Path.Combine(workspace.Directory, "pending-blocks.json");
        var failingStore = new RecordingStore { FailuresRemaining = int.MaxValue };
        var worker = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, failingStore, _clock, pendingBlocksPath: pendingPath);
        _sampler.Current = new ActivitySample("WINWORD", "Brief 0", null);
        worker.Poll();

        for (int index = 1; index <= 101; index++)
        {
            Advance(TimeSpan.FromSeconds(6));
            _sampler.Current = new ActivitySample("WINWORD", $"Brief {index}", null);
            worker.Poll();
        }

        Assert.True(worker.IsStorageDegraded);
        Assert.Equal(101, new PendingBlockSpool(pendingPath).Load().Count);
    }

    [Fact]
    public void ExcludedApp_IsNotRecorded_AndDoesNotCarryTime()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        SettingsService settings = workspace.CreateSettings(s => s.ExcludedProcessNames = new() { "WhatsApp" });
        var worker = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store, _clock, settings,
            Path.Combine(workspace.Directory, "pending-blocks.json"));

        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        worker.Poll();
        Advance(TimeSpan.FromSeconds(30));
        _sampler.Current = new ActivitySample("whatsapp", "Chat with family", null);
        worker.Poll();
        Advance(TimeSpan.FromSeconds(60));
        _sampler.Current = new ActivitySample("chrome", "Docket", null);
        worker.Poll();
        Advance(TimeSpan.FromSeconds(20));
        worker.Stop();

        Assert.Collection(_store.Blocks,
            a => Assert.Equal(("WINWORD", 30), (a.ProcessName, a.DurationSeconds)),
            c => Assert.Equal(("chrome", 20), (c.ProcessName, c.DurationSeconds)));
    }

    [Fact]
    public void LockScreen_IsAlwaysExcluded()
    {
        _sampler.Current = new ActivitySample("LockApp", "Windows Default Lock Screen", null);
        PollFor(TimeSpan.FromSeconds(30));
        _worker.Stop();

        Assert.Empty(_store.Blocks);
    }

    [Fact]
    public void Pause_FinalisesBlock_RecordsNothing_UntilExpiry()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        SettingsService settings = workspace.CreateSettings();
        var worker = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store, _clock, settings,
            Path.Combine(workspace.Directory, "pending-blocks.json"));

        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        worker.Poll();
        Advance(TimeSpan.FromSeconds(30));
        worker.Pause(TimeSpan.FromMinutes(15));
        Assert.Equal(30, Assert.Single(_store.Blocks).DurationSeconds);
        Assert.NotNull(settings.Current.PausedUntil);

        int samplesBefore = _sampler.SampleCount;
        for (int i = 0; i < 10; i++)
        {
            Advance(TimeSpan.FromMinutes(1));
            worker.Poll();
        }

        Assert.Equal(samplesBefore, _sampler.SampleCount);
        Assert.Single(_store.Blocks);

        Advance(TimeSpan.FromMinutes(6));
        worker.Poll();
        Advance(TimeSpan.FromSeconds(10));
        worker.Stop();

        Assert.Null(worker.PausedUntil);
        Assert.Equal(2, _store.Blocks.Count);
        Assert.Equal(10, _store.Blocks[1].DurationSeconds);
    }

    [Fact]
    public void PauseUntilResumed_SurvivesRestart()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        SettingsService settings = workspace.CreateSettings();
        new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store, _clock, settings,
            Path.Combine(workspace.Directory, "pending-blocks.json")).Pause(null);

        var restarted = new TimeTrackerWorker(NullLogger<TimeTrackerWorker>.Instance, _sampler, _store,
            _clock, new SettingsService(new ClientConfigurationStore(workspace.SettingsPath)),
            Path.Combine(workspace.Directory, "pending-blocks.json"));
        Assert.Equal(DateTimeOffset.MaxValue, restarted.PausedUntil);

        restarted.Resume();
        Assert.Null(new SettingsService(new ClientConfigurationStore(workspace.SettingsPath)).Current.PausedUntil);
    }

    [Fact]
    public void SessionLock_EndsActivityAtLastInput_AndRecordsNothingWhileLocked()
    {
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        PollFor(TimeSpan.FromSeconds(30));
        _sampler.Idle = TimeSpan.FromSeconds(4);
        _worker.SetSessionLocked(true);
        Assert.Equal(26, Assert.Single(_store.Blocks).DurationSeconds);

        _sampler.Idle = TimeSpan.Zero;
        PollFor(TimeSpan.FromSeconds(60));
        Assert.Single(_store.Blocks);

        _worker.SetSessionLocked(false);
        PollFor(TimeSpan.FromSeconds(10));
        _worker.Stop();
        Assert.Equal(10, _store.Blocks[1].DurationSeconds);
    }

    [Fact]
    public void LiveBlockSeconds_ReportsUnsavedTime()
    {
        _sampler.Current = new ActivitySample("WINWORD", "A.docx", null);
        PollFor(TimeSpan.FromSeconds(20));

        Assert.Equal(20, _worker.GetLiveBlockSeconds());
    }

    private void Advance(TimeSpan by) => _clock.Advance(by);

    /// <summary>Polls every two seconds, as the worker loop does, for <paramref name="duration"/>.</summary>
    private void PollFor(TimeSpan duration, bool idleSinceStart = false)
    {
        TimeSpan idle = _sampler.Idle;
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromSeconds(2))
        {
            _worker.Poll();
            Advance(TimeSpan.FromSeconds(2));
            _sampler.Idle = idleSinceStart ? idle + elapsed + TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        }
        _worker.Poll();
    }

    private sealed class CommitThenThrowStore(TimeRepository repository) : ITimeBlockStore
    {
        private bool _throwAfterFirstCommit = true;

        public void SaveTimeBlock(TimeBlockRecord block)
        {
            repository.SaveTimeBlock(block);
            if (_throwAfterFirstCommit)
            {
                _throwAfterFirstCommit = false;
                throw new IOException("simulated crash after commit");
            }
        }
    }

    public void Dispose() => _pendingWorkspace.Dispose();
}
