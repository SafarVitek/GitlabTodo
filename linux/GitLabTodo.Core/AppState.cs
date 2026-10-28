using System.Text.Json;

namespace GitLabTodo.Core;

/// What the app needs from the screen: confirmations, messages, the clipboard and the browser.
public interface IUserInterface
{
    Task<bool> Confirm(string title, string message, string action);

    /// Returns the index of the clicked button.
    Task<int> Show(string title, string message, IReadOnlyList<string>? buttons = null, bool isWarning = false);

    Task Copy(string text);

    void Open(string url);
}

/// The items of all connected GitLabs in the user's work order, what they wait on, and the actions on them.
/// Lives on the UI thread; `Changed` tells the views to redraw.
public sealed class AppState
{
    public static readonly double[] RefreshIntervals = [1, 2, 5, 10, 15, 30];

    public sealed record FocusRequest(string? ItemId, bool SearchesBlocker, int Serial);

    private readonly bool isPreview;
    private readonly Settings settings;
    private readonly Dictionary<Guid, string> tokens;
    private readonly Dictionary<Guid, long> userIds = [];
    private Plan plan;
    private Dictionary<Guid, List<TodoItem>> fetchedItems = [];
    private bool refreshPending;
    private CancellationTokenSource? timer;
    private SyncState? syncState;
    private CancellationTokenSource? syncDelay;
    private bool isSyncing;
    private bool syncPending;

    public AppState(IUserInterface ui)
    {
        Ui = ui;
        settings = Storage.Load<Settings>(Storage.SettingsPath) ?? new Settings();
        var stored = Storage.Load<Dictionary<string, string>>(Storage.TokensPath) ?? [];
        tokens = stored.Where(t => Guid.TryParse(t.Key, out _)).ToDictionary(t => Guid.Parse(t.Key), t => t.Value);
        plan = Storage.Load<Plan>(Storage.PlanPath) ?? new Plan();
        syncState = Storage.Load<SyncState>(Storage.SyncPath);

        // Start from the last known state, so the list is not empty until the first refresh finishes.
        if (Storage.Load<Snapshot>(Storage.SnapshotPath) is { } snapshot)
        {
            fetchedItems = snapshot.Items.GroupBy(i => i.AccountId).ToDictionary(g => g.Key, g => g.ToList());
            LastRefresh = snapshot.RefreshedAt;
        }
        RebuildItems();
    }

    /// In-memory state over sample data for screenshots and tests; touches neither files nor network.
    public AppState(IUserInterface ui, Snapshot snapshot)
    {
        Ui = ui;
        isPreview = true;
        settings = new Settings
        {
            Accounts = snapshot.Items.Select(i => i.AccountId).Distinct()
                .Select(id => new GitLabAccount { Id = id, Name = "GitLab", BaseUrl = "https://gitlab.example.com" }).ToList(),
        };
        tokens = [];
        plan = new Plan
        {
            Order = snapshot.Items.Where(i => i.IsPlaced).Select(i => i.Id).ToList(),
            Waits = snapshot.Items.Where(i => i.Wait is not null).DistinctBy(i => i.Id).ToDictionary(i => i.Id, i => i.Wait!),
            Released = snapshot.Items.Where(i => i.ReleasedBy is not null).DistinctBy(i => i.Id)
                .ToDictionary(i => i.Id, i => new Release(DateTimeOffset.Now, [i.ReleasedBy!])),
            Notes = snapshot.Items.Where(i => i.Note is not null).DistinctBy(i => i.Id).ToDictionary(i => i.Id, i => i.Note!),
        };
        fetchedItems = snapshot.Items.GroupBy(i => i.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        LastRefresh = snapshot.RefreshedAt;
        RebuildItems();
    }

    public event Action? Changed;

    public IUserInterface Ui { get; set; }
    public IReadOnlyList<GitLabAccount> Accounts => settings.Accounts;
    /// All items in work order: placed ones first, then the new ones.
    public List<TodoItem> Items { get; private set; } = [];
    public Dictionary<Guid, string> AccountErrors { get; private set; } = [];
    public DateTimeOffset? LastRefresh { get; private set; }
    public bool IsRefreshing { get; private set; }
    public HashSet<string> RunningActions { get; } = [];
    /// Asks the main window to show an item, e.g. after a click in the tray menu.
    public FocusRequest Focus { get; private set; } = new(null, false, 0);
    /// Other people's issues and merge requests in the timeline: added with „+“ or waited on.
    public List<ExternalBlocker> TimelineExternal { get; private set; } = [];
    /// Place of each item, own or not, in the work order.
    public Dictionary<string, int> OrderPositions { get; private set; } = [];
    public Dictionary<string, int> TimelineDelays => plan.Delays;
    /// An item whose notes open for writing once it is shown.
    public string? NoteToEdit { get; private set; }
    /// Syncing the plan through a GitLab repository, null while it is off.
    public SyncState? SyncState => syncState;
    public SyncStatus SyncStatus { get; private set; }
    public string? SyncError { get; private set; }
    public double RefreshMinutes => settings.RefreshMinutes > 0 ? settings.RefreshMinutes : 5;
    public bool KeepInTray => settings.KeepInTray;
    public double TimelineZoom => settings.TimelineZoom;
    public string Theme => settings.Theme;
    public bool CheckUpdates => settings.CheckUpdates;

    public string? Token(Guid accountId) => tokens.GetValueOrDefault(accountId);

    public void RequestFocus(string? itemId, bool editsNote = false)
    {
        // A merge request shown as part of its issue is selected through the issue.
        var topLevel = itemId is null ? null : Items.FirstOrDefault(i => i.Id == itemId || i.MergeRequests.Any(m => m.Id == itemId))?.Id;
        Focus = new FocusRequest(topLevel ?? itemId, false, Focus.Serial + 1);
        NoteToEdit = editsNote ? topLevel ?? itemId : null;
        Changed?.Invoke();
    }

    /// True once for the item whose notes were asked to open.
    public bool TakeNoteToEdit(string itemId)
    {
        if (NoteToEdit != itemId)
        {
            return false;
        }
        NoteToEdit = null;
        return true;
    }

    public void RequestBlockerSearch(string itemId)
    {
        Focus = new FocusRequest(itemId, true, Focus.Serial + 1);
        Changed?.Invoke();
    }

    /// Finds top-level items as well as merge requests grouped under their issue.
    public TodoItem? ItemWithId(string id)
    {
        foreach (var item in Items)
        {
            if (item.Id == id)
            {
                return item;
            }
            if (item.MergeRequests.FirstOrDefault(m => m.Id == id) is { } mergeRequest)
            {
                return mergeRequest;
            }
        }
        return null;
    }

    // MARK: Refreshing

    public async Task Refresh()
    {
        if (isPreview)
        {
            return;
        }
        if (IsRefreshing)
        {
            refreshPending = true;
            return;
        }
        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            do
            {
                refreshPending = false;
                await FetchAll();
            }
            while (refreshPending);
            await SyncPlan();
        }
        finally
        {
            IsRefreshing = false;
            Changed?.Invoke();
        }
    }

