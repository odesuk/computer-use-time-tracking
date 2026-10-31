using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ComputerUse.TimeTracking.Native;
using ComputerUse.TimeTracking.Services;

namespace ComputerUse.TimeTracking.UI;

/// <summary>Settings and first-run setup. Same fields and validation as the Windows dialog.</summary>
internal sealed class SettingsWindow : Window
{
    private const string AccessibilityPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    private readonly TaskCompletionSource<ClientSetupSettings?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ClientSetupSettings _original;
    private readonly bool _firstRun;
    private readonly TextBox _reportDirectory;
    private readonly TextBox? _endOfDay;
    private readonly CheckBox? _includeTitles;
    private readonly TextBox? _excludedApps;
    private readonly CheckBox? _startAtLogin;

    private SettingsWindow(ClientSetupSettings current, bool firstRun)
    {
        _original = current;
        _firstRun = firstRun;
        Title = firstRun ? $"{ProductContext.Current.DisplayName} — setup" : $"{ProductContext.Current.DisplayName} — Settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _reportDirectory = new TextBox { Text = current.EffectivePdfReportDirectory };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await BrowseForFolderAsync();

        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(_reportDirectory);
        folderRow.Children.Add(browse);

        var content = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        content.Children.Add(Heading(firstRun ? "Set up your daily activity reports" : "Settings"));
        content.Children.Add(Label("Where should daily PDF reports be saved?"));
        content.Children.Add(folderRow);
        content.Children.Add(Hint("Reports are PDFs saved in this folder. Activity stays on this Mac."));

        if (!firstRun)
        {
            content.Children.Add(Label("Write today's report at (24-hour time)"));
            _endOfDay = new TextBox
            {
                Text = current.EndOfDayReportTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                Width = 100,
            };
            content.Children.Add(_endOfDay);
            content.Children.Add(Hint("Used when the day ends; a report is also written at sign-out."));

            _includeTitles = new CheckBox
            {
                Content = "Include window titles in the PDF",
                IsChecked = current.IncludeWindowTitlesInPdf,
            };
            content.Children.Add(_includeTitles);
            content.Children.Add(Hint("Off: the PDF shows time per app only. Titles can contain document and email subjects."));

            content.Children.Add(Label("Never record these apps (names, one per line)"));
            _excludedApps = new TextBox
            {
                Text = string.Join(Environment.NewLine, current.ExcludedProcessNames),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 90,
            };
            content.Children.Add(_excludedApps);
            content.Children.Add(Hint("For example Messages or Spotify. Time in these apps is treated like idle time."));

            content.Children.Add(Label("Local data retention"));
            content.Children.Add(Hint(
                $"Raw activity is kept for {ClientSetupSettings.RawActivityRetentionLimitDays} days and generated JSON for " +
                $"{ClientSetupSettings.GeneratedJsonRetentionDays} days. PDFs stay until you delete them."));

            bool packaged = LoginItem.PackagedExecutable() is not null;
            _startAtLogin = new CheckBox
            {
                Content = "Start when I log in",
                IsEnabled = packaged,
                IsChecked = LoginItem.IsEnabled,
            };
            content.Children.Add(_startAtLogin);
            content.Children.Add(Hint(packaged
                ? "Takes effect at your next login."
                : "Available in the packaged app only."));

            content.Children.Add(Label("Window titles"));
            content.Children.Add(Hint(MacNative.IsAccessibilityTrusted()
                ? "Accessibility access is on, so window titles are recorded."
                : "Window titles need Accessibility access. Without it, reports show app names only."));
            var accessibility = new Button { Content = "Open Accessibility settings", HorizontalAlignment = HorizontalAlignment.Left };
            accessibility.Click += (_, _) => Try(() => Shell.OpenUrl(AccessibilityPane));
            content.Children.Add(accessibility);
        }

        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = firstRun ? "Save and start" : "Save", MinWidth = 90, IsDefault = true };
        save.Click += (_, _) => Save();
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, save },
        });

        Content = new ScrollViewer { Content = content };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>Shows the window and completes with the chosen settings, or null when cancelled.</summary>
    public static Task<ClientSetupSettings?> ShowAsync(ClientSetupSettings current, bool firstRun)
    {
        var window = new SettingsWindow(current, firstRun);
        window.Show();
        return window._result.Task;
    }

    private void Save()
    {
        ClientSetupSettings result = _original.Clone();

        if (!TryValidateFolder(_reportDirectory.Text ?? string.Empty, out string? folder, out string? folderError))
        {
            Warn(folderError!);
            return;
        }

        result.PdfReportDirectory = folder!;

        if (!_firstRun)
        {
            if (!TimeOnly.TryParseExact(_endOfDay!.Text?.Trim() ?? string.Empty, new[] { "HH:mm", "H:mm" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly endOfDay))
            {
                Warn("Enter the report time as HH:mm, for example 18:00.");
                return;
            }

            result.EndOfDayReportTime = endOfDay;
            result.IncludeWindowTitlesInPdf = _includeTitles!.IsChecked == true;
            result.ExcludedProcessNames = (_excludedApps!.Text ?? string.Empty)
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ApplyStartAtLogin()) return;
        }

        result.SetupCompleted = true;
        _result.TrySetResult(result);
        Close();
    }

    private bool ApplyStartAtLogin()
    {
        bool wanted = _startAtLogin!.IsChecked == true;
        if (!_startAtLogin.IsEnabled || wanted == LoginItem.IsEnabled) return true;

        try
        {
            LoginItem.SetEnabled(wanted);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Warn($"Start at login could not be changed: {ex.Message}");
            return false;
        }
    }

    private async Task BrowseForFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder for daily reports",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.IsFile) _reportDirectory.Text = folders[0].Path.LocalPath;
    }

    // Writable check: create and delete a temp file, so a read-only or offline folder is caught now rather than
    // at the first report.
    private static bool TryValidateFolder(string text, out string? fullPath, out string? error)
    {
        fullPath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Choose a folder for daily PDF reports.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(text.Trim());
            Directory.CreateDirectory(fullPath);
            string probe = Path.Combine(fullPath, $".time-tracker-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"The report folder can't be used: {ex.Message}";
            return false;
        }
    }

    private void Warn(string message) => _ = MessageWindow.ShowAsync(Title ?? ProductContext.Current.DisplayName, message);

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
    };
}
