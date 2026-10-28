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
    /// Like the other lines, in the accent color, as VaultSSH shows its update.
    private readonly Button update = new()
    {
        Padding = new Thickness(10, 0),
        Height = 34,
        CornerRadius = new CornerRadius(7),
        Margin = new Thickness(8, 0, 8, 8),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        IsVisible = false,
        Classes = { "subtle" },
    };
    private IReadOnlyList<SidebarItem> shown = [];
    private string? updateVersion;
    private object? selected;

    public Sidebar()
    {
        Background = Paint.Sidebar;
        BorderBrush = Paint.LineSoft;
        BorderThickness = new Thickness(0, 0, 1, 0);
        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://gitlab-todo/Assets/icon.png"))),
            Width = 28,
            Height = 28,
        };
        var name = Views.Label("GitLab Todo", 15, Paint.Primary(), FontWeight.SemiBold);
        name.LetterSpacing = -0.15;
        var title = new Border { Child = Views.Row(10, logo, name), Padding = new Thickness(16, 16, 12, 18) };
        Brand = title;
        var footerLine = new DockPanel();
        DockPanel.SetDock(spinner, Dock.Left);
        footerLine.Children.AddRange([spinner, status]);
        var footer = new Border
        {
            Child = footerLine,
            Padding = new Thickness(18, 10),
            BorderBrush = Paint.LineSoft,
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

    /// The logo and name at the top, which move the window when the app draws its frame.
    public Control Brand { get; }

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
        var row = Views.Row(10, new Icon(IconKind.Download, Paint.AccentBrush(), 16), Views.Label($"Aktualizovat na {version}", 13.5, Paint.AccentBrush(), FontWeight.Medium));
        row.Margin = new Thickness(1, 0, 0, 0);
        update.Content = row;
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
                    var section = Views.Label(item.Name, 12.5, Paint.Tertiary, FontWeight.Medium);
                    section.Margin = new Thickness(10, 16, 0, 5);
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
        private static readonly BoxShadows Marked = BoxShadows.Parse("inset 2 0 0 0 #0A84FF");

        private readonly TextBlock name;
        private readonly TextBlock count;
        private bool isSelected;
        private bool isHovered;

        public Entry(SidebarItem item, Sidebar sidebar)
        {
            Item = item;
            CornerRadius = new CornerRadius(7);
            Padding = new Thickness(10, 0);
            Height = 34;
            Cursor = new Cursor(StandardCursorType.Hand);
            name = Views.Label(item.Name, 13.5, null, FontWeight.Medium);
            count = Views.Label(item.Count.ToString(), 12, Paint.Tertiary);
            var icon = new Panel { Width = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
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

        /// Muted until picked or under the pointer; the picked one raised with the accent along its left edge.
        private void Update()
        {
            Background = isSelected || isHovered ? Paint.Selected : Brushes.Transparent;
            BoxShadow = isSelected ? Marked : default;
            name.Foreground = isSelected || isHovered ? Paint.Primary() : Paint.Primary(0.72);
            count.Foreground = isSelected ? Paint.Secondary : Paint.Tertiary;
        }
    }
}
