using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Colors of the app. Text and backgrounds are the foreground color at some opacity, like the macOS app's
/// `.primary.opacity(…)`, so they work in light and dark mode alike.
public static class Paint
{
    public static readonly Color Accent = Color.Parse("#0A84FF");

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static Color Foreground => IsDark ? Colors.White : Colors.Black;

    public static IBrush Brush(string hex, double opacity = 1) => new SolidColorBrush(Color.Parse(hex), opacity);

    public static IBrush Brush(Color color, double opacity = 1) => new SolidColorBrush(color, opacity);

    public static IBrush Primary(double opacity = 0.9) => new SolidColorBrush(Foreground, opacity);

    public static IBrush Secondary => Primary(0.58);

    public static IBrush Tertiary => Primary(0.38);

    public static IBrush AccentBrush(double opacity = 1) => new SolidColorBrush(Accent, opacity);

    /// Behind blocks and cards, a step lighter than the window in dark mode.
    public static IBrush ControlBackground => IsDark ? Brush("#2C2C2E") : Brushes.White;

    public static IBrush Waiting(double opacity = 1) => Brush(Palette.Waiting, opacity);

    public static IBrush Headline => Brush(Palette.Headline);

    public static IBrush Kind(ItemKind kind, double opacity = 1) => Brush(kind.Color(), opacity);

    public static IBrush Project(int index) => Brush(Palette.Project(index));

    /// `#RRGGBB` from GitLab, or `fallback` when it is something else.
    public static Color Hex(string hex, Color fallback) => Color.TryParse(hex, out var color) ? color : fallback;
}
