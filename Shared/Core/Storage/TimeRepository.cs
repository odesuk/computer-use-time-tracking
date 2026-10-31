using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ComputerUse.TimeTracking.Providers;
using ComputerUse.TimeTracking.Tracking;
using Microsoft.Data.Sqlite;

namespace ComputerUse.TimeTracking.Storage;

public class TimeBlockRecord
{
    public long Id { get; set; }
    /// <summary>Stable client-generated identity used when a durable retry is replayed.</summary>
    public Guid? ClientBlockId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationSeconds { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? AppDisplayName { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public string? EmailSubject { get; set; }
    public string? EmailSender { get; set; }
    public string? EmailRecipients { get; set; }
    public string? EmailConversationKey { get; set; }
    public EmailJunkStatus EmailJunkStatus { get; set; } = EmailJunkStatus.Unknown;
    public string ActivityCategory { get; set; } = "General";
    public string? MatchedMatterId { get; set; }
}

public sealed record LocalMatter(string MatterId, string MatterName);

public sealed record UnassignedEmailConversation(
    string ConversationKey,
    string Subject,
    string Sender,
    string Recipients,
    long RawSeconds,
    int ItemCount,
    bool IsUnidentifiedBlock,
    bool IsMessageIdentity);

public sealed class DailyActivityGroup
{
    public string? MatterId { get; init; }
    public string Category { get; init; } = string.Empty;
    public string ActivityTitle { get; init; } = string.Empty;
    public long RawSeconds { get; init; }
    public int ItemCount { get; init; }
}

public sealed class ActivitySummaryRecord
{
    public DateTime ActivityDate { get; init; }
    public string MatterId { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string ActivityTitle { get; init; } = string.Empty;
    public long RawSeconds { get; init; }
    public int ItemCount { get; init; }
}

/// <summary>One (process, title) pair's active time within a queried range.</summary>
public sealed record AppTitleUsageRow(
    string ProcessName,
    string? DisplayName,
    string Title,
    long Seconds,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    int ItemCount);

public class TimeRepository : ITimeBlockStore
{
    private readonly string? _databasePath;

    public TimeRepository()
    {
    }

    /// <param name="databasePath">Database file; the default per-user database when null.</param>
    public TimeRepository(string? databasePath)
    {
        _databasePath = databasePath;
    }

    public void SaveTimeBlock(TimeBlockRecord block)
    {
        ArgumentNullException.ThrowIfNull(block);

        using var connection = TimeTrackerDatabase.Open(_databasePath);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO TimeBlocks
(ClientBlockId, StartTime, EndTime, DurationSeconds, ProcessName, AppDisplayName, WindowTitle, EmailSubject, EmailSender, EmailRecipients, EmailConversationKey, EmailJunkStatus, ActivityCategory, MatchedMatterId, Status)
            VALUES
($clientBlockId, $start, $end, $duration, $process, $displayName, $title, $emailSub, $emailSender, $emailRecip, $conversationKey, $junkStatus, $category, $matterId, 'Unassigned')
" + (block.ClientBlockId.HasValue ? "ON CONFLICT(ClientBlockId) DO NOTHING;" : ";");
cmd.Parameters.AddWithValue("$clientBlockId", (object?)block.ClientBlockId?.ToString("N") ?? DBNull.Value);

        cmd.Parameters.AddWithValue("$start", FormatUtc(block.StartTime));
        cmd.Parameters.AddWithValue("$end", FormatUtc(block.EndTime));
        cmd.Parameters.AddWithValue("$duration", block.DurationSeconds);
        cmd.Parameters.AddWithValue("$process", block.ProcessName);
        cmd.Parameters.AddWithValue("$displayName", string.IsNullOrWhiteSpace(block.AppDisplayName) ? DBNull.Value : block.AppDisplayName);
        cmd.Parameters.AddWithValue("$title", block.WindowTitle);
        cmd.Parameters.AddWithValue("$emailSub", (object?)block.EmailSubject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$emailSender", (object?)block.EmailSender ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$emailRecip", (object?)block.EmailRecipients ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conversationKey", (object?)block.EmailConversationKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$junkStatus", (int)block.EmailJunkStatus);
        cmd.Parameters.AddWithValue("$category", block.ActivityCategory);
        cmd.Parameters.AddWithValue("$matterId", (object?)block.MatchedMatterId ?? DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    public void SaveMatter(LocalMatter matter)
    {
        ArgumentNullException.ThrowIfNull(matter);
        if (string.IsNullOrWhiteSpace(matter.MatterId) || string.IsNullOrWhiteSpace(matter.MatterName))
            throw new ArgumentException("A matter ID and name are required.", nameof(matter));

        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO LocalMatters (MatterId, MatterName)
VALUES ($matterId, $matterName)
ON CONFLICT(MatterId) DO UPDATE SET MatterName = excluded.MatterName;";
        command.Parameters.AddWithValue("$matterId", matter.MatterId.Trim());
        command.Parameters.AddWithValue("$matterName", matter.MatterName.Trim());
        command.ExecuteNonQuery();
    }

    public List<LocalMatter> GetMatters()
    {
        var matters = new List<LocalMatter>();
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MatterId, MatterName FROM LocalMatters ORDER BY MatterName, MatterId;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) matters.Add(new LocalMatter(reader.GetString(0), reader.GetString(1)));
        return matters;
    }

    public void AssignEmailConversationToMatter(string conversationKey, string matterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(matterId);
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using (var matter = connection.CreateCommand())
        {
            matter.Transaction = transaction;
            matter.CommandText = "SELECT 1 FROM LocalMatters WHERE MatterId = $matterId;";
            matter.Parameters.AddWithValue("$matterId", matterId);
            if (matter.ExecuteScalar() is null) throw new KeyNotFoundException($"Matter '{matterId}' is not in the local matter list.");
        }
        using (var assignment = connection.CreateCommand())
        {
            assignment.Transaction = transaction;
            assignment.CommandText = @"
INSERT INTO EmailConversationAssignments (ConversationKey, MatterId, AssignedAt)
VALUES ($conversationKey, $matterId, $assignedAt)
ON CONFLICT(ConversationKey) DO UPDATE SET MatterId = excluded.MatterId, AssignedAt = excluded.AssignedAt;";
            assignment.Parameters.AddWithValue("$conversationKey", conversationKey);
            assignment.Parameters.AddWithValue("$matterId", matterId);
            assignment.Parameters.AddWithValue("$assignedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            assignment.ExecuteNonQuery();
        }
        using (var blocks = connection.CreateCommand())
        {
            blocks.Transaction = transaction;
            blocks.CommandText = @"
UPDATE TimeBlocks SET MatchedMatterId = $matterId
WHERE EmailConversationKey = $conversationKey AND ActivityCategory = 'Email' AND EmailJunkStatus = $notJunkStatus;";
            blocks.Parameters.AddWithValue("$matterId", matterId);
            blocks.Parameters.AddWithValue("$conversationKey", conversationKey);
            blocks.Parameters.AddWithValue("$notJunkStatus", (int)EmailJunkStatus.NotJunk);
            blocks.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<UnassignedEmailConversation> GetUnassignedEmailConversations()
    {
        var conversations = new List<UnassignedEmailConversation>();
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT EmailConversationKey, COALESCE(NULLIF(EmailSubject, ''), WindowTitle), COALESCE(EmailSender, ''), COALESCE(EmailRecipients, ''),
       SUM(DurationSeconds), COUNT(*),
       CASE WHEN EmailConversationKey LIKE 'email-v1:block:%' OR EmailConversationKey LIKE 'email-v1:legacy:%' THEN 1 ELSE 0 END,
       CASE WHEN EmailConversationKey LIKE 'email-v1:message:%' THEN 1 ELSE 0 END
FROM TimeBlocks
WHERE ActivityCategory = 'Email' AND EmailConversationKey IS NOT NULL AND EmailJunkStatus = $notJunkStatus AND MatchedMatterId IS NULL
GROUP BY EmailConversationKey ORDER BY MAX(StartTime) DESC;";
        command.Parameters.AddWithValue("$notJunkStatus", (int)EmailJunkStatus.NotJunk);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            conversations.Add(new UnassignedEmailConversation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), checked((int)reader.GetInt64(5)), reader.GetInt64(6) == 1, reader.GetInt64(7) == 1));
        }
        return conversations;
    }

    public List<TimeBlockRecord> GetApprovedTimeBlocks()
    {
        var blocks = new List<TimeBlockRecord>();
        using var connection = TimeTrackerDatabase.Open(_databasePath);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, StartTime, EndTime, DurationSeconds, ProcessName, WindowTitle,
                   EmailSubject, EmailSender, EmailRecipients, ActivityCategory, MatchedMatterId
            FROM TimeBlocks
            WHERE Status = 'Approved'
              AND NULLIF(TRIM(MatchedMatterId), '') IS NOT NULL
              AND EndTime > StartTime
            ORDER BY StartTime;
        ";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            blocks.Add(new TimeBlockRecord
            {
                Id = reader.GetInt64(0),
                StartTime = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                EndTime = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DurationSeconds = reader.GetInt32(3),
                ProcessName = reader.GetString(4),
                WindowTitle = reader.GetString(5),
                EmailSubject = reader.IsDBNull(6) ? null : reader.GetString(6),
                EmailSender = reader.IsDBNull(7) ? null : reader.GetString(7),
                EmailRecipients = reader.IsDBNull(8) ? null : reader.GetString(8),
                ActivityCategory = reader.IsDBNull(9) ? "General" : reader.GetString(9),
                MatchedMatterId = reader.GetString(10)
            });
        }

        return blocks;
    }

    public void ApproveTimeBlocks(IReadOnlyCollection<long> blockIds)
    {
        ArgumentNullException.ThrowIfNull(blockIds);
        if (blockIds.Count == 0) return;

        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            UPDATE TimeBlocks
            SET Status = 'Approved'
            WHERE Id = $id
              AND Status = 'Unassigned'
              AND NULLIF(TRIM(MatchedMatterId), '') IS NOT NULL
              AND EndTime > StartTime;
        ";
        var idParameter = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (long blockId in new HashSet<long>(blockIds))
        {
            idParameter.Value = blockId;
            if (cmd.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Time block {blockId} is not an unassigned, matter-associated block eligible for approval.");
            }
        }

        transaction.Commit();
    }

    public void ArchiveApprovedTimeBlocks(
        IReadOnlyCollection<long> blockIds,
        IReadOnlyCollection<ActivitySummaryRecord> summaries)
    {
        ArgumentNullException.ThrowIfNull(blockIds);
        ArgumentNullException.ThrowIfNull(summaries);
        var distinctBlockIds = new HashSet<long>(blockIds);
        if (distinctBlockIds.Count == 0)
        {
            if (summaries.Count != 0)
            {
                throw new ArgumentException("Summaries cannot be archived without source blocks.", nameof(summaries));
            }

            return;
        }

        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using var summaryCommand = connection.CreateCommand();
        summaryCommand.Transaction = transaction;
        summaryCommand.CommandText = @"
            INSERT INTO DailyActivitySummaries
                (ActivityDate, MatterId, ActivityCategory, ActivityTitle, RawSeconds, ItemCount)
            VALUES
                ($date, $matter, $category, $activity, $seconds, $count)
            ON CONFLICT(ActivityDate, MatterId, ActivityCategory, ActivityTitle)
            DO UPDATE SET
                RawSeconds = DailyActivitySummaries.RawSeconds + excluded.RawSeconds,
                ItemCount = DailyActivitySummaries.ItemCount + excluded.ItemCount;
        ";
        var dateParameter = summaryCommand.Parameters.Add("$date", SqliteType.Text);
        var matterParameter = summaryCommand.Parameters.Add("$matter", SqliteType.Text);
        var categoryParameter = summaryCommand.Parameters.Add("$category", SqliteType.Text);
        var activityParameter = summaryCommand.Parameters.Add("$activity", SqliteType.Text);
        var secondsParameter = summaryCommand.Parameters.Add("$seconds", SqliteType.Integer);
        var countParameter = summaryCommand.Parameters.Add("$count", SqliteType.Integer);

        foreach (ActivitySummaryRecord summary in summaries)
        {
            if (string.IsNullOrWhiteSpace(summary.MatterId) || string.IsNullOrWhiteSpace(summary.ActivityTitle))
            {
                throw new ArgumentException("Archived summaries require a matter ID and activity title.", nameof(summaries));
            }

            dateParameter.Value = summary.ActivityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            matterParameter.Value = summary.MatterId;
            categoryParameter.Value = summary.Category;
            activityParameter.Value = summary.ActivityTitle;
            secondsParameter.Value = summary.RawSeconds;
            countParameter.Value = summary.ItemCount;
            summaryCommand.ExecuteNonQuery();
        }

        using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM TimeBlocks WHERE Id = $id AND Status = 'Approved';";
        var idParameter = deleteCommand.Parameters.Add("$id", SqliteType.Integer);

        foreach (long blockId in distinctBlockIds)
        {
            idParameter.Value = blockId;
            if (deleteCommand.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Time block {blockId} was not in the Approved state; archive transaction was rolled back.");
            }
        }

        transaction.Commit();
    }

    public List<DailyActivityGroup> GetDailyActivityGroups(DateTime date)
    {
        var results = new List<DailyActivityGroup>();
        var localDate = date.Kind == DateTimeKind.Utc ? date.ToLocalTime().Date : date.Date;
        var utcStart = DateTime.SpecifyKind(localDate, DateTimeKind.Local).ToUniversalTime();
        var utcEnd = DateTime.SpecifyKind(localDate.AddDays(1), DateTimeKind.Local).ToUniversalTime();

        using var connection = TimeTrackerDatabase.Open(_databasePath);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            WITH ActivityContributions AS (
                SELECT
                    MatterId,
                    ActivityCategory,
                    ActivityTitle,
                    RawSeconds,
                    ItemCount
                FROM DailyActivitySummaries
                WHERE ActivityDate = $activityDate

                UNION ALL

                SELECT
                    MatchedMatterId,
                    COALESCE(ActivityCategory, 'General') AS ActivityCategory,
                    COALESCE(NULLIF(EmailSubject, ''), WindowTitle) AS ActivityTitle,
                    SUM(CAST(ROUND((
                        julianday(CASE WHEN EndTime < $end THEN EndTime ELSE $end END) -
                        julianday(CASE WHEN StartTime > $start THEN StartTime ELSE $start END)
                    ) * 86400) AS INTEGER)) AS RawSeconds,
                    COUNT(*) AS ItemCount
                FROM TimeBlocks
                WHERE EndTime > $start AND StartTime < $end
                GROUP BY MatchedMatterId,
                         COALESCE(ActivityCategory, 'General'),
                         COALESCE(NULLIF(EmailSubject, ''), WindowTitle)
            ), DailyGroups AS (
                SELECT
                    MatterId,
                    ActivityCategory,
                    ActivityTitle,
                    SUM(RawSeconds) AS RawSeconds,
                    SUM(ItemCount) AS ItemCount
                FROM ActivityContributions
                GROUP BY MatterId, ActivityCategory, ActivityTitle
            )
            SELECT MatterId, ActivityCategory, ActivityTitle, RawSeconds, ItemCount
            FROM DailyGroups
            ORDER BY RawSeconds DESC, ActivityTitle;
        ";
        cmd.Parameters.AddWithValue("$activityDate", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$start", utcStart.ToString("o"));
        cmd.Parameters.AddWithValue("$end", utcEnd.ToString("o"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? matterId = reader.IsDBNull(0) ? null : reader.GetString(0);
            string category = reader.IsDBNull(1) ? "General" : reader.GetString(1);
            string activityTitle = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            results.Add(new DailyActivityGroup
            {
                MatterId = matterId,
                Category = category,
                ActivityTitle = activityTitle,
                RawSeconds = reader.GetInt64(3),
                ItemCount = checked((int)reader.GetInt64(4))
            });
        }

        return results;
    }

    /// <summary>
    /// Active seconds per (process, title) between two UTC instants. Blocks crossing either bound
    /// are clipped to it. Title is the email subject when present, otherwise the window title.
    /// </summary>
    public List<AppTitleUsageRow> GetAppTitleUsage(DateTime utcStart, DateTime utcEnd)
    {
        var results = new List<AppTitleUsageRow>();
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ProcessName,
                   MAX(AppDisplayName) AS DisplayName,
                   COALESCE(NULLIF(EmailSubject, ''), WindowTitle) AS Title,
                   SUM(CAST(ROUND((julianday(MIN(EndTime, $end)) - julianday(MAX(StartTime, $start))) * 86400) AS INTEGER)) AS Seconds,
                   MIN(MAX(StartTime, $start)) AS FirstSeen,
                   MAX(MIN(EndTime, $end)) AS LastSeen,
                   COUNT(*) AS ItemCount
            FROM TimeBlocks
            WHERE EndTime > $start AND StartTime < $end
            GROUP BY ProcessName, Title;
        ";
        cmd.Parameters.AddWithValue("$start", FormatUtc(utcStart));
        cmd.Parameters.AddWithValue("$end", FormatUtc(utcEnd));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AppTitleUsageRow(
                ProcessName: reader.GetString(0),
                DisplayName: reader.IsDBNull(1) ? null : reader.GetString(1),
                Title: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Seconds: reader.GetInt64(3),
                FirstSeenUtc: ParseUtc(reader.GetString(4)),
                LastSeenUtc: ParseUtc(reader.GetString(5)),
                ItemCount: checked((int)reader.GetInt64(6))));
        }

        return results;
    }

    /// <summary>Total recorded seconds between two UTC instants, clipped to the bounds.</summary>
    public long GetActiveSeconds(DateTime utcStart, DateTime utcEnd)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(CAST(ROUND((julianday(MIN(EndTime, $end)) - julianday(MAX(StartTime, $start))) * 86400) AS INTEGER)), 0)
            FROM TimeBlocks
            WHERE EndTime > $start AND StartTime < $end;
        ";
        cmd.Parameters.AddWithValue("$start", FormatUtc(utcStart));
        cmd.Parameters.AddWithValue("$end", FormatUtc(utcEnd));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public bool HasActivity(DateTime utcStart, DateTime utcEnd)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM TimeBlocks WHERE EndTime > $start AND StartTime < $end);";
        cmd.Parameters.AddWithValue("$start", FormatUtc(utcStart));
        cmd.Parameters.AddWithValue("$end", FormatUtc(utcEnd));
        return (long)(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    /// <summary>
    /// Deletes every raw activity block that ended before <paramref name="cutoffUtc"/>.
    /// </summary>
    public int PurgeRawActivityBefore(DateTime cutoffUtc)
    {
        using var connection = TimeTrackerDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using var blocks = connection.CreateCommand();
        blocks.Transaction = transaction;
        blocks.CommandText = "DELETE FROM TimeBlocks WHERE EndTime < $cutoff;";
        blocks.Parameters.AddWithValue("$cutoff", FormatUtc(cutoffUtc));
        int deleted = blocks.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    // Stored timestamps are ISO-8601 UTC in one fixed-width format, so text comparison is chronological.
    internal static string FormatUtc(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
