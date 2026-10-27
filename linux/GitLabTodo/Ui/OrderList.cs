using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// The work order as a list: drag rows up and down to reorder, new items at the end under their own heading.
public sealed class OrderList : Panel
{
    private const double DragThreshold = 4;
    private const double Edge = 40;

    private readonly AppState state;
    private readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel rows = new() { Margin = new Thickness(8, 6) };
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    private readonly Border insertion = new() { Height = 2, Background = Paint.AccentBrush(), CornerRadius = new CornerRadius(1), IsVisible = false };
    private readonly Control placeholder = Views.Placeholder(IconKind.CheckCircle, "Nic tu není");
    private readonly DispatcherTimer autoScroll;
    private List<TodoItem> items = [];
    private string? selection;
    private (int Index, Point Start)? press;
    private bool isDragging;
    private int destination;
    private double pointerY;
    private bool rebuildPending;

    public OrderList(AppState state)
    {
        this.state = state;
        var content = new Panel();
        content.Children.Add(rows);
        content.Children.Add(overlay);
        overlay.Children.Add(insertion);
        scroller.Content = content;
        Children.Add(scroller);
        Children.Add(placeholder);
        autoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => ScrollTowardsPointer());

        rows.AddHandler(PointerPressedEvent, OnRowsPressed, handledEventsToo: false);
        rows.PointerMoved += OnRowsMoved;
        rows.PointerReleased += OnRowsReleased;
        rows.PointerCaptureLost += (_, _) => EndDrag();
    }

    public event Action<string>? Selected;

    public bool ShowsProject { get; set; } = true;

    public void Show(List<TodoItem> shown, string? selected)
    {
        items = shown;
        selection = selected;
        // Rebuilding under a drag would pull the rows from under the pointer; the drop rebuilds anyway.
        if (press is not null)
        {
            rebuildPending = true;
            return;
        }
        Rebuild();
    }

    public void Select(string? selected)
    {
        selection = selected;
        foreach (var row in rows.Children.OfType<Row>())
        {
            row.IsSelected = row.Item.Id == selected;
        }
    }

    public void ScrollToSelection()
    {
        if (rows.Children.OfType<Row>().FirstOrDefault(r => r.Item.Id == selection) is { } row)
        {
            row.BringIntoView();
        }
    }

    private void Rebuild()
    {
        rebuildPending = false;
        rows.Children.Clear();
        var firstNew = items.FirstOrDefault(i => !i.IsPlaced)?.Id;
        foreach (var item in items)
        {
            rows.Children.Add(new Row(item, state, ShowsProject, item.Id == firstNew) { IsSelected = item.Id == selection });
        }
        placeholder.IsVisible = items.Count == 0;
    }

    private void OnRowsPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<Row>(includeSelf: true) is not { } row)
        {
            return;
        }
        var point = e.GetCurrentPoint(rows);
        if (point.Properties.IsRightButtonPressed)
        {
            Selected?.Invoke(row.Item.Id);
            Menus.Open(row, Menus.ForItem(row.Item, state));
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            state.Ui.Open(row.Item.WebUrl);
            e.Handled = true;
            return;
        }
        Selected?.Invoke(row.Item.Id);
        press = (rows.Children.IndexOf(row), point.Position);
        e.Pointer.Capture(rows);
        e.Handled = true;
    }

    private void OnRowsMoved(object? sender, PointerEventArgs e)
    {
        if (press is not { } started)
        {
            return;
        }
        var position = e.GetPosition(rows);
        if (!isDragging && Math.Abs(position.Y - started.Start.Y) < DragThreshold)
        {
            return;
        }
        if (!isDragging)
        {
            isDragging = true;
            Cursor = new Cursor(StandardCursorType.DragMove);
            ((Row)rows.Children[started.Index]).Opacity = 0.45;
            autoScroll.Start();
        }
        pointerY = e.GetPosition(scroller).Y;
        UpdateInsertion(position.Y);
    }

    private void OnRowsReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (press is { } started && isDragging)
        {
            var from = started.Index;
            var ids = items.Select(i => i.Id).ToList();
            EndDrag();
            if (destination != from && destination != from + 1)
            {
                state.Move(ids, [from], destination);
                return;
            }
        }
        EndDrag();
    }

    private void EndDrag()
    {
        press = null;
        isDragging = false;
        autoScroll.Stop();
        insertion.IsVisible = false;
        Cursor = null;
        foreach (var row in rows.Children)
        {
            row.Opacity = 1;
        }
        if (rebuildPending)
        {
            Rebuild();
        }
    }

    /// The gap nearest the pointer: before the first row whose middle is below it.
    private void UpdateInsertion(double y)
    {
        var children = rows.Children.OfType<Row>().ToList();
        destination = children.Count;
        for (var index = 0; index < children.Count; index++)
        {
            var bounds = children[index].Bounds;
            if (y < bounds.Y + bounds.Height / 2)
            {
                destination = index;
                break;
            }
        }
        var lineY = destination < children.Count
            ? children[destination].Bounds.Y + children[destination].RowTop
            : children.LastOrDefault()?.Bounds.Bottom ?? 0;
        insertion.Width = Math.Max(0, rows.Bounds.Width - 16);
        Canvas.SetLeft(insertion, rows.Margin.Left + 8);
        Canvas.SetTop(insertion, rows.Margin.Top + lineY - 1);
        insertion.IsVisible = true;
    }

    /// Scrolls while a row is dragged near the top or bottom edge, faster the closer it is.
    private void ScrollTowardsPointer()
    {
        var height = scroller.Viewport.Height;
        var step = pointerY < Edge ? -Speed(Edge - pointerY) : pointerY > height - Edge ? Speed(pointerY - (height - Edge)) : 0;
        if (step == 0)
        {
            return;
        }
        var maximum = Math.Max(0, scroller.Extent.Height - height);
        var offset = Math.Clamp(scroller.Offset.Y + step, 0, maximum);
        if (offset == scroller.Offset.Y)
        {
            return;
        }
        scroller.Offset = scroller.Offset.WithY(offset);
        UpdateInsertion(pointerY + offset - rows.Margin.Top);
    }

    private static double Speed(double depth)
    {
        var closeness = Math.Min(1, depth / Edge);
        return 14 * closeness * closeness;
    }

    /// One item: circle, title, the line below, and on the right the next step and the project.
    private sealed class Row : Border
    {
        private readonly Border card;
        private readonly Control? action;
        private bool isSelected;
        private bool isHovered;

        public Row(TodoItem item, AppState state, bool showsProject, bool startsNewItems)
        {
            Item = item;
            var panel = new StackPanel();
            if (startsNewItems)
            {
                var heading = Views.Label("Nezařazené – přetáhni nahoru do pořadí", 12.5, Paint.Headline, FontWeight.Bold);
                heading.Margin = new Thickness(10, 14, 0, 6);
                panel.Children.Add(heading);
                RowTop = 14 + 6 + 17;
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
            var stage = Views.StageButton(item, state);
            stage.Margin = new Thickness(0, 1, 0, 0);
            grid.Children.Add(stage);
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(Views.Label(item.Title, 13.5));
            text.Children.Add(Views.ItemMeta(item));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (item.NextStep is { } step)
            {
                action = Views.ActionButton(step, state);
                action.IsVisible = step.Action.IsProminent();
                Grid.SetColumn(action, 2);
                grid.Children.Add(action);
            }
            if (showsProject)
            {
                var project = Views.ProjectLabel(item);
                project.VerticalAlignment = VerticalAlignment.Top;
                project.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetColumn(project, 3);
                grid.Children.Add(project);
            }
            card = new Border
            {
                Child = grid,
                Padding = new Thickness(10, 7),
                CornerRadius = new CornerRadius(7),
                MinHeight = 44,
                Opacity = item.IsWaiting ? 0.7 : 1,
                Background = Brushes.Transparent,
            };
            panel.Children.Add(card);
            Child = panel;
            ToolTip.SetTip(card, item.ShortReference);
            ToolTip.SetShowDelay(card, 1200);
            PointerEntered += (_, _) =>
            {
                isHovered = true;
                Update();
            };
            PointerExited += (_, _) =>
            {
                isHovered = false;
                Update();
            };
        }

        public TodoItem Item { get; }

        /// Where the card starts below a heading, for the insertion line.
        public double RowTop { get; }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                isSelected = value;
                Update();
            }
        }

        private void Update()
        {
            card.Background = isSelected ? Paint.AccentBrush(0.16) : isHovered ? Paint.Primary(0.04) : Brushes.Transparent;
            if (action is not null && Item.NextStep is { } step)
            {
                action.IsVisible = step.Action.IsProminent() || isHovered || isSelected;
            }
        }
    }
}
