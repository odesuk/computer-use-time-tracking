using System.Text.Json;
using ComputerUse.TimeTracking.Storage;

namespace ComputerUse.TimeTracking.Services;

/// <summary>Small local write-ahead file for blocks awaiting a SQLite write.</summary>
internal sealed class PendingBlockSpool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;

    public PendingBlockSpool(string? path = null) => _path = Path.GetFullPath(path ?? AppPaths.PendingBlocks);

    public IReadOnlyList<TimeBlockRecord> Load()
    {
        using FileStream fileLock = AcquireLock();
        RecoverTemporaryFile();
        if (!File.Exists(_path)) return Array.Empty<TimeBlockRecord>();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        return JsonSerializer.Deserialize<List<TimeBlockRecord>>(stream, JsonOptions) ?? new List<TimeBlockRecord>();
    }

    public void Write(IReadOnlyCollection<TimeBlockRecord> blocks)
    {
        string? folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        using FileStream fileLock = AcquireLock();
        RecoverTemporaryFile();

        string temporaryPath = TemporaryPath;
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, blocks, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }

    private FileStream AcquireLock()
    {
        string? folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string TemporaryPath => _path + ".tmp";

    private void RecoverTemporaryFile()
    {
        string temporaryPath = TemporaryPath;
        if (!File.Exists(temporaryPath)) return;

        try
        {
            using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.None);
            List<TimeBlockRecord>? records = JsonSerializer.Deserialize<List<TimeBlockRecord>>(stream, JsonOptions);
            if (records is null || records.Any(record => record is null ||
                !record.ClientBlockId.HasValue || record.EndTime <= record.StartTime ||
                record.DurationSeconds <= 0 || string.IsNullOrWhiteSpace(record.ProcessName)))
            {
                throw new JsonException("Pending block temporary file was incomplete.");
            }
        }
        catch (JsonException)
        {
            // An interrupted write never replaced the prior spool; discard only its incomplete temp file.
            File.Delete(temporaryPath);
            return;
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }
}
