using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// The plan kept in a repository on one of the GitLabs, so that other devices see the same order, links and notes.
/// What is typed stays while the rest of the settings refresh; it applies with „Použít“.
public sealed class SyncSection : StackPanel
{
    private readonly AppState state;
    private readonly ComboBox account = new() { MinWidth = 220 };
    private readonly TextBox project = new() { Watermark = "skupina/gitlab-todo-data" };
    private readonly StackPanel repository = new() { Spacing = 10 };
    private readonly Button create = new() { Content = "Vytvořit soukromé repo" };
    private readonly Button apply = new() { Content = "Použít" };
    private readonly Button syncNow = new() { Content = "Synchronizovat teď" };
    private readonly TextBlock status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel statusRow = new();
    private readonly TextBlock warning = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string accountsShown = "";
    private bool isUpdating;
    private bool isCreating;

    public SyncSection(AppState state)
    {
        this.state = state;
        Spacing = 10;
        project.Text = state.SyncState?.Settings.Project ?? "";
        project.TextChanged += (_, _) => UpdateButtons();
        account.SelectionChanged += (_, _) =>
        {
            if (isUpdating)
            {
                return;
            }
            error.IsVisible = false;
            // Turning it off needs no confirming.
            if (SelectedAccount is null && state.SyncState is not null)
            {
                state.ConfigureSync(null);
            }
            UpdateButtons();
        };
        create.Click += async (_, _) =>
        {
            if (SelectedAccount is not { } accountId)
            {
                return;
            }
            isCreating = true;
            UpdateButtons();
            try
            {
                project.Text = await state.CreateSyncRepository(accountId);
                Apply();
            }
            catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException)
            {
                ShowError(exception.Message);
            }
            isCreating = false;
            UpdateButtons();
        };
        ToolTip.SetTip(create, "Založí soukromý repozitář gitlab-todo-data pod tvým účtem");
        apply.Classes.Add("accent");
        apply.Click += (_, _) => Apply();
        syncNow.Click += (_, _) => _ = state.SyncPlan();

        warning.Foreground = Paint.Waiting();
        warning.Text = "Repozitář není soukromý, poznámky v něm uvidí i ostatní.";
        error.Foreground = Paint.Brush(Palette.Threads);
        // One under the other where the window is narrow.
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.AddRange([create, apply]);
        repository.Children.AddRange([SettingsWindow.Field("Repozitář", project), buttons]);
        DockPanel.SetDock(syncNow, Dock.Right);
        statusRow.Children.AddRange([syncNow, status]);
        var hint = new TextBlock
        {
            Text = "Pořadí, vazby, odklady a poznámky se ukládají do souboru gitlab-todo.json v tomto repozitáři, změny se odešlou při obnovení. "
                + "Na dalším počítači zvol stejný GitLab a repozitář. Tokeny ani nastavení se nesynchronizují.",
            FontSize = 11.5,
            Foreground = Paint.Secondary,
            TextWrapping = TextWrapping.Wrap,
        };
        Children.AddRange([SettingsWindow.Field("GitLab", account), repository, statusRow, warning, error, hint]);
        Update();
    }

    private Guid? SelectedAccount => account.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;

    private bool HasChanges =>
        SelectedAccount != state.SyncState?.Settings.AccountId || PlanSync.ProjectPath(project.Text ?? "") != (state.SyncState?.Settings.Project ?? "");

    /// Follows the state; what the user picked or typed and has not applied yet stays.
    public void Update()
    {
        isUpdating = true;
        var accounts = string.Join("\n", state.Accounts.Select(a => $"{a.Id} {a.Name}"));
        if (accounts != accountsShown)
        {
            var selected = accountsShown.Length == 0 ? state.SyncState?.Settings.AccountId : SelectedAccount;
            accountsShown = accounts;
            account.Items.Clear();
            account.Items.Add(new ComboBoxItem { Content = "Vypnuto" });
            foreach (var gitLab in state.Accounts)
            {
                account.Items.Add(new ComboBoxItem { Content = gitLab.Name, Tag = gitLab.Id });
            }
            account.SelectedItem = account.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Guid id && id == selected) ?? account.Items[0];
        }
        isUpdating = false;

        var sync = state.SyncState;
        status.Text = state.SyncStatus switch
        {
            SyncStatus.Syncing => "Synchronizuji…",
            SyncStatus.Failed => state.SyncError,
            _ when sync?.SyncedAt is { } date => $"Synchronizováno {Views.Relative(date)}",
            _ => "Zatím nesynchronizováno",
        };
        status.Foreground = state.SyncStatus switch
        {
            SyncStatus.Failed => Paint.Brush(Palette.Threads),
            SyncStatus.Synced => Paint.Brush(Palette.Ready),
            _ => Paint.Secondary,
        };
        warning.IsVisible = sync?.Visibility is { } visibility && visibility != "private" && !HasChanges;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        repository.IsVisible = SelectedAccount is not null;
        create.IsEnabled = !isCreating;
        apply.IsEnabled = HasChanges && SelectedAccount is not null && PlanSync.ProjectPath(project.Text ?? "").Length > 0;
        statusRow.IsVisible = state.SyncState is not null && !HasChanges;
        syncNow.IsEnabled = state.SyncStatus != SyncStatus.Syncing;
    }

    private void Apply()
    {
        if (SelectedAccount is not { } accountId)
        {
            return;
        }
        error.IsVisible = false;
        state.ConfigureSync(new SyncSettings(accountId, PlanSync.ProjectPath(project.Text ?? "")));
    }

    private void ShowError(string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }
}
