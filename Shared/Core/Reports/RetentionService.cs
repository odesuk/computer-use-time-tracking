using System.Globalization;
using System.Text.RegularExpressions;
using ComputerUse.TimeTracking.Services;
using ComputerUse.TimeTracking.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComputerUse.TimeTracking.Reports;

public sealed class RetentionService
{
    private static readonly Regex GeneratedSidecarName = new(@"^\d{4}-\d{2}-\d{2}\.(?:json|csv)(?:\.tmp)?$", RegexOptions.CultureInvariant);
    private readonly TimeRepository _repository;
    private readonly ReportRunRepository _runs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(TimeRepository repository, ReportRunRepository runs, TimeProvider timeProvider, ILogger<RetentionService>? logger = null)
    {
        _repository = repository;
        _runs = runs;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<RetentionService>.Instance;
    }

    public int Purge()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        var failures = new List<Exception>();
        int sidecars = Try(() => PurgeSidecarsBefore(now.AddDays(-ClientSetupSettings.GeneratedJsonRetentionDays)), failures, "generated report sidecars");
        int blocks = Try(() => _repository.PurgeRawActivityBefore(now.AddDays(-ClientSetupSettings.RawActivityRetentionLimitDays)), failures, "raw activity");
        int runs = Try(() => _runs.PurgeBefore(now.AddDays(-ClientSetupSettings.RawActivityRetentionLimitDays)), failures, "report runs");
        if (failures.Count > 0) throw new AggregateException("One or more retention targets could not be cleaned.", failures);
        if (blocks > 0 || runs > 0 || sidecars > 0) _logger.LogInformation("Retention removed {Blocks} activity blocks, {Runs} report records, and {Sidecars} report sidecars.", blocks, runs, sidecars);
        return blocks;
    }

    private int PurgeSidecarsBefore(DateTime cutoffUtc)
    {
        if (!Directory.Exists(AppPaths.JsonReports)) return 0;
        int deleted = 0;
        var failures = new List<Exception>();
        foreach (string path in Directory.EnumerateFiles(AppPaths.JsonReports, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (!IsGeneratedSidecarName(name)) continue;
            try { if (File.GetLastWriteTimeUtc(path) < cutoffUtc) { File.Delete(path); deleted++; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogError(ex, "Retention cleanup could not inspect or delete report sidecar {Path}.", path); failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("One or more report sidecars could not be deleted.", failures);
        return deleted;
    }

    internal static bool IsGeneratedSidecarName(string name) =>
        GeneratedSidecarName.IsMatch(name) &&
        DateOnly.TryParseExact(name[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private int Try(Func<int> action, ICollection<Exception> failures, string target)
    {
        try { return action(); }
        catch (Exception ex) { _logger.LogError(ex, "Retention cleanup failed for {Target}.", target); failures.Add(ex); return 0; }
    }
}
