using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitLabTodo;
using GitLabTodo.Core;
using GitLabTodo.Ui;

// The app on sample data, clicked through with a simulated mouse and keyboard and no display: sidebar, list, notes,
// blocker search, timeline, theme switch and settings. Catches what the logic tests cannot, like a crash in an event.
// Headless drawing has a stand-in font of its own, so the app's font is left out here.
AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
    .SetupWithoutStarting();

var failures = 0;
void Check(bool condition, string message)
{
    Console.WriteLine((condition ? "ok   " : "FAIL ") + message);
    if (!condition)
    {
        failures++;
    }
}

void Pump()
{
    for (var round = 0; round < 5; round++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

// The app's own title bars, as on Linux, also when the tests run elsewhere.
WindowChrome.IsDrawn = true;
var ui = new DesktopUserInterface();
var state = new AppState(ui, PreviewData.Snapshot());
var shell = new Shell(Application.Current!, new ClassicDesktopStyleApplicationLifetime(), state, ui);
var window = new MainWindow(state, shell) { Width = 1280, Height = 760 };
ui.Owner = window;
window.Show();
Pump();

TodoItem Item(string reference) => state.Items.First(i => i.ShortReference == reference);
Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
void Click(Point point, MouseButton button = MouseButton.Left)
{
    window.MouseDown(point, button);
    window.MouseUp(point, button);
    Pump();
}
void Drag(Point from, Point to)
{
    window.MouseDown(from, MouseButton.Left);
    // The button stays down while moving, as the system reports it.
    window.MouseMove(from + new Vector(8, -8), RawInputModifiers.LeftMouseButton);
    window.MouseMove(to, RawInputModifiers.LeftMouseButton);
    Pump();
    window.MouseUp(to, MouseButton.Left);
    Pump();
}
List<Control> Rows() => window.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "Row" && c.IsEffectivelyVisible).ToList();
Segmented Mode() => window.GetVisualDescendants().OfType<Segmented>().First();
IEnumerable<string> Texts(Visual root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

// Every button that is only an icon says what it does in a tooltip.
void CheckIconTips(Visual root, string where)
{
    var icons = root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is Icon or StageCircle).ToList();
    var silent = icons.Count(b => ToolTip.GetTip(b) is null);
    Check(icons.Count > 0 && silent == 0, $"icon buttons in {where} have tooltips: {icons.Count - silent} of {icons.Count}");
}
CheckIconTips(window, "the list");

// Title bar: no system frame, the window's buttons at the end of the top bar.
Check(window.SystemDecorations == SystemDecorations.None, "the main window draws its own frame");
Button WindowButton(Window owner, string tip) => owner.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == tip);
Click(Center(WindowButton(window, "Maximalizovat")));
Check(window.WindowState == WindowState.Maximized && WindowButton(window, "Obnovit velikost").IsEffectivelyVisible, "maximize maximizes, the button then restores");
Click(Center(WindowButton(window, "Obnovit velikost")));
Check(window.WindowState == WindowState.Normal, "restore makes it normal again");

// Sidebar: a click on an entry filters the list. Rebuilding the old list box inside its selection event crashed the app.
foreach (var (name, count) in new[] { ("backend", 4), ("Čeká", 4), ("infra", 2), ("Vše", 11) })
{
    var entry = window.GetVisualDescendants().OfType<Control>()
        .First(c => c.GetType().Name == "Entry" && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == name));
    Click(Center(entry));
    Check(Rows().Count == count, $"sidebar {name} shows {count} items: {Rows().Count}");
}

// List: a row dragged to the top moves there.
var rows = Rows();
Drag(Center(rows[5]), rows[0].TranslatePoint(new Point(rows[0].Bounds.Width / 2, 4), window)!.Value);
Check(state.Items[0].ShortReference == "infra#5", $"dragged to the top: {state.Items[0].ShortReference}");

