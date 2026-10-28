using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitLabTodo.Core;
using GitLabTodo.Platform;

namespace GitLabTodo.Ui;

public sealed class SettingsWindow : Window
{
    private readonly AppState state;
    private readonly Shell shell;
    private StackPanel accounts = null!;
    private ComboBox interval = null!;
    private CheckBox keepInTray = null!;
    private ComboBox theme = null!;
    private SyncSection sync = null!;
    private UpdatesSection updates = null!;

    public SettingsWindow(AppState state, Shell shell)
    {
        this.state = state;
        this.shell = shell;
        Title = "Nastavení";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Paint.Sharpen(this);
        BuildContent();
        // Colors are taken when controls are made, a new theme needs new ones.
        ActualThemeVariantChanged += (_, _) => BuildContent();
        state.Changed += ScheduleUpdate;
        shell.Updater.Changed += ScheduleUpdate;
        Closed += (_, _) =>
        {
            state.Changed -= ScheduleUpdate;
            shell.Updater.Changed -= ScheduleUpdate;
        };
        // Taller than a small screen with every section, so it scrolls there.
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromVisual(this) is { } screen)
            {
                MaxHeight = screen.WorkingArea.Height / screen.Scaling - 60;
            }
        };
    }

    private void BuildContent()
    {
        accounts = new StackPanel { Spacing = 6 };
        interval = new ComboBox { MinWidth = 120 };
        keepInTray = new CheckBox { Content = "Po zavření okna běžet dál v systémové liště" };
        theme = new ComboBox { MinWidth = 160 };
        foreach (var (key, name) in new[] { ("system", "Podle systému"), ("light", "Světlý"), ("dark", "Tmavý") })
        {
            theme.Items.Add(new ComboBoxItem { Content = name, Tag = key });
        }
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem { Tag: string key } && key != state.Theme)
            {
                state.SetTheme(key);
                // The windows rebuild for the new theme, not while this list is still handling the pick.
                Dispatcher.UIThread.Post(() => Shell.ApplyTheme(key));
            }
        };
        var autostart = new CheckBox { Content = "Spouštět po přihlášení" };
        var autostartError = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var add = new Button { Content = "Přidat GitLab…" };
        add.Click += (_, _) => _ = Edit(null);
        foreach (var minutes in AppState.RefreshIntervals)
        {
            interval.Items.Add(new ComboBoxItem { Content = $"{minutes} min", Tag = minutes });
        }
        interval.SelectionChanged += (_, _) =>
        {
            if (interval.SelectedItem is ComboBoxItem { Tag: double minutes } && minutes != state.RefreshMinutes)
            {
                state.SetRefreshMinutes(minutes);
            }
        };
        keepInTray.IsCheckedChanged += (_, _) => state.SetKeepInTray(keepInTray.IsChecked == true);
        autostart.IsChecked = Autostart.IsEnabled;
        autostart.IsCheckedChanged += (_, _) =>
        {
            try
            {
                Autostart.Set(autostart.IsChecked == true);
                autostartError.IsVisible = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                autostartError.Text = exception.Message;
                autostartError.Foreground = Paint.Brush(Palette.Threads);
                autostartError.IsVisible = true;
            }
        };
        var quit = new Button { Content = "Ukončit GitLab Todo" };
        quit.Click += (_, _) => shell.Quit();
        ToolTip.SetTip(quit, "I z lišty, kde aplikace běží po zavření okna (Ctrl Q)");

        var gitLab = new StackPanel { Spacing = 10 };
        gitLab.Children.AddRange([accounts, add]);
        var refresh = new StackPanel { Spacing = 10 };
        refresh.Children.AddRange([Field("Interval", interval), keepInTray, autostart, autostartError]);
        var look = Field("Motiv", theme);
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(20) };
        sync = new SyncSection(state);
        updates = new UpdatesSection(state, shell.Updater);
        content.Children.AddRange([
            Section("GitLab", gitLab),
            Section("Synchronizace", sync),
            Section("Obnovování", refresh),
            Section("Vzhled", look),
            Section("Aktualizace", updates),
            quit,
        ]);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Update();
    }

    public static Control Section(string title, Control content)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Views.SectionTitle(title));
        panel.Children.Add(new Border { Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Background = Paint.Primary(0.04) });
        return panel;
    }

    public static Control Field(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        var text = Views.Label(label, 13);
        Grid.SetColumn(control, 1);
        grid.Children.AddRange([text, control]);
        return grid;
    }

    /// After the change that caused it is done, e.g. a pick in the interval list.
    private void ScheduleUpdate() => Dispatcher.UIThread.Post(Update);

    private void Update()
    {
        accounts.Children.Clear();
        foreach (var account in state.Accounts)
        {
            accounts.Children.Add(AccountRow(account));
        }
        if (state.Accounts.Count == 0)
        {
            accounts.Children.Add(Views.Label("Zatím žádný. Přidej firemní GitLab a token se scope api.", 12.5, Paint.Secondary));
        }
        interval.SelectedItem = interval.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (double)i.Tag! == state.RefreshMinutes);
        keepInTray.IsChecked = state.KeepInTray;
        theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == state.Theme) ?? theme.Items[0];
        sync.Update();
        updates.Update();
    }

    private Control AccountRow(GitLabAccount account)
    {
        var error = state.AccountErrors.GetValueOrDefault(account.Id);
        var status = new Icon(error is null ? IconKind.CheckCircle : IconKind.WarningFilled, error is null ? Paint.Brush(Palette.Ready) : Paint.Waiting(), 16);
        ToolTip.SetTip(status, error ?? "Připojeno");
        var details = new List<string> { account.BaseUrl, $"→ {account.TargetBranch}" };
        if (account.DefaultReviewer.Length > 0)
        {
            details.Add($"reviewer @{account.DefaultReviewer}");
        }
        var text = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0) };
        text.Children.Add(Views.Label(account.Name, 13.5));
        text.Children.Add(Views.Label(string.Join(" · ", details), 11.5, Paint.Secondary));
        var edit = new Button { Content = "Upravit", FontSize = 12.5 };
        edit.Click += (_, _) => _ = Edit(account);
        var remove = Views.IconButton(IconKind.Trash, "Odebrat", async () =>
        {
            var confirmed = await state.Ui.Confirm($"Odebrat {account.Name}?", "Smaže se i uložený token. Pořadí a vazby jeho položek zmizí.", "Odebrat");
            if (confirmed)
            {
                state.RemoveAccount(account);
            }
        });
        var row = new DockPanel();
        DockPanel.SetDock(status, Dock.Left);
        var buttons = Views.Row(4, edit, remove);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.AddRange([status, buttons, text]);
        return row;
    }

    private async Task Edit(GitLabAccount? account)
    {
        var editor = new AccountEditor(state, account);
        await editor.ShowDialog(this);
    }
}

