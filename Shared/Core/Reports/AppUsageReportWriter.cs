using System.Globalization;
using System.Text;
using System.Text.Json;
using ComputerUse.TimeTracking.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComputerUse.TimeTracking.Reports;

public interface IDailyReportWriter
{
    /// <summary>Writes the day's PDF (and JSON sidecar) and returns the PDF path actually written.</summary>
    Task<string> WriteAsync(DateOnly date, bool isFinal, DateTimeOffset generatedAt, CancellationToken cancellationToken);
}

/// <summary>Writes one dated PDF to the configured folder plus a JSON sidecar.</summary>
public sealed class AppUsageReportWriter : IDailyReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppUsageReportService _reports;
    private readonly AppUsagePdfWriter _pdfWriter;
    private readonly SettingsService _settings;
    private readonly ILogger<AppUsageReportWriter> _logger;
    private readonly string _jsonDirectory;

    public AppUsageReportWriter(
        AppUsageReportService reports,
        AppUsagePdfWriter pdfWriter,
        SettingsService settings,
        ILogger<AppUsageReportWriter>? logger = null,
        string? jsonDirectory = null)
    {
        _reports = reports;
        _pdfWriter = pdfWriter;
        _settings = settings;
        _logger = logger ?? NullLogger<AppUsageReportWriter>.Instance;
        _jsonDirectory = jsonDirectory ?? AppPaths.JsonReports;
    }

    public static string PdfFileName(DateOnly date) =>
        $"{ProductContext.Current.ReportFilePrefix} {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.pdf";

    /// <summary>Where today's report would be written with the current settings.</summary>
    public string GetDefaultPdfPath(DateOnly date) =>
        Path.Combine(_settings.Current.EffectivePdfReportDirectory, PdfFileName(date));

    public async Task<string> WriteAsync(DateOnly date, bool isFinal, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        DailyAppUsageReport report = _reports.Create(date, generatedAt, isFinal);

        Directory.CreateDirectory(_jsonDirectory);
        string jsonPath = Path.Combine(_jsonDirectory, $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json");
        await WriteAtomicallyAsync(jsonPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);

        byte[] pdf = _pdfWriter.Render(report);
        string pdfPath = GetDefaultPdfPath(date);
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
        try
        {
            await WriteAtomicallyAsync(pdfPath, pdf, cancellationToken).ConfigureAwait(false);
            return pdfPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The PDF is probably open in a viewer that locks it. Write a time-stamped copy next to
            // it rather than failing; the caller shows the path actually written.
            string fallback = FallbackPath(pdfPath, generatedAt);
            _logger.LogWarning("Report {Path} is in use ({Error}); wrote {Fallback} instead.",
                Path.GetFileName(pdfPath), ex.GetType().Name, Path.GetFileName(fallback));
            await WriteAtomicallyAsync(fallback, pdf, cancellationToken).ConfigureAwait(false);
            return fallback;
        }
    }

    internal static string FallbackPath(string pdfPath, DateTimeOffset generatedAt)
    {
        string directory = Path.GetDirectoryName(pdfPath)!;
        string stem = Path.GetFileNameWithoutExtension(pdfPath);
        string candidate = Path.Combine(directory, $"{stem} ({generatedAt.ToString("HH-mm", CultureInfo.InvariantCulture)}).pdf");
        if (!File.Exists(candidate)) return candidate;
        return Path.Combine(directory, $"{stem} ({generatedAt.ToString("HH-mm-ss", CultureInfo.InvariantCulture)}).pdf");
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        string temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