// Notes: an empty one opens on a click, typing and Escape save it, a checkbox ticks a task off.
Click(Center(Rows().First(r => r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Zvětšit disk na build serveru"))));
var notes = window.GetVisualDescendants().OfType<NotesSection>().First();
Click(Center((Control)notes.Children[3]));
var editor = notes.GetVisualDescendants().OfType<TextBox>().First();
Check(editor.IsEffectivelyVisible && editor.IsFocused, "note editor opens with the cursor in it");
window.KeyTextInput("Meeting s týmem");
window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
window.KeyTextInput("- [ ] poslat zápis");
window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
Pump();
Check(Item("infra#5").Note?.Text == "Meeting s týmem\n- [ ] poslat zápis", $"note saved: {Item("infra#5").Note?.Text}");
Click(Center(notes.GetVisualDescendants().OfType<Icon>().First(i => i.Kind == IconKind.Checkbox)));
Check(Item("infra#5").Note?.OpenTasks.Count == 0, "task ticked off in the note");

// Blocker search: the own items offered before typing, picked with a click.
var search = window.GetVisualDescendants().OfType<WorkItemSearch>().First();
search.FocusField();
Pump();
var suggestion = search.GetVisualDescendants().OfType<Border>().First(b => b.Cursor is not null && b.IsEffectivelyVisible);
Click(Center(suggestion));
Check(Item("infra#5").IsWaiting, $"suggestion picked: infra#5 waits on {Item("infra#5").WaitDescription}");
state.StopWaiting(Item("infra#5"));
Pump();

// Timeline.
Mode().Select(1, notify: true);
Pump();
CheckIconTips(window, "the timeline");
var canvas = window.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "TimelineCanvas");
var metrics = new TimelineMetrics(state.TimelineZoom);
(int Lane, TimelineBlock Block) Find(string reference)
{
    var lanes = state.TimelineSchedule().Lanes;
    for (var lane = 0; lane < lanes.Count; lane++)
    {
        if (lanes[lane].Blocks.FirstOrDefault(b => (b.Item?.ShortReference ?? b.External?.ShortReference) == reference) is { } block)
        {
            return (lane, block);
        }
    }
    throw new InvalidOperationException($"{reference} is not in the timeline");
}
Point OnCanvas(double x, double y) => canvas.TranslatePoint(new Point(x, y), window)!.Value;
Point BlockCenter(string reference)
{
    var (lane, block) = Find(reference);
    var frame = metrics.Block(lane, block.Slot);
    return OnCanvas(frame.Center.X, frame.Center.Y);
}
Point Handle(string reference)
{
    var (lane, block) = Find(reference);
    return OnCanvas(metrics.X(block.Slot) + metrics.BlockWidth, metrics.Y(lane) + metrics.BlockHeight / 2);
}

Drag(Handle("backend#12"), BlockCenter("docs#3"));
Check(Item("docs#3").Wait?.ItemIds.Contains(Item("backend#12").Id) == true, "arrow from a dot makes the target wait");
var (frontendLane, review) = Find("frontend!52");
var emptyStep = state.TimelineSchedule().StepCount;
Drag(BlockCenter("frontend!52"), OnCanvas(metrics.X(emptyStep) + metrics.BlockWidth / 2, metrics.Y(frontendLane) + metrics.BlockHeight / 2));
Check(Find("frontend!52").Block.Slot > review.Slot && Find("frontend!52").Block.Delay > 0, "dropped on an empty step: put off");
Drag(BlockCenter("infra#9"), BlockCenter("frontend#7"));
Check(Item("infra#9").Wait?.ItemIds.Contains(Item("frontend#7").Id) == true, "dropped on another project's block: waits on it");
Click(BlockCenter("backend#33"), MouseButton.Right);
window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
Pump();
window.MouseWheel(BlockCenter("backend#33"), new Vector(0, 1), RawInputModifiers.Control);
Pump();
Check(Math.Abs(state.TimelineZoom - 1.1) < 0.001, $"Ctrl + wheel zooms: {state.TimelineZoom}");

// Scrolling at the edge: only while a block is held.
var timelineScroller = canvas.FindAncestorOfType<ScrollViewer>()!;
// Timers do not run in the headless platform, so its ticks are called the way the timer would.
var autoScroll = (DispatcherTimer)canvas.GetType().GetField("autoScroll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(canvas)!;
var scrollTick = canvas.GetType().GetMethod("ScrollTowardsPointer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
void ScrollTicks(int count)
{
    for (var tick = 0; tick < count; tick++)
    {
        scrollTick.Invoke(canvas, null);
        Pump();
    }
}
Point RightEdge() => timelineScroller.TranslatePoint(new Point(timelineScroller.Bounds.Width - 8, timelineScroller.Bounds.Height / 3), window)!.Value;
void ResetScroll()
{
    timelineScroller.Offset = new Vector(0, 0);
    Pump();
}
ResetScroll();
window.MouseMove(BlockCenter("backend#12"));
window.MouseMove(RightEdge());
ScrollTicks(15);
Check(timelineScroller.Offset.X == 0, $"hovering out to the right edge does not scroll: {timelineScroller.Offset.X}");
var held = BlockCenter("backend#12");
window.MouseDown(held, MouseButton.Left);
window.MouseMove(held + new Vector(12, 0), RawInputModifiers.LeftMouseButton);
window.MouseMove(RightEdge(), RawInputModifiers.LeftMouseButton);
// The release never arrives, the next move has the button up.
window.MouseMove(RightEdge() - new Vector(2, 0));
// Held at the edge until then, it may have scrolled a little; from here on it must not.
var stoppedAt = timelineScroller.Offset.X;
ScrollTicks(15);
Check(timelineScroller.Offset.X == stoppedAt && !autoScroll.IsEnabled, $"a missed release stops it: {stoppedAt} → {timelineScroller.Offset.X}");
// Lets the simulated mouse go, and presses elsewhere so that it is not a double click.
window.MouseUp(RightEdge(), MouseButton.Left);
Pump();
held = BlockCenter("infra#5");
window.MouseDown(held, MouseButton.Left);
window.MouseMove(held + new Vector(12, 0), RawInputModifiers.LeftMouseButton);
window.MouseMove(RightEdge(), RawInputModifiers.LeftMouseButton);
ScrollTicks(15);
var scrolled = timelineScroller.Offset.X;
window.MouseUp(RightEdge(), MouseButton.Left);
ScrollTicks(10);
Check(scrolled > 0 && timelineScroller.Offset.X == scrolled && !autoScroll.IsEnabled, $"a held block scrolls at the edge, the release stops it: {scrolled}");
ResetScroll();

// A theme switch rebuilds the window, settings change the refresh.
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Pump();
Mode().Select(0, notify: true);
Pump();
Check(Rows().Count == 11, "dark theme rebuilt the list");
var settings = new SettingsWindow(state, shell);
settings.Show();
Pump();
ComboBox Picker(string option) => settings.GetVisualDescendants().OfType<ComboBox>()
    .First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Content as string == option));
Picker("5 min").SelectedIndex = 0;
Pump();
Check(state.RefreshMinutes == AppState.RefreshIntervals[0], $"refresh interval picked: {state.RefreshMinutes} min");
Picker("Podle systému").SelectedIndex = 1;
Pump();
Check(state.Theme == "light" && Application.Current.ActualThemeVariant == ThemeVariant.Light && Rows().Count == 11, "light theme picked, the window rebuilt for it");

Check(settings.SystemDecorations == SystemDecorations.None && Texts(settings).Contains("Nastavení"), "the settings have their own title bar");
var switches = settings.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
Check(switches.Count >= 2, $"on/off settings are switches: {switches.Count}");

// Sync: a GitLab and a repository, applied; „Vypnuto“ turns it off again.
CheckIconTips(settings, "the settings");
var syncSection = settings.GetVisualDescendants().OfType<SyncSection>().First();
var gitLabPicker = syncSection.GetVisualDescendants().OfType<ComboBox>().First();
gitLabPicker.SelectedIndex = 1;
Pump();
var repositoryField = syncSection.GetVisualDescendants().OfType<TextBox>().First(t => t.Watermark as string == "skupina/gitlab-todo-data");
Check(repositoryField.IsEffectivelyVisible, "picking a GitLab asks for the repository");
repositoryField.Text = "https://gitlab.example.com/me/gitlab-todo-data.git";
Pump();
var useButton = syncSection.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Použít");
// A click in the settings window, not the main one.
var usePoint = useButton.TranslatePoint(new Point(useButton.Bounds.Width / 2, useButton.Bounds.Height / 2), settings)!.Value;
settings.MouseDown(usePoint, MouseButton.Left);
settings.MouseUp(usePoint, MouseButton.Left);
Pump();
Check(state.SyncState?.Settings.Project == "me/gitlab-todo-data", $"sync set to the repository from a pasted URL: {state.SyncState?.Settings.Project}");
gitLabPicker.SelectedIndex = 0;
Pump();
Check(state.SyncState is null, "„Vypnuto“ turns syncing off");

// Updates: only an AppImage installs them; others just show their version.
UpdatesSection Updates(Window owner) => owner.GetVisualDescendants().OfType<UpdatesSection>().First();
Check(Texts(Updates(settings)).Any(t => t.StartsWith($"Verze {Updater.Version}.") && t.Contains("Nix")), "outside an AppImage the settings say how to update");
Check(!Updates(settings).GetVisualDescendants().OfType<Button>().Any(), "and offer no update button");
void Offer(string? version)
{
    typeof(Updater).GetProperty(nameof(Updater.Available))!.SetValue(shell.Updater, version is null ? null : new AvailableUpdate(version, null, "https://example.invalid/update", []));
    (typeof(Updater).GetField(nameof(Updater.Changed), BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell.Updater) as Action)?.Invoke();
    Pump();
}
Button? SidebarUpdate() => window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible
    && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Aktualizovat na") == true));
Check(SidebarUpdate() is null, "no update button in the sidebar without a newer release");
Offer("9.9.9");
Check(SidebarUpdate() is { } offered && Texts(offered).Contains("Aktualizovat na 9.9.9"), "a newer release shows „Aktualizovat na 9.9.9“ in the sidebar");
CheckIconTips(window, "the main window with an update");
Click(Center(SidebarUpdate()!));
Check(typeof(Shell).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell) is SettingsWindow, "the sidebar button opens the settings");
var image = Path.Combine(Path.GetTempPath(), $"gitlab-todo-uitest-{Environment.ProcessId}.AppImage");
File.WriteAllText(image, "");
Environment.SetEnvironmentVariable("APPIMAGE", image);
var appImageSettings = new SettingsWindow(state, shell);
appImageSettings.Show();
Pump();
Button UpdateAction() => Updates(appImageSettings).GetVisualDescendants().OfType<Button>().Single(b => b is not ToggleSwitch);
Check(UpdateAction().Content as string == "Nainstalovat a restartovat" && Texts(Updates(appImageSettings)).Contains("GitLab Todo 9.9.9 je k dispozici"), "an AppImage offers to install the newer release");
// Some desktops make a window narrower than it asks to be: nothing may stick out of it then.
var narrowPicker = appImageSettings.GetVisualDescendants().OfType<SyncSection>().First().GetVisualDescendants().OfType<ComboBox>().First();
narrowPicker.SelectedIndex = 1;
foreach (var width in new[] { 540, 420, 360 })
{
    appImageSettings.Width = width;
    Pump();
    // Text scrolled inside a field is clipped by it.
    var outside = appImageSettings.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.FindAncestorOfType<TextBox>() is null)
        .FirstOrDefault(c => c.TranslatePoint(new Point(c.Bounds.Width, 0), appImageSettings)!.Value.X > width + 0.5);
    Check(outside is null, $"the settings fit {width} px: {outside?.GetType().Name} {(outside as TextBlock)?.Text ?? (outside as ContentControl)?.Content as string}");
}
narrowPicker.SelectedIndex = 0;
appImageSettings.Width = 540;
Pump();
Offer(null);
Check(UpdateAction().Content as string == "Zkontrolovat teď" && SidebarUpdate() is null, "without one it offers a check, and the sidebar button goes away");
var closeButton = WindowButton(appImageSettings, "Zavřít");
var closePoint = closeButton.TranslatePoint(new Point(closeButton.Bounds.Width / 2, closeButton.Bounds.Height / 2), appImageSettings)!.Value;
var settingsClosed = false;
appImageSettings.Closed += (_, _) => settingsClosed = true;
appImageSettings.MouseDown(closePoint, MouseButton.Left);
appImageSettings.MouseUp(closePoint, MouseButton.Left);
Pump();
Check(settingsClosed, "the close button in the title bar closes the settings");
Environment.SetEnvironmentVariable("APPIMAGE", null);
File.Delete(image);

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