public sealed class AccountEditor : Window
{
    private readonly AppState state;
    private readonly Guid id;
    private readonly bool isNew;
    private readonly TextBox name = new() { Watermark = "Firemní GitLab" };
    private readonly TextBox url = new() { Watermark = "https://gitlab.example.com" };
    private readonly TextBox token = new() { PasswordChar = '•' };
    private readonly TextBox targetBranch = new() { Watermark = "master" };
    private readonly TextBox reviewer = new() { Watermark = "uživatelské jméno" };
    private readonly TextBlock hint = new() { Text = "Personal Access Token musí mít scope api.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button verify = new() { Content = "Ověřit" };
    private readonly Button save = new() { Content = "Uložit", IsDefault = true };
    private bool isWorking;

    public AccountEditor(AppState state, GitLabAccount? account)
    {
        this.state = state;
        id = account?.Id ?? Guid.NewGuid();
        isNew = account is null;
        Title = isNew ? "Přidat GitLab" : $"Upravit {account!.Name}";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Paint.Sharpen(this);
        ShowInTaskbar = false;

        name.Text = account?.Name ?? "";
        url.Text = account?.BaseUrl ?? "";
        targetBranch.Text = account?.TargetBranch ?? "master";
        reviewer.Text = account?.DefaultReviewer ?? "";
        token.Watermark = isNew ? "glpat-…" : "Ponechat uložený";
        hint.Foreground = Paint.Secondary;
        foreach (var field in new[] { name, url, token, targetBranch, reviewer })
        {
            field.TextChanged += (_, _) => UpdateButtons();
        }
        verify.Click += (_, _) => _ = Verify();
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Zrušit", IsCancel = true };
        cancel.Click += (_, _) => Close();
        save.Classes.Add("accent");

        var connection = new StackPanel { Spacing = 10 };
        connection.Children.AddRange([SettingsWindow.Field("Název", name), SettingsWindow.Field("URL", url)]);
        var auth = new StackPanel { Spacing = 10 };
        auth.Children.AddRange([SettingsWindow.Field("Token", token), hint]);
        var actions = new StackPanel { Spacing = 10 };
        actions.Children.AddRange([SettingsWindow.Field("Cílová větev MR", targetBranch), SettingsWindow.Field("Výchozí reviewer", reviewer)]);
        var footer = new DockPanel();
        var buttons = Views.Row(8, verify, cancel, save);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.AddRange([buttons, status]);
        var content = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        content.Children.AddRange([
            SettingsWindow.Section("Připojení", connection),
            SettingsWindow.Section("Přístup", auth),
            SettingsWindow.Section("Akce", actions),
            footer,
        ]);
        Content = content;
        UpdateButtons();
    }

    private Uri? ApiUrl => GitLabAccount.ApiUrlFrom(url.Text ?? "");

    private string EffectiveToken
    {
        get
        {
            var typed = (token.Text ?? "").Trim();
            return typed.Length == 0 ? state.Token(id) ?? "" : typed;
        }
    }

    private void UpdateButtons()
    {
        verify.IsEnabled = ApiUrl is not null && EffectiveToken.Length > 0 && !isWorking;
        save.IsEnabled = (name.Text ?? "").Trim().Length > 0 && ApiUrl is not null && EffectiveToken.Length > 0;
    }

    private void ShowStatus(string message, IconKind? icon, IBrush brush)
    {
        var text = new TextBlock { Text = message, FontSize = 12, Foreground = brush, TextWrapping = TextWrapping.Wrap, MaxWidth = 270 };
        status.Content = icon is { } kind
            ? Views.Row(6, new Icon(kind, brush, 14) { VerticalAlignment = VerticalAlignment.Top }, text)
            : Views.Row(6, new Spinner(), text);
    }

    private void SetWorking(bool working)
    {
        isWorking = working;
        UpdateButtons();
    }

    private async Task Verify()
    {
        if (ApiUrl is not { } apiUrl)
        {
            return;
        }
        var client = new GitLabClient(apiUrl, EffectiveToken);
        var username = GitLabAccount.Username(reviewer.Text ?? "");
        SetWorking(true);
        ShowStatus("Ověřuji…", null, Paint.Secondary);
        try
        {
            var user = await client.CurrentUser();
            var message = $"Přihlášen jako @{user.Username}";
            List<string>? scopes = null;
            try
            {
                scopes = await client.TokenScopes();
            }
            catch (GitLabException)
            {
                // Older GitLabs cannot tell, the actions will show.
            }
            if (scopes is not null && !scopes.Contains("api"))
            {
                ShowStatus($"{message}, ale token nemá scope api, akce nebudou fungovat", IconKind.WarningFilled, Paint.Brush(Palette.Threads));
                return;
            }
            if (username.Length > 0)
            {
                await client.UserId(username);
                message += $", reviewer @{username} existuje";
            }
            ShowStatus(message, IconKind.CheckCircle, Paint.Brush(Palette.Ready));
        }
        catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException)
        {
            ShowStatus(exception is TaskCanceledException ? "GitLab neodpověděl včas" : exception.Message, IconKind.WarningFilled, Paint.Brush(Palette.Threads));
        }
        finally
        {
            SetWorking(false);
        }
    }

    private void Save()
    {
        var branch = (targetBranch.Text ?? "").Trim();
        var account = new GitLabAccount
        {
            Id = id,
            Name = (name.Text ?? "").Trim(),
            BaseUrl = GitLabAccount.NormalizedBaseUrl(url.Text ?? ""),
            TargetBranch = branch.Length == 0 ? "master" : branch,
            DefaultReviewer = GitLabAccount.Username(reviewer.Text ?? ""),
        };
        var typed = (token.Text ?? "").Trim();
        state.SaveAccount(account, typed.Length == 0 ? null : typed);
        Close();
    }
}
