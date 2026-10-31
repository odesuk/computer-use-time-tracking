using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComputerUse.TimeTracking.Services;
using Microsoft.Win32;

namespace ComputerUse.TimeTracking.UI;

/// <summary>
/// First-run setup (PDF folder only) and the Settings window opened from the tray. Does not
/// create a WPF <see cref="Application"/>: the app owns the one Application per process.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly bool _firstRun;
    private readonly ClientSetupSettings _original;
    private readonly TextBox _reportDirectory;
    private readonly TextBox? _endOfDay;
    private readonly CheckBox? _includeTitles;
    private readonly TextBox? _excludedApps;
    private readonly CheckBox? _startAtSignIn;
    private readonly TextBlock? _startAtSignInStatus;
    private bool _showingStartupState;

    private SettingsWindow(ClientSetupSettings settings, bool firstRun)
    {
        _firstRun = firstRun;
        _original = settings.Clone();
        Title = firstRun
            ? $"{ProductContext.Current.DisplayName} setup"
            : $"{ProductContext.Current.DisplayName} settings";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;

        var content = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        content.Children.Add(new TextBlock
        {
            Text = firstRun ? "Set up your daily activity reports" : "Settings",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });

        if (firstRun)
        {
            content.Children.Add(Hint(
                $"{ProductContext.Current.DisplayName} records which apps and windows you work in while you are active at the keyboard or " +
                $"mouse, and saves a daily PDF report. {ProductContext.Current.PrivacyNotice} " +
                "A tray icon shows that tracking is on, and you can pause or exit it at any time.",
                bottom: 16));
        }

        content.Children.Add(Label("Where should daily PDF reports be saved?"));
        var folderRow = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var browseButton = new Button { Content = "Browse…", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browseButton, Dock.Right);
        folderRow.Children.Add(browseButton);
        _reportDirectory = new TextBox { Text = settings.EffectivePdfReportDirectory, MinWidth = 350, Padding = new Thickness(5) };
        folderRow.Children.Add(_reportDirectory);
        content.Children.Add(folderRow);
        browseButton.Click += BrowseForFolder;
        content.Children.Add(Hint(
            "Each PDF summarises your active time by app, including document and email titles. Reports stay on this PC.",
            bottom: 16));

        if (!firstRun)
        {
            content.Children.Add(Label("Write today's report at (24-hour time)"));
            _endOfDay = new TextBox
            {
                Text = settings.EndOfDayReportTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 6, 0, 4)
            };
            content.Children.Add(_endOfDay);
            content.Children.Add(Hint(
                "Reports are also written when you exit or sign out, and finalised the next time the app starts after midnight.",
                bottom: 16));

            _includeTitles = new CheckBox
            {
                Content = "Include window titles and email subjects in the PDF",
                IsChecked = settings.IncludeWindowTitlesInPdf,
                FontWeight = FontWeights.SemiBold
            };
            content.Children.Add(_includeTitles);
            content.Children.Add(Hint("Off: the PDF shows time per app only.", left: 22, bottom: 16));

            content.Children.Add(Label("Never record these apps (process names, one per line)"));
            _excludedApps = new TextBox
            {
                Text = string.Join(Environment.NewLine, settings.ExcludedProcessNames),
                AcceptsReturn = true,
                Height = 70,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 6, 0, 4)
            };
            content.Children.Add(_excludedApps);
            content.Children.Add(Hint("For example WhatsApp or Spotify. Time in these apps is treated like idle time.", bottom: 16));

            content.Children.Add(Label("Local data retention"));
            content.Children.Add(Hint(
                $"Raw activity is kept for {ClientSetupSettings.RawActivityRetentionLimitDays} days. Generated CSV and JSON sidecars are kept for {ClientSetupSettings.GeneratedJsonRetentionDays} days. PDF reports and matter summaries are not deleted.",
                bottom: 16));

            _startAtSignIn = new CheckBox { Content = "Start when I sign in to Windows", FontWeight = FontWeights.SemiBold, IsEnabled = false };
            content.Children.Add(_startAtSignIn);
            _startAtSignInStatus = Hint("Checking…", left: 22, bottom: 16);
            content.Children.Add(_startAtSignInStatus);
            Loaded += async (_, _) => await LoadStartupStateAsync();
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var cancelButton = new Button { Content = firstRun ? "Cancel" : "Close", IsCancel = true, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
        var saveButton = new Button { Content = firstRun ? "Save and start" : "Save", IsDefault = true, Padding = new Thickness(16, 7, 16, 7) };
        cancelButton.Click += (_, _) => DialogResult = false;
        saveButton.Click += SaveSettings;
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);
        content.Children.Add(buttons);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public ClientSetupSettings? Settings { get; private set; }

    /// <summary>Shows the window modally and returns the chosen settings, or null when cancelled.</summary>
    public static ClientSetupSettings? ShowDialog(ClientSetupSettings current, bool firstRun)
    {
        var window = new SettingsWindow(current, firstRun);
        bool? accepted = window.ShowDialog();
        return accepted == true ? window.Settings : null;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold };

    private static TextBlock Hint(string text, double left = 0, double bottom = 12) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(left, 4, 0, bottom)
    };

    private async Task LoadStartupStateAsync()
    {
        if (_startAtSignIn is null || _startAtSignInStatus is null) return;
        StartupTaskService.State state = await StartupTaskService.GetStateAsync();
        ShowStartupState(state);
        _startAtSignIn.Checked += async (_, _) =>
        {
            if (!_showingStartupState) ShowStartupState(await StartupTaskService.SetEnabledAsync(true));
        };
        _startAtSignIn.Unchecked += async (_, _) =>
        {
            if (!_showingStartupState) ShowStartupState(await StartupTaskService.SetEnabledAsync(false));
        };
    }

    // Disabled-by-user and by-policy are shown, not hidden: the user can only fix them in Windows.
    private void ShowStartupState(StartupTaskService.State state)
    {
        if (_startAtSignIn is null || _startAtSignInStatus is null) return;
        _showingStartupState = true;
        try
        {
            _startAtSignIn.IsChecked = state == StartupTaskService.State.Enabled;
            _startAtSignIn.IsEnabled = state is StartupTaskService.State.Enabled or StartupTaskService.State.Disabled;
            _startAtSignInStatus.Text = StartupTaskService.Describe(state);
        }
        finally
        {
            _showingStartupState = false;
        }
    }

    private void BrowseForFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Choose a folder for daily {ProductContext.Current.ShortName} PDF reports",
            FolderName = Directory.Exists(_reportDirectory.Text) ? _reportDirectory.Text : string.Empty
        };

        if (dialog.ShowDialog(this) == true) _reportDirectory.Text = dialog.FolderName;
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        ClientSetupSettings result = _original.Clone();

        if (!TryValidateFolder(_reportDirectory.Text, out string? folder, out string? folderError))
        {
            Warn(folderError!);
            return;
        }

        result.PdfReportDirectory = folder!;

        if (!_firstRun)
        {
            if (!TimeOnly.TryParseExact(_endOfDay!.Text.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out TimeOnly endOfDay))
            {
                Warn("Enter the report time as HH:mm, for example 18:00.");
                return;
            }

            result.EndOfDayReportTime = endOfDay;
            result.IncludeWindowTitlesInPdf = _includeTitles!.IsChecked == true;
            result.ExcludedProcessNames = _excludedApps!.Text
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        result.SetupCompleted = true;
        Settings = result;
        DialogResult = true;
    }

    // Writable check: create and delete a temp file, so a read-only or offline folder is caught now
    // rather than at the first report.
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

    private void Warn(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
