using System.Globalization;
using Avalonia;
using Avalonia.Media;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

public enum IconKind
{
    Plus,
    PlusCircle,
    Close,
    CloseCircleFilled,
    Check,
    CheckCircle,
    Pause,
    PauseCircleFilled,
    PlayCircleFilled,
    Clock,
    Refresh,
    Gear,
    Sidebar,
    ZoomIn,
    ZoomOut,
    ExternalLink,
    Copy,
    WarningFilled,
    BubbleFilled,
    TextBubble,
    Branch,
    Calendar,
    Person,
    Merge,
    Circle,
    Tray,
    Search,
    ToTop,
    ToBottom,
    Server,
    Trash,
    Note,
    Checklist,
    Checkbox,
    CheckboxChecked,
}

/// Icons drawn as vectors on a 16 × 16 grid, sharp at any size and zoom. In the spirit of the SF Symbols the macOS
/// app uses, without bundling an icon font.
public static class Icons
{
    public static void Draw(DrawingContext context, IconKind kind, Rect rect, IBrush brush, IBrush? contrast = null)
    {
        var scale = Math.Min(rect.Width, rect.Height) / 16;
        var origin = new Point(rect.X + (rect.Width - 16 * scale) / 2, rect.Y + (rect.Height - 16 * scale) / 2);
        Point P(double x, double y) => new(origin.X + x * scale, origin.Y + y * scale);
        var pen = new Pen(brush, 1.5 * scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var thin = new Pen(brush, 1.25 * scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        contrast ??= Brushes.White;
        var cut = new Pen(contrast, 1.5 * scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        void Line(Pen p, double x1, double y1, double x2, double y2) => context.DrawLine(p, P(x1, y1), P(x2, y2));
        void Circle(IBrush? fill, Pen? p, double x, double y, double r) => context.DrawEllipse(fill, p, P(x, y), r * scale, r * scale);
        void Box(Pen p, double x1, double y1, double x2, double y2, double r) =>
            context.DrawRectangle(null, p, new RoundedRect(new Rect(P(x1, y1), P(x2, y2)), r * scale));
        void Path(IBrush? fill, Pen? p, bool closed, params Point[] points)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(P(points[0].X, points[0].Y), fill is not null);
                foreach (var point in points.Skip(1))
                {
                    g.LineTo(P(point.X, point.Y));
                }
                g.EndFigure(closed);
            }
            context.DrawGeometry(fill, p, geometry);
        }
        void Magnifier()
        {
            Circle(null, pen, 7, 7, 4.5);
            Line(pen, 10.4, 10.4, 14, 14);
        }

        switch (kind)
        {
            case IconKind.Plus:
                Line(pen, 8, 3, 8, 13);
                Line(pen, 3, 8, 13, 8);
                break;
            case IconKind.PlusCircle:
                Circle(null, thin, 8, 8, 6.5);
                Line(thin, 8, 5, 8, 11);
                Line(thin, 5, 8, 11, 8);
                break;
            case IconKind.Close:
                Line(pen, 4, 4, 12, 12);
                Line(pen, 12, 4, 4, 12);
                break;
            case IconKind.CloseCircleFilled:
                Circle(brush, null, 8, 8, 7);
                Line(cut, 5.5, 5.5, 10.5, 10.5);
                Line(cut, 10.5, 5.5, 5.5, 10.5);
                break;
            case IconKind.Check:
                Path(null, pen, false, new(3, 8.5), new(6.5, 12), new(13, 4.5));
                break;
            case IconKind.CheckCircle:
                Circle(null, thin, 8, 8, 6.5);
                Path(null, thin, false, new(5, 8.3), new(7.2, 10.5), new(11, 5.8));
                break;
            case IconKind.Pause:
                Line(pen, 6, 4, 6, 12);
                Line(pen, 10, 4, 10, 12);
                break;
            case IconKind.PauseCircleFilled:
                Circle(brush, null, 8, 8, 7);
                Line(cut, 6.3, 5.3, 6.3, 10.7);
                Line(cut, 9.7, 5.3, 9.7, 10.7);
                break;
            case IconKind.PlayCircleFilled:
                Circle(brush, null, 8, 8, 7);
                Path(contrast, null, true, new(6.4, 4.9), new(11.2, 8), new(6.4, 11.1));
                break;
            case IconKind.Clock:
                Circle(null, thin, 8, 8, 6.5);
                Path(null, thin, false, new(8, 4.5), new(8, 8), new(10.5, 9.5));
                break;
            case IconKind.Refresh:
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(P(8, 2.5), false);
                    g.ArcTo(P(3.24, 5.25), new Size(5.5 * scale, 5.5 * scale), 0, true, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
                Path(null, pen, false, new(10.2, 0.6), new(8, 2.5), new(10.2, 4.4));
                break;
            }
            case IconKind.Gear:
                Circle(null, thin, 8, 8, 2.2);
                Circle(null, thin, 8, 8, 4.8);
                for (var spoke = 0; spoke < 8; spoke++)
                {
                    var angle = spoke * Math.PI / 4;
                    Line(pen, 8 + 4.8 * Math.Cos(angle), 8 + 4.8 * Math.Sin(angle), 8 + 7 * Math.Cos(angle), 8 + 7 * Math.Sin(angle));
                }
                break;
            case IconKind.Sidebar:
                Box(thin, 1.5, 2.5, 14.5, 13.5, 2);
                Line(thin, 10, 2.5, 10, 13.5);
                break;
            case IconKind.ZoomIn:
                Magnifier();
                Line(thin, 7, 5, 7, 9);
                Line(thin, 5, 7, 9, 7);
                break;
            case IconKind.ZoomOut:
                Magnifier();
                Line(thin, 5, 7, 9, 7);
                break;
            case IconKind.Search:
                Magnifier();
                break;
            case IconKind.ExternalLink:
                Box(thin, 2, 2, 14, 14, 2.5);
                Line(thin, 6, 10, 10.5, 5.5);
                Path(null, thin, false, new(7, 5.5), new(10.5, 5.5), new(10.5, 9));
                break;
            case IconKind.Copy:
                Box(thin, 6, 6, 14, 14, 1.5);
                Path(null, thin, false, new(3.5, 10), new(2, 10), new(2, 2), new(10, 2), new(10, 3.5));
                break;
            case IconKind.WarningFilled:
                Path(brush, new Pen(brush, 1.5 * scale, lineJoin: PenLineJoin.Round), true, new(8, 1.8), new(14.8, 13.8), new(1.2, 13.8));
                Line(cut, 8, 5.8, 8, 9.4);
                Circle(contrast, null, 8, 11.6, 0.9);
                break;
            case IconKind.BubbleFilled:
                context.DrawRectangle(brush, null, new RoundedRect(new Rect(P(1.5, 2), P(14.5, 11)), 3 * scale));
                Path(brush, null, true, new(4, 10), new(4, 14.5), new(8.5, 10));
                break;
            case IconKind.TextBubble:
                Box(thin, 1.5, 2, 14.5, 11, 3);
                Path(null, thin, false, new(4, 11), new(4, 14.5), new(7.5, 11));
                Line(thin, 5, 5.5, 11, 5.5);
                Line(thin, 5, 8, 9, 8);
                break;
            case IconKind.Branch:
                Circle(null, thin, 5, 3.3, 1.6);
                Circle(null, thin, 5, 12.7, 1.6);
                Circle(null, thin, 11, 4.3, 1.6);
                Line(thin, 5, 4.9, 5, 11.1);
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(P(11, 5.9), false);
                    g.QuadraticBezierTo(P(11, 9.5), P(5, 10.2));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, thin, geometry);
                break;
            }
            case IconKind.Calendar:
                Box(thin, 2, 3, 14, 14, 2);
                Line(thin, 2, 6.5, 14, 6.5);
                Line(thin, 5.5, 1.5, 5.5, 4.5);
                Line(thin, 10.5, 1.5, 10.5, 4.5);
                break;
            case IconKind.Person:
            {
                Circle(null, thin, 8, 8, 6.5);
                Circle(null, thin, 8, 6.3, 2.3);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(P(3.8, 12.7), false);
                    g.QuadraticBezierTo(P(8, 8.2), P(12.2, 12.7));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, thin, geometry);
                break;
            }
            case IconKind.Merge:
                DrawMerge(context, rect, brush, 1.6);
                break;
            case IconKind.Circle:
                Circle(null, pen, 8, 8, 5.5);
                break;
            case IconKind.Tray:
                Box(thin, 2, 3, 14, 13, 2);
                Line(thin, 5, 6.5, 11, 6.5);
                Line(thin, 5, 9.5, 11, 9.5);
                break;
            case IconKind.ToTop:
                Line(pen, 3, 2.5, 13, 2.5);
                Line(pen, 8, 14, 8, 5.5);
                Path(null, pen, false, new(4.5, 9), new(8, 5.5), new(11.5, 9));
                break;
            case IconKind.ToBottom:
                Line(pen, 3, 13.5, 13, 13.5);
                Line(pen, 8, 2, 8, 10.5);
                Path(null, pen, false, new(4.5, 7), new(8, 10.5), new(11.5, 7));
                break;
            case IconKind.Server:
                Box(thin, 2, 2.5, 14, 7, 1.5);
                Box(thin, 2, 9, 14, 13.5, 1.5);
                Circle(brush, null, 4.6, 4.75, 0.7);
                Circle(brush, null, 4.6, 11.25, 0.7);
                break;
            case IconKind.Note:
                Box(thin, 2.5, 1.5, 13.5, 14.5, 2);
                Line(thin, 5, 5, 11, 5);
                Line(thin, 5, 8, 11, 8);
                Line(thin, 5, 11, 9, 11);
                break;
            case IconKind.Checklist:
                Path(null, thin, false, new(1.5, 4), new(3, 5.5), new(5.5, 2.5));
                Line(thin, 8, 4, 14.5, 4);
                Box(thin, 1.8, 9.6, 5.2, 13, 0.8);
                Line(thin, 8, 11.3, 14.5, 11.3);
                break;
            case IconKind.Checkbox:
                Box(thin, 2, 2, 14, 14, 3.5);
                break;
            case IconKind.CheckboxChecked:
                context.DrawRectangle(brush, null, new RoundedRect(new Rect(P(1.5, 1.5), P(14.5, 14.5)), 3.5 * scale));
                Path(null, cut, false, new(4.6, 8.3), new(7, 10.6), new(11.4, 5.6));
                break;
            case IconKind.Trash:
                Line(thin, 2.5, 4.5, 13.5, 4.5);
                Path(null, thin, false, new(6, 4.5), new(6, 2.5), new(10, 2.5), new(10, 4.5));
                Path(null, thin, false, new(4, 4.5), new(4.8, 14), new(11.2, 14), new(12, 4.5));
                break;
        }
    }

    /// Two branches joining into one, pointing up: the merge request glyph.
    public static void DrawMerge(DrawingContext context, Rect rect, IBrush brush, double weight)
    {
        var scale = Math.Min(rect.Width, rect.Height) / 16;
        var origin = new Point(rect.X + (rect.Width - 16 * scale) / 2, rect.Y + (rect.Height - 16 * scale) / 2);
        Point P(double x, double y) => new(origin.X + x * scale, origin.Y + y * scale);
        var pen = new Pen(brush, weight * scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(3.5, 14.5), false);
            g.LineTo(P(3.5, 11));
            g.QuadraticBezierTo(P(3.5, 7.5), P(8, 6.5));
            g.EndFigure(false);
            g.BeginFigure(P(12.5, 14.5), false);
            g.LineTo(P(12.5, 11));
            g.QuadraticBezierTo(P(12.5, 7.5), P(8, 6.5));
            g.EndFigure(false);
            g.BeginFigure(P(8, 6.5), false);
            g.LineTo(P(8, 1.8));
            g.EndFigure(false);
            g.BeginFigure(P(5.2, 4.3), false);
            g.LineTo(P(8, 1.5));
            g.LineTo(P(10.8, 4.3));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}

