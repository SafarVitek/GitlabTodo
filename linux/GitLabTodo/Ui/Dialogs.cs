using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// A message with buttons, the first one the default. Returns the index of the clicked button, the last one on Escape.
public sealed class MessageDialog : Window
{
    private int result;

    private MessageDialog(string title, string message, IReadOnlyList<string> buttons, bool isWarning)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Paint.Sharpen(this);
        ShowInTaskbar = false;
        result = buttons.Count - 1;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var index = buttons.Count - 1; index >= 0; index--)
        {
            var position = index;
            var button = new Button { Content = buttons[index], MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (index == 0)
            {
                button.Classes.Add("accent");
                button.IsDefault = true;
            }
            button.Click += (_, _) =>
            {
                result = position;
                Close();
            };
            row.Children.Add(button);
        }

        var text = new StackPanel { Spacing = 8 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new SelectableTextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Paint.Primary(0.8) });
        var body = new DockPanel { Margin = new Thickness(20, 18, 20, 16) };
        var icon = new Icon(isWarning ? IconKind.WarningFilled : IconKind.CheckCircle, isWarning ? Paint.Waiting() : Paint.AccentBrush(), 30)
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0),
        };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(row, Dock.Bottom);
        row.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(row);
        body.Children.Add(icon);
        body.Children.Add(text);
        // Without a system title bar the whole dialog moves it; Escape or a button closes it.
        var surface = new Border { Child = body };
        WindowChrome.MakeDraggable(surface, this);
        Content = WindowChrome.Frame(this, surface);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    public static async Task<int> Show(Window? owner, string title, string message, IReadOnlyList<string> buttons, bool isWarning)
    {
        var dialog = new MessageDialog(title, message, buttons, isWarning);
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            // Without a window open, e.g. from the tray menu, it stands on its own.
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }
        return dialog.result;
    }
}

/// Dialogs over the main window, the system clipboard and the default browser.
public sealed class DesktopUserInterface : IUserInterface
{
    public Window? Owner { get; set; }

    public async Task<bool> Confirm(string title, string message, string action) =>
        await MessageDialog.Show(Owner, title, message, [action, "Zrušit"], isWarning: false) == 0;

    public Task<int> Show(string title, string message, IReadOnlyList<string>? buttons = null, bool isWarning = false) =>
        MessageDialog.Show(Owner, title, message, buttons ?? ["OK"], isWarning);

    public async Task Copy(string text)
    {
        if (Owner?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", uri.AbsoluteUri) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = Show("Odkaz nejde otevřít", $"{uri.AbsoluteUri}\n\n{exception.Message}", isWarning: true);
        }
    }
}
