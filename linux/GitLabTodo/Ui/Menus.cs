using Avalonia.Controls;
using GitLabTodo.Core;

namespace GitLabTodo.Ui;

/// Context menus shared by the list and the timeline.
public static class Menus
{
    public static MenuItem Item(string header, Action action, bool isEnabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = isEnabled };
        item.Click += (_, _) => action();
        return item;
    }

    public static MenuItem Submenu(string header, IEnumerable<Control> items)
    {
        var menu = new MenuItem { Header = header };
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }
        return menu;
    }

    public static List<Control> ForItem(TodoItem item, AppState state)
    {
        var ui = state.Ui;
        var items = new List<Control>
        {
            Item("Na začátek pořadí", () => state.MoveToTop(item)),
            Item(item.IsPlaced ? "Na konec pořadí" : "Zařadit na konec", () => state.MoveToEnd(item)),
            new Separator(),
        };
        var candidates = state.BlockerCandidates(item).Select(candidate =>
        {
            var toggle = new MenuItem
            {
                Header = $"{candidate.ShortReference}  {candidate.Title}",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = item.Wait?.ItemIds.Contains(candidate.Id) ?? false,
            };
            toggle.Click += (_, _) => state.ToggleBlocker(candidate, item);
            return (Control)toggle;
        });
        items.Add(Submenu("Čeká na", new Control[] { Item("Hledat issue nebo MR…", () => state.RequestBlockerSearch(item.Id)), new Separator() }.Concat(candidates)));
        var blockers = (item.Wait?.ItemIds ?? [])
            .Select(id => state.Items.FirstOrDefault(i => i.Id == id))
            .OfType<TodoItem>()
            .Select(blocker => (blocker.Id, blocker.ShortReference))
            .Concat((item.Wait?.External ?? []).Select(blocker => (blocker.Id, blocker.ShortReference)))
            .ToList();
        if (LinkRemoval(item.Id, item.References, blockers, state) is { } removal)
        {
            items.Add(removal);
        }
        if (item.IsWaiting)
        {
            items.Add(Item("Už nečeká", () => state.StopWaiting(item)));
        }
        items.Add(new Separator());
        foreach (var step in item.Steps)
        {
            items.Add(Item(step.Action.Title(), () => _ = state.Perform(step.Action, step.Target.Id)));
        }
        if (item.CheckoutCommand is { } command)
        {
            items.Add(Item("Kopírovat git checkout", () => _ = ui.Copy(command)));
        }
        items.Add(Item("Kopírovat odkaz", () => _ = ui.Copy(item.WebUrl)));
        items.Add(Item(item.Kind == ItemKind.Issue ? "Otevřít issue" : "Otevřít MR", () => ui.Open(item.WebUrl)));
        foreach (var mergeRequest in item.MergeRequests)
        {
            items.Add(Item($"Otevřít MR {mergeRequest.LocalReference}", () => ui.Open(mergeRequest.WebUrl)));
        }
        return items;
    }

    /// „Odebrat vazbu“ with every link of an item: what it waits on and what waits on it.
    public static MenuItem? LinkRemoval(string id, string reference, IReadOnlyList<(string Id, string Reference)> blockers, AppState state)
    {
        var dependents = state.Dependents(id);
        if (blockers.Count == 0 && dependents.Count == 0)
        {
            return null;
        }
        return Submenu("Odebrat vazbu",
            blockers.Select(blocker => Item($"{reference} čeká na {blocker.Reference}", () => state.RemoveLink(id, blocker.Id)))
                .Concat(dependents.Select(dependent => Item($"{dependent.ShortReference} čeká na {reference}", () => state.RemoveLink(dependent.Id, id)))));
    }

    public static void Open(Control target, IEnumerable<Control> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }
        menu.Open(target);
    }
}
