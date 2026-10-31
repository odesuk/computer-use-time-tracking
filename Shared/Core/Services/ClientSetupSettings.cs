using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComputerUse.TimeTracking.Services;

/// <summary>
/// User settings persisted as JSON. Every property added after the first release has a default,
/// so settings files written by older versions keep loading.
/// </summary>
public sealed class ClientSetupSettings
{
    public const int RawActivityRetentionLimitDays = 60;
    public const int GeneratedJsonRetentionDays = 7;

    public bool SetupCompleted { get; set; }

    public string PdfReportDirectory { get; set; } = string.Empty;

    /// <summary>Preserves settings owned by a separately compiled host without understanding them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Local time at which today's report is written if the app is running.</summary>
    public TimeOnly EndOfDayReportTime { get; set; } = new(18, 0);

    /// <summary>Off: the PDF shows app totals only, with no window titles or email subjects.</summary>
    public bool IncludeWindowTitlesInPdf { get; set; } = true;

    /// <summary>Process names that are never recorded (e.g. personal messaging apps).</summary>
    public List<string> ExcludedProcessNames { get; set; } = new();

    /// <summary>Kept so existing settings files deserialize; retention is fixed by policy.</summary>
    public int RawActivityRetentionDays { get; set; } = RawActivityRetentionLimitDays;

    public int EffectiveRetentionDays => RawActivityRetentionLimitDays;

    /// <summary>Set while tracking is paused; <see cref="DateTimeOffset.MaxValue"/> means until resumed.</summary>
    public DateTimeOffset? PausedUntil { get; set; }

    public int SchemaVersion { get; set; } = 2;

    public ClientSetupSettings Clone() => new()
    {
        SetupCompleted = SetupCompleted,
        PdfReportDirectory = PdfReportDirectory,
        ExtensionData = ExtensionData is null ? null : new Dictionary<string, JsonElement>(ExtensionData),
        EndOfDayReportTime = EndOfDayReportTime,
        IncludeWindowTitlesInPdf = IncludeWindowTitlesInPdf,
        ExcludedProcessNames = new List<string>(ExcludedProcessNames ?? new List<string>()),
        RawActivityRetentionDays = RawActivityRetentionDays,
        PausedUntil = PausedUntil,
        SchemaVersion = SchemaVersion,
    };

    /// <summary>The configured PDF folder, or the default when none has been chosen yet.</summary>
    public string EffectivePdfReportDirectory => string.IsNullOrWhiteSpace(PdfReportDirectory)
        ? AppPaths.DefaultPdfReportDirectory
        : PdfReportDirectory;
}
