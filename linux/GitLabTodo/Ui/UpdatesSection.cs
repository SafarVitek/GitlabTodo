using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// The version, and for an AppImage the check for newer releases and their install.
public sealed class UpdatesSection : StackPanel
{
    private readonly Updater updater;
    private readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button action = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock notes = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 8, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
    private readonly TextBlock error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public UpdatesSection(AppState state, Updater updater)
    {
        this.updater = updater;
        Spacing = 10;
        if (!Updater.CanUpdate)
        {
            Children.Add(Hint($"Verze {Updater.Version}. Tuhle kopii aktualizuješ tak, jak jsi ji nainstaloval (Nix, balíček .deb nebo build ze zdrojáků), ne aplikací samotnou."));
            return;
        }
        var check = new CheckBox { Content = "Kontrolovat aktualizace při spuštění", IsChecked = state.CheckUpdates };
        check.IsCheckedChanged += (_, _) => state.SetCheckUpdates(check.IsChecked == true);
        action.Click += (_, _) => _ = updater.Available is null ? updater.Check() : updater.InstallAndRestart();
        error.Foreground = Paint.Brush(Palette.Threads);
        notes.Foreground = Paint.Secondary;
        var row = new DockPanel();
        DockPanel.SetDock(action, Dock.Right);
        status.Margin = new Thickness(0, 0, 12, 0);
        row.Children.AddRange([action, status]);
        Children.AddRange([
            check,
            Hint($"Verze {Updater.Version}. Nové verze se stahují z GitHubu a nainstalují se, jen když mají podpis vydání GitLab Todo. Nic se neinstaluje bez tvého kliknutí."),
            row,
            notes,
            error,
        ]);
        Update();
    }

    public void Update()
    {
        if (!Updater.CanUpdate)
        {
            return;
        }
        var available = updater.Available;
        status.Text = available is not null ? $"GitLab Todo {available.Version} je k dispozici"
            : updater.HasChecked ? "Máš nejnovější verzi."
            : "Podívej se na GitHubu po novější verzi.";
        status.FontWeight = available is null ? FontWeight.Normal : FontWeight.SemiBold;
        action.Content = available is null
            ? updater.IsChecking ? "Kontroluji…" : "Zkontrolovat teď"
            : updater.Progress is { } progress ? $"Instaluji {Math.Round(progress * 100)} %" : "Nainstalovat a restartovat";
        action.Classes.Set("accent", available is not null);
        action.IsEnabled = !updater.IsChecking && updater.Progress is null;
        // What is new, from the release's notes.
        notes.Text = available?.Notes;
        notes.IsVisible = !string.IsNullOrWhiteSpace(available?.Notes);
        error.Text = updater.Error;
        error.IsVisible = updater.Error is not null;
    }

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Foreground = Paint.Secondary, TextWrapping = TextWrapping.Wrap };
}
