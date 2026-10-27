using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// The user's notes on the item, e.g. from a meeting. Shown with the tasks as checkboxes, a click on the text edits it.
/// Saved a moment after typing stops and when leaving the editor.
public sealed partial class NotesSection : StackPanel
{
    private readonly AppState state;
    private readonly ContentControl badge = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel lines = new() { Spacing = 4 };
    private readonly Border view;
    private readonly TextBlock updated = new() { FontSize = 11.5 };
    private readonly Border placeholder;
    private readonly StackPanel editor = new() { Spacing = 6 };
    private readonly TextBox text;
    private readonly DispatcherTimer saveTimer;
    private TodoItem? item;
    private bool isEditing;

    public NotesSection(AppState state)
    {
        this.state = state;
        Spacing = 8;
        saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            saveTimer!.Stop();
            Save();
        });

        var add = new Button
        {
            Content = Views.Row(3, new Icon(IconKind.Plus, Paint.AccentBrush(), 10), Views.Label("Úkol", 12, Paint.AccentBrush(), FontWeight.Medium)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
        };
        ToolTip.SetTip(add, "Přidat úkol „- [ ] …“ na konec poznámky");
        add.Click += (_, _) => AddTask();
        var header = new DockPanel();
        var end = Views.Row(6, badge, add);
        DockPanel.SetDock(end, Dock.Right);
        header.Children.AddRange([end, Views.SectionTitle("Poznámky", IconKind.Note)]);

        view = Views.Box(lines);
        view.Cursor = new Cursor(StandardCursorType.Ibeam);
        ToolTip.SetTip(view, "Kliknutím upravíš");
        view.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
            {
                StartEditing();
                e.Handled = true;
            }
        };
        updated.Foreground = Paint.Tertiary;

        placeholder = Views.Box(Views.IconLabel(IconKind.Note, "Zápis z meetingu, úkoly, cokoli k tomuhle", Paint.Secondary, 12.5));
        placeholder.Cursor = new Cursor(StandardCursorType.Hand);
        placeholder.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(placeholder).Properties.IsLeftButtonPressed)
            {
                StartEditing();
                e.Handled = true;
            }
        };

        text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            FontSize = 13,
        };
        text.TextChanged += (_, _) =>
        {
            if (isEditing)
            {
                saveTimer.Stop();
                saveTimer.Start();
            }
        };
        text.LostFocus += (_, _) => Finish();
        text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            {
                Finish();
                e.Handled = true;
            }
        };
        var done = new Button { Content = "Hotovo", FontSize = 12, Padding = new Thickness(10, 2) };
        done.Click += (_, _) => Finish();
        ToolTip.SetTip(done, "Ctrl Enter");
        var hint = new TextBlock
        {
            Text = "Úkol napíšeš jako „- [ ] text“, ukládá se průběžně.",
            FontSize = 11.5,
            Foreground = Paint.Tertiary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var footer = new DockPanel();
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.AddRange([done, hint]);
        editor.Children.AddRange([text, footer]);

        Children.AddRange([header, view, updated, placeholder, editor]);
    }

    public void Show(TodoItem? shown)
    {
        if (item is { } previous && previous.Id != shown?.Id && isEditing)
        {
            saveTimer.Stop();
            isEditing = false;
            state.SetNote(text.Text ?? "", previous.Id);
        }
        item = shown;
        Render();
        // A click on the item in the tray menu lands here, ready to write.
        if (shown is not null && state.TakeNoteToEdit(shown.Id))
        {
            StartEditing();
        }
    }

    /// The note as stored now; `item` can be a moment behind right after a save.
    private string StoredText => item is null ? "" : state.ItemWithId(item.Id)?.Note?.Text ?? "";

    private void Render()
    {
        var note = item?.Note;
        badge.Content = note is { Tasks.Count: > 0 } ? Views.NoteBadge(note) : null;
        editor.IsVisible = isEditing;
        view.IsVisible = !isEditing && note is not null;
        updated.IsVisible = view.IsVisible;
        placeholder.IsVisible = !isEditing && note is null;
        if (isEditing || note is null)
        {
            return;
        }
        updated.Text = $"Upraveno {Views.Relative(note.UpdatedAt)}";
        lines.Children.Clear();
        var tasks = note.Tasks.ToDictionary(t => t.Line);
        for (var index = 0; index < note.Lines.Length; index++)
        {
            var line = note.Lines[index];
            if (tasks.TryGetValue(index, out var task))
            {
                lines.Children.Add(TaskRow(task));
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                lines.Children.Add(new Border { Height = 2 });
            }
            else
            {
                lines.Children.Add(new TextBlock { Text = Bulleted(line), FontSize = 13, TextWrapping = TextWrapping.Wrap });
            }
        }
    }

    private Control TaskRow(ItemNote.NoteTask task)
    {
        var label = new TextBlock
        {
            Text = task.Text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = task.IsDone ? Paint.Secondary : Paint.Primary(),
            TextDecorations = task.IsDone ? TextDecorations.Strikethrough : null,
        };
        // A compact box of its own: Fluent's check box is a whole line tall.
        var box = new Border
        {
            Child = new Icon(task.IsDone ? IconKind.CheckboxChecked : IconKind.Checkbox, task.IsDone ? Paint.Brush(Palette.Ready) : Paint.Secondary, 16),
            Padding = new Thickness(0, 1, 8, 0),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(box, task.IsDone ? "Vrátit jako nehotové" : "Hotovo");
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed || item is not { } current)
            {
                return;
            }
            // Not a click into the note to edit it.
            e.Handled = true;
            state.ToggleTask(task.Line, current.Id);
        };
        var row = new DockPanel();
        DockPanel.SetDock(box, Dock.Left);
        row.Children.AddRange([box, label]);
        return row;
    }

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    /// `- point` shown as `• point`.
    private static string Bulleted(string line) =>
        BulletLine().Match(line) is { Success: true } match ? $"{match.Groups[1].Value}•  {match.Groups[2].Value}" : line;

    private void StartEditing()
    {
        if (item is null)
        {
            return;
        }
        if (!isEditing)
        {
            text.Text = StoredText;
            isEditing = true;
        }
        Render();
        text.CaretIndex = text.Text?.Length ?? 0;
        // The editor is laid out only after this update.
        Dispatcher.UIThread.Post(() => text.Focus(), DispatcherPriority.Input);
    }

    private void AddTask()
    {
        if (!isEditing)
        {
            text.Text = StoredText;
            isEditing = true;
        }
        var current = text.Text ?? "";
        if (current.Length > 0 && !current.EndsWith('\n'))
        {
            current += "\n";
        }
        text.Text = current + "- [ ] ";
        StartEditing();
    }

    private void Save()
    {
        if (isEditing && item is { } current)
        {
            state.SetNote(text.Text ?? "", current.Id);
        }
    }

    private void Finish()
    {
        if (!isEditing || item is not { } current)
        {
            return;
        }
        saveTimer.Stop();
        isEditing = false;
        state.SetNote(text.Text ?? "", current.Id);
        Render();
    }
}
