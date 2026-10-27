using Avalonia;
using Avalonia.Media;

namespace GitLabTodo.Ui;

/// Lucide icons (https://lucide.dev, ISC license, see THIRD_PARTY_NOTICES.md), 24 × 24 strokes from lucide-static
/// 0.460.0, one path per SVG element.
public static class LucideIcons
{
    private static readonly Dictionary<string, Geometry[]> Parsed = [];

    private static readonly Dictionary<string, string[]> Paths = new()
    {
        ["arrow-down-to-line"] = ["M12 17V3", "m6 11 6 6 6-6", "M19 21H5"],
        ["arrow-up-to-line"] = ["M5 3h14", "m18 13-6-6-6 6", "M12 7v14"],
        ["calendar"] = ["M8 2v4", "M16 2v4", "M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z", "M3 10h18"],
        ["check"] = ["M20 6 9 17l-5-5"],
        ["circle-check"] = ["M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z", "m9 12 2 2 4-4"],
        ["circle-dot"] = ["M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z", "M11 12a1 1 0 1 0 2 0a1 1 0 1 0 -2 0z"],
        ["circle-pause"] = ["M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z", "M10 15L10 9", "M14 15L14 9"],
        ["circle-plus"] = ["M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z", "M8 12h8", "M12 8v8"],
        ["circle-user-round"] = ["M18 20a6 6 0 0 0-12 0", "M8 10a4 4 0 1 0 8 0a4 4 0 1 0 -8 0z", "M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z"],
        ["clock"] = ["M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z", "M12 6L12 12L16 14"],
        ["copy"] = ["M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-10a2 2 0 0 1 2 -2z", "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"],
        ["external-link"] = ["M15 3h6v6", "M10 14 21 3", "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"],
        ["folder-git-2"] = ["M9 20H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H20a2 2 0 0 1 2 2v5", "M11 12a2 2 0 1 0 4 0a2 2 0 1 0 -4 0z", "M18 19c-2.8 0-5-2.2-5-5v8", "M18 19a2 2 0 1 0 4 0a2 2 0 1 0 -4 0z"],
        ["git-branch"] = ["M6 3L6 15", "M15 6a3 3 0 1 0 6 0a3 3 0 1 0 -6 0z", "M3 18a3 3 0 1 0 6 0a3 3 0 1 0 -6 0z", "M18 9a9 9 0 0 1-9 9"],
        ["git-merge"] = ["M15 18a3 3 0 1 0 6 0a3 3 0 1 0 -6 0z", "M3 6a3 3 0 1 0 6 0a3 3 0 1 0 -6 0z", "M6 21V9a9 9 0 0 0 9 9"],
        ["inbox"] = ["M22 12L16 12L14 15L10 15L8 12L2 12", "M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"],
        ["list-checks"] = ["m3 17 2 2 4-4", "m3 7 2 2 4-4", "M13 6h8", "M13 12h8", "M13 18h8"],
        ["message-square-text"] = ["M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z", "M13 8H7", "M17 12H7"],
        ["notepad-text"] = ["M8 2v4", "M12 2v4", "M16 2v4", "M6 4h12a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z", "M8 10h6", "M8 14h8", "M8 18h5"],
        ["panel-right"] = ["M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z", "M15 3v18"],
        ["plus"] = ["M5 12h14", "M12 5v14"],
        ["power"] = ["M12 2v10", "M18.4 6.6a9 9 0 1 1-12.77.04"],
        ["refresh-cw"] = ["M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8", "M21 3v5h-5", "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16", "M8 16H3v5"],
        ["search"] = ["M3 11a8 8 0 1 0 16 0a8 8 0 1 0 -16 0z", "m21 21-4.3-4.3"],
        ["server"] = ["M4 2h16a2 2 0 0 1 2 2v4a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-4a2 2 0 0 1 2 -2z", "M4 14h16a2 2 0 0 1 2 2v4a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-4a2 2 0 0 1 2 -2z", "M6 6L6.01 6", "M6 18L6.01 18"],
        ["settings"] = ["M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z", "M9 12a3 3 0 1 0 6 0a3 3 0 1 0 -6 0z"],
        ["trash-2"] = ["M3 6h18", "M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6", "M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2", "M10 11L10 17", "M14 11L14 17"],
        ["x"] = ["M18 6 6 18", "m6 6 12 12"],
        ["zoom-in"] = ["M3 11a8 8 0 1 0 16 0a8 8 0 1 0 -16 0z", "M21 21L16.65 16.65", "M11 8L11 14", "M8 11L14 11"],
        ["zoom-out"] = ["M3 11a8 8 0 1 0 16 0a8 8 0 1 0 -16 0z", "M21 21L16.65 16.65", "M8 11L14 11"],
    };

    /// Strokes the icon into `rect` the way Lucide draws it: round caps and joins, 2 units wide at 24.
    public static void Draw(DrawingContext context, string name, Rect rect, IBrush brush)
    {
        if (!Parsed.TryGetValue(name, out var geometries))
        {
            geometries = Paths[name].Select(Geometry.Parse).ToArray();
            Parsed[name] = geometries;
        }
        var size = Math.Min(rect.Width, rect.Height);
        var scale = size / 24;
        var origin = new Point(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2);
        var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            foreach (var geometry in geometries)
            {
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }
}
