using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace GitLabTodo.Ui;

/// One line of the sidebar: a filter with its icon and count, or a section title when `Tag` is null.
public sealed record SidebarItem(object? Tag, string Name, Func<Control>? Icon = null, int Count = 0);

/// Filters on the left: everything, waiting, then the projects, grouped by GitLab when there are more of them.
/// Its own lines rather than a list box: rebuilding a list box from its selection event is what crashed the app.
public sealed class Sidebar : Border
{
    private readonly StackPanel lines = new() { Spacing = 1, Margin = new Thickness(8, 4, 8, 12) };
    private readonly List<Entry> entries = [];
    private readonly TextBlock status = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Spinner spinner = new(12) { IsVisible = false, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button update = new()
    {
        Background = Paint.AccentBrush(),
        Foreground = Brushes.White,
        Padding = new Thickness(10, 7),
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(10, 0, 10, 10),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        IsVisible = false,
        Classes = { "filled" },
    };
    private IReadOnlyList<SidebarItem> shown = [];
    private string? updateVersion;
    private object? selected;

    public Sidebar()
    {
        Background = Paint.Sidebar;
        BorderBrush = Paint.Line;
        BorderThickness = new Thickness(0, 0, 1, 0);
        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://gitlab-todo/Assets/icon.png"))),
            Width = 22,
            Height = 22,
        };
        var title = Views.Row(9, logo, Views.Label("GitLab Todo", 14, Paint.Primary(), FontWeight.SemiBold));
        title.Margin = new Thickness(18, 16, 12, 14);
        var footerLine = new DockPanel();
        DockPanel.SetDock(spinner, Dock.Left);
        footerLine.Children.AddRange([spinner, status]);
        var footer = new Border
        {
            Child = footerLine,
            Padding = new Thickness(18, 10),
            BorderBrush = Paint.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        status.Foreground = Paint.Secondary;
        update.Click += (_, _) => UpdateClicked?.Invoke();
        var content = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(update, Dock.Bottom);
        content.Children.AddRange([title, footer, update]);
        content.Children.Add(new ScrollViewer { Content = lines, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Child = content;
    }

    public event Action<object>? Picked;

    public event Action? UpdateClicked;

    /// A newer release above the footer, like VaultSSH does it; a click opens the settings to install it.
    public void ShowUpdate(string? version)
    {
        update.IsVisible = version is not null;
        if (version is null || version == updateVersion)
        {
            return;
        }
        updateVersion = version;
        update.Content = Views.Row(7, new Icon(IconKind.Download, Brushes.White, 14), Views.Label($"Aktualizovat na {version}", 13, Brushes.White, FontWeight.SemiBold));
        ToolTip.SetTip(update, $"GitLab Todo {version} je k dispozici. Instalace je v nastavení.");
    }

    /// When the list was refreshed, or that it is being refreshed right now.
    public void ShowStatus(string text, bool isBusy)
    {
        status.Text = text;
        ToolTip.SetTip(status, text);
        spinner.IsVisible = isBusy;
    }

    /// Rebuilds the lines only when they changed, otherwise just moves the selection.
    public void Show(IReadOnlyList<SidebarItem> items, object? selection)
    {
        selected = selection;
        if (!items.SequenceEqual(shown, ItemComparer.Instance))
        {
            shown = items;
            entries.Clear();
            lines.Children.Clear();
            foreach (var item in items)
            {
                if (item.Tag is null)
                {
                    var section = Views.Label(item.Name, 11.5, Paint.Tertiary, FontWeight.SemiBold);
                    section.Margin = new Thickness(10, 14, 0, 4);
                    lines.Children.Add(section);
                    continue;
                }
                var entry = new Entry(item, this);
                entries.Add(entry);
                lines.Children.Add(entry);
            }
        }
        foreach (var entry in entries)
        {
            entry.IsSelected = Equals(entry.Item.Tag, selected);
        }
    }

    /// Icons are made anew for every build, so lines compare by what they show.
    private sealed class ItemComparer : IEqualityComparer<SidebarItem>
    {
        public static readonly ItemComparer Instance = new();

        public bool Equals(SidebarItem? x, SidebarItem? y) =>
            x is not null && y is not null && Equals(x.Tag, y.Tag) && x.Name == y.Name && x.Count == y.Count && x.Icon is null == y.Icon is null;

        public int GetHashCode(SidebarItem item) => HashCode.Combine(item.Tag, item.Name, item.Count);
    }

    private sealed class Entry : Border
    {
        private readonly TextBlock name;
        private readonly TextBlock count;
        private bool isSelected;
        private bool isHovered;

        public Entry(SidebarItem item, Sidebar sidebar)
        {
            Item = item;
            CornerRadius = new CornerRadius(7);
            Padding = new Thickness(10, 0);
            Height = 32;
            Cursor = new Cursor(StandardCursorType.Hand);
            name = Views.Label(item.Name, 13.5);
            count = Views.Label(item.Count.ToString(), 12, Paint.Secondary);
            var icon = new Panel { Width = 18, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            if (item.Icon?.Invoke() is { } image)
            {
                image.HorizontalAlignment = HorizontalAlignment.Center;
                icon.Children.Add(image);
            }
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(count, Dock.Right);
            row.Children.AddRange([icon, count, name]);
            Child = row;
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
            PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && item.Tag is { } tag)
                {
                    e.Handled = true;
                    sidebar.Picked?.Invoke(tag);
                }
            };
            Update();
        }

        public SidebarItem Item { get; }

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
            Background = isSelected ? Paint.AccentBrush(Paint.IsDark ? 0.26 : 0.13) : isHovered ? Paint.Primary(0.05) : Brushes.Transparent;
            name.FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal;
            name.Foreground = isSelected && !Paint.IsDark ? Paint.Brush("#0060DF") : Paint.Primary();
            count.Foreground = isSelected ? name.Foreground : Paint.Secondary;
        }
    }
}
