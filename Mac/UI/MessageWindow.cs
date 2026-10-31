using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ComputerUse.TimeTracking.UI;

/// <summary>
/// Message and confirmation windows. The app has no main window to own them, so each one is a standalone window
/// that closes on its button or the title-bar close box.
/// </summary>
internal static class MessageWindow
{
    public static Task ShowAsync(string title, string message) => AskAsync(title, message, okText: "OK", cancelText: null);

    public static Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        AskAsync(title, message, okText: confirmText, cancelText: "Cancel");

    private static Task<bool> AskAsync(string title, string message, string okText, string? cancelText)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, MinWidth = 90, IsCancel = true };
            cancel.Click += (_, _) => Finish(window, result, false);
            buttons.Children.Add(cancel);
        }

        var ok = new Button { Content = okText, MinWidth = 90, IsDefault = true };
        ok.Click += (_, _) => Finish(window, result, true);
        buttons.Children.Add(ok);

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
        window.Closed += (_, _) => result.TrySetResult(false);
        window.Show();
        return result.Task;
    }

    private static void Finish(Window window, TaskCompletionSource<bool> result, bool value)
    {
        result.TrySetResult(value);
        window.Close();
    }
}
