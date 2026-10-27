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
    window.MouseMove(from + new Vector(8, -8));
    window.MouseMove(to);
    Pump();
    window.MouseUp(to, MouseButton.Left);
    Pump();
}
List<Control> Rows() => window.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "Row" && c.IsEffectivelyVisible).ToList();
Segmented Mode() => window.GetVisualDescendants().OfType<Segmented>().First();

// Sidebar: a click on an entry filters the list. Rebuilding it inside its own selection event crashed the app.
foreach (var (name, count) in new[] { ("backend", 4), ("Čeká", 4), ("infra", 2), ("Vše", 11) })
{
    var entry = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == name));
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
Click(Center(notes.GetVisualDescendants().OfType<CheckBox>().First()));
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

// A theme switch rebuilds the window, settings change the refresh.
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Pump();
Mode().Select(0, notify: true);
Pump();
Check(Rows().Count == 11, "dark theme rebuilt the list");
var settings = new SettingsWindow(state, shell);
settings.Show();
Pump();
settings.GetVisualDescendants().OfType<ComboBox>().First().SelectedIndex = 0;
Pump();
Check(state.RefreshMinutes == AppState.RefreshIntervals[0], $"refresh interval picked: {state.RefreshMinutes} min");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