/// Text drawn straight into a drawing context, for the timeline and the small custom controls.
public static class Text
{
    public static Typeface Face(FontWeight weight = FontWeight.Normal) => new(FontFamily.Default, FontStyle.Normal, weight);

    public static FormattedText Make(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal, double maxWidth = double.PositiveInfinity, int maxLines = 1)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face(weight), Math.Max(1, size), brush)
        {
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (!double.IsInfinity(maxWidth))
        {
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
        }
        return formatted;
    }
}

/// The circle in front of every item: a green ring for an issue, a purple one with the merge glyph for a merge request.
/// A dashed ring is a draft, an orange badge a merge conflict.
public static class StageGlyph
{
    public static void Draw(DrawingContext context, TodoItem item, Point center, double size)
    {
        var stage = item.Stage;
        var radius = size / 2;
        var line = Math.Max(1.4, size / 12);
        context.DrawEllipse(Paint.Kind(stage, 0.12), null, center, radius, radius);
        IDashStyle? dash = item.IsDraftStage ? new DashStyle([size / 7 / line, size / 10 / line], 0) : null;
        context.DrawEllipse(null, new Pen(Paint.Kind(stage), line, dash), center, radius - line / 2, radius - line / 2);
        if (stage == ItemKind.MergeRequest)
        {
            var glyph = size * 0.5;
            Icons.DrawMerge(context, new Rect(center.X - glyph / 2, center.Y - glyph / 2, glyph, glyph), Paint.Kind(stage), 2.4);
        }
        if (item.HasMergeConflict)
        {
            // A merge conflict needs fixing before anything else happens with the MR.
            var badge = size * 0.29;
            var badgeCenter = new Point(center.X + radius * 0.82, center.Y - radius * 0.72);
            context.DrawEllipse(Paint.Waiting(), null, badgeCenter, badge, badge);
            var pen = new Pen(Brushes.White, Math.Max(1, size * 0.09), lineCap: PenLineCap.Round);
            context.DrawLine(pen, new Point(badgeCenter.X, badgeCenter.Y - badge * 0.5), new Point(badgeCenter.X, badgeCenter.Y + badge * 0.12));
            context.DrawEllipse(Brushes.White, null, new Point(badgeCenter.X, badgeCenter.Y + badge * 0.5), size * 0.045, size * 0.045);
        }
    }
}

