using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Search field with suggestions while typing. Before typing it offers `Suggestions`, the user's own items.
public sealed class WorkItemSearch : StackPanel
{
    private readonly TextBox field;
    private readonly Spinner spinner = new() { IsVisible = false };
    private readonly StackPanel results = new() { Spacing = 1 };
    private readonly TextBlock message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Func<string, Task<List<BlockerCandidate>>> search;
    private readonly Action<BlockerCandidate> pick;
    private List<BlockerCandidate> found = [];
    private CancellationTokenSource? running;

    public WorkItemSearch(string prompt, Func<string, Task<List<BlockerCandidate>>> search, Action<BlockerCandidate> pick)
    {
        this.search = search;
        this.pick = pick;
        Spacing = 4;
        field = new TextBox
        {
            Watermark = prompt,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 4),
            MinHeight = 0,
        };
        field.TextChanged += (_, _) => _ = RunSearch();
        field.GotFocus += (_, _) => ShowResults();
        field.LostFocus += (_, _) => ShowResults();
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && found.FirstOrDefault() is { } first)
            {
                Add(first);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                field.Text = "";
            }
        };
        var row = new DockPanel();
        var icon = new Icon(IconKind.Search, Paint.Secondary, 14) { Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(spinner, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(spinner);
        row.Children.Add(field);
        Children.Add(row);
        Children.Add(results);
        Children.Add(message);
    }

    /// The user's own items offered before typing, with the GitLab they come from when it is another one.
    public IReadOnlyList<(TodoItem Item, string Reference, string? GitLabName)> Suggestions { get; set; } = [];
    public Action<TodoItem>? PickSuggestion { get; set; }
    /// Own project, whose items are shown as `#12` rather than `project#12`.
    public int OwnProjectId { get; set; }

    public void FocusField() => field.Focus();

    public void Reset()
    {
        running?.Cancel();
        found = [];
        field.Text = "";
        message.IsVisible = false;
        ShowResults();
    }

    /// Words need three letters for GitLab's search, a number or a link works right away.
    private bool IsLongEnough(string text) => text.Length >= 3 || int.TryParse(text.Trim('#', '!'), out _);

    private async Task RunSearch()
    {
        running?.Cancel();
        var cancellation = new CancellationTokenSource();
        running = cancellation;
        var text = (field.Text ?? "").Trim();
        if (!IsLongEnough(text))
        {
            found = [];
            message.IsVisible = false;
            spinner.IsVisible = false;
            ShowResults();
            return;
        }
        try
        {
            // Wait until the user stops typing.
            await Task.Delay(300, cancellation.Token);
            spinner.IsVisible = true;
            var hits = await search(text);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }
            found = hits;
            ShowMessage(found.Count == 0 ? "Nic otevřeného jsem nenašel" : null, isError: false);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is GitLabException or HttpRequestException or InvalidOperationException)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }
            found = [];
            ShowMessage(exception.Message, isError: true);
        }
        finally
        {
            if (running == cancellation)
            {
                spinner.IsVisible = false;
            }
        }
        ShowResults();
    }

    private void ShowMessage(string? text, bool isError)
    {
        message.Text = text;
        message.IsVisible = text is not null;
        message.Foreground = isError ? Paint.Brush(Palette.Threads) : Paint.Secondary;
    }

    private void ShowResults()
    {
        results.Children.Clear();
        if (string.IsNullOrEmpty(field.Text) && field.IsFocused && Suggestions.Count > 0)
        {
            results.Children.Add(new TextBlock { Text = "Z tvého seznamu", FontSize = 11.5, Foreground = Paint.Secondary, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var (item, reference, gitLabName) in Suggestions.Take(6))
            {
                results.Children.Add(Row(item.Stage, reference, item.Title, false, gitLabName, () => PickSuggestion?.Invoke(item)));
            }
        }
        foreach (var candidate in found)
        {
            var blocker = candidate.Blocker;
            var reference = blocker.ProjectId == OwnProjectId ? Names.LocalReference(blocker.Reference) : blocker.ShortReference;
            var row = Row(candidate.Kind, reference, blocker.Title, candidate.ListedItemId is not null, candidate.GitLabName, () => Add(candidate));
            ToolTip.SetTip(row, blocker.Reference);
            results.Children.Add(row);
        }
    }

    private void Add(BlockerCandidate candidate)
    {
        try
        {
            pick(candidate);
            Reset();
        }
        catch (GitLabException exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
    }

    /// Acts on press rather than click, before the field loses focus and the suggestions go away.
    private static Border Row(ItemKind kind, string reference, string title, bool isListed, string? gitLabName, Action onPick)
    {
        var line = new DockPanel();
        var tags = Views.Row(4);
        if (gitLabName is not null)
        {
            tags.Children.Add(Views.Tag(gitLabName, "#007AFF"));
        }
        if (isListed)
        {
            tags.Children.Add(Views.Tag("v seznamu", Paint.Secondary, Paint.Primary(0.08)));
        }
        DockPanel.SetDock(tags, Dock.Right);
        var start = Views.Row(6,
            new Icon(kind == ItemKind.Issue ? IconKind.Circle : IconKind.Merge, Paint.Kind(kind), 13),
            Views.Label(reference, 12.5, Paint.Secondary));
        start.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(start, Dock.Left);
        line.Children.Add(tags);
        line.Children.Add(start);
        line.Children.Add(Views.Label(title, 12.5));
        var row = new Border
        {
            Child = line,
            Padding = new Thickness(4, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerEntered += (_, _) => row.Background = Paint.AccentBrush(0.15);
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                // After the press is handled: picking clears the list this row is in.
                Dispatcher.UIThread.Post(onPick);
            }
        };
        return row;
    }
}
