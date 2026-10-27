using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Sizes of the timeline at a zoom level. Everything is laid out and drawn at its final size, text included,
/// so the timeline stays sharp at any zoom rather than being a scaled picture.
public readonly record struct TimelineMetrics(double Zoom)
{
    public double Padding => 20 * Zoom;
    public double HeaderWidth => 170 * Zoom;
    public double BlockWidth => 200 * Zoom;
    public double BlockHeight => 72 * Zoom;
    public double ColumnGap => 20 * Zoom;
    public double LaneGap => 14 * Zoom;
    public double RulerHeight => 26 * Zoom;

    public double Scaled(double value) => value * Zoom;

    /// Lines thinner than this get hard to see when zoomed out.
    public double Line(double width) => Math.Max(1, width * Zoom);

    public double X(int step) => Padding + HeaderWidth + step * (BlockWidth + ColumnGap);

    public double Y(int lane) => Padding + RulerHeight + lane * (BlockHeight + LaneGap);

    /// The step whose column, including half of the gaps around it, contains `x`.
    public int StepAt(double x) => Math.Max(0, (int)Math.Floor((x - Padding - HeaderWidth + ColumnGap / 2) / (BlockWidth + ColumnGap)));

    public Size Size(int steps, int lanes) => new(X(steps) + Padding, Y(lanes) + Padding);

    public Rect Block(int lane, int step) => new(X(step), Y(lane), BlockWidth, BlockHeight);
}

/// Horizontal timeline: a lane per project, items as equally long steps, arrows where one waits on another.
public sealed class TimelineView : Panel
{
    private readonly ScrollViewer scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TimelineCanvas canvas;
    private readonly Control placeholder = Views.Placeholder(IconKind.CheckCircle, "Nic tu není");

    public TimelineView(AppState state)
    {
        canvas = new TimelineCanvas(state, scroller);
        scroller.Content = canvas;
        Children.Add(scroller);
        Children.Add(placeholder);
    }

    public event Action<string>? Selected
    {
        add => canvas.Selected += value;
        remove => canvas.Selected -= value;
    }

    /// Ctrl + wheel asks for the next zoom level.
    public event Action<double>? ZoomRequested
    {
        add => canvas.ZoomRequested += value;
        remove => canvas.ZoomRequested -= value;
    }

    public void Show(TimelineSchedule schedule, string? selection, double zoom)
    {
        placeholder.IsVisible = schedule.Lanes.Count == 0;
        var old = canvas.Metrics.Zoom;
        if (canvas.Show(schedule, selection, zoom) && Math.Abs(old - zoom) > 0.001)
        {
            KeepCenter(old, zoom);
        }
    }

    public void Select(string? selection) => canvas.Select(selection);

    /// Zooming keeps what was in the middle of the view in the middle.
    private void KeepCenter(double old, double zoom)
    {
        var viewport = scroller.Viewport;
        if (viewport.Width <= 0)
        {
            return;
        }
        var center = new Point(scroller.Offset.X + viewport.Width / 2, scroller.Offset.Y + viewport.Height / 2);
        var factor = zoom / old;
        scroller.UpdateLayout();
        scroller.Offset = new Vector(Math.Max(0, center.X * factor - viewport.Width / 2), Math.Max(0, center.Y * factor - viewport.Height / 2));
    }
}

/// Draws the whole timeline and handles the pointer: selecting, dragging blocks within and between lanes, drawing
/// „waits on“ arrows from the dots, context menus and the lanes' „+“.
internal sealed class TimelineCanvas : Control
{
    private const double EdgeZone = 56;
    private const double MaxScrollStep = 16;

    private readonly AppState state;
    private readonly ScrollViewer scroller;
    private readonly DispatcherTimer autoScroll;
    private TimelineSchedule schedule = new() { Lanes = [], StepCount = 0 };
    private int steps = 4;
    private string? selection;
    private Hit hover = Hit.None;
    private LinkDrag? link;
    private BlockDrag? drag;
    private Point viewportPointer;
    private (TimelineSchedule Schedule, string? Selection, double Zoom)? pending;

