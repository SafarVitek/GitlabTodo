using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Detail of the selected item: what it is, its next step, its place in the order and what it waits on.
/// The search and the note field stay the same controls while the item does, so a refresh never eats what is typed.
public sealed class Inspector : Panel
{
    private readonly AppState state;
    private readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Control placeholder = Views.Placeholder(IconKind.Sidebar, "Vyber položku", "Tady uvidíš detail a nastavíš, na co čeká.");
    private readonly ContentControl header = new();
    private readonly ContentControl step = new();
    private readonly ContentControl order = new();
    private readonly StackPanel blockers = new() { Spacing = 2 };
    private readonly ContentControl mergeRequest = new();
    private readonly WorkItemSearch search;
    private readonly TextBox note;
    private TodoItem? item;

    public Inspector(AppState state)
    {
        this.state = state;
        search = new WorkItemSearch("slovo z názvu, číslo nebo odkaz", text => state.SearchBlockers(text, item!), candidate => state.AddBlocker(candidate, item!))
        {
            PickSuggestion = blocker => state.ToggleBlocker(blocker, item!),
        };
        note = new TextBox
        {
            Watermark = "Jiný důvod, např. odpověď zákazníka",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 4),
            MinHeight = 0,
        };
        note.LostFocus += (_, _) => SaveNote();
        note.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SaveNote();
            }
        };

        var noteRow = new DockPanel();
        var noteIcon = new Icon(IconKind.TextBubble, Paint.Secondary, 14) { Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(noteIcon, Dock.Left);
        noteRow.Children.Add(noteIcon);
        noteRow.Children.Add(note);
        var wait = new StackPanel { Spacing = 8 };
        wait.Children.Add(Views.SectionTitle("Čeká na", IconKind.Pause));
        wait.Children.Add(blockers);
        wait.Children.Add(Views.Box(search));
        wait.Children.Add(Views.Box(noteRow));
        wait.Children.Add(Hint("Až se všechno dokončí, čekání skončí samo a položka se ukáže jako připravená."));

        var content = new StackPanel { Spacing = 22, Margin = new Thickness(16) };
        content.Children.AddRange([header, step, order, wait, mergeRequest]);
        scroller.Content = content;
        Children.Add(scroller);
        Children.Add(placeholder);
        Show(null);
    }

    public void Show(TodoItem? shown)
    {
        if (item is { } previous && previous.Id != shown?.Id)
        {
            SaveNote();
            search.Reset();
        }
        var isNewItem = item?.Id != shown?.Id;
        item = shown;
        scroller.IsVisible = shown is not null;
        placeholder.IsVisible = shown is null;
        if (shown is null)
        {
            return;
        }
        if (isNewItem || !note.IsFocused)
        {
            note.Text = shown.Wait?.Note ?? "";
        }
        header.Content = Header(shown);
        // A waiting item is not up yet, its actions stay in the context menu.
        step.Content = shown.NextStep is { } next && !shown.IsWaiting ? Views.ActionButton(next, state, large: true) : null;
        step.IsVisible = step.Content is not null;
        order.Content = Order(shown);
        UpdateBlockers(shown);
        mergeRequest.Content = shown.MergeRequest is { } request ? MergeRequest(shown, request) : null;
        mergeRequest.IsVisible = mergeRequest.Content is not null;
    }

    /// Puts the cursor into the blocker search, e.g. after „Hledat issue nebo MR…“ in a context menu.
    public void FocusSearch() => search.FocusField();

    private void SaveNote()
    {
        if (item is { } current)
        {
            state.SetWaitNote(note.Text ?? "", current.Id);
        }
    }

    private Control Header(TodoItem shown)
    {
        var open = Views.IconButton(IconKind.ExternalLink, shown.Kind == ItemKind.Issue ? "Otevřít issue v GitLabu" : "Otevřít MR v GitLabu", () => state.Ui.Open(shown.WebUrl), 14);
        var top = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        top.Children.Add(open);
        top.Children.Add(Views.Row(6,
            new StageCircle(shown, 15),
            Views.Label(shown.Stage == ItemKind.Issue ? "Issue" : "Merge request", 12, Paint.Kind(shown.Stage), FontWeight.SemiBold),
            Views.Label("·", 12, Paint.Tertiary),
            Views.ProjectLabel(shown, maxWidth: 160)));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(top);
        panel.Children.Add(new SelectableTextBlock { Text = shown.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Meta(shown));
        return panel;
    }

    /// Like the list's line under the title, wrapped since the inspector is narrow.
    private static Control Meta(TodoItem shown)
    {
        var line = (StackPanel)Views.ItemMeta(shown);
        var children = line.Children.ToList();
        line.Children.Clear();
        var wrap = new WrapPanel();
        foreach (var child in children)
        {
            child.Margin = new Thickness(0, 0, 8, 4);
            wrap.Children.Add(child);
        }
        return wrap;
    }

    private Control Order(TodoItem shown)
    {
        var position = shown.IsWaiting
            ? "Čeká, zatím není na řadě"
            : state.Items.WorkNumbers().TryGetValue(shown.Id, out var number) ? $"{number}. na řadě" : "Nezařazená";
        var buttons = Views.Row(2,
            Views.IconButton(IconKind.ToTop, "Na začátek pořadí", () => state.MoveToTop(shown)),
            Views.IconButton(IconKind.ToBottom, shown.IsPlaced ? "Na konec pořadí" : "Zařadit na konec pořadí", () => state.MoveToEnd(shown)));
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(Views.Label(position, 13.5, weight: FontWeight.Medium));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Views.SectionTitle("Pořadí", IconKind.Tray));
        panel.Children.Add(row);
        panel.Children.Add(Hint("Jinak pořadí měníš přetažením v seznamu."));
        return panel;
    }

    private void UpdateBlockers(TodoItem shown)
    {
        blockers.Children.Clear();
        foreach (var blocker in state.Items.Where(i => shown.Wait?.ItemIds.Contains(i.Id) ?? false))
        {
            var reference = blocker.ProjectKey == shown.ProjectKey ? blocker.LocalReference : blocker.ShortReference;
            blockers.Children.Add(BlockerRow(blocker.Stage, reference, blocker.Title, blocker.WebUrl, () => state.ToggleBlocker(blocker, shown)));
        }
        foreach (var blocker in shown.Wait?.External ?? [])
        {
            blockers.Children.Add(BlockerRow(blocker.Kind, blocker.ShortReference, blocker.Title, blocker.WebUrl, () => state.RemoveBlocker(blocker, shown)));
        }
        blockers.IsVisible = blockers.Children.Count > 0;
        search.OwnProjectId = shown.ProjectId;
        search.Suggestions = state.BlockerCandidates(shown)
            .Where(candidate => !(shown.Wait?.ItemIds.Contains(candidate.Id) ?? false))
            .Select(candidate => (
                candidate,
                candidate.ProjectId == shown.ProjectId ? candidate.LocalReference : candidate.ShortReference,
                candidate.AccountId == shown.AccountId ? null : candidate.AccountName))
            .ToList();
    }

    private Border BlockerRow(ItemKind kind, string reference, string title, string url, Action onRemove)
    {
        var remove = Views.IconButton(IconKind.CloseCircleFilled, "Už na to nečeká", onRemove, 13);
        remove.Padding = new Thickness(2);
        remove.Opacity = 0.45;
        var line = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        var start = Views.Row(6, new Icon(kind == ItemKind.Issue ? IconKind.Circle : IconKind.Merge, Paint.Kind(kind), 13), Views.Label(reference, 12.5, Paint.Secondary));
        start.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(start, Dock.Left);
        line.Children.AddRange([remove, start, Views.Label(title, 12.5)]);
        var row = new Border
        {
            Child = line,
            Padding = new Thickness(6, 3),
            CornerRadius = new CornerRadius(6),
            Background = Paint.Waiting(0.08),
        };
        ToolTip.SetTip(row, "Dvojklikem otevřeš v GitLabu");
        row.PointerEntered += (_, _) =>
        {
            row.Background = Paint.Waiting(0.14);
            remove.Opacity = 1;
        };
        row.PointerExited += (_, _) =>
        {
            row.Background = Paint.Waiting(0.08);
            remove.Opacity = 0.45;
        };
        row.DoubleTapped += (_, _) => state.Ui.Open(url);
        return row;
    }

    private Control MergeRequest(TodoItem shown, TodoItem request)
    {
        var box = new StackPanel { Spacing = 8 };
        var top = new DockPanel();
        if (request.Id != shown.Id)
        {
            var open = new Button { Content = "Otevřít", FontSize = 12, Padding = new Thickness(10, 2) };
            open.Click += (_, _) => state.Ui.Open(request.WebUrl);
            DockPanel.SetDock(open, Dock.Right);
            top.Children.Add(open);
        }
        top.Children.Add(Views.Row(6,
            Views.Label(request.ShortReference, 13, weight: FontWeight.Medium),
            request.IsDraft ? Views.Tag("Draft", Paint.Secondary, Paint.Primary(0.08)) : Views.Tag("Ready", Palette.Ready)));
        box.Children.Add(top);
        if (request.HasConflicts)
        {
            box.Children.Add(Views.IconLabel(IconKind.WarningFilled, "Merge konflikt, je potřeba ho vyřešit", Paint.Waiting(), 12.5));
        }
        if (shown.ReviewThreads is { } threads)
        {
            var isDone = threads.Resolved == threads.Total;
            box.Children.Add(Views.IconLabel(
                IconKind.BubbleFilled,
                isDone ? "Všechna vlákna vyřešená" : $"Vyřešeno {threads.Resolved} z {threads.Total} vláken",
                Paint.Brush(isDone ? Palette.Ready : Palette.Threads),
                12.5));
        }
        if (request.SourceBranch is { } branch)
        {
            var row = new DockPanel();
            if (shown.CheckoutCommand is { } command)
            {
                var copy = Views.IconButton(IconKind.Copy, $"Kopírovat „{command}“", () => _ = state.Ui.Copy(command), 13);
                copy.Padding = new Thickness(3);
                DockPanel.SetDock(copy, Dock.Right);
                row.Children.Add(copy);
            }
            var name = new SelectableTextBlock { Text = branch, FontSize = 12.5, Foreground = Paint.Secondary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var icon = new Icon(IconKind.Branch, Paint.Secondary, 13) { Margin = new Thickness(0, 0, 4, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(name);
            box.Children.Add(row);
        }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Views.SectionTitle("Merge request", IconKind.Merge));
        panel.Children.Add(Views.Box(box));
        return panel;
    }

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 11.5, Foreground = Paint.Tertiary, TextWrapping = TextWrapping.Wrap };
}
