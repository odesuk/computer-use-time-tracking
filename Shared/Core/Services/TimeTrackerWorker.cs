using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Storage;
using ComputerUse.TimeTracking.Tracking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ComputerUse.TimeTracking.Services;

/// <summary>Flushes the in-progress block so readers (reports, status) see time up to now.</summary>
public interface IActivityCheckpoint
{
    void Checkpoint();
}

/// <summary>User-facing tracking controls (tray menu).</summary>
public interface ITrackingControl
{
    /// <summary>When tracking resumes; <see cref="DateTimeOffset.MaxValue"/> means until resumed; null when not paused.</summary>
    DateTimeOffset? PausedUntil { get; }

    /// <param name="duration">How long to pause; null pauses until <see cref="Resume"/>.</param>
    void Pause(TimeSpan? duration);

    void Resume();
}

public class TimeTrackerWorker : BackgroundService, IActivityCheckpoint, ITrackingControl
{
    /// <summary>Kept for compatibility; every positive foreground interval is retained.</summary>
    public const int MinimumBlockSeconds = 5;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(1);

    // A gap this long between polls means the machine slept or the process was suspended.
    private static readonly TimeSpan SuspendGapThreshold = TimeSpan.FromSeconds(60);

    // Never recorded, whatever the settings say: the lock screen and the tracker itself.
    private static readonly string[] AlwaysExcludedProcessNames = { "LockApp", CurrentProcessName() };

    private readonly ILogger<TimeTrackerWorker> _logger;
    private readonly IActivitySampler _sampler;
    private readonly ITimeBlockStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly SettingsService? _settings;
    private readonly PendingBlockSpool _pendingSpool;
    private readonly TimeSpan _idleThreshold = TimeSpan.FromMinutes(3);

    // Guards all block state below. Sampling (P/Invoke, COM) happens outside it.
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly Queue<TimeBlockRecord> _pendingBlocks = new();

    private DateTime? _lastPollTime;
    private DateTime? _blockStartTime;
    private string _currentProcess = string.Empty;
    private string? _currentDisplayName;
    private string _currentTitle = string.Empty;
    private EmailActivityMetadata? _currentEmail;
    private DateTimeOffset? _pausedUntil;
    private DateTime? _lastCheckpointTime;
    private bool _sessionLocked;
    private bool _storageDegraded;
    private bool _spoolUnreadable;
    private bool _spoolNeedsClear;

