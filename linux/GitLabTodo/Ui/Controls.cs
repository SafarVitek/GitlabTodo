using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// One of `Icons`, in the size the control gets.
public sealed class Icon : Control
{
    public Icon(IconKind kind, IBrush? brush = null, double size = 14)
    {
        Kind = kind;
        Brush = brush;
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public IconKind Kind { get; }
    /// The text color when not set.
    public IBrush? Brush { get; set; }

    public override void Render(DrawingContext context) =>
        Icons.Draw(context, Kind, new Rect(Bounds.Size), Brush ?? Paint.Primary(0.8), Paint.ControlBackground);
}

public sealed class StageCircle(TodoItem item, double size) : Control
{
    protected override Size MeasureOverride(Size availableSize) => new(size, size);

    public override void Render(DrawingContext context) => StageGlyph.Draw(context, item, new Point(size / 2, size / 2), size);
}

/// Labels that fit the width the control gets, the rest as „+N“ with all names in the tooltip.
public sealed class LabelsView : Control
{
    private readonly IReadOnlyList<ItemLabel> labels;
    private readonly double fontSize;

    public LabelsView(IReadOnlyList<ItemLabel> labels, double fontSize = 10.5)
    {
        this.labels = labels;
        this.fontSize = fontSize;
        VerticalAlignment = VerticalAlignment.Center;
        if (labels.Count > 0)
        {
            ToolTip.SetTip(this, string.Join(", ", labels.Select(l => l.Name)));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = labels.Count == 0 ? 0 : Text.Make("Ag", fontSize, Brushes.Black).Height + 2;
        return new Size(Math.Min(availableSize.Width, LabelChips.FullWidth(labels, fontSize)), height);
    }

    public override void Render(DrawingContext context) => LabelChips.Draw(context, labels, new Rect(Bounds.Size), fontSize);
}

/// A small rotating arc while something loads.
public sealed class Spinner : Control
{
    private readonly DispatcherTimer timer;
    private double angle;

    public Spinner(double size = 14)
    {
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            angle = (angle + 12) % 360;
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var radius = Bounds.Width / 2 - 1.5;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        Point At(double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(angle), false);
            g.ArcTo(At(angle + 270), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Paint.Secondary, 2, lineCap: PenLineCap.Round), geometry);
    }
}

/// Mutually exclusive buttons in one rounded group, like a segmented picker.
public sealed class Segmented : Border
{
    private readonly List<Border> segments = [];
    private int selected;
    private int? hovered;

    public Segmented(IReadOnlyList<Control> contents, int selected = 0)
    {
        this.selected = selected;
        CornerRadius = new CornerRadius(8);
        Background = Paint.Background;
        BorderBrush = Paint.Line;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var index = 0; index < contents.Count; index++)
        {
            var position = index;
            var segment = new Border
            {
                Child = contents[index],
                Padding = new Thickness(11, 3),
                MinHeight = 24,
                CornerRadius = new CornerRadius(6),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            segment.PointerPressed += (_, e) =>
            {
                if (IsEnabled && e.GetCurrentPoint(segment).Properties.IsLeftButtonPressed)
                {
                    Select(position, notify: true);
                    e.Handled = true;
                }
            };
            segment.PointerEntered += (_, _) =>
            {
                hovered = position;
                Update();
            };
            segment.PointerExited += (_, _) =>
            {
                hovered = null;
                Update();
            };
            segments.Add(segment);
            row.Children.Add(segment);
        }
        Child = row;
        Update();
    }

    public event Action<int>? SelectionChanged;

    public int Selected => selected;

    public void Select(int index, bool notify = false)
    {
        selected = index;
        Update();
        if (notify)
        {
            SelectionChanged?.Invoke(index);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty)
        {
            Opacity = IsEnabled ? 1 : 0.45;
        }
    }

