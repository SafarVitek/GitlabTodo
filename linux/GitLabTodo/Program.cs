using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using GitLabTodo.Core;
using GitLabTodo.Platform;
using GitLabTodo.Ui;

namespace GitLabTodo;

internal static class Program
{
    /// `--hidden` starts in the tray (for autostart), `--preview` shows sample data and touches nothing.
    [STAThread]
    public static int Main(string[] args)
    {
        var isPreview = args.Contains("--preview");
        if (!isPreview && SingleInstance.NotifyRunning())
        {
            // The running one shows its window.
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
        .LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "GitLab Todo";
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Light] = new ColorPaletteResources { Accent = Paint.Accent, RegionColor = Colors.White },
                [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Paint.Accent, RegionColor = Color.Parse("#1F1F22") },
            },
        });
        Styles.Add(new Look());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var isPreview = args.Contains("--preview");
            var ui = new DesktopUserInterface();
            var state = isPreview ? new AppState(ui, PreviewData.Snapshot()) : new AppState(ui);
            Shell.ApplyTheme(state.Theme);
            new Shell(this, desktop, state, ui).Start(showsWindow: !args.Contains("--hidden"), isPreview: isPreview);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
