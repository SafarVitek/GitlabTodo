using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using GitLabTodo.Core;
using GitLabTodo.Platform;

namespace GitLabTodo.Ui;

/// The app's windows and its tray icon. Closing the main window keeps the app in the tray, like the macOS app in the
/// menu bar, so the list keeps refreshing.
public sealed class Shell
{
    private readonly Application application;
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly AppState state;
    private readonly DesktopUserInterface ui;
    private readonly DispatcherTimer trayUpdate;
    private readonly WindowIcon icon = new(AssetLoader.Open(new Uri("avares://gitlab-todo/Assets/icon.png")));
    private MainWindow? main;
    private SettingsWindow? settings;
    private TrayIcon? tray;
    private bool isQuitting;

    public Shell(Application application, IClassicDesktopStyleApplicationLifetime desktop, AppState state, DesktopUserInterface ui)
    {
        this.application = application;
        this.desktop = desktop;
        this.state = state;
        this.ui = ui;
        // The tray menu is rebuilt at most twice a second, not on every change.
        trayUpdate = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            trayUpdate!.Stop();
            UpdateTray();
        });
    }

    public void Start(bool showsWindow, bool listens)
    {
        tray = new TrayIcon { Icon = icon, ToolTipText = "GitLab Todo", IsVisible = true };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(application, [tray]);
        UpdateTray();
        state.Changed += () => trayUpdate.Start();
        if (listens)
        {
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowMain));
        }
        if (showsWindow)
        {
            ShowMain();
        }
        state.StartTimer();
    }

    public void ShowMain()
    {
        if (main is null)
        {
            main = new MainWindow(state, this) { Icon = icon };
            main.Closing += (_, e) =>
            {
                if (isQuitting)
                {
                    return;
                }
                if (state.KeepInTray)
                {
                    e.Cancel = true;
                    main.Hide();
                }
                else
                {
                    Quit();
                }
            };
            ui.Owner = main;
        }
        main.Show();
        if (main.WindowState == WindowState.Minimized)
        {
            main.WindowState = WindowState.Normal;
        }
        main.Activate();
    }

    public void ShowSettings()
    {
        if (settings is null)
        {
            settings = new SettingsWindow(state, this) { Icon = icon };
            settings.Closed += (_, _) => settings = null;
            if (main is { IsVisible: true })
            {
                settings.Show(main);
            }
            else
            {
                settings.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                settings.Show();
            }
        }
        settings.Activate();
    }

    public void Quit()
    {
        isQuitting = true;
        SingleInstance.Stop();
        tray?.Dispose();
        desktop.Shutdown();
    }

    /// What is up next, a click shows it in the main window.
    private void UpdateTray()
    {
        if (tray is null)
        {
            return;
        }
        var menu = new NativeMenu();
        var ready = state.Items.Where(i => !i.IsWaiting).ToList();
        var numbers = state.Items.WorkNumbers();
        if (state.Accounts.Count == 0)
        {
            menu.Items.Add(Item("Přidat GitLab…", ShowSettings));
        }
        else if (ready.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem(state.LastRefresh is null ? "Načítám…" : "Nic na práci") { IsEnabled = false });
        }
        foreach (var item in ready.Take(10))
        {
            var title = item.Title.Length > 60 ? item.Title[..59] + "…" : item.Title;
            var prefix = numbers.TryGetValue(item.Id, out var number) ? $"{number}. " : "• ";
            menu.Items.Add(Item($"{prefix}{title}  ({item.ShortReference})", () =>
            {
                ShowMain();
                state.RequestFocus(item.Id);
            }));
        }
        if (ready.Count > 10)
        {
            menu.Items.Add(new NativeMenuItem($"… a {ready.Count - 10} dalších") { IsEnabled = false });
        }
        var waiting = state.Items.Count - ready.Count;
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(waiting > 0 ? $"Otevřít GitLab Todo ({waiting} čeká)" : "Otevřít GitLab Todo", ShowMain));
        menu.Items.Add(Item("Obnovit", () => _ = state.Refresh()));
        menu.Items.Add(Item("Nastavení…", ShowSettings));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Ukončit", Quit));
        tray.Menu = menu;
        tray.ToolTipText = state.AccountErrors.Count > 0
            ? $"GitLab Todo: {string.Join(", ", state.AccountErrors.Values)}"
            : $"GitLab Todo: {ready.Count} na řadě";
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