    /// The picked segment raised, the others muted until the pointer is over them.
    private void Update()
    {
        for (var position = 0; position < segments.Count; position++)
        {
            var isSelected = position == selected;
            var isHovered = position == hovered && IsEnabled;
            segments[position].Background = isSelected ? Paint.Selected : isHovered ? Paint.Primary(0.05) : Brushes.Transparent;
            segments[position].Child!.Opacity = isSelected || isHovered ? 1 : 0.68;
        }
    }
}

/// Small building blocks shared by the list, the inspector and the timeline's popovers.
public static class Views
{
    public static TextBlock Label(string text, double size = 13, IBrush? brush = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush ?? Paint.Primary(),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Tag(string title, IBrush color, IBrush background) => new()
    {
        Child = Label(title, 10.5, color, FontWeight.SemiBold),
        Background = background,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(5, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Tag(string title, string hex) => Tag(title, Paint.Brush(hex), Paint.Brush(hex, 0.14));

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        row.Children.AddRange(children);
        return row;
    }

    public static Control IconLabel(IconKind icon, string text, IBrush brush, double size = 12, FontWeight weight = FontWeight.Normal) =>
        Row(3, new Icon(icon, brush, size - 1), Label(text, size, brush, weight));

    public static Control ConflictIcon(double size = 12)
    {
        var icon = new Icon(IconKind.WarningFilled, Paint.Waiting(), size);
        ToolTip.SetTip(icon, "Merge konflikt, je potřeba ho vyřešit");
        return icon;
    }

    /// Review threads as resolved/total in red while some are open, a check mark once all are resolved.
    public static Control ThreadsBadge(int resolved, int total, double size = 12)
    {
        var isDone = resolved == total;
        var brush = Paint.Brush(isDone ? Palette.Ready : Palette.Threads);
        var badge = isDone
            ? Row(2, new Icon(IconKind.BubbleFilled, brush, size - 2), new Icon(IconKind.Check, brush, size - 1))
            : Row(2, new Icon(IconKind.BubbleFilled, brush, size - 2), Label($"{resolved}/{total}", size, brush, FontWeight.SemiBold));
        ToolTip.SetTip(badge, isDone ? "Všechna vlákna jsou vyřešená" : $"Vyřešeno {resolved} z {total} vláken");
        return badge;
    }

    /// The item has notes: tasks as done/total in blue, green once all are done, a plain note as its icon.
    public static Control NoteBadge(ItemNote note, double size = 12)
    {
        var tasks = note.Tasks;
        var done = tasks.Count(t => t.IsDone);
        var brush = tasks.Count == 0 ? Paint.Secondary : Paint.Brush(done == tasks.Count ? Palette.Ready : Palette.Note);
        Control badge = tasks.Count == 0
            ? new Icon(IconKind.Note, brush, size)
            : IconLabel(IconKind.Checklist, $"{done}/{tasks.Count}", brush, size, FontWeight.SemiBold);
        ToolTip.SetTip(badge, note.Preview);
        return badge;
    }

    public static Control WaitMark(string description, bool showsLabel = true, double size = 12) =>
        IconLabel(IconKind.PauseCircleFilled, showsLabel ? $"čeká na {description}" : description, Paint.Waiting(), size, FontWeight.Medium);

    public static Control ReadyMark(string finished, double size = 12)
    {
        var mark = IconLabel(IconKind.PlayCircleFilled, "připraveno", Paint.Brush(Palette.Ready), size, FontWeight.SemiBold);
        ToolTip.SetTip(mark, $"{finished} je hotové, můžeš začít");
        return mark;
    }

    public static Control ProjectLabel(TodoItem item, bool showsName = true, double maxWidth = 140)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = Paint.Project(item.ProjectColor), VerticalAlignment = VerticalAlignment.Center };
        if (!showsName)
        {
            return dot;
        }
        var name = Label(item.ProjectName, 12, Paint.Secondary);
        name.MaxWidth = maxWidth;
        return Row(4, name, dot);
    }

    /// References, merge request state and waiting in one secondary line.
    public static Control ItemMeta(TodoItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, ClipToBounds = true };
        row.Children.Add(Label(item.References, 12, Paint.Secondary));
        if (item.ReleasedBy is { } releasedBy)
        {
            row.Children.Add(ReadyMark(releasedBy));
        }
        if (item.IsDraftStage)
        {
            row.Children.Add(Tag("Draft", Paint.Secondary, Paint.Primary(0.08)));
        }
        if (item.HasMergeConflict)
        {
            row.Children.Add(ConflictIcon());
        }
        if (item.ReviewThreads is { } threads)
        {
            row.Children.Add(ThreadsBadge(threads.Resolved, threads.Total));
        }
        if (item.Labels.Count > 0)
        {
            row.Children.Add(new LabelsView(item.Labels) { MaxWidth = 220 });
        }
        if (item.Role == ItemRole.Mentioned)
        {
            row.Children.Add(Tag("zmínka", Palette.MergeRequest));
        }
        if (!item.IsPlaced)
        {
            row.Children.Add(Tag("nové", Paint.AccentBrush(), Paint.AccentBrush(0.14)));
        }
        if (item.WaitDescription is { } waitDescription)
        {
            row.Children.Add(WaitMark(waitDescription));
        }
        if (item.Note is { } note)
        {
            row.Children.Add(NoteBadge(note));
        }
        if (item.MergeRequest is { Role: ItemRole.Assigned, SourceBranch: { } branch })
        {
            row.Children.Add(IconLabel(IconKind.Branch, branch, Paint.Secondary));
        }
        if (item.DueDate is { } due)
        {
            var overdue = due.Date < DateTime.Today;
            row.Children.Add(IconLabel(IconKind.Calendar, $"{due.Day}. {due.Month}.", Paint.Brush(overdue ? Palette.Threads : Palette.Ready)));
        }
        return row;
    }