    public TimelineCanvas(AppState state, ScrollViewer scroller)
    {
        this.state = state;
        this.scroller = scroller;
        ClipToBounds = true;
        Focusable = true;
        autoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => ScrollTowardsPointer());
    }

    public event Action<string>? Selected;
    public event Action<double>? ZoomRequested;

    public TimelineMetrics Metrics { get; private set; } = new(1);

    /// Returns false when a drag is in progress: a new layout would move the blocks from under the pointer, so it
    /// waits for the drop.
    public bool Show(TimelineSchedule shown, string? selected, double zoom)
    {
        if (link is not null || drag is not null)
        {
            pending = (shown, selected, zoom);
            return false;
        }
        Apply(shown, selected, zoom);
        return true;
    }

    private void Apply(TimelineSchedule shown, string? selected, double zoom)
    {
        schedule = shown;
        selection = selected;
        // One empty step at the end to drop blocks behind the last one.
        steps = Math.Max(shown.StepCount + 1, 4);
        Metrics = new TimelineMetrics(zoom);
        hover = Hit.None;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Select(string? selected)
    {
        selection = selected;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => Metrics.Size(steps, schedule.Lanes.Count);

    // MARK: Hit testing

    private abstract record Hit
    {
        public static readonly Hit None = new NoHit();
    }

    private sealed record NoHit : Hit;

    private sealed record BlockHit(TimelineBlock Block, int Lane, Rect Frame) : Hit;

    private sealed record HandleHit(TimelineBlock Block, Point Center) : Hit;

    private sealed record PlusHit(TimelineLane Lane) : Hit;

    private sealed record ArrowHit(Edge Edge) : Hit;

    private sealed record LinkDrag(TimelineBlock From, Point Start, Point Location);

    private sealed record BlockDrag(TimelineBlock Block, int Lane, Point Press, Vector Grab, Point Location, bool IsMoving);

    private double HandleSize => Math.Max(12, Metrics.Scaled(16));

    private Hit HitAt(Point point, bool arrows = true)
    {
        var m = Metrics;
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            foreach (var block in schedule.Lanes[lane].Blocks)
            {
                var center = new Point(m.X(block.Slot) + m.BlockWidth, m.Y(lane) + m.BlockHeight / 2);
                if (Distance(point, center) <= HandleSize / 2 + 1)
                {
                    return new HandleHit(block, center);
                }
            }
        }
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            if (Distance(point, PlusCenter(lane)) <= Math.Max(10, m.Scaled(10)))
            {
                return new PlusHit(schedule.Lanes[lane]);
            }
        }
        if (BlockAt(point) is { } hit)
        {
            return hit;
        }
        if (arrows)
        {
            foreach (var edge in Edges())
            {
                if (edge.Points.Zip(edge.Points.Skip(1)).Any(segment => SegmentDistance(point, segment.First, segment.Second) <= 6))
                {
                    return new ArrowHit(edge);
                }
            }
        }
        return Hit.None;
    }

    private BlockHit? BlockAt(Point point)
    {
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            foreach (var block in schedule.Lanes[lane].Blocks)
            {
                var frame = Metrics.Block(lane, block.Slot);
                if (frame.Contains(point))
                {
                    return new BlockHit(block, lane, frame);
                }
            }
        }
        return null;
    }

    /// The lane whose background, a little larger than its blocks, contains the point.
    private int? LaneAt(Point point)
    {
        var inset = Metrics.Scaled(4);
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            var y = Metrics.Y(lane);
            if (point.Y >= y - inset && point.Y <= y + Metrics.BlockHeight + inset && point.X >= Metrics.Padding - inset)
            {
                return lane;
            }
        }
        return null;
    }

    private Point PlusCenter(int lane) =>
        new(Metrics.Padding + Metrics.HeaderWidth - Metrics.Scaled(14) - Metrics.Scaled(7), Metrics.Y(lane) + Metrics.BlockHeight / 2);

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SegmentDistance(Point point, Point a, Point b)
    {
        var length = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
        var t = length == 0 ? 0 : Math.Clamp(((point.X - a.X) * (b.X - a.X) + (point.Y - a.Y) * (b.Y - a.Y)) / length, 0, 1);
        return Distance(point, new Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
    }

    // MARK: Arrows

    /// A „waits on“ link between two blocks, routed through the free space between columns and lanes.
    private sealed record Edge(string DependentId, string BlockerId, string DependentReference, string BlockerReference, List<Point> Points);

    private List<Edge> Edges()
    {
        var places = new Dictionary<string, (int Lane, TimelineBlock Block)>();
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            foreach (var block in schedule.Lanes[lane].Blocks)
            {
                places.TryAdd(block.Id, (lane, block));
            }
        }
        var edges = new List<Edge>();
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            foreach (var block in schedule.Lanes[lane].Blocks)
            {
                foreach (var blockerId in block.Blockers)
                {
                    if (places.TryGetValue(blockerId, out var blocker))
                    {
                        edges.Add(new Edge(block.Id, blockerId, block.Reference, blocker.Block.Reference, Route((blocker.Lane, blocker.Block.Slot), (lane, block.Slot))));
                    }
                }
            }
        }
        return edges;
    }

    /// Right out of the blocker, down or up in the gap between columns, along the gap between lanes next to the
    /// target and into it from the left. Blocks are never crossed except by links inside one lane.
    private List<Point> Route((int Lane, int Step) from, (int Lane, int Step) to)
    {
        var m = Metrics;
        var start = new Point(m.X(from.Step) + m.BlockWidth, m.Y(from.Lane) + m.BlockHeight / 2);
        var end = new Point(m.X(to.Step) - 2, m.Y(to.Lane) + m.BlockHeight / 2);
        if (from.Lane == to.Lane || to.Step <= from.Step)
        {
            return [start, end];
        }
        var lastGap = m.X(to.Step) - m.ColumnGap / 2;
        if (to.Step == from.Step + 1)
        {
            return [start, new(lastGap, start.Y), new(lastGap, end.Y), end];
        }
        var firstGap = start.X + m.ColumnGap / 2;
        var channel = to.Lane > from.Lane ? m.Y(to.Lane) - m.LaneGap / 2 : m.Y(to.Lane) + m.BlockHeight + m.LaneGap / 2;
        return [start, new(firstGap, start.Y), new(firstGap, channel), new(lastGap, channel), new(lastGap, end.Y), end];
    }

    private StreamGeometry ArrowPath(List<Point> points)
    {
        var corner = Metrics.Scaled(8);
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], false);
        for (var index = 1; index < points.Count - 1; index++)
        {
            var (previous, point, next) = (points[index - 1], points[index], points[index + 1]);
            var radius = Math.Min(corner, Math.Min(Distance(previous, point), Distance(point, next)) / 2);
            g.LineTo(Toward(point, previous, radius));
            g.QuadraticBezierTo(point, Toward(point, next, radius));
        }
        g.LineTo(points[^1]);
        g.EndFigure(false);
        return geometry;
    }

    private static Point Toward(Point from, Point to, double distance)
    {
        var length = Distance(from, to);
        return length == 0 ? from : new Point(from.X + (to.X - from.X) / length * distance, from.Y + (to.Y - from.Y) / length * distance);
    }

    // MARK: Drawing

    public override void Render(DrawingContext context)
    {
        var m = Metrics;
        var size = m.Size(steps, schedule.Lanes.Count);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        DrawRuler(context, size);

        var targetLane = drag is { IsMoving: true } moving && BlockAt(moving.Location) is null ? LaneAt(moving.Location) : null;
        var inset = m.Scaled(4);
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            var isTarget = targetLane == lane && drag?.Lane == lane;
            var background = new Rect(m.Padding - inset, m.Y(lane) - inset, size.Width - m.Padding * 2 + inset * 2, m.BlockHeight + inset * 2);
            context.DrawRectangle(Paint.Primary(isTarget ? 0.08 : 0.03), null, new RoundedRect(background, m.Scaled(12)));
            DrawLaneHeader(context, lane);
            DrawGaps(context, lane);
        }

        foreach (var edge in Edges())
        {
            var isHovered = hover is ArrowHit arrow && arrow.Edge == edge;
            var pen = new Pen(Paint.Waiting(isHovered ? 1 : 0.8), m.Line(isHovered ? 2.6 : 1.6), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, ArrowPath(edge.Points));
            DrawHead(context, edge.Points[^2], edge.Points[^1], Math.Max(4, m.Scaled(7)), new Pen(Paint.Waiting(), pen.Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
        }

        var target = drag is { IsMoving: true } dragged ? BlockAt(dragged.Location) : null;
        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            foreach (var block in schedule.Lanes[lane].Blocks)
            {
                var isDragged = drag is { IsMoving: true } && drag.Block.Id == block.Id;
                using (context.PushOpacity(isDragged ? 0.35 : 1))
                {
                    DrawBlock(context, block, m.Block(lane, block.Slot), block.Id == selection, target?.Block.Id == block.Id && !isDragged);
                }
            }
        }
        if (targetLane is { } dropLane && drag?.Lane == dropLane && drag.IsMoving)
        {
            // Where the dragged block would go on the empty step.
            var slot = m.Block(dropLane, m.StepAt(drag.Location.X));
            context.DrawRectangle(null, new Pen(Paint.AccentBrush(), 2, new DashStyle([2.5, 1.5], 0)), new RoundedRect(slot, m.Scaled(9)));
        }

        for (var lane = 0; lane < schedule.Lanes.Count; lane++)
        {
            foreach (var block in schedule.Lanes[lane].Blocks)
            {
                var center = new Point(m.X(block.Slot) + m.BlockWidth, m.Y(lane) + m.BlockHeight / 2);
                var isHovered = hover is HandleHit handle && handle.Block.Id == block.Id;
                var dot = Math.Max(isHovered ? 8 : 6, m.Scaled(isHovered ? 12 : 9));
                context.DrawEllipse(Paint.Waiting(isHovered ? 1 : 0.75), new Pen(Paint.ControlBackground, m.Line(1.5)), center, dot / 2, dot / 2);
            }
        }

        if (drag is { IsMoving: true } ghost)
        {
            var origin = ghost.Location - ghost.Grab;
            using (context.PushOpacity(0.85))
            {
                DrawBlock(context, ghost.Block, new Rect(origin.X, origin.Y, m.BlockWidth, m.BlockHeight), true, false);
            }
        }
        if (link is not null)
        {
            DrawLinkPreview(context, link);
        }
    }

    private void DrawRuler(DrawingContext context, Size size)
    {
        var m = Metrics;
        var line = Paint.Primary(0.06);
        for (var step = 0; step < steps; step++)
        {
            var label = Text.Make(step == 0 ? "Teď" : $"+{step}", m.Scaled(11), step == 0 ? Paint.Headline : Paint.Secondary, FontWeight.SemiBold);
            context.DrawText(label, new Point(m.X(step) + (m.BlockWidth - label.Width) / 2, m.Padding));
            context.FillRectangle(line, new Rect(m.X(step) - m.ColumnGap / 2, m.Padding + m.RulerHeight, 1, Math.Max(0, size.Height - m.Padding * 2 - m.RulerHeight)));
        }
    }

    private void DrawLaneHeader(DrawingContext context, int index)
    {
        var m = Metrics;
        var lane = schedule.Lanes[index];
        var y = m.Y(index);
        var dot = m.Scaled(9);
        var x = m.Padding + m.Scaled(10);
        var plus = PlusCenter(index);
        var nameWidth = plus.X - m.Scaled(9) - (x + dot + m.Scaled(6));
        var name = Text.Make(lane.Name, m.Scaled(13), Paint.Primary(), FontWeight.SemiBold, nameWidth, 2);
        context.DrawEllipse(Paint.Project(lane.Color), null, new Point(x + dot / 2, y + m.BlockHeight / 2), dot / 2, dot / 2);
        context.DrawText(name, new Point(x + dot + m.Scaled(6), y + (m.BlockHeight - name.Height) / 2));
        var isHovered = hover is PlusHit hit && hit.Lane.Key == lane.Key;
        var iconSize = m.Scaled(15);
        if (isHovered)
        {
            context.DrawEllipse(Paint.Primary(0.08), null, plus, iconSize * 0.75, iconSize * 0.75);
        }
        Icons.Draw(context, IconKind.PlusCircle, new Rect(plus.X - iconSize / 2, plus.Y - iconSize / 2, iconSize, iconSize), isHovered ? Paint.AccentBrush() : Paint.Secondary);
    }

    /// Steps where the lane stands still: put off by the user, or because the next item waits on something.
    private void DrawGaps(DrawingContext context, int index)
    {
        var m = Metrics;
        var next = 0;
        foreach (var block in schedule.Lanes[index].Blocks)
        {
            for (var step = next; step < block.Slot; step++)
            {
                var isDelay = step < block.DelayedUntil;
                var color = isDelay ? Paint.Foreground : Color.Parse(Palette.Waiting);
                var frame = m.Block(index, step).Deflate(0.5);
                var dash = new DashStyle([m.Line(4), m.Line(3)], 0);
                context.DrawRectangle(null, new Pen(Paint.Brush(color, isDelay ? 0.22 : 0.35), 1, dash), new RoundedRect(frame, m.Scaled(9)));
                var brush = Paint.Brush(color, isDelay ? 0.5 : 0.8);
                var label = Text.Make(isDelay ? "odloženo" : "čeká", m.Scaled(11), brush);
                var icon = m.Scaled(11);
                var width = icon + m.Scaled(4) + label.Width;
                var origin = new Point(frame.Center.X - width / 2, frame.Center.Y);
                Icons.Draw(context, isDelay ? IconKind.Clock : IconKind.Pause, new Rect(origin.X, origin.Y - icon / 2, icon, icon), brush);
                context.DrawText(label, new Point(origin.X + icon + m.Scaled(4), origin.Y - label.Height / 2));
            }
            next = block.Slot + 1;
        }
    }

    private void DrawBlock(DrawingContext context, TimelineBlock block, Rect frame, bool isSelected, bool isTarget)
    {
        var m = Metrics;
        var corner = m.Scaled(9);
        var shape = new RoundedRect(frame, corner);
        var item = block.Item;
        context.DrawRectangle(item is null ? Paint.Primary(0.04) : Paint.ControlBackground, null, shape);
        using (context.PushClip(shape))
        {
            var stripe = item is null ? Paint.Primary(0.3) : item.IsWaiting ? Paint.Waiting() : Paint.Kind(item.Stage);
            context.FillRectangle(stripe, new Rect(frame.X, frame.Y, m.Scaled(4), frame.Height));

            var left = frame.X + m.Scaled(4) + m.Scaled(8);
            var right = frame.Right - m.Scaled(8);
            var top = frame.Y + m.Scaled(7);
            var rowHeight = m.Scaled(14);
            var circle = m.Scaled(13);
            if (item is not null)
            {
                StageGlyph.Draw(context, item, new Point(left + circle / 2, top + rowHeight / 2), circle);
            }
            else
            {
                Icons.Draw(context, IconKind.Person, new Rect(left, top + (rowHeight - circle) / 2, circle, circle), Paint.Secondary);
            }
            var iconsLeft = DrawStatusIcons(context, block, right, top + rowHeight / 2);
            var referenceX = left + circle + m.Scaled(5);
            var reference = Text.Make(block.Reference, m.Scaled(11), Paint.Secondary, maxWidth: Math.Max(1, iconsLeft - m.Scaled(2) - referenceX));
            context.DrawText(reference, new Point(referenceX, top + (rowHeight - reference.Height) / 2));

            // The labels keep their row at the bottom, the title gets two lines when there is room for them.
            var bottom = frame.Bottom - m.Scaled(7);
            var footer = item is { Labels.Count: 0 } ? 0 : Text.Make("Ag", m.Scaled(10), Brushes.Black).Height + m.Scaled(2);
            var titleTop = top + rowHeight + m.Scaled(3);
            var title = Text.Make(block.Title, m.Scaled(12.5), item?.IsWaiting == true ? Paint.Secondary : Paint.Primary(), FontWeight.Medium, right - left, 2);
            var line = Text.Make("Ag", m.Scaled(12.5), Brushes.Black, FontWeight.Medium).Height;
            title.MaxTextHeight = Math.Max(line, bottom - footer - m.Scaled(2) - titleTop);
            context.DrawText(title, new Point(left, titleTop));

            if (item is not null)
            {
                LabelChips.Draw(context, item.Labels, new Rect(left, bottom - footer, right - left, footer), m.Scaled(10), m.Zoom);
            }
            else
            {
                var foreign = Text.Make("cizí", m.Scaled(10), Paint.Secondary, FontWeight.SemiBold);
                context.DrawText(foreign, new Point(left, bottom - foreign.Height));
            }
        }

        var inner = new RoundedRect(frame.Deflate(1), Math.Max(0, corner - 1));
        if (isTarget)
        {
            context.DrawRectangle(null, new Pen(Paint.AccentBrush(), 2, new DashStyle([2.5, 1.5], 0)), inner);
        }
        else if (isSelected)
        {
            context.DrawRectangle(null, new Pen(Paint.AccentBrush(), 2), inner);
        }
        else if (item is null)
        {
            context.DrawRectangle(null, new Pen(Paint.Primary(0.35), 1, new DashStyle([m.Line(4), m.Line(3)], 0)), new RoundedRect(frame.Deflate(0.5), corner));
        }
        else
        {
            context.DrawRectangle(null, new Pen(Paint.Primary(0.1), 1), new RoundedRect(frame.Deflate(0.5), corner));
        }
    }

    /// Delay, waiting, ready, conflict and review threads, right-aligned in the block's top row. Returns their left edge.
    private double DrawStatusIcons(DrawingContext context, TimelineBlock block, double right, double centerY)
    {
        var m = Metrics;
        var size = m.Scaled(11);
        var gap = m.Scaled(4);
        var parts = new List<(double Width, Action<double> Draw)>();
        void Add(IconKind kind, IBrush brush) =>
            parts.Add((size, x => Icons.Draw(context, kind, new Rect(x, centerY - size / 2, size, size), brush)));
        if (block.Delay > 0)
        {
            Add(IconKind.Clock, Paint.Secondary);
        }
        if (block.Item is { } item)
        {
            if (item.IsWaiting)
            {
                Add(IconKind.PauseCircleFilled, Paint.Waiting());
            }
            if (item.ReleasedBy is not null)
            {
                Add(IconKind.PlayCircleFilled, Paint.Brush(Palette.Ready));
            }
            if (item.HasMergeConflict)
            {
                Add(IconKind.WarningFilled, Paint.Waiting());
            }
            if (item.ReviewThreads is { } threads)
            {
                var isDone = threads.Resolved == threads.Total;
                var brush = Paint.Brush(isDone ? Palette.Ready : Palette.Threads);
                var text = Text.Make($"{threads.Resolved}/{threads.Total}", m.Scaled(11), brush, FontWeight.SemiBold);
                var bubble = size * 0.85;
                var width = bubble + m.Scaled(2) + (isDone ? size : text.Width);
                parts.Add((width, x =>
                {
                    Icons.Draw(context, IconKind.BubbleFilled, new Rect(x, centerY - bubble / 2, bubble, bubble), brush);
                    var after = x + bubble + m.Scaled(2);
                    if (isDone)
                    {
                        Icons.Draw(context, IconKind.Check, new Rect(after, centerY - size / 2, size, size), brush);
                    }
                    else
                    {
                        context.DrawText(text, new Point(after, centerY - text.Height / 2));
                    }
                }));
            }
        }
        var x = right;
        foreach (var (width, draw) in Enumerable.Reverse(parts))
        {
            x -= width;
            draw(x);
            x -= gap;
        }
        return parts.Count == 0 ? right : x + gap;
    }

    private static void DrawHead(DrawingContext context, Point from, Point end, double size, Pen pen)
    {
        var length = Distance(from, end);
        var (dx, dy) = length == 0 ? (1.0, 0.0) : ((end.X - from.X) / length, (end.Y - from.Y) / length);
        var back = new Point(end.X - dx * size, end.Y - dy * size);
        var side = new Vector(-dy * size * 0.65, dx * size * 0.65);
        context.DrawLine(pen, back + side, end);
        context.DrawLine(pen, back - side, end);
    }

    /// The arrow following the pointer, the block under it and what letting go would do.
    private void DrawLinkPreview(DrawingContext context, LinkDrag drawn)
    {
        var target = BlockAt(drawn.Location);
        var isValid = target is not null && state.CanLink(target.Block.Id, drawn.From.Id);
        var color = target is null ? Color.Parse(Palette.Waiting) : isValid ? Paint.Accent : Color.Parse(Palette.Threads);
        var brush = Paint.Brush(color);
        // Over a block the arrow snaps to where the finished one will end.
        var end = target is null ? drawn.Location : new Point(target.Frame.X - 2, target.Frame.Center.Y);
        if (target is not null)
        {
            var shape = new RoundedRect(target.Frame, Metrics.Scaled(9));
            context.DrawRectangle(Paint.Brush(color, 0.12), new Pen(brush, 2), shape);
        }
        var bend = Math.Max(30 * Metrics.Zoom, Math.Abs(end.X - drawn.Start.X) / 2);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(drawn.Start, false);
            g.CubicBezierTo(new Point(drawn.Start.X + bend, drawn.Start.Y), new Point(end.X - bend, end.Y), end);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, 2, new DashStyle([3, 2], 0), PenLineCap.Round), geometry);
        context.DrawEllipse(brush, null, end, 4, 4);

        // The hint stays readable at any zoom.
        var label = target is null
            ? $"Pusť na položku, která na {drawn.From.Reference} čeká"
            : isValid ? $"{target.Block.Reference} čeká na {drawn.From.Reference}" : "Sem vazba nejde";
        var text = Text.Make(label, 12, Brushes.White, FontWeight.SemiBold);
        var capsule = new Rect(drawn.Location.X + 14, drawn.Location.Y + 14, text.Width + 16, text.Height + 8);
        context.DrawRectangle(brush, null, new RoundedRect(capsule, capsule.Height / 2));
        context.DrawText(text, new Point(capsule.X + 8, capsule.Y + 4));
    }

    // MARK: Pointer

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var hit = HitAt(point.Position);
        if (point.Properties.IsRightButtonPressed)
        {
            OpenMenu(hit);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        switch (hit)
        {
            case HandleHit handle:
                link = new LinkDrag(handle.Block, handle.Center, point.Position);
                e.Pointer.Capture(this);
                break;
            case PlusHit plus:
                OpenLaneSearch(plus.Lane);
                break;
            case BlockHit block when e.ClickCount >= 2:
                state.Ui.Open(block.Block.WebUrl);
                break;
            case BlockHit block:
                if (block.Block.Item is { } item)
                {
                    Selected?.Invoke(item.Id);
                }
                drag = new BlockDrag(block.Block, block.Lane, point.Position, point.Position - block.Frame.TopLeft, point.Position, false);
                e.Pointer.Capture(this);
                break;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        viewportPointer = e.GetPosition(scroller);
        if (link is not null)
        {
            link = link with { Location = position };
            autoScroll.Start();
            InvalidateVisual();
            return;
        }
        if (drag is not null)
        {
            if (!drag.IsMoving && Distance(position, drag.Press) < 4)
            {
                return;
            }
            drag = drag with { Location = position, IsMoving = true };
            Cursor = new Cursor(StandardCursorType.DragMove);
            autoScroll.Start();
            InvalidateVisual();
            return;
        }
        var hit = HitAt(position);
        if (hit != hover)
        {
            hover = hit;
            Cursor = hit is NoHit or ArrowHit ? null : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, Tip(hit));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var position = e.GetPosition(this);
        // Acting before the drag ends, so the drop is judged against the layout the user saw.
        if (link is { } drawn && BlockAt(position) is { } target && state.CanLink(target.Block.Id, drawn.From.Id))
        {
            state.Link(target.Block.Id, drawn.From.Id);
        }
        else if (drag is { IsMoving: true } dragged)
        {
            Drop(dragged, position);
        }
        EndDrags();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrags();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hover != Hit.None && link is null && drag is null)
        {
            hover = Hit.None;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Delta.Y != 0)
        {
            ZoomRequested?.Invoke(Zoom.Step(Metrics.Zoom, e.Delta.Y > 0 ? 0.1 : -0.1));
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    private void EndDrags()
    {
        link = null;
        drag = null;
        autoScroll.Stop();
        Cursor = null;
        InvalidateVisual();
        if (pending is { } next)
        {
            pending = null;
            Apply(next.Schedule, next.Selection, next.Zoom);
        }
    }

    /// Onto a block of the same lane moves it there, onto one of another lane makes it wait, onto an empty step of its
    /// own lane puts it there with a delay if needed.
    private void Drop(BlockDrag dragged, Point position)
    {
        var laneIds = schedule.Lanes[dragged.Lane].Blocks.Select(b => b.Id).ToList();
        if (BlockAt(position) is { } target)
        {
            if (target.Block.Id == dragged.Block.Id)
            {
                return;
            }
            if (laneIds.Contains(target.Block.Id))
            {
                state.PlaceInLane(dragged.Block.Id, target.Block.Id, laneIds);
            }
            else
            {
                state.LinkInTimeline(dragged.Block.Id, target.Block.Id);
            }
        }
        else if (LaneAt(position) == dragged.Lane)
        {
            state.PlaceInTimeline(dragged.Block.Id, Metrics.StepAt(position.X), laneIds);
        }
    }

    /// Scrolls sideways while something is dragged near or past the left or right edge, faster the closer it is.
    /// Only while the pointer is level with the timeline, give or take a little.
    private void ScrollTowardsPointer()
    {
        var viewport = scroller.Viewport;
        if (viewportPointer.Y < -40 || viewportPointer.Y > viewport.Height + 40)
        {
            return;
        }
        var x = viewportPointer.X;
        var dx = x < EdgeZone ? -Speed(EdgeZone - x) : x > viewport.Width - EdgeZone ? Speed(x - (viewport.Width - EdgeZone)) : 0;
        if (dx == 0)
        {
            return;
        }
        var maximum = Math.Max(0, scroller.Extent.Width - viewport.Width);
        var offset = Math.Clamp(scroller.Offset.X + dx, 0, maximum);
        if (offset == scroller.Offset.X)
        {
            return;
        }
        scroller.Offset = scroller.Offset.WithX(offset);
        // The content moved under a pointer that stood still.
        var position = viewportPointer + scroller.Offset;
        if (link is not null)
        {
            link = link with { Location = position };
        }
        if (drag is not null)
        {
            drag = drag with { Location = position };
        }
        InvalidateVisual();
    }

    private static double Speed(double depth)
    {
        var closeness = Math.Min(1, depth / EdgeZone);
        return MaxScrollStep * closeness * closeness;
    }

    private static string? Tip(Hit hit) => hit switch
    {
        HandleHit => "Přidat vazbu",
        PlusHit plus => $"Přidat cizí issue nebo MR z {plus.Lane.Name}",
        ArrowHit arrow => $"{arrow.Edge.DependentReference} čeká na {arrow.Edge.BlockerReference}. Pravým tlačítkem vazbu odebereš.",
        BlockHit { Block.Item: null } block => $"Cizí: {block.Block.Reference} {block.Block.Title}",
        BlockHit block => $"{block.Block.Title}\nPřetáhni v řádku pro změnu pořadí, na jiný projekt pro vazbu „čeká na“",
        _ => null,
    };

    // MARK: Menus and popovers

    private void OpenMenu(Hit hit)
    {
        switch (hit)
        {
            case ArrowHit arrow:
                Menus.Open(this, [Menus.Item($"Odebrat vazbu: {arrow.Edge.DependentReference} čeká na {arrow.Edge.BlockerReference}",
                    () => state.RemoveLink(arrow.Edge.DependentId, arrow.Edge.BlockerId))]);
                break;
            case BlockHit or HandleHit:
                var block = hit is BlockHit b ? b.Block : ((HandleHit)hit).Block;
                if (block.Item is { } item)
                {
                    Selected?.Invoke(item.Id);
                }
                Menus.Open(this, BlockMenu(block));
                break;
        }
    }

    private List<Control> BlockMenu(TimelineBlock block)
    {
        var items = new List<Control>
        {
            Menus.Item("O krok později", () => state.Delay(block.Id, 1)),
            Menus.Item("O krok dřív", () => state.Delay(block.Id, -1), block.Delay > 0),
        };
        if (block.Delay > 0)
        {
            items.Add(Menus.Item("Zrušit odklad", () => state.Delay(block.Id, -block.Delay)));
        }
        items.Add(new Separator());
        if (block.Item is { } item)
        {
            items.AddRange(Menus.ForItem(item, state));
        }
        else if (block.External is { } external)
        {
            if (Menus.LinkRemoval(external.Id, block.Reference, [], state) is { } removal)
            {
                items.Add(removal);
            }
            items.Add(Menus.Item("Otevřít v GitLabu", () => state.Ui.Open(external.WebUrl)));
            items.Add(state.IsWaitedOn(external)
                ? Menus.Item("Čeká na to některá tvoje položka", () => { }, isEnabled: false)
                : Menus.Item("Odebrat z timeline", () => state.RemoveFromTimeline(external)));
        }
        return items;
    }

    /// „+“ of a lane: someone else's issue or merge request of that project into the timeline.
    private void OpenLaneSearch(TimelineLane lane)
    {
        var search = new WorkItemSearch("slovo z názvu nebo číslo", text => state.SearchProject(text, lane.AccountId, lane.ProjectId), candidate => state.AddToTimeline(candidate.Blocker))
        {
            OwnProjectId = lane.ProjectId,
        };
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(new TextBlock { Text = $"Přidat do {lane.Name}", FontSize = 15, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Cizí issue nebo MR z tohoto projektu. Můžeš ho pak přetahovat a navazovat na něj, po zavření zmizí.",
            FontSize = 12,
            Foreground = Paint.Secondary,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Views.Box(search));
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(search.FocusField, DispatcherPriority.Input);
        flyout.ShowAt(this, showAtPointer: true);
    }
}