    private async Task FetchAll()
    {
        var jobs = Accounts.Select(async account =>
        {
            try
            {
                return (account.Id, Items: await Client(account).TodoItems(account), Error: (string?)null);
            }
            catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException)
            {
                return (account.Id, Items: (List<TodoItem>?)null, Error: Describe(exception));
            }
        }).ToList();

        foreach (var (accountId, fetched, error) in await Task.WhenAll(jobs))
        {
            if (fetched is not null)
            {
                var previous = fetchedItems.GetValueOrDefault(accountId) ?? [];
                fetchedItems[accountId] = fetched;
                AccountErrors.Remove(accountId);
                // Merge requests grouped under their issue are still there, they just moved.
                PrunePlan(accountId, fetched.SelectMany(i => i.MergeRequests.Select(m => m.Id).Prepend(i.Id)).ToHashSet(), previous);
            }
            else
            {
                // Keep the last known items, a VPN hiccup should not empty the list.
                AccountErrors[accountId] = error!;
            }
        }
        await RefreshExternalBlockers();
        LastRefresh = DateTimeOffset.Now;
        RebuildItems();
    }

    private static string Describe(Exception exception) => exception switch
    {
        TaskCanceledException => "GitLab neodpověděl včas",
        HttpRequestException http => $"GitLab není dostupný: {http.Message}",
        _ => exception.Message,
    };

    /// Checks the issues and merge requests of other people that items wait on. Closed and merged ones end the wait,
    /// the ones that cannot be checked right now stay.
    private async Task RefreshExternalBlockers()
    {
        var blockers = plan.Waits.Values.SelectMany(w => w.External).Concat(plan.Tracked).DistinctBy(b => b.Id).ToList();
        var jobs = blockers
            .Select(blocker => (Blocker: blocker, Account: Accounts.FirstOrDefault(a => a.Id == blocker.AccountId)))
            .Where(job => job.Account is not null && TryClient(job.Account) is not null)
            .Select(async job =>
            {
                try
                {
                    var workItem = await TryClient(job.Account!)!.WorkItem(job.Blocker.ProjectId.ToString(), job.Blocker.Kind, job.Blocker.Iid);
                    return (job.Blocker.Id, WorkItem: (GLWorkItem?)workItem);
                }
                catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    return (job.Blocker.Id, WorkItem: (GLWorkItem?)null);
                }
            })
            .ToList();
        if (jobs.Count == 0)
        {
            return;
        }
        var current = (await Task.WhenAll(jobs)).Where(r => r.WorkItem is not null).ToDictionary(r => r.Id, r => r.WorkItem!);
        ApplyExternalStates(
            current.Where(c => c.Value.IsOpen).ToDictionary(c => c.Key, c => c.Value.Title),
            current.Where(c => !c.Value.IsOpen).Select(c => c.Key).ToHashSet());
    }

    /// Drops finished external blockers and keeps titles of open ones current.
    public void ApplyExternalStates(Dictionary<string, string> openTitles, HashSet<string> finished)
    {
        var before = PlanJson();
        ExternalBlocker Updated(ExternalBlocker blocker) =>
            openTitles.TryGetValue(blocker.Id, out var title) ? blocker with { Title = title } : blocker;
        plan.Tracked = plan.Tracked.Where(b => !finished.Contains(b.Id)).Select(Updated).ToList();
        plan.Order.RemoveAll(finished.Contains);
        plan.Delays = plan.Delays.Where(d => !finished.Contains(d.Key)).ToDictionary();
        foreach (var (itemId, wait) in plan.Waits.ToList())
        {
            var done = wait.External.Where(b => finished.Contains(b.Id)).ToList();
            var updated = wait with { External = wait.External.Where(b => !finished.Contains(b.Id)).Select(Updated).ToList() };
            if (updated.IsEmpty && done.Count > 0)
            {
                Release(itemId, done.Select(b => b.ShortReference).ToList());
            }
            SetWait(itemId, updated);
        }
        if (PlanJson() != before)
        {
            PlanChanged();
        }
    }

    /// Everything `itemId` waited on got done: it shows as ready to start until the user looks at it.
    private void Release(string itemId, List<string> finished) => plan.Released[itemId] = new Release(DateTimeOffset.Now, finished);

    /// The user has seen the item, it no longer needs to stand out as ready.
    public void AcknowledgeRelease(string itemId)
    {
        if (plan.Released.Remove(itemId))
        {
            PlanChanged();
        }
    }

    private void RebuildItems()
    {
        // A release nobody looked at for a week is old news.
        var weekAgo = DateTimeOffset.Now.AddDays(-7);
        if (plan.Released.Values.Any(r => r.Date <= weekAgo))
        {
            plan.Released = plan.Released.Where(r => r.Value.Date > weekAgo).ToDictionary();
            SavePlan();
        }
        var accountIds = Accounts.Select(a => a.Id).ToHashSet();
        fetchedItems = fetchedItems.Where(f => accountIds.Contains(f.Key)).ToDictionary();
        AccountErrors = AccountErrors.Where(e => accountIds.Contains(e.Key)).ToDictionary();

        var byId = new Dictionary<string, TodoItem>();
        foreach (var item in fetchedItems.Values.SelectMany(i => i))
        {
            byId.TryAdd(item.Id, item);
        }
        var issueOfMergeRequest = new Dictionary<string, string>();
        foreach (var issue in byId.Values)
        {
            foreach (var mergeRequest in issue.MergeRequests)
            {
                issueOfMergeRequest.TryAdd(mergeRequest.Id, issue.Id);
            }
        }
        FoldGroupedMergeRequests(issueOfMergeRequest);
        var position = new Dictionary<string, int>();
        for (var index = 0; index < plan.Order.Count; index++)
        {
            position.TryAdd(plan.Order[index], index);
        }
        Items = byId.Values
            .Select(item =>
            {
                var wait = plan.Waits.GetValueOrDefault(item.Id);
                return item with
                {
                    IsPlaced = position.ContainsKey(item.Id),
                    Wait = wait,
                    WaitDescription = wait is null ? null : Describe(wait, item, byId),
                    ReleasedBy = plan.Released.TryGetValue(item.Id, out var release) ? string.Join(", ", release.Finished) : null,
                    Note = plan.Notes.GetValueOrDefault(item.Id),
                };
            })
            .OrderBy(item => position.TryGetValue(item.Id, out var place) ? place : int.MaxValue)
            .ThenByDescending(item => item.ActivityAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .WithProjectColors();
        var external = plan.Tracked.ToList();
        foreach (var blocker in plan.Waits.Values.SelectMany(w => w.External))
        {
            if (!external.Any(e => e.Id == blocker.Id))
            {
                external.Add(blocker);
            }
        }
        TimelineExternal = external;
        OrderPositions = position;
        PublishSnapshot();
        Changed?.Invoke();
    }

    /// Once a merge request is grouped under its issue, the issue takes the earlier of their two places,
    /// and waiting on the merge request means waiting on the issue.
    private void FoldGroupedMergeRequests(Dictionary<string, string> issueOf)
    {
        if (issueOf.Count == 0)
        {
            return;
        }
        var before = PlanJson();
        plan.Order = plan.Order.Select(id => issueOf.GetValueOrDefault(id) ?? id).Distinct().ToList();
        var waits = new Dictionary<string, Wait>();
        foreach (var (itemId, wait) in plan.Waits)
        {
            var owner = issueOf.GetValueOrDefault(itemId) ?? itemId;
            var merged = waits.GetValueOrDefault(owner) ?? new Wait();
            var itemIds = merged.ItemIds.ToList();
            foreach (var blocker in wait.ItemIds.Select(id => issueOf.GetValueOrDefault(id) ?? id))
            {
                if (blocker != owner && !itemIds.Contains(blocker))
                {
                    itemIds.Add(blocker);
                }
            }
            var external = merged.External.ToList();
            external.AddRange(wait.External.Where(b => !external.Any(e => e.Id == b.Id)));
            merged = new Wait { ItemIds = itemIds, External = external, Note = merged.Note ?? wait.Note };
            if (merged.IsEmpty)
            {
                waits.Remove(owner);
            }
            else
            {
                waits[owner] = merged;
            }
        }
        plan.Waits = waits;
        // Notes written on a merge request before it joined its issue go below the issue's own.
        foreach (var (itemId, note) in plan.Notes.ToList())
        {
            if (issueOf.GetValueOrDefault(itemId) is not { } owner)
            {
                continue;
            }
            plan.Notes.Remove(itemId);
            plan.Notes[owner] = plan.Notes.TryGetValue(owner, out var own)
                ? new ItemNote(own.Text + "\n\n" + note.Text, own.UpdatedAt > note.UpdatedAt ? own.UpdatedAt : note.UpdatedAt)
                : note;
        }
        if (PlanJson() != before)
        {
            SavePlan();
        }
    }

    private static string Describe(Wait wait, TodoItem item, Dictionary<string, TodoItem> items)
    {
        var parts = wait.ItemIds
            .Where(items.ContainsKey)
            .Select(id => items[id])
            .Select(blocker => blocker.ProjectKey == item.ProjectKey ? blocker.LocalReference : blocker.ShortReference)
            .Concat(wait.External.Select(b => b.ShortReference))
            .ToList();
        if (!string.IsNullOrEmpty(wait.Note))
        {
            parts.Add(wait.Note);
        }
        return string.Join(", ", parts);
    }

    private void PublishSnapshot()
    {
        if (isPreview)
        {
            return;
        }
        TrySave(Storage.SnapshotPath, new Snapshot { Items = Items, RefreshedAt = LastRefresh });
    }

    /// Refreshes every few minutes. Waits are checked against the wall clock, which keeps going during suspend, so the
    /// list is refreshed soon after the computer wakes up rather than when the interrupted wait would have ended.
    public void StartTimer()
    {
        if (isPreview)
        {
            return;
        }
        timer?.Cancel();
        var cancellation = new CancellationTokenSource();
        timer = cancellation;
        _ = RunTimer(cancellation.Token);
    }

    private async Task RunTimer(CancellationToken cancellation)
    {
        var due = DateTimeOffset.Now;
        var lastTick = DateTimeOffset.Now;
        while (!cancellation.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            if (now - lastTick > TimeSpan.FromMinutes(1))
            {
                // Just woke up: give the network and VPN a moment to come back first.
                due = now.AddSeconds(15);
            }
            lastTick = now;
            if (now >= due)
            {
                await Refresh();
                due = DateTimeOffset.Now.AddMinutes(RefreshMinutes);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    // MARK: Work order

    /// Applies a drag in a possibly filtered list; items hidden by the filter keep their places.
    /// `destination` is an index in `visibleIds` before the move, like a drop between two rows.
    public void Move(IReadOnlyList<string> visibleIds, IReadOnlyCollection<int> source, int destination)
    {
        var visible = visibleIds.ToList();
        MoveElements(visible, source, destination);
        var moved = source.Select(i => visibleIds[i]).ToHashSet();
        var order = plan.Order.Where(id => !moved.Contains(id)).ToList();
        for (var index = 0; index < visible.Count; index++)
        {
            var id = visible[index];
            if (!moved.Contains(id))
            {
                continue;
            }
            // Anchor on the nearest placed neighbour in the visible list.
            var next = visible.Skip(index + 1).FirstOrDefault(order.Contains);
            var previous = visible.Take(index).LastOrDefault(order.Contains);
            if (next is not null)
            {
                order.Insert(order.IndexOf(next), id);
            }
            else if (previous is not null)
            {
                order.Insert(order.IndexOf(previous) + 1, id);
            }
            else
            {
                order.Add(id);
            }
        }
        plan.Order = order;
        PlanChanged();
    }

    /// Like Swift's `move(fromOffsets:toOffset:)`: `destination` counts in the list before the move.
    public static void MoveElements<T>(List<T> list, IReadOnlyCollection<int> source, int destination)
    {
        var indices = source.Distinct().Order().ToList();
        var moving = indices.Select(i => list[i]).ToList();
        foreach (var index in indices.AsEnumerable().Reverse())
        {
            list.RemoveAt(index);
        }
        list.InsertRange(destination - indices.Count(i => i < destination), moving);
    }

    public void MoveToTop(TodoItem item)
    {
        plan.Order.Remove(item.Id);
        plan.Order.Insert(0, item.Id);
        PlanChanged();
    }

    public void MoveToEnd(TodoItem item)
    {
        plan.Order.Remove(item.Id);
        plan.Order.Add(item.Id);
        PlanChanged();
    }

    public void ToggleBlocker(TodoItem blocker, TodoItem item)
    {
        var wait = plan.Waits.GetValueOrDefault(item.Id) ?? new Wait();
        var itemIds = wait.ItemIds.Contains(blocker.Id) ? wait.ItemIds.Where(id => id != blocker.Id).ToList() : [.. wait.ItemIds, blocker.Id];
        UpdateWait(wait with { ItemIds = itemIds }, item.Id);
    }

    public void SetWaitNote(string note, string itemId)
    {
        var current = plan.Waits.GetValueOrDefault(itemId) ?? new Wait();
        var trimmed = note.Trim();
        var wait = current with { Note = trimmed.Length == 0 ? null : trimmed };
        if (!wait.SameAs(current))
        {
            UpdateWait(wait, itemId);
        }
    }

    public void StopWaiting(TodoItem item) => UpdateWait(new Wait(), item.Id);

    // MARK: Notes

    /// Saves the note as written; an empty one is removed.
    public void SetNote(string text, string itemId)
    {
        if (text == (plan.Notes.GetValueOrDefault(itemId)?.Text ?? ""))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            plan.Notes.Remove(itemId);
        }
        else
        {
            plan.Notes[itemId] = new ItemNote(text, DateTimeOffset.Now);
        }
        PlanChanged();
    }

    public void ToggleTask(int line, string itemId)
    {
        if (plan.Notes.GetValueOrDefault(itemId) is { } note)
        {
            plan.Notes[itemId] = note.Toggling(line);
            PlanChanged();
        }
    }

    /// Finds open issues and merge requests `item` could wait on: by words in the title, by number, or by a pasted
    /// URL or `group/project#12`. Leaves out the item itself and what it already waits on.
    public async Task<List<BlockerCandidate>> SearchBlockers(string text, TodoItem item)
    {
        List<(GitLabAccount Account, ItemKind Kind, GLWorkItem WorkItem)> found;
        if (WorkItemReference.Parse(text) is { } reference && (reference.Host is not null || reference.Path is not null))
        {
            var account = Accounts.FirstOrDefault(a => a.Id == item.AccountId);
            var project = reference.Path ?? item.ProjectId.ToString();
            if (reference.Host is { } host)
            {
                account = Accounts.FirstOrDefault(a => a.ApiUrl?.Host == host);
                // A GitLab served under a sub-path has it in front of the project path.
                var basePath = account is null ? "" : new Uri(GitLabAccount.NormalizedBaseUrl(account.BaseUrl)).AbsolutePath.Trim('/');
                if (basePath.Length > 0 && project.StartsWith(basePath + "/", StringComparison.Ordinal))
                {
                    project = project[(basePath.Length + 1)..];
                }
            }
            if (account is null)
            {
                throw GitLabException.UnknownGitLab(reference.Host ?? "");
            }
            var workItem = await Client(account).WorkItem(project, reference.Kind, reference.Iid);
            if (!workItem.IsOpen)
            {
                throw GitLabException.Closed;
            }
            found = [(account, reference.Kind, workItem)];
        }
        else
        {
            found = await SearchEverywhere(text, item);
        }

        var own = item.MergeRequests.Select(m => m.Id).Prepend(item.Id).ToHashSet();
        var seen = new HashSet<string>();
        var candidates = new List<BlockerCandidate>();
        foreach (var (account, kind, workItem) in found)
        {
            var listedId = TodoItem.MakeId(account.Id, kind, workItem.Id);
            if (!workItem.IsOpen || own.Contains(listedId) || !seen.Add(listedId))
            {
                continue;
            }
            var listed = Items.FirstOrDefault(i => i.Id == listedId || i.MergeRequests.Any(m => m.Id == listedId));
            var candidate = new BlockerCandidate(account.Id, kind, workItem, listed?.Id)
            {
                GitLabName = account.Id != item.AccountId ? account.Name : null,
            };
            var waitsAlready = listed is not null
                ? item.Wait?.ItemIds.Contains(listed.Id) ?? false
                : item.Wait?.External.Any(b => b.Id == candidate.Blocker.Id) ?? false;
            if (!waitsAlready)
            {
                candidates.Add(candidate);
            }
        }
        return candidates;
    }

    /// Searches every connected GitLab at once, the item's own first. Only the item's own GitLab can fail the search,
    /// the others just contribute nothing when they are unreachable.
    private async Task<List<(GitLabAccount, ItemKind, GLWorkItem)>> SearchEverywhere(string text, TodoItem item)
    {
        var jobs = Accounts
            .Where(account => TryClient(account) is not null)
            .Select(async account =>
            {
                // A project ID means nothing in another GitLab.
                int? projectId = account.Id == item.AccountId ? item.ProjectId : null;
                try
                {
                    return (Account: account, Hits: await TryClient(account)!.SearchWorkItems(text, projectId), Error: (Exception?)null);
                }
                catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    return (Account: account, Hits: new List<(ItemKind Kind, GLWorkItem Item)>(), Error: exception);
                }
            })
            .ToList();
        var found = new List<(GitLabAccount, ItemKind, GLWorkItem)>();
        foreach (var (account, hits, error) in (await Task.WhenAll(jobs)).OrderBy(r => r.Account.Id == item.AccountId ? 0 : 1))
        {
            if (error is not null && account.Id == item.AccountId)
            {
                throw error is GitLabException ? error : new GitLabException(Describe(error));
            }
            found.AddRange(hits.Select(h => (account, h.Kind, h.Item)));
        }
        return found;
    }

    /// One that is in the list already becomes an ordinary blocker, anything else an external one.
    public void AddBlocker(BlockerCandidate candidate, TodoItem item)
    {
        if (candidate.ListedItemId is not { } listedId)
        {
            AddBlocker(candidate.Blocker, item);
            return;
        }
        var listed = BlockerCandidates(item).FirstOrDefault(i => i.Id == listedId) ?? throw GitLabException.Cycle;
        if (!(item.Wait?.ItemIds.Contains(listed.Id) ?? false))
        {
            ToggleBlocker(listed, item);
        }
    }

    public void AddBlocker(ExternalBlocker blocker, TodoItem item)
    {
        var wait = plan.Waits.GetValueOrDefault(item.Id) ?? new Wait();
        if (wait.External.Any(b => b.Id == blocker.Id))
        {
            return;
        }
        UpdateWait(wait with { External = [.. wait.External, blocker] }, item.Id);
    }

    public void RemoveBlocker(ExternalBlocker blocker, TodoItem item)
    {
        var wait = plan.Waits.GetValueOrDefault(item.Id) ?? new Wait();
        UpdateWait(wait with { External = wait.External.Where(b => b.Id != blocker.Id).ToList() }, item.Id);
    }

    /// Items `item` can wait on: anything but itself and items that already wait on it.
    public List<TodoItem> BlockerCandidates(TodoItem item) =>
        Items.Where(i => i.Id != item.Id && !Blockers(i.Id).Contains(item.Id)).ToList();

    /// Everything `itemId` waits on, directly or through other items.
    private HashSet<string> Blockers(string itemId)
    {
        var found = new HashSet<string>();
        var queue = new Stack<string>(plan.Waits.GetValueOrDefault(itemId)?.ItemIds ?? []);
        while (queue.TryPop(out var next))
        {
            if (found.Add(next))
            {
                foreach (var blocker in plan.Waits.GetValueOrDefault(next)?.ItemIds ?? [])
                {
                    queue.Push(blocker);
                }
            }
        }
        return found;
    }

    private void SetWait(string itemId, Wait wait)
    {
        if (wait.IsEmpty)
        {
            plan.Waits.Remove(itemId);
        }
        else
        {
            plan.Waits[itemId] = wait;
        }
    }

    private void UpdateWait(Wait wait, string itemId)
    {
        SetWait(itemId, wait);
        if (!wait.IsEmpty)
        {
            plan.Released.Remove(itemId);
            KeepBehindBlockers(itemId);
        }
        PlanChanged();
    }

    /// An item waiting on another one of its own project goes after it in the work order. A blocker not in the
    /// order yet is placed right before the item instead.
    private void KeepBehindBlockers(string itemId)
    {
        if (Items.FirstOrDefault(i => i.Id == itemId) is not { } item || plan.Waits.GetValueOrDefault(itemId) is not { } wait)
        {
            return;
        }
        var ownProject = wait.ItemIds.Where(id => Items.FirstOrDefault(i => i.Id == id)?.ProjectKey == item.ProjectKey);
        var colleagues = wait.External.Where(b => $"{b.AccountId.IdString()}:{b.ProjectId}" == item.ProjectKey).Select(b => b.Id);
        var sameLane = ownProject.Concat(colleagues).ToList();
        foreach (var blocker in sameLane.Where(b => !plan.Order.Contains(b)))
        {
            var position = plan.Order.IndexOf(itemId);
            if (position >= 0)
            {
                plan.Order.Insert(position, blocker);
            }
        }
        var lastBlocker = sameLane.Select(id => plan.Order.IndexOf(id)).DefaultIfEmpty(-1).Max();
        var current = plan.Order.IndexOf(itemId);
        if (current < 0 || current >= lastBlocker)
        {
            return;
        }
        plan.Order.RemoveAt(current);
        // The blocker moved one up with the removal, so this lands right after it.
        plan.Order.Insert(lastBlocker, itemId);
    }

    /// Closed issues and merged MRs leave the plan, and whatever waited on them can go on.
    private void PrunePlan(Guid accountId, HashSet<string> ids, List<TodoItem> previous)
    {
        var prefix = accountId.IdString() + ":";
        // Only own items can be gone: `account:kind:id`. Other people's ones (`account:project:kind:iid`) are checked separately.
        bool IsGone(string id) => id.StartsWith(prefix, StringComparison.Ordinal) && id.Split(':').Length == 3 && !ids.Contains(id);
        var before = PlanJson();
        var previousById = new Dictionary<string, TodoItem>();
        foreach (var item in previous.SelectMany(i => i.MergeRequests.Prepend(i)))
        {
            previousById.TryAdd(item.Id, item);
        }
        plan.Order.RemoveAll(IsGone);
        foreach (var (itemId, wait) in plan.Waits.ToList())
        {
            var done = wait.ItemIds.Where(IsGone).ToList();
            var updated = wait with { ItemIds = wait.ItemIds.Where(id => !IsGone(id)).ToList() };
            if (!IsGone(itemId) && updated.IsEmpty && done.Count > 0)
            {
                Release(itemId, done.Select(id => previousById.GetValueOrDefault(id)?.ShortReference ?? "položka").ToList());
            }
            SetWait(itemId, IsGone(itemId) ? new Wait() : updated);
        }
        plan.Released = plan.Released.Where(r => !IsGone(r.Key)).ToDictionary();
        plan.Delays = plan.Delays.Where(d => !IsGone(d.Key)).ToDictionary();
        if (PlanJson() != before)
        {
            SavePlan();
        }
    }

    private string PlanJson() => JsonSerializer.Serialize(plan, Storage.Json);

    private void PlanChanged()
    {
        SavePlan();
        RebuildItems();
        ScheduleSync();
    }

    private void SavePlan()
    {
        if (!isPreview)
        {
            TrySave(Storage.PlanPath, plan);
        }
    }

    private static void TrySave<T>(string path, T value, bool isPrivate = false)
    {
        try
        {
            Storage.Save(path, value, isPrivate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nepovedlo se uložit {path}: {exception.Message}");
        }
    }

    // MARK: Timeline

    /// Open issues and merge requests of one project that are not in the timeline yet, for its „+“.
    public async Task<List<BlockerCandidate>> SearchProject(string text, Guid accountId, int projectId)
    {
        if (Accounts.FirstOrDefault(a => a.Id == accountId) is not { } account)
        {
            return [];
        }
        var found = await Client(account).SearchWorkItems(text, projectId, onlyInProject: true);
        var shown = TimelineExternal.Select(b => b.Id).ToHashSet();
        return found
            .Where(hit =>
            {
                var listedId = TodoItem.MakeId(account.Id, hit.Kind, hit.Item.Id);
                return hit.Item.IsOpen && !Items.Any(i => i.Id == listedId || i.MergeRequests.Any(m => m.Id == listedId));
            })
            .Select(hit => new BlockerCandidate(account.Id, hit.Kind, hit.Item, null))
            .Where(candidate => !shown.Contains(candidate.Blocker.Id))
            .ToList();
    }

    /// Someone else's issue or merge request at the end of its project's lane.
    public void AddToTimeline(ExternalBlocker blocker)
    {
        if (plan.Tracked.Any(b => b.Id == blocker.Id))
        {
            return;
        }
        plan.Tracked.Add(blocker);
        if (!plan.Order.Contains(blocker.Id))
        {
            plan.Order.Add(blocker.Id);
        }
        PlanChanged();
    }

    /// Whatever waits on it keeps it in the timeline.
    public void RemoveFromTimeline(ExternalBlocker blocker)
    {
        plan.Tracked.RemoveAll(b => b.Id == blocker.Id);
        if (!IsWaitedOn(blocker))
        {
            plan.Order.Remove(blocker.Id);
        }
        PlanChanged();
    }

    public bool IsWaitedOn(ExternalBlocker blocker) => plan.Waits.Values.Any(w => w.External.Any(b => b.Id == blocker.Id));

    /// Drag within a lane: onto a block moves it there, onto the lane's empty end moves it last.
    public void PlaceInLane(string draggedId, string? targetId, IReadOnlyList<string> laneIds)
    {
        var from = laneIds.IndexOf(draggedId);
        if (from < 0)
        {
            return;
        }
        var target = targetId is null ? -1 : laneIds.IndexOf(targetId);
        // Moving right lands after the target, moving left before it.
        var destination = target < 0 ? laneIds.Count : from < target ? target + 1 : target;
        Move(laneIds, [from], destination);
    }

    public TimelineSchedule TimelineSchedule() => Core.TimelineSchedule.Build(Items, TimelineExternal, OrderPositions, plan.Delays);

    /// Drag onto an empty step of the lane: the item goes behind the blocks starting before that step and waits there,
    /// as long as nothing it waits on keeps it later anyway.
    public void PlaceInTimeline(string id, int step, IReadOnlyList<string> laneIds)
    {
        var lane = TimelineSchedule().Lanes.FirstOrDefault(l => l.Blocks.Any(b => b.Id == id));
        var from = laneIds.IndexOf(id);
        if (lane is null || from < 0)
        {
            return;
        }
        var previous = lane.Blocks.LastOrDefault(b => b.Id != id && b.Slot < step);
        // Never ahead of what it waits on in the same lane.
        var waitedOn = (lane.Blocks.FirstOrDefault(b => b.Id == id)?.Blockers ?? []).ToHashSet();
        var lastWaitedOn = laneIds.Select((laneId, index) => waitedOn.Contains(laneId) ? index : -1).Max();
        var earliest = lastWaitedOn + 1;
        var destination = Math.Max(earliest, previous is null ? 0 : laneIds.IndexOf(previous.Id) + 1);
        plan.Delays.Remove(id);
        if (destination == from || destination == from + 1)
        {
            PlanChanged();
        }
        else
        {
            Move(laneIds, [from], destination);
        }
        // Where it starts without a delay decides how long the delay is.
        var natural = TimelineSchedule().Lanes.SelectMany(l => l.Blocks).FirstOrDefault(b => b.Id == id)?.Slot ?? step;
        if (step > natural)
        {
            plan.Delays[id] = step - natural;
        }
        PlanChanged();
    }

    public void Delay(string id, int steps)
    {
        var delay = Math.Max(0, plan.Delays.GetValueOrDefault(id) + steps);
        if (delay > 0)
        {
            plan.Delays[id] = delay;
        }
        else
        {
            plan.Delays.Remove(id);
        }
        PlanChanged();
    }

    /// Drop onto a block of another project: the dragged item waits on it. Someone else's item cannot wait,
    /// so dragging it onto one of the user's makes the user's item wait on it instead.
    public void LinkInTimeline(string draggedId, string targetId)
    {
        if (Items.FirstOrDefault(i => i.Id == draggedId) is { } dragged)
        {
            AddTimelineBlocker(targetId, dragged);
        }
        else if (Items.FirstOrDefault(i => i.Id == targetId) is { } target)
        {
            AddTimelineBlocker(draggedId, target);
        }
    }

    /// Whether `dependentId` can wait on `blockerId`: only the user's own items wait, and never in a loop.
    public bool CanLink(string dependentId, string blockerId)
    {
        if (dependentId == blockerId || Items.FirstOrDefault(i => i.Id == dependentId) is not { } dependent)
        {
            return false;
        }
        if (Items.Any(i => i.Id == blockerId))
        {
            return BlockerCandidates(dependent).Any(i => i.Id == blockerId);
        }
        return TimelineExternal.Any(b => b.Id == blockerId);
    }

    public void RemoveLink(string dependentId, string blockerId)
    {
        if (Items.FirstOrDefault(i => i.Id == dependentId) is not { } dependent)
        {
            return;
        }
        if (Items.FirstOrDefault(i => i.Id == blockerId) is { } blocker)
        {
            if (dependent.Wait?.ItemIds.Contains(blockerId) == true)
            {
                ToggleBlocker(blocker, dependent);
            }
        }
        else if (dependent.Wait?.External.FirstOrDefault(b => b.Id == blockerId) is { } external)
        {
            RemoveBlocker(external, dependent);
        }
    }

    /// Items waiting on `id`, own or someone else's.
    public List<TodoItem> Dependents(string id) =>
        Items.Where(i => (i.Wait?.ItemIds.Contains(id) ?? false) || (i.Wait?.External.Any(b => b.Id == id) ?? false)).ToList();

    /// An arrow drawn from one block to another: the one it points at waits on the one it starts from.
    public void Link(string dependentId, string blockerId)
    {
        if (Items.FirstOrDefault(i => i.Id == dependentId) is { } dependent)
        {
            AddTimelineBlocker(blockerId, dependent);
        }
    }

    private void AddTimelineBlocker(string blockerId, TodoItem item)
    {
        if (Items.FirstOrDefault(i => i.Id == blockerId) is { } listed)
        {
            if (!BlockerCandidates(item).Any(i => i.Id == listed.Id))
            {
                _ = Ui.Show("Tahle vazba nejde", $"{listed.ShortReference} už čeká na {item.ShortReference}, vznikl by kruh.", isWarning: true);
                return;
            }
            if (!(item.Wait?.ItemIds.Contains(listed.Id) ?? false))
            {
                ToggleBlocker(listed, item);
            }
        }
        else if (TimelineExternal.FirstOrDefault(b => b.Id == blockerId) is { } external)
        {
            AddBlocker(external, item);
        }
    }

    // MARK: Actions

    public async Task Perform(ItemAction action, string itemId)
    {
        if (RunningActions.Contains(itemId))
        {
            return;
        }
        if (ItemWithId(itemId) is not { } item || Accounts.FirstOrDefault(a => a.Id == item.AccountId) is not { } account)
        {
            await Ui.Show("Položka nenalezena", "Seznam se mezitím změnil, zkus to po obnovení znovu.");
            return;
        }

        RunningActions.Add(itemId);
        Changed?.Invoke();
        try
        {
            var client = Client(account);
            if (action == ItemAction.CreateMergeRequest)
            {
                await CreateMergeRequest(item, account, client);
            }
            else
            {
                await MarkReady(item, account, client);
            }
        }
        catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException)
        {
            await Ui.Show("Akce se nepovedla", Describe(exception), isWarning: true);
        }
        finally
        {
            RunningActions.Remove(itemId);
            Changed?.Invoke();
        }
    }

    private async Task CreateMergeRequest(TodoItem issue, GitLabAccount account, GitLabClient client)
    {
        var branch = GitLabClient.BranchName(issue);
        var confirmed = await Ui.Confirm(
            "Vytvořit merge request?",
            $"{issue.Reference}\n{issue.Title}\n\nVětev: {branch} → {account.TargetBranch}\nAssignee: ty",
            "Vytvořit MR");
        if (!confirmed)
        {
            return;
        }

        var assigneeId = await CurrentUserId(account, client);
        // The next refresh groups the new merge request under the issue, keeping its place in the work order.
        var mergeRequest = await client.CreateMergeRequest(issue, branch, account.TargetBranch, assigneeId);
        var command = $"git fetch origin && git checkout {mergeRequest.SourceBranch}";
        await Ui.Copy(command);
        _ = Refresh();

        var choice = await Ui.Show(
            $"Merge request {mergeRequest.References?.Short ?? $"!{mergeRequest.Iid}"} je připravený",
            $"Větev: {mergeRequest.SourceBranch}\n\nPříkaz pro checkout je ve schránce:\n{command}",
            ["OK", "Otevřít MR"]);
        if (choice == 1)
        {
            Ui.Open(mergeRequest.WebUrl);
        }
    }

    private async Task MarkReady(TodoItem mergeRequest, GitLabAccount account, GitLabClient client)
    {
        var reviewer = account.DefaultReviewer;
        var reviewerLine = reviewer.Length == 0 ? "Výchozí reviewer není nastavený, zruší se jen draft." : $"Reviewer: @{reviewer}";
        var confirmed = await Ui.Confirm(
            "Označit MR jako ready?",
            $"{mergeRequest.Reference}\n{mergeRequest.Title}\n\n{reviewerLine}",
            "Označit jako ready");
        if (!confirmed)
        {
            return;
        }

        long? reviewerId = reviewer.Length == 0 ? null : await client.UserId(reviewer);
        await client.MarkReady(mergeRequest.ProjectId, mergeRequest.Iid, reviewerId);
        await Refresh();
    }

    private async Task<long> CurrentUserId(GitLabAccount account, GitLabClient client)
    {
        if (userIds.TryGetValue(account.Id, out var id))
        {
            return id;
        }
        id = (await client.CurrentUser()).Id;
        userIds[account.Id] = id;
        return id;
    }

    private GitLabClient Client(GitLabAccount account)
    {
        var apiUrl = account.ApiUrl ?? throw GitLabException.InvalidUrl;
        var token = tokens.GetValueOrDefault(account.Id);
        return string.IsNullOrEmpty(token) ? throw GitLabException.MissingToken : new GitLabClient(apiUrl, token);
    }

    private GitLabClient? TryClient(GitLabAccount account)
    {
        try
        {
            return Client(account);
        }
        catch (GitLabException)
        {
            return null;
        }
    }

    // MARK: Settings

    public void SaveAccount(GitLabAccount account, string? token)
    {
        var index = settings.Accounts.FindIndex(a => a.Id == account.Id);
        if (index >= 0)
        {
            settings.Accounts[index] = account;
        }
        else
        {
            settings.Accounts.Add(account);
        }
        if (token is not null)
        {
            tokens[account.Id] = token;
            SaveTokens();
        }
        userIds.Remove(account.Id);
        SaveSettings();
        StartTimer();
        Changed?.Invoke();
    }

    public void RemoveAccount(GitLabAccount account)
    {
        settings.Accounts.RemoveAll(a => a.Id == account.Id);
        tokens.Remove(account.Id);
        userIds.Remove(account.Id);
        SaveTokens();
        PrunePlan(account.Id, [], []);
        // Notes outlive their items, not their GitLab.
        plan.Notes = plan.Notes.Where(n => !n.Key.StartsWith(account.Id.IdString() + ":", StringComparison.Ordinal)).ToDictionary();
        SavePlan();
        if (syncState?.Settings.AccountId == account.Id)
        {
            ConfigureSync(null);
        }
        SaveSettings();
        RebuildItems();
    }

    public void SetRefreshMinutes(double minutes)
    {
        settings.RefreshMinutes = minutes;
        SaveSettings();
        StartTimer();
        Changed?.Invoke();
    }

    public void SetKeepInTray(bool keep)
    {
        settings.KeepInTray = keep;
        SaveSettings();
        Changed?.Invoke();
    }

    public void SetCheckUpdates(bool check)
    {
        settings.CheckUpdates = check;
        SaveSettings();
        Changed?.Invoke();
    }

    public void SetTheme(string theme)
    {
        settings.Theme = theme;
        SaveSettings();
        Changed?.Invoke();
    }

    public void SetTimelineZoom(double zoom)
    {
        settings.TimelineZoom = zoom;
        SaveSettings();
        Changed?.Invoke();
    }

    private void SaveSettings()
    {
        if (!isPreview)
        {
            TrySave(Storage.SettingsPath, settings);
        }
    }

    private void SaveTokens()
    {
        if (!isPreview)
        {
            TrySave(Storage.TokensPath, tokens.ToDictionary(t => t.Key.IdString(), t => t.Value), isPrivate: true);
        }
    }
    // MARK: Sync

    /// Turns syncing on with a repository, or off with null. A new repository starts from scratch: the first sync
    /// merges what is there with this device's plan.
    public void ConfigureSync(SyncSettings? settings)
    {
        syncDelay?.Cancel();
        syncState = settings is null ? null : new SyncState { Settings = settings };
        SyncStatus = SyncStatus.Idle;
        SyncError = null;
        SaveSyncState();
        Changed?.Invoke();
        if (settings is not null)
        {
            _ = SyncPlan();
        }
    }

    /// A private repository named `gitlab-todo-data` in the user's namespace; returns its `group/project` path.
    public async Task<string> CreateSyncRepository(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId) ?? throw GitLabException.MissingToken;
        return (await Client(account).CreatePrivateProject("gitlab-todo-data")).PathWithNamespace;
    }

    /// Exchanges the plan with the repository. Runs after every refresh and shortly after a change on this device.
    public async Task SyncPlan()
    {
        if (isPreview || syncState is null)
        {
            return;
        }
        if (isSyncing)
        {
            syncPending = true;
            return;
        }
        isSyncing = true;
        try
        {
            do
            {
                syncPending = false;
                if (syncState is not { } state || Accounts.FirstOrDefault(a => a.Id == state.Settings.AccountId) is not { } account)
                {
                    return;
                }
                SyncStatus = SyncStatus.Syncing;
                Changed?.Invoke();
                try
                {
                    var client = Client(account);
                    var project = await client.Project(state.Settings.Project);
                    var store = new GitLabPlanStore(client, project.Id, project.DefaultBranch ?? "main");
                    var started = PlanSync.Copy(plan);
                    var (synced, next) = await PlanSync.Sync(started, state, store, Accounts);
                    // The settings changed while syncing, this result belongs to the old repository.
                    if (syncState?.Settings != state.Settings)
                    {
                        continue;
                    }
                    // What changed on this device in the meantime stays, the next sync sends it.
                    if (!PlanSync.Same(plan, started))
                    {
                        synced = PlanSync.Merge(started, plan, synced);
                    }
                    next.Base = next.Base is null ? null : PlanSync.Copy(next.Base);
                    next.SyncedAt = DateTimeOffset.Now;
                    next.Visibility = project.Visibility;
                    syncState = next;
                    SaveSyncState();
                    if (!PlanSync.Same(synced, plan))
                    {
                        plan = PlanSync.Copy(synced);
                        SavePlan();
                        RebuildItems();
                    }
                    SyncStatus = SyncStatus.Synced;
                    SyncError = null;
                }
                catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    SyncStatus = SyncStatus.Failed;
                    SyncError = Describe(exception);
                }
                Changed?.Invoke();
            }
            while (syncPending);
        }
        finally
        {
            isSyncing = false;
        }
    }

    /// Changes go out a little later, after a burst of editing, rather than with every keystroke.
    private void ScheduleSync()
    {
        if (isPreview || syncState is null)
        {
            return;
        }
        syncDelay?.Cancel();
        var cancellation = new CancellationTokenSource();
        syncDelay = cancellation;
        _ = SyncLater(cancellation.Token);
    }

    private async Task SyncLater(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellation);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        await SyncPlan();
    }

    private void SaveSyncState()
    {
        if (isPreview)
        {
            return;
        }
        if (syncState is null)
        {
            File.Delete(Storage.SyncPath);
        }
        else
        {
            TrySave(Storage.SyncPath, syncState);
        }
    }
}

public enum SyncStatus
{
    Idle,
    Syncing,
    Synced,
    Failed,
}


internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(list[index], value))
            {
                return index;
            }
        }
        return -1;
    }
}
