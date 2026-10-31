using System.Text.RegularExpressions;

namespace ComputerUse.TimeTracking;

internal static class LocalStateReset
{
    public static bool TryDelete(string pdfReportDirectory, out string? error)
    {
        try
        {
            if (Directory.Exists(pdfReportDirectory))
            {
                foreach (string path in Directory.EnumerateFiles(pdfReportDirectory, "*.pdf"))
                {
                    if (GeneratedReportFileName().IsMatch(Path.GetFileName(path))) File.Delete(path);
                }
            }

            if (Directory.Exists(AppPaths.DataDirectory))
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Matches AppUsageReportWriter.PdfFileName and its time-stamped fallback names.
    private static Regex GeneratedReportFileName() => new(
        $"^{Regex.Escape(ProductContext.Current.ReportFilePrefix)} \\d{{4}}-\\d{{2}}-\\d{{2}}( \\(\\d{{2}}-\\d{{2}}(?:-\\d{{2}})?\\))?\\.pdf$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
