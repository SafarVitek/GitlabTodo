using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Sidebar with projects, the work order as a list or a timeline, and the selected item's detail.
public sealed class MainWindow : Window
{
    private abstract record Filter;

    private sealed record AllItems : Filter;

    private sealed record WaitingItems : Filter;

    private sealed record ProjectItems(string Key) : Filter;

    private readonly AppState state;
    private readonly Shell shell;
    private Filter filter = new AllItems();
    private KindFilter kindFilter = KindFilter.All;
    private bool isTimeline;
    private bool showsInspector = true;
    private string? selection;
    private int focusSerial;
    private bool isUpdatePending;
    private Parts parts = null!;

    public MainWindow(AppState state, Shell shell)
    {
        this.state = state;
        this.shell = shell;
        Title = "GitLab Todo";
        Width = 1100;
        Height = 700;
        MinWidth = 860;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildContent();
        state.Changed += ScheduleUpdate;
        // Colors are taken when controls are made, a new theme needs new ones.
        ActualThemeVariantChanged += (_, _) =>
        {
            BuildContent();
            Update();
        };
        AddHandler(KeyDownEvent, OnShortcut, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Update();
    }

    /// The controls of one build of the window.
    private sealed record Parts(
        Sidebar Sidebar,
        TextBlock Title,
        TextBlock Subtitle,
        Segmented Mode,
        Segmented Kind,
        StackPanel ZoomControls,
        TextBlock ZoomLabel,
        Button Refresh,
        StackPanel Errors,
        Border ErrorBanner,
        OrderList OrderList,
        TimelineView Timeline,
        Inspector Inspector,
        Border InspectorPane);

    private void BuildContent()
    {
        var orderList = new OrderList(state);
        orderList.Selected += Select;
        var timeline = new TimelineView(state);
        timeline.Selected += Select;
        timeline.ZoomRequested += state.SetTimelineZoom;
        var inspector = new Inspector(state);

        var mode = new Segmented([Views.Label("Pořadí", 12.5), Views.Label("Timeline", 12.5)], isTimeline ? 1 : 0);
        mode.SelectionChanged += index =>
        {
            isTimeline = index == 1;
            Update();
        };
        var kind = new Segmented([
            Views.Label("Vše", 12.5),
            Views.Row(4, new Icon(IconKind.Circle, Paint.Kind(ItemKind.Issue), 11), Views.Label("Issue", 12.5)),
            Views.Row(4, new Icon(IconKind.Merge, Paint.Kind(ItemKind.MergeRequest), 12), Views.Label("MR", 12.5)),
        ], (int)kindFilter);
        kind.SelectionChanged += index =>
        {
            kindFilter = (KindFilter)index;
            Update();
        };

        var zoomLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 42, TextAlignment = TextAlignment.Center, Foreground = Paint.Secondary };
        ToolTip.SetTip(zoomLabel, "Zoom timeline, jde i Ctrl + kolečkem. Ctrl 0 vrátí 100 %.");
        var zoomControls = Views.Row(0,
            Views.IconButton(IconKind.ZoomOut, "Oddálit (Ctrl −)", () => ZoomBy(-0.1)),
            zoomLabel,
            Views.IconButton(IconKind.ZoomIn, "Přiblížit (Ctrl +)", () => ZoomBy(0.1)),
            Separator());
        var refresh = Views.IconButton(IconKind.Refresh, "Obnovit (F5)", () => _ = state.Refresh());
        var title = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var subtitle = new TextBlock { FontSize = 12.5, Foreground = Paint.Secondary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        titles.Children.AddRange([title, subtitle]);

        var inspectorPane = new Border
        {
            Child = inspector,
            Width = 340,
            BorderBrush = Paint.Line,
            BorderThickness = new Thickness(1, 0, 0, 0),
            IsVisible = showsInspector,
        };
        var end = Views.Row(4,
            zoomControls,
            refresh,
            Views.IconButton(IconKind.Gear, "Nastavení (Ctrl ,)", shell.ShowSettings),
            Views.IconButton(IconKind.Sidebar, "Detail", () =>
            {
                showsInspector = !showsInspector;
                inspectorPane.IsVisible = showsInspector;
            }));
        var switches = Views.Row(8, mode, kind);
        switches.Margin = new Thickness(0, 0, 12, 0);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(switches, 1);
        Grid.SetColumn(end, 2);
        bar.Children.AddRange([titles, switches, end]);
        var toolbar = new Border
        {
            Child = bar,
            Padding = new Thickness(22, 12, 12, 12),
            MinHeight = 66,
            BorderBrush = Paint.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var errors = new StackPanel { Spacing = 4 };
        var errorBanner = new Border
        {
            Child = errors,
            Background = Paint.Waiting(0.1),
            BorderBrush = Paint.Waiting(0.25),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 8),
            IsVisible = false,
        };
        var content = new Panel();
        content.Children.AddRange([orderList, timeline]);
        var center = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(errorBanner, Dock.Top);
        center.Children.AddRange([toolbar, errorBanner, content]);

        var sidebar = new Sidebar { Width = 232 };
        sidebar.Picked += tag =>
        {
            filter = (Filter)tag;
            ScheduleUpdate();
        };
        var layout = new DockPanel();
        DockPanel.SetDock(sidebar, Dock.Left);
        DockPanel.SetDock(inspectorPane, Dock.Right);
        layout.Children.AddRange([sidebar, inspectorPane, center]);
        Content = layout;
        parts = new Parts(sidebar, title, subtitle, mode, kind, zoomControls, zoomLabel, refresh, errors, errorBanner, orderList, timeline, inspector, inspectorPane);
    }

    private List<TodoItem> FilteredItems
    {
        get
        {
            var items = filter switch
            {
                WaitingItems => state.Items.Where(i => i.IsWaiting),
                ProjectItems project => state.Items.Where(i => i.ProjectKey == project.Key),
                _ => state.Items,
            };
            return items.Where(i => kindFilter.Includes(i)).ToList();
        }
    }

    /// Several changes in a row, e.g. from one refresh, redraw once.
    private void ScheduleUpdate()
    {
        if (isUpdatePending)
        {
            return;
        }
        isUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            isUpdatePending = false;
            Update();
        });
    }

    private void Update()
    {
        ApplyFocusRequest();
        UpdateSidebar();
        var items = FilteredItems;
        parts.Title.Text = filter switch
        {
            WaitingItems => "Čeká",
            ProjectItems project => state.Items.FirstOrDefault(i => i.ProjectKey == project.Key)?.ProjectName ?? "Projekt",
            _ => "Vše",
        };
        var summary = new List<string> { $"{items.Count} položek" };
        if (items.Count(i => i.IsWaiting) is > 0 and var waiting)
        {
            summary.Add($"{waiting} čeká");
        }
        if (items.Count(i => !i.IsPlaced) is > 0 and var unplaced)
        {
            summary.Add($"{unplaced} nezařazených");
        }
        parts.Subtitle.Text = string.Join(" · ", summary);
        ToolTip.SetTip(parts.Subtitle, parts.Subtitle.Text);

        parts.Mode.Select(isTimeline ? 1 : 0);
        // An overview across projects, the sidebar and kind filters do not apply.
        parts.Kind.IsEnabled = !isTimeline;
        ToolTip.SetTip(parts.Kind, isTimeline ? "Timeline ukazuje všechno napříč projekty" : "Zobrazit issue, merge requesty nebo obojí");
        parts.ZoomControls.IsVisible = isTimeline;
        parts.ZoomLabel.Text = $"{Math.Round(state.TimelineZoom * 100)} %";
        parts.Sidebar.ShowStatus(
            state.IsRefreshing ? "Obnovuji…" : state.LastRefresh is { } refreshed ? $"Aktualizováno {Views.Relative(refreshed)}" : "Zatím neaktualizováno",
            state.IsRefreshing);
        parts.Refresh.IsEnabled = !state.IsRefreshing;

        var failing = state.Accounts.Where(a => state.AccountErrors.ContainsKey(a.Id)).ToList();
        parts.Errors.Children.Clear();
        foreach (var account in failing)
        {
            parts.Errors.Children.Add(Views.IconLabel(IconKind.WarningFilled, $"{account.Name}: {state.AccountErrors[account.Id]}", Paint.Waiting(), 12));
        }
        parts.ErrorBanner.IsVisible = failing.Count > 0;

        parts.OrderList.IsVisible = !isTimeline;
        parts.Timeline.IsVisible = isTimeline;
        if (isTimeline)
        {
            parts.Timeline.Show(state.TimelineSchedule(), selection, state.TimelineZoom);
        }
        else
        {
            parts.OrderList.ShowsProject = filter is not ProjectItems;
            parts.OrderList.Show(items, selection);
        }
        parts.InspectorPane.IsVisible = showsInspector;
        parts.Inspector.Show(state.Items.FirstOrDefault(i => i.Id == selection));
    }

    private void UpdateSidebar()
    {
        var tabs = ProjectTab.Tabs(state.Items);
        var accountNames = tabs.Select(t => t.AccountName).Distinct().Order(StringComparer.Ordinal).ToList();
        var items = new List<SidebarItem>
        {
            new(new AllItems(), "Vše", () => new Icon(IconKind.Tray, Paint.AccentBrush(), 16), state.Items.Count),
            new(new WaitingItems(), "Čeká", () => new Icon(IconKind.Pause, Paint.Waiting(), 16), state.Items.Count(i => i.IsWaiting)),
        };
        foreach (var accountName in accountNames)
        {
            items.Add(new SidebarItem(null, accountNames.Count > 1 ? accountName : "Projekty"));
            items.AddRange(tabs.Where(t => t.AccountName == accountName).Select(tab =>
                new SidebarItem(new ProjectItems(tab.Key), tab.Name, () => new Ellipse { Width = 9, Height = 9, Fill = Paint.Project(tab.Color) }, tab.Count)));
        }
        if (!items.Any(i => Equals(i.Tag, filter)))
        {
            // The project is gone, e.g. its last item got done.
            filter = new AllItems();
        }
        parts.Sidebar.Show(items, filter);
    }

    private static Border Separator() => new() { Width = 1, Height = 20, Background = Paint.Line, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };

    private void Select(string id)
    {
        selection = id;
        showsInspector = true;
        parts.InspectorPane.IsVisible = true;
        parts.OrderList.Select(id);
        parts.Timeline.Select(id);
        parts.Inspector.Show(state.Items.FirstOrDefault(i => i.Id == id));
        // Picking an item shows its detail and acknowledges that it is ready to start.
        state.AcknowledgeRelease(id);
    }

    /// Shows the item asked for, e.g. from the tray menu, switching to its project if the filter hides it.
    private void ApplyFocusRequest()
    {
        if (state.Focus.Serial == focusSerial)
        {
            return;
        }
        focusSerial = state.Focus.Serial;
        if (state.Focus.ItemId is not { } itemId || state.Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
        {
            return;
        }
        if (!FilteredItems.Any(i => i.Id == itemId))
        {
            filter = new ProjectItems(item.ProjectKey);
            kindFilter = KindFilter.All;
            parts.Kind.Select(0);
        }
        selection = itemId;
        showsInspector = true;
        var searches = state.Focus.SearchesBlocker;
        Dispatcher.UIThread.Post(() =>
        {
            parts.OrderList.ScrollToSelection();
            if (searches)
            {
                parts.Inspector.FocusSearch();
            }
        }, DispatcherPriority.Loaded);
    }

    private void ZoomBy(double step) => state.SetTimelineZoom(Zoom.Step(state.TimelineZoom, step));

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.F5:
            case Key.R when control:
                _ = state.Refresh();
                break;
            case Key.OemPlus or Key.Add when control && isTimeline:
                ZoomBy(0.1);
                break;
            case Key.OemMinus or Key.Subtract when control && isTimeline:
                ZoomBy(-0.1);
                break;
            case Key.D0 or Key.NumPad0 when control && isTimeline:
                state.SetTimelineZoom(1);
                break;
            case Key.OemComma when control:
                shell.ShowSettings();
                break;
            case Key.Q when control:
                shell.Quit();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
