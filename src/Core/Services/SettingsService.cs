using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComputerUse.TimeTracking.Services;

/// <summary>
/// Live settings. Readers (tracker, report writer, scheduler) call <see cref="Current"/> on each
/// use, so changes from the Settings window apply without a restart.
/// </summary>
public sealed class SettingsService
{
    private readonly ClientConfigurationStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _gate = new();
    private ClientSetupSettings _current;

    public SettingsService(ClientConfigurationStore store, ILogger<SettingsService>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<SettingsService>.Instance;
        _current = LoadOrDefault();
    }

    /// <summary>Raised after a successful save, with the new settings. May run on any thread.</summary>
    public event EventHandler<ClientSetupSettings>? Changed;

    /// <summary>A copy of the current settings; mutating it has no effect until <see cref="Save"/>.</summary>
    public ClientSetupSettings Current
    {
        get
        {
            lock (_gate) return _current.Clone();
        }
    }

    public void Save(ClientSetupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ClientSetupSettings copy = settings.Clone();
        lock (_gate)
        {
            _store.Save(copy);
            _current = copy;
        }

        Changed?.Invoke(this, copy.Clone());
    }

    /// <summary>Applies <paramref name="change"/> to the current settings and saves them.</summary>
    public ClientSetupSettings Update(Action<ClientSetupSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ClientSetupSettings updated;
        lock (_gate)
        {
            updated = _current.Clone();
            change(updated);
            if (string.IsNullOrWhiteSpace(updated.PdfReportDirectory))
            {
                updated.PdfReportDirectory = updated.EffectivePdfReportDirectory;
            }

            _store.Save(updated);
            _current = updated;
        }

        Changed?.Invoke(this, updated.Clone());
        return updated.Clone();
    }

    private ClientSetupSettings LoadOrDefault()
    {
        try
        {
            ClientSetupSettings? loaded = _store.Load();
            if (loaded is null) return new ClientSetupSettings();
            loaded.ExcludedProcessNames ??= new List<string>();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file must not stop tracking. Setup runs again and overwrites it.
            _logger.LogError(ex, "The settings file could not be read; using defaults.");
            return new ClientSetupSettings();
        }
    }
}
