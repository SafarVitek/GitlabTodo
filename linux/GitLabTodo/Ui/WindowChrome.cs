using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitLabTodo.Ui;

/// On Linux the app draws its own title bars, like VaultSSH: the system's differ from desktop to desktop and look
/// foreign next to the app. The window is then moved by dragging its top bar and resized at its edges. Elsewhere, and
/// with „Systémový rámeček oken“ in the settings, the windows keep the system's frame.
public static class WindowChrome
{
    private const double Grip = 5;

    /// Decided once at launch, before the first window opens.
    public static bool IsDrawn { get; set; } = OperatingSystem.IsLinux();

    /// The window's content without a system frame: a hairline around it while not maximized, and grips at the edges
    /// to resize it.
    public static Control Frame(Window window, Control content)
    {
        if (!IsDrawn)
        {
            return content;
        }
        window.SystemDecorations = SystemDecorations.None;
        var border = new Border { Child = content, BorderBrush = Paint.Line };
        var panel = new Panel();
        panel.Children.Add(border);
        var grips = new List<Control>();
        if (window.CanResize)
        {
            grips.AddRange([
                Edge(window, WindowEdge.North, StandardCursorType.TopSide, HorizontalAlignment.Stretch, VerticalAlignment.Top),
                Edge(window, WindowEdge.South, StandardCursorType.BottomSide, HorizontalAlignment.Stretch, VerticalAlignment.Bottom),
                Edge(window, WindowEdge.West, StandardCursorType.LeftSide, HorizontalAlignment.Left, VerticalAlignment.Stretch),
                Edge(window, WindowEdge.East, StandardCursorType.RightSide, HorizontalAlignment.Right, VerticalAlignment.Stretch),
                Edge(window, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Top),
                Edge(window, WindowEdge.NorthEast, StandardCursorType.TopRightCorner, HorizontalAlignment.Right, VerticalAlignment.Top),
                Edge(window, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Bottom),
                Edge(window, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, HorizontalAlignment.Right, VerticalAlignment.Bottom),
            ]);
            panel.Children.AddRange(grips);
        }
        void Update()
        {
            var isFull = window.WindowState is WindowState.Maximized or WindowState.FullScreen;
            border.BorderThickness = new Thickness(isFull ? 0 : 1);
            foreach (var grip in grips)
            {
                grip.IsVisible = !isFull;
            }
        }
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                Update();
            }
        };
        Update();
        return panel;
    }

    /// Dragging `area` moves the window, a double click maximizes it. Buttons and fields in it keep their clicks.
    public static void MakeDraggable(Control area, Window window)
    {
        if (!IsDrawn)
        {
            return;
        }
        // Hit everywhere, also where nothing is drawn.
        if (area is Panel { Background: null } panel)
        {
            panel.Background = Brushes.Transparent;
        }
        else if (area is Border { Background: null } border)
        {
            border.Background = Brushes.Transparent;
        }
        area.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(area).Properties.IsLeftButtonPressed)
            {
                return;
            }
            e.Handled = true;
            if (e.ClickCount == 2 && window.CanResize)
            {
                ToggleMaximized(window);
                return;
            }
            window.BeginMoveDrag(e);
        };
    }

    /// Minimize, maximize and close at the right end of the main window's top bar.
    public static Control? Buttons(Window window)
    {
        if (!IsDrawn)
        {
            return null;
        }
        var maximize = Views.IconButton(IconKind.Maximize, "Maximalizovat", () => ToggleMaximized(window), 15);
        void Update()
        {
            var isMaximized = window.WindowState == WindowState.Maximized;
            maximize.Content = new Icon(isMaximized ? IconKind.Restore : IconKind.Maximize, Paint.Primary(0.75), 15);
            ToolTip.SetTip(maximize, isMaximized ? "Obnovit velikost" : "Maximalizovat");
        }
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                Update();
            }
        };
        Update();
        var row = Views.Row(2,
            Views.IconButton(IconKind.Minimize, "Minimalizovat", () => window.WindowState = WindowState.Minimized, 15),
            maximize,
            CloseButton(window));
        row.Margin = new Thickness(6, 0, 0, 0);
        return row;
    }

    /// A title and a close button over a window's content: settings and the GitLab form.
    public static Control WithTitleBar(Window window, string title, Control content)
    {
        if (!IsDrawn)
        {
            return content;
        }
        var bar = new DockPanel { Height = 42 };
        var close = CloseButton(window);
        close.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(close, Dock.Right);
        var text = Views.Label(title, 13.5, Paint.Primary(), FontWeight.SemiBold);
        text.Margin = new Thickness(20, 0, 0, 0);
        bar.Children.AddRange([close, text]);
        var header = new Border { Child = bar, BorderBrush = Paint.LineSoft, BorderThickness = new Thickness(0, 0, 0, 1) };
        MakeDraggable(header, window);
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.AddRange([header, content]);
        return layout;
    }

    public static void ToggleMaximized(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// Red under the pointer, like most desktops show it.
    private static Button CloseButton(Window window)
    {
        var button = Views.IconButton(IconKind.Close, "Zavřít", window.Close, 15);
        button.Classes.Add("window-close");
        var icon = (Icon)button.Content!;
        button.PointerEntered += (_, _) =>
        {
            icon.Brush = Brushes.White;
            icon.InvalidateVisual();
        };
        button.PointerExited += (_, _) =>
        {
            icon.Brush = Paint.Primary(0.75);
            icon.InvalidateVisual();
        };
        return button;
    }

    private static Control Edge(Window window, WindowEdge edge, StandardCursorType cursor, HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        var isCorner = edge is WindowEdge.NorthWest or WindowEdge.NorthEast or WindowEdge.SouthWest or WindowEdge.SouthEast;
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Width = isCorner || horizontal != HorizontalAlignment.Stretch ? Grip * (isCorner ? 2 : 1) : double.NaN,
            Height = isCorner || vertical != VerticalAlignment.Stretch ? Grip * (isCorner ? 2 : 1) : double.NaN,
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                window.BeginResizeDrag(edge, e);
            }
        };
        return grip;
    }
}
