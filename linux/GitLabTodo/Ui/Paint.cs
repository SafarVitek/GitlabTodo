using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Colors of the app. Surfaces come from `Look.axaml` for the current theme; text and overlays are the text color at
/// some opacity, like the macOS app's `.primary.opacity(…)`, so they work in light and dark mode alike.
public static class Paint
{
    public static readonly Color Accent = Color.Parse("#0A84FF");

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static Color Foreground => Token("AppTextColor");

    public static IBrush Background => Brush(Token("AppBackgroundColor"));

    /// Behind the sidebar.
    public static IBrush Sidebar => Brush(Token("AppSidebarColor"));

    /// Cards and fields set into the background.
    public static IBrush Surface => Brush(Token("AppSurfaceColor"));

    /// Dividers and outlines.
    public static IBrush Line => Brush(Token("AppLineColor"));

    /// A color of the current theme from `Look.axaml`.
    private static Color Token(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Color color
            ? color
            : IsDark ? Colors.White : Colors.Black;

    public static IBrush Brush(string hex, double opacity = 1) => new SolidColorBrush(Color.Parse(hex), opacity);

    public static IBrush Brush(Color color, double opacity = 1) => new SolidColorBrush(color, opacity);

    public static IBrush Primary(double opacity = 1) => new SolidColorBrush(Foreground, opacity);

    public static IBrush Secondary => Primary(0.58);

    public static IBrush Tertiary => Primary(0.38);

    public static IBrush AccentBrush(double opacity = 1) => new SolidColorBrush(Accent, opacity);

    /// Blocks and cards raised above the background.
    public static IBrush ControlBackground => Brush(Token("AppRaisedColor"));

    public static IBrush Waiting(double opacity = 1) => Brush(Palette.Waiting, opacity);

    public static IBrush Headline => Brush(Palette.Headline);

    public static IBrush Kind(ItemKind kind, double opacity = 1) => Brush(kind.Color(), opacity);

    public static IBrush Project(int index) => Brush(Palette.Project(index));

    /// Subpixel antialiased text, sharper on the ordinary low-density screens common on Linux desktops.
    public static void Sharpen(Visual visual) => RenderOptions.SetTextRenderingMode(visual, TextRenderingMode.SubpixelAntialias);

    /// `#RRGGBB` from GitLab, or `fallback` when it is something else.
    public static Color Hex(string hex, Color fallback) => Color.TryParse(hex, out var color) ? color : fallback;
}