/// GitLab labels as chips in their colors: as many as fit, the rest as „+N“.
public static class LabelChips
{
    /// Draws into `rect` and returns the width used.
    public static double Draw(DrawingContext context, IReadOnlyList<ItemLabel> labels, Rect rect, double fontSize, double scale = 1)
    {
        var chips = labels.Select(label => Chip(label, fontSize)).ToList();
        var gap = 3 * scale;
        var padding = 5 * scale;
        double Width(FormattedText text) => text.WidthIncludingTrailingWhitespace + padding * 2;
        var shown = chips.Count;
        while (shown > 0)
        {
            var total = chips.Take(shown).Sum(Width) + gap * (shown - 1);
            if (shown < chips.Count)
            {
                total += gap + Width(More(chips.Count - shown, fontSize));
            }
            if (total <= rect.Width)
            {
                break;
            }
            shown--;
        }
        var x = rect.X;
        var height = Math.Min(rect.Height, chips.Count == 0 ? 0 : chips[0].Height + 2 * scale);
        for (var index = 0; index < chips.Count; index++)
        {
            var text = index < shown ? chips[index] : More(chips.Count - shown, fontSize);
            var background = index < shown
                ? Paint.Brush(Paint.Hex(labels[index].Color, Colors.Gray))
                : Paint.Primary(0.08);
            var chip = new Rect(x, rect.Y, Width(text), height);
            if (chip.Right > rect.Right + 0.5)
            {
                break;
            }
            context.DrawRectangle(background, null, new RoundedRect(chip, height / 2));
            context.DrawText(text, new Point(x + padding, rect.Y + (height - text.Height) / 2));
            x = chip.Right + gap;
            if (index >= shown)
            {
                break;
            }
        }
        return Math.Max(0, x - gap - rect.X);
    }

    /// Width all labels would take, for measuring.
    public static double FullWidth(IReadOnlyList<ItemLabel> labels, double fontSize, double scale = 1) =>
        labels.Count == 0 ? 0 : labels.Sum(l => Chip(l, fontSize).WidthIncludingTrailingWhitespace + 10 * scale) + 3 * scale * (labels.Count - 1);

    private static FormattedText Chip(ItemLabel label, double fontSize) =>
        Text.Make(label.Name.Replace("::", ": "), fontSize, Paint.Brush(Paint.Hex(label.TextColor, Colors.White)), FontWeight.Medium);

    private static FormattedText More(int count, double fontSize) => Text.Make($"+{count}", fontSize, Paint.Secondary, FontWeight.Medium);
}