    public static Button IconButton(IconKind icon, string tip, Action action, double size = 16)
    {
        var button = new Button
        {
            Content = new Icon(icon, Paint.Primary(0.75), size),
            Padding = new Thickness(7),
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "subtle" },
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// „+ MR“ or „Ready“ next to an item.
    public static Button ActionButton(ItemStep step, AppState state, bool large = false)
    {
        var isRunning = state.RunningActions.Contains(step.Target.Id);
        var button = new Button
        {
            Content = Row(4,
                new Icon(step.Action == ItemAction.CreateMergeRequest ? IconKind.Plus : IconKind.Check, Brushes.White, large ? 14 : 11),
                Label(large ? step.Action.Title() : step.Action.ShortTitle().TrimStart('+', ' '), large ? 13 : 11.5, Brushes.White, FontWeight.SemiBold)),
            Background = step.Action.IsProminent() ? Paint.AccentBrush() : Paint.Kind(ItemKind.MergeRequest),
            Foreground = Brushes.White,
            Padding = large ? new Thickness(14, 9) : new Thickness(9, 3),
            CornerRadius = new CornerRadius(large ? 8 : 6),
            IsEnabled = !isRunning,
            Classes = { "filled" },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = large ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(button, step.Action.Title());
        button.Click += (_, _) => _ = state.Perform(step.Action, step.Target.Id);
        return button;
    }

    /// The item's circle; clicking it runs the next step, like ticking off a task.
    public static Control StageButton(TodoItem item, AppState state, double size = 19)
    {
        var circle = new StageCircle(item, size);
        if (item.NextStep is not { } step)
        {
            ToolTip.SetTip(circle, item.Stage == ItemKind.Issue ? "Issue" : "Merge request");
            return circle;
        }
        var button = new Button
        {
            Content = circle,
            Padding = new Thickness(0),
            Classes = { "subtle" },
            CornerRadius = new CornerRadius(size),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = !state.RunningActions.Contains(step.Target.Id),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(button, step.Action.Title());
        button.Click += (_, _) => _ = state.Perform(step.Action, step.Target.Id);
        return button;
    }

    /// Rounded background for fields and cards in the inspector.
    /// Set into the surface around it, like a field.
    public static Border Box(Control child) => new()
    {
        Child = child,
        Padding = new Thickness(12, 10),
        CornerRadius = new CornerRadius(9),
        Background = Paint.Background,
        BorderBrush = Paint.LineSoft,
        BorderThickness = new Thickness(1),
    };

    /// Above a section, quieter than the content under it.
    public static TextBlock SectionTitle(string title) => new()
    {
        Text = title,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = Paint.Secondary,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// A big symbol and a line of text where a list is empty.
    public static Control Placeholder(IconKind icon, string title, string? detail = null)
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Icon(icon, Paint.Tertiary, 40) { HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Paint.Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        if (detail is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12.5,
                Foreground = Paint.Tertiary,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 240,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return panel;
    }

    /// „před 3 min“
    public static string Relative(DateTimeOffset date)
    {
        var elapsed = DateTimeOffset.Now - date;
        return elapsed.TotalSeconds switch
        {
            < 60 => "před chvílí",
            < 3600 => $"před {(int)elapsed.TotalMinutes} min",
            < 86400 => $"před {(int)elapsed.TotalHours} h",
            _ => $"{date.LocalDateTime.Day}. {date.LocalDateTime.Month}. {date.LocalDateTime:HH:mm}",
        };
    }

    /// „1 krok“, „3 kroky“, „5 kroků“
    public static string Steps(int count) => $"{count} {(count == 1 ? "krok" : count is > 1 and < 5 ? "kroky" : "kroků")}";
}