    public TimeTrackerWorker(
        ILogger<TimeTrackerWorker> logger,
        IActivitySampler sampler,
        ITimeBlockStore store,
        TimeProvider timeProvider,
        SettingsService? settings = null,
        string? pendingBlocksPath = null)
    {
        _logger = logger;
        _sampler = sampler;
        _store = store;
        _timeProvider = timeProvider;
        _settings = settings;
        _pausedUntil = settings?.Current.PausedUntil;
        _pendingSpool = new PendingBlockSpool(pendingBlocksPath);
        try
        {
            foreach (TimeBlockRecord? block in _pendingSpool.Load())
            {
                if (block?.ClientBlockId.HasValue == true) _pendingBlocks.Enqueue(block);
            }
            _spoolNeedsClear = _pendingBlocks.Count > 0;
        }
        catch (Exception ex)
        {
            _storageDegraded = true;
            _spoolUnreadable = true;
            _logger.LogError(ex, "Could not read pending time blocks; storage needs attention.");
        }
    }

    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (_gate) return _pausedUntil;
        }
    }

    /// <summary>True while a pending block cannot be durably saved to the local store.</summary>
    public bool IsStorageDegraded
    {
        get
        {
            lock (_gate) return _storageDegraded;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{ProductName} service running.", ProductContext.Current.DisplayName);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    // One bad sample (a COM hiccup, a vanished process) must not stop tracking.
                    _logger.LogError(ex, "Activity poll failed.");
                }

                await Task.Delay(PollInterval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>Finalises the live block and retries any blocks that failed to save.</summary>
    internal void Stop()
    {
        DateTime now = UtcNow;
        TimeSpan idle = SafeIdleTime();
        lock (_gate)
        {
            EndActivity(idle >= _idleThreshold ? now - idle : now);
        }
        FlushPendingBlocks();
    }

    internal void Poll()
    {
        DateTime now = UtcNow;
        TimeSpan idleTime = _sampler.GetIdleTime();

        bool suspended;
        lock (_gate) suspended = _sessionLocked || IsPaused(now);
        ActivitySample? sample = !suspended && idleTime < _idleThreshold ? _sampler.Sample() : null;

        // A slow sample (Outlook busy or showing a modal dialog) must not look like sleep to the next
        // poll, so the gap is measured from when this poll finished.
        DateTime finished = UtcNow;
        lock (_gate)
        {
            Apply(now, idleTime, sample);
            _lastPollTime = finished;
            if (!_lastCheckpointTime.HasValue || now - _lastCheckpointTime.Value >= CheckpointInterval)
            {
                CheckpointCore(now, idleTime);
            }
        }
        FlushPendingBlocks();
    }

    /// <summary>
    /// Saves the live block up to the last input and continues the same activity in a new block,
    /// so a report generated now includes it. Splitting one block into two adjacent blocks neither
    /// loses nor double-counts time.
    /// </summary>
    public void Checkpoint()
    {
        DateTime now = UtcNow;
        TimeSpan idle = SafeIdleTime();
        lock (_gate)
        {
            CheckpointCore(now, idle);
        }
        FlushPendingBlocks();
    }

    /// <summary>Seconds in the live block, not yet saved.</summary>
    public long GetLiveBlockSeconds()
    {
        DateTime now = UtcNow;
        lock (_gate)
        {
            if (!_blockStartTime.HasValue) return 0;
            return Math.Max(0, (long)(now - _blockStartTime.Value).TotalSeconds);
        }
    }

    /// <summary>
    /// Ends the current activity at the last input (screen locked, machine suspending).
    /// </summary>
    public void EndActivityNow(string reason)
    {
        DateTime now = UtcNow;
        TimeSpan idle = SafeIdleTime();
        lock (_gate)
        {
            DateTime end = now - idle;
            if (_blockStartTime.HasValue && end < _blockStartTime.Value) end = _blockStartTime.Value;
            EndActivity(end);
        }

        FlushPendingBlocks();

        _logger.LogDebug("Activity ended: {Reason}.", reason);
    }

    /// <summary>While locked nothing is recorded, even if a window stays in the foreground.</summary>
    public void SetSessionLocked(bool locked)
    {
        if (locked) EndActivityNow("session locked");
        lock (_gate) _sessionLocked = locked;
    }

    public void Pause(TimeSpan? duration)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset until = duration is { } d && d > TimeSpan.Zero ? now + d : DateTimeOffset.MaxValue;
        lock (_gate)
        {
            EndActivity(now.UtcDateTime);
            _pausedUntil = until;
        }

        PersistPause(until);
        FlushPendingBlocks();
        _logger.LogInformation("Tracking paused until {Until}.", until == DateTimeOffset.MaxValue ? "resumed" : until.ToLocalTime().ToString("t"));
    }

    public void Resume()
    {
        lock (_gate) _pausedUntil = null;
        PersistPause(null);
        _logger.LogInformation("Tracking resumed.");
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private void Apply(DateTime now, TimeSpan idleTime, ActivitySample? sample)
    {
        if (_lastPollTime.HasValue && now - _lastPollTime.Value > SuspendGapThreshold)
        {
            // End the block when polling stopped (or at the last input, if earlier), so time
            // spent asleep is never recorded even where the idle counter does not span sleep.
            DateTime lastInput = now - idleTime;
            EndActivity(lastInput < _lastPollTime.Value ? lastInput : _lastPollTime.Value);
        }

        if (_pausedUntil.HasValue && now >= _pausedUntil.Value.UtcDateTime)
        {
            _pausedUntil = null;
            ThreadPool.QueueUserWorkItem(_ => PersistPause(null));
        }

        if (_sessionLocked || IsPaused(now))
        {
            EndActivity(now);
            return;
        }

        if (idleTime >= _idleThreshold)
        {
            // Exclude the time after the last keyboard or mouse input.
            EndActivity(now - idleTime);
            return;
        }

        // Paused or locked between the check in Poll and here: nothing was sampled.
        if (sample is null) return;

        if (IsExcluded(sample.ProcessName))
        {
            // Treated like idle: the excluded app's time is neither recorded nor carried forward.
            EndActivity(now);
            return;
        }

        string title = WindowTitleNormalizer.Normalize(sample.WindowTitle);
        bool hasChanged = !string.Equals(sample.ProcessName, _currentProcess, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(title, _currentTitle, StringComparison.Ordinal) ||
                          !string.Equals(EmailIdentity(sample.Email), EmailIdentity(_currentEmail), StringComparison.Ordinal);

        if (hasChanged)
        {
            FinalizeCurrentBlock(now);

            _blockStartTime = now;
            _currentProcess = sample.ProcessName;
            _currentDisplayName = sample.DisplayName;
            _currentTitle = title;
            _currentEmail = sample.Email;
        }
        else
        {
            // Same activity: keep the latest subject, so a draft carries its final subject.
            if (sample.Email != null) _currentEmail = sample.Email;
            if (!string.IsNullOrWhiteSpace(sample.DisplayName)) _currentDisplayName = sample.DisplayName;
        }
    }

    private void CheckpointCore(DateTime now, TimeSpan idle)
    {
        _lastCheckpointTime = now;

        // Untitled windows (desktop, taskbar) are never saved; see FinalizeCurrentBlock.
        if (!_blockStartTime.HasValue || string.IsNullOrEmpty(_currentTitle)) return;

        // Split at the last input, not at "now": if the user has been idle for a minute and
        // stays idle, the idle cut-off will trim the second half to nothing.
        DateTime split = now - idle;

        // Polling stopped (sleep without a suspend notification): nothing after the last poll is work.
        if (_lastPollTime.HasValue && now - _lastPollTime.Value > SuspendGapThreshold && _lastPollTime.Value < split)
        {
            split = _lastPollTime.Value;
        }

        if (split < _blockStartTime.Value) split = _blockStartTime.Value;
        if ((split - _blockStartTime.Value).TotalSeconds < MinimumBlockSeconds) return;

        SaveBlock(_blockStartTime.Value, split);
        _blockStartTime = split;
    }

    // Classic Outlook gives a stable EntryID; subject edits while composing then don't split blocks.
    private static string? EmailIdentity(EmailActivityMetadata? metadata) =>
        metadata is null ? null : !string.IsNullOrEmpty(metadata.EntryId) ? metadata.EntryId : metadata.Subject;

    private bool IsPaused(DateTime now) => _pausedUntil.HasValue && now < _pausedUntil.Value.UtcDateTime;

    private bool IsExcluded(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (string excluded in AlwaysExcludedProcessNames)
        {
            if (string.Equals(excluded, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (_settings is null) return false;
        foreach (string excluded in _settings.Current.ExcludedProcessNames)
        {
            if (string.Equals(excluded?.Trim(), processName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Ends the activity at <paramref name="endTime"/>.</summary>
    private void EndActivity(DateTime endTime)
    {
        FinalizeCurrentBlock(endTime);
    }

    private void FinalizeCurrentBlock(DateTime endTime)
    {
        if (!_blockStartTime.HasValue) return;

        DateTime start = _blockStartTime.Value;
        int durationSeconds = (int)Math.Max(0, (endTime - start).TotalSeconds);

        // Keep every positive interval with the process that was actually foreground.
        if (durationSeconds > 0 && !string.IsNullOrWhiteSpace(_currentProcess))
        {
            SaveBlock(start, endTime);
        }

        ResetCurrent();
    }

    private void SaveBlock(DateTime start, DateTime end)
    {
        var record = new TimeBlockRecord
        {
            ClientBlockId = Guid.NewGuid(),
            StartTime = start,
            EndTime = end,
            DurationSeconds = (int)(end - start).TotalSeconds,
            ProcessName = _currentProcess,
            AppDisplayName = _currentDisplayName,
            WindowTitle = _currentTitle,
            EmailSubject = _currentEmail?.Subject,
            EmailSender = _currentEmail?.SenderEmail,
            EmailRecipients = _currentEmail?.Recipients,
            EmailConversationKey = EmailConversationIdentity.CreateKey(_currentEmail) ??
                (_currentEmail is null ? null : EmailConversationIdentity.CreateUnidentifiedBlockKey()),
            EmailJunkStatus = _currentEmail?.JunkStatus ?? EmailJunkStatus.Unknown,
            ActivityCategory = _currentEmail != null ? "Email" : "Document"
        };

        _pendingBlocks.Enqueue(record);
    }

    // Saves queued blocks in order outside the activity-state lock, so the tray stays responsive.
    private void FlushPendingBlocks()
    {
        lock (_flushGate)
        {
            while (true)
            {
                List<TimeBlockRecord> blocks;
                int expired;
                bool spoolUnreadable;
                bool spoolNeedsClear;
                lock (_gate)
                {
                    expired = RemoveExpiredPendingBlocks(UtcNow.AddDays(-ClientSetupSettings.RawActivityRetentionLimitDays));
                    blocks = _pendingBlocks.ToList();
                    spoolUnreadable = _spoolUnreadable;
                    spoolNeedsClear = _spoolNeedsClear || expired > 0;
                }

                if (expired > 0)
                {
                    _logger.LogWarning("Removed {Count} pending time blocks past the raw activity retention limit.", expired);
                }

                bool spoolWritten = false;
                if (!spoolUnreadable && (blocks.Count > 0 || spoolNeedsClear))
                {
                    try
                    {
                        _pendingSpool.Write(blocks);
                        spoolWritten = true;
                        lock (_gate) _spoolNeedsClear = false;
                    }
                    catch (Exception ex)
                    {
                        // The database may still be available (for example, a transient file lock).
                        // Keep retrying the spool, but do not stop recording solely because it failed.
                        SetStorageDegraded(ex, "Could not durably queue pending time blocks");
                    }
                }

                if (blocks.Count == 0)
                {
                    if (!spoolUnreadable && (!spoolNeedsClear || spoolWritten)) SetStorageRecovered();
                    return;
                }

                TimeBlockRecord block = blocks[0];
                try
                {
                    _store.SaveTimeBlock(block);
                }
                catch (Exception ex)
                {
                    // Never log titles, subjects or recipients: logs have no confidentiality controls.
                    SetStorageDegraded(ex, "Failed to save a pending time block");
                    return;
                }

                lock (_gate)
                {
                    if (_pendingBlocks.Count > 0 && _pendingBlocks.Peek().ClientBlockId == block.ClientBlockId)
                    {
                        _pendingBlocks.Dequeue();
                    }

                    _spoolNeedsClear = true;
                }
                _logger.LogDebug("Saved {Seconds}s | App: {App}", block.DurationSeconds, block.ProcessName);
            }
        }
    }

    private int RemoveExpiredPendingBlocks(DateTime cutoff)
    {
        int removed = 0;
        int count = _pendingBlocks.Count;
        for (int index = 0; index < count; index++)
        {
            TimeBlockRecord block = _pendingBlocks.Dequeue();
            if (block.EndTime < cutoff)
            {
                removed++;
                continue;
            }

            _pendingBlocks.Enqueue(block);
        }

        return removed;
    }

    private void SetStorageDegraded(Exception exception, string message)
    {
        lock (_gate) _storageDegraded = true;
        _logger.LogError(exception, "{Message}; tracking will retry.", message);
    }

    private void SetStorageRecovered()
    {
        bool recovered;
        lock (_gate)
        {
            recovered = _storageDegraded;
            _storageDegraded = false;
        }

        if (recovered) _logger.LogInformation("Local time-block storage recovered.");
    }

    private void ResetCurrent()
    {
        _blockStartTime = null;
        _currentProcess = string.Empty;
        _currentDisplayName = null;
        _currentTitle = string.Empty;
        _currentEmail = null;
    }

    private void PersistPause(DateTimeOffset? until)
    {
        if (_settings is null) return;
        try
        {
            _settings.Update(settings => settings.PausedUntil = until);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the pause state.");
        }
    }

    private TimeSpan SafeIdleTime()
    {
        try
        {
            TimeSpan idle = _sampler.GetIdleTime();
            return idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static string CurrentProcessName()
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            return current.ProcessName;
        }
        catch (Exception)
        {
            return ProductContext.Current.ExecutableProcessName;
        }
    }
}
