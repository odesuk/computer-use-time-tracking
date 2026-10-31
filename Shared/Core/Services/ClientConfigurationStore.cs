using System.IO;
using System.Text.Json;

namespace ComputerUse.TimeTracking.Services;

public sealed class ClientConfigurationStore
{
    public static string DefaultSettingsPath => AppPaths.Settings;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public ClientConfigurationStore(string? settingsPath = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath ?? DefaultSettingsPath);
    }

    public ClientSetupSettings? Load()
    {
        if (!File.Exists(_settingsPath)) return null;

        using var stream = File.OpenRead(_settingsPath);
        return JsonSerializer.Deserialize<ClientSetupSettings>(stream);
    }

    public void Save(ClientSetupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.PdfReportDirectory))
        {
            throw new ArgumentException("A PDF report folder is required.", nameof(settings));
        }

        string reportDirectory = Path.GetFullPath(settings.PdfReportDirectory.Trim());
        Directory.CreateDirectory(reportDirectory);
        string? settingsDirectory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(settingsDirectory)) Directory.CreateDirectory(settingsDirectory);

        settings.PdfReportDirectory = reportDirectory;
        settings.SetupCompleted = true;

        string temporaryPath = _settingsPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
