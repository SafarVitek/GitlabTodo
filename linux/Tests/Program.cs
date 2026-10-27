using System.Text.Json;
using GitLabTodo.Core;

var failures = 0;
void Check(bool condition, string message)
{
    Console.WriteLine((condition ? "ok   " : "FAIL ") + message);
    if (!condition)
    {
        failures++;
    }
}

AppState Preview(Snapshot? snapshot = null) => new(new SilentUserInterface(), snapshot ?? PreviewData.Snapshot());
List<string> Refs(IEnumerable<TodoItem> items) => items.Select(i => i.ShortReference).ToList();
bool Same(IEnumerable<string> actual, params string[] expected) => actual.SequenceEqual(expected);

var model = Preview();
TodoItem Item(string reference) => model.Items.First(i => i.ShortReference == reference);
string Id(string reference) => Item(reference).Id;

Check(Same(Refs(model.Items), "backend#12", "backend#33", "frontend#7", "backend#21", "frontend!52", "infra#5", "infra#9", "frontend#18", "frontend#27", "docs#3", "backend#44"),
    $"initial order {string.Join(" ", Refs(model.Items))}");
Check(Item("backend#33").Stage == ItemKind.MergeRequest && Item("backend#33").References == "#33 → !34", $"issue with MR is in review: {Item("backend#33").References}");
Check(Item("backend#33").NextStep is { Action: ItemAction.MarkReady, Target.Kind: ItemKind.MergeRequest }, "Ready targets the merge request");
Check(Item("frontend#7").NextStep?.Action == ItemAction.CreateMergeRequest, "issue without MR offers MR creation");
Check(Item("frontend!52").NextStep is null, "ready MR with a reviewer has no step");
Check(model.ItemWithId(Item("backend#33").MergeRequests[0].Id)?.Kind == ItemKind.MergeRequest, "nested MR is found by ID");
Check(model.Items.Count(i => KindFilter.Issues.Includes(i)) == 9 && model.Items.Count(i => KindFilter.MergeRequests.Includes(i)) == 2, "kind filter counts");

var all = model.Items.Select(i => i.Id).ToList();
model.Move(all, [all.IndexOf(Id("infra#5"))], 0);
Check(Same(Refs(model.Items).Take(2), "infra#5", "backend#12"), "move to top");

var backend = model.Items.Where(i => i.ProjectKey == Item("backend#12").ProjectKey).Select(i => i.Id).ToList();
model.Move(backend, [backend.IndexOf(Id("backend#33"))], backend.IndexOf(Id("backend#12")));
Check(Same(Refs(model.Items).Take(4), "infra#5", "backend#33", "backend#12", "frontend#7"), $"filtered move keeps hidden items {string.Join(" ", Refs(model.Items).Take(4))}");

all = model.Items.Select(i => i.Id).ToList();
model.Move(all, [all.IndexOf(Id("docs#3"))], 1);
Check(Item("docs#3").IsPlaced && Refs(model.Items)[1] == "docs#3", "new item placed by drag");
Check(!Item("backend#44").IsPlaced, "other new item stays new");

model.ToggleBlocker(Item("frontend#7"), Item("docs#3"));
model.ToggleBlocker(Item("infra#5"), Item("docs#3"));
Check(Item("docs#3").WaitDescription == "frontend#7, infra#5", "waits on two items");
Check(!model.BlockerCandidates(Item("frontend#7")).Any(i => i.Id == Id("docs#3")), "no direct cycle");
Check(!model.BlockerCandidates(Item("backend#12")).Any(i => i.Id == Id("backend#21")), "no indirect cycle");

var moved = new List<int> { 0, 1, 2, 3, 4 };
AppState.MoveElements(moved, [1, 3], 0);
Check(Same(moved.Select(n => n.ToString()), "1", "3", "0", "2", "4"), "move several up");
AppState.MoveElements(moved, [0], 5);
Check(Same(moved.Select(n => n.ToString()), "3", "0", "2", "4", "1"), "move to the end");

// Timeline: lanes per project, one step per item, waiting pushes items back.
var tm = Preview();
TimelineSchedule Schedule() => TimelineSchedule.Build(tm.Items, tm.TimelineExternal, tm.OrderPositions);
int? Step(string reference) => Schedule().Lanes.SelectMany(l => l.Blocks)
    .FirstOrDefault(b => (b.Item?.ShortReference ?? b.External?.ShortReference) == reference)?.Slot;
List<string> LaneIds(string name) => Schedule().Lanes.First(l => l.Name == name).Blocks.Select(b => b.Id).ToList();
string Tid(string reference) => tm.Items.First(i => i.ShortReference == reference).Id;
Check(Same(Schedule().Lanes.Select(l => l.Name), "backend", "frontend", "infra", "docs", "platform"),
    $"a lane per project, colleagues' last: {string.Join(" ", Schedule().Lanes.Select(l => l.Name))}");
Check(Step("backend#21") == 2 && Step("infra#9") == 1 && Step("frontend#27") == 3 && Step("platform#500") == 0, "steps follow order and waits");
tm.PlaceInLane(Tid("backend#33"), Tid("backend#12"), LaneIds("backend"));
Check(Same(Schedule().Lanes[0].Blocks.Take(2).Select(b => b.Item!.ShortReference), "backend#33", "backend#12"), "drag reorders within a lane");
tm.LinkInTimeline(Tid("infra#5"), Tid("backend#12"));
Check(Same(tm.Items.First(i => i.ShortReference == "infra#5").Wait?.ItemIds ?? [], Tid("backend#12")), "drag onto another project makes it wait");
Check(Step("infra#5") == 2 && Step("infra#9") == 3, $"waiting on another project leaves a gap: {Step("infra#5")}, {Step("infra#9")}");
var api = new ExternalBlocker(Guid.NewGuid(), 901, ItemKind.Issue, 77, "Kolegovo API", "team/api#77", "https://x");
tm.AddToTimeline(api);
Check(Schedule().Lanes.Any(l => l.Name == "api") && Step("api#77") is not null, "+ adds a colleague's issue");
tm.LinkInTimeline(api.Id, Tid("docs#3"));
Check(Same(tm.Items.First(i => i.ShortReference == "docs#3").Wait?.External.Select(b => b.Id) ?? [], api.Id), "dragging a colleague's issue onto own makes own wait");
tm.RemoveFromTimeline(api);
Check(Schedule().Lanes.Any(l => l.Name == "api"), "stays while something waits on it");
tm.StopWaiting(tm.Items.First(i => i.ShortReference == "docs#3"));
tm.RemoveFromTimeline(api);
Check(!Schedule().Lanes.Any(l => l.Name == "api"), "removed once nothing waits on it");
// #12 waiting on #9 closes a loop through lane order (#9 after #5, #5 waits on #12): must still finish.
tm.ToggleBlocker(tm.Items.First(i => i.ShortReference == "infra#9"), tm.Items.First(i => i.ShortReference == "backend#12"));
var looped = Schedule();
Check(looped.Lanes.Sum(l => l.Blocks.Count) == tm.Items.Count + tm.TimelineExternal.Count && looped.StepCount < 40, "a dependency loop does not hang the schedule");

// Arrows from a block's dot, also between two GitLabs.
var second = Guid.NewGuid();
var foreign = new TodoItem
{
    Id = TodoItem.MakeId(second, ItemKind.Issue, 9001), AccountId = second, AccountName = "Druhý GitLab", Kind = ItemKind.Issue,
    Role = ItemRole.Assigned, ProjectId = 55, Iid = 3, Title = "Issue ve druhém GitLabu", Reference = "lab/other#3", WebUrl = "https://y",
    ActivityAt = DateTimeOffset.Now, IsPlaced = true,
};
var twoLabs = PreviewData.Snapshot();
twoLabs.Items.Add(foreign);
var lm = Preview(twoLabs);
string Lid(string reference) => lm.Items.First(i => i.ShortReference == reference).Id;
Check(lm.Accounts.Count == 2, "two GitLabs");
Check(lm.CanLink(Lid("backend#12"), Lid("other#3")), "own item can wait on one in the other GitLab");
Check(!lm.CanLink(Lid("backend#12"), Lid("backend#12")), "not on itself");
Check(!lm.CanLink(Lid("backend#12"), Lid("backend#21")), "not in a loop (#21 waits on #12)");
var labColleague = lm.TimelineExternal[0];
Check(!lm.CanLink(labColleague.Id, Lid("backend#12")), "someone else's issue cannot wait");
Check(lm.CanLink(Lid("docs#3"), labColleague.Id), "own item can wait on someone else's");
lm.Link(Lid("backend#12"), Lid("other#3"));
Check(Same(lm.Items.First(i => i.ShortReference == "backend#12").Wait?.ItemIds ?? [], Lid("other#3")), "arrow across GitLabs makes it wait");
var crossLab = TimelineSchedule.Build(lm.Items, lm.TimelineExternal, lm.OrderPositions);
int? StepOf(string reference) => crossLab.Lanes.SelectMany(l => l.Blocks).FirstOrDefault(b => b.Item?.ShortReference == reference)?.Slot;
Check(StepOf("other#3") == 0 && StepOf("backend#12") == 1, "pushed behind its blocker in the other GitLab");

// Putting items off: empty steps before them, marked as delay rather than wait.
var dm = Preview();
string Did(string reference) => dm.Items.First(i => i.ShortReference == reference).Id;
TimelineBlock? Block(string reference) => dm.TimelineSchedule().Lanes.SelectMany(l => l.Blocks).FirstOrDefault(b => b.Item?.ShortReference == reference);
dm.Delay(Did("frontend#7"), 3);
Check(Block("frontend#7")?.Slot == 3 && Block("frontend#7")?.DelayedUntil == 3, "put off by three steps");
Check(Block("frontend!52")?.Slot == 4, "what follows moves along");
dm.Delay(Did("frontend#7"), -1);
Check(Block("frontend#7")?.Slot == 2, "one step back");
dm.Delay(Did("frontend#7"), -10);
Check(Block("frontend#7") is { Slot: 0, Delay: 0 }, "never before its natural place");
var infra = dm.TimelineSchedule().Lanes.First(l => l.Name == "infra").Blocks.Select(b => b.Id).ToList();
dm.PlaceInTimeline(Did("infra#9"), 5, infra);
Check(Block("infra#9") is { Slot: 5, Delay: 4 }, $"dropped onto an empty step: {Block("infra#9")?.Slot}");
dm.PlaceInTimeline(Did("infra#9"), 0, infra);
Check(Block("infra#9") is { Slot: 1, Delay: 0 }, "dropped before what it waits on stays right after it");
dm.Link(Did("docs#3"), Did("infra#5"));
dm.RemoveLink(Did("docs#3"), Did("infra#5"));
Check(!dm.Items.First(i => i.ShortReference == "docs#3").IsWaiting, "link removed");
Check(Same(dm.Dependents(Did("backend#12")).Select(i => i.ShortReference), "backend#21"), "dependents of an item");
var legacyDelays = JsonSerializer.Deserialize<Plan>("""{"order":["a"]}""", Storage.Json)!;
Check(legacyDelays.Delays.Count == 0 && legacyDelays.Tracked.Count == 0 && legacyDelays.Notes.Count == 0, "older plans load without delays and notes");

Check(Zoom.Step(1.0, 0.1) == 1.1 && Zoom.Step(0.3, -0.1) == 0.3 && Zoom.Step(1.47, 0.1) == 1.5 && Zoom.Step(0.83, -0.1) == 0.7, "zoom steps by 10 % within its range");

// A link back to a later item of the same project reorders, rather than drawing an arrow backwards.
var bm = Preview();
string Bid(string reference) => bm.Items.First(i => i.ShortReference == reference).Id;
bm.Link(Bid("backend#12"), Bid("backend#33"));
var backendLane = bm.TimelineSchedule().Lanes.First(l => l.Name == "backend").Blocks;
Check(Same(backendLane.Take(3).Select(b => b.Item!.ShortReference), "backend#33", "backend#12", "backend#21"),
    $"waiting item moved behind its blocker: {string.Join(" ", backendLane.Select(b => b.Item!.ShortReference))}");
Check(backendLane.First(b => b.Item?.ShortReference == "backend#12").Slot == 1, "and starts after it");
Check(Same(bm.Items.Where(i => i.ProjectName == "backend").Take(2).Select(i => i.ShortReference), "backend#33", "backend#12"), "the work order in the list follows");
// Older data where the waiting item comes first: the timeline puts it behind anyway.
var stale = PreviewData.Snapshot().Items;
var sso = stale.FindIndex(i => i.ShortReference == "backend#12");
stale[sso] = stale[sso] with { Wait = new Wait { ItemIds = [stale.First(i => i.ShortReference == "backend#33").Id] } };
var staleSchedule = TimelineSchedule.Build(stale, [], stale.Select((item, index) => (item.Id, index)).ToDictionary(p => p.Id, p => p.index));
Check(Same(staleSchedule.Lanes.First(l => l.Name == "backend").Blocks.Take(2).Select(b => b.Item!.ShortReference), "backend#33", "backend#12"), "timeline straightens stored order");

// Notes: free text with GitLab-style tasks, kept locally per item.
var noteText = new ItemNote("Zápis\n- [ ] první\n  * [x] druhý\n[ ] třetí\n- [ ]\n- bod\n[x]neúkol", DateTimeOffset.Now);
Check(Same(noteText.Tasks.Select(t => t.Text), "první", "druhý", "třetí", "") && noteText.Tasks.Select(t => t.IsDone).SequenceEqual([false, true, false, false]),
    $"tasks parsed: {string.Join(", ", noteText.Tasks.Select(t => t.Text))}");
Check(noteText.OpenTasks.Select(t => t.Line).SequenceEqual([1, 3, 4]), "open tasks");
Check(noteText.Toggling(1).Text == "Zápis\n- [x] první\n  * [x] druhý\n[ ] třetí\n- [ ]\n- bod\n[x]neúkol", "tick a task off");
Check(!noteText.Toggling(2).Tasks[1].IsDone && noteText.Toggling(5) == noteText, "tick back on, other lines untouched");
var nm = Preview();
string Nid(string reference) => nm.Items.First(i => i.ShortReference == reference).Id;
Check(nm.Items.First(i => i.ShortReference == "backend#33").Note?.OpenTasks.Count == 2, "preview note on the issue with its MR");
nm.SetNote("Meeting\n- [ ] zavolat", Nid("infra#5"));
Check(Same(nm.Items.First(i => i.ShortReference == "infra#5").Note?.Tasks.Select(t => t.Text) ?? [], "zavolat"), "note saved on the item");
nm.ToggleTask(1, Nid("infra#5"));
Check(nm.Items.First(i => i.ShortReference == "infra#5").Note?.OpenTasks.Count == 0, "task ticked off");
nm.SetNote("  \n", Nid("infra#5"));
Check(nm.Items.First(i => i.ShortReference == "infra#5").Note is null, "an empty note is removed");
var mergeRequestId = nm.Items.First(i => i.ShortReference == "backend#33").MergeRequests[0].Id;
nm.SetNote("z MR", mergeRequestId);
Check(nm.Items.First(i => i.ShortReference == "backend#33").Note?.Text.EndsWith("\n\nz MR") == true, "a note on a grouped MR joins its issue");
nm.RequestFocus(Nid("docs#3"), editsNote: true);
Check(nm.TakeNoteToEdit(Nid("docs#3")) && !nm.TakeNoteToEdit(Nid("docs#3")), "a request opens the note once");
var notedPlan = JsonSerializer.Deserialize<Plan>("""{"order":[],"notes":{"a":{"text":"x","updatedAt":"2026-10-27T12:00:00Z"}}}""", Storage.Json)!;
Check(notedPlan.Notes["a"].Text == "x" && JsonSerializer.Serialize(notedPlan, Storage.Json).Contains("\"updatedAt\": \"2026-10-27T12:00:00Z\""), "notes load and save like the macOS app's");

// Grouping: an MR closing an issue, or named after it, joins the issue.
var account = Guid.NewGuid();
TodoItem Gl(ItemKind kind, int iid, long gid, string? branch = null) => new()
{
    Id = TodoItem.MakeId(account, kind, gid), AccountId = account, AccountName = "a", Kind = kind, Role = ItemRole.Assigned,
    ProjectId = 7, Iid = iid, Title = "t", Reference = $"g/p{(kind == ItemKind.Issue ? "#" : "!")}{iid}", WebUrl = "https://x",
    ActivityAt = DateTimeOffset.Now, SourceBranch = branch,
};
var grouped = GitLabClient.Group(
    [Gl(ItemKind.Issue, 1372, 500), Gl(ItemKind.Issue, 10, 501)],
    [Gl(ItemKind.MergeRequest, 1192, 900, "feature-x"), Gl(ItemKind.MergeRequest, 77, 901, "10-something"), Gl(ItemKind.MergeRequest, 78, 902, "main-fix")],
    new Dictionary<string, List<long>> { [TodoItem.MakeId(account, ItemKind.MergeRequest, 900)] = [500] },
    account);
Check(grouped.Count == 3 && grouped[0].MergeRequests.Select(m => m.Iid).SequenceEqual([1192]), "MR grouped by closes_issues");
Check(grouped[1].MergeRequests.Select(m => m.Iid).SequenceEqual([77]), "MR grouped by branch name");
Check(grouped[2] is { Iid: 78, Kind: ItemKind.MergeRequest }, "unrelated MR stays on its own");
var gitLabJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
var threads = JsonSerializer.Deserialize<List<GLDiscussion>>(
    """[{"notes":[{"resolvable":false}]},{"notes":[{"resolvable":true,"resolved":false},{"resolvable":true,"resolved":true}]},{"notes":[{"resolvable":true,"resolved":true}]}]""",
    gitLabJson)!;
Check(threads.Count(t => t.IsResolvable) == 2 && threads.Count(t => t.IsUnresolved) == 1, "threads counted like GitLab");
Check(Item("backend#33").ReviewThreads is (1, 3), "x/y shown on the issue of the MR");
Check(Item("frontend!52").ReviewThreads is { } done && done.Resolved == done.Total, "all resolved");
Check(Item("frontend#7").ReviewThreads is null, "no threads without an MR");
// Waiting on other people's issues.
var url = WorkItemReference.Parse("https://gitlab.example.com/group/sub/project/-/issues/123");
Check(url is { Host: "gitlab.example.com", Path: "group/sub/project", Kind: ItemKind.Issue, Iid: 123 }, "URL reference");
Check(WorkItemReference.Parse("https://g.example.com/a/b/-/merge_requests/45")?.Kind == ItemKind.MergeRequest, "MR URL");
Check(WorkItemReference.Parse(" group/project!45 ") is { Path: "group/project", Kind: ItemKind.MergeRequest, Iid: 45 }, "path!iid");
Check(WorkItemReference.Parse("#12") is { Path: null, Iid: 12 }, "#iid in the same project");
Check(WorkItemReference.Parse("hello") is null && WorkItemReference.Parse("https://example.com/foo") is null, "garbage rejected");
var legacyPlan = JsonSerializer.Deserialize<Plan>("""{"order":["a","b"],"waits":{"b":{"itemIDs":["a"]}}}""", Storage.Json)!;
Check(Same(legacyPlan.Order, "a", "b") && Same(legacyPlan.Waits["b"].ItemIds, "a") && legacyPlan.Released.Count == 0, "plan saved by an older version still loads");

Check(Item("frontend#27").WaitDescription == "platform#500" && Item("frontend#27").IsWaiting, "waits on a colleague's issue");
Check(Item("frontend#7").ReleasedBy == "platform#480", "released item marked");
var colleague = new ExternalBlocker(Guid.NewGuid(), 901, ItemKind.Issue, 77, "x", "team/api#77", "https://x");
model.AddBlocker(colleague, Item("infra#5"));
model.ToggleBlocker(Item("backend#12"), Item("infra#5"));
model.ApplyExternalStates([], [colleague.Id]);
Check(Item("infra#5").IsWaiting && Item("infra#5").ReleasedBy is null, "still waits on its own item, not released yet");
model.ApplyExternalStates([], [Item("frontend#27").Wait!.External[0].Id]);
Check(!Item("frontend#27").IsWaiting && Item("frontend#27").ReleasedBy == "platform#500", "colleague's issue done: ready to start");
model.AcknowledgeRelease(Id("frontend#27"));
Check(Item("frontend#27").ReleasedBy is null, "ready mark gone once looked at");
var labels = JsonSerializer.Deserialize<List<GLLabel>>("""["plain", {"name":"bug","color":"#dc143c","text_color":"#FFFFFF"}]""", gitLabJson)!;
Check(labels.Select(l => l.ItemLabel).SequenceEqual([new ItemLabel("plain", "#6699cc", "#FFFFFF"), new ItemLabel("bug", "#dc143c", "#FFFFFF")]), "labels with and without details");
Check(GitLabClient.IssueIidFromBranch("1372-remove-timeline") == 1372 && GitLabClient.IssueIidFromBranch("feature-1372") is null, "branch prefix parsing");

// Only on Linux: files shared with the macOS app's format, and GitLab's naming rules.
var plan = new Plan
{
    Order = ["x"],
    Waits = new() { ["x"] = new Wait { External = [colleague] } },
    Released = new() { ["x"] = new Release(DateTimeOffset.Parse("2026-10-27T12:00:00Z"), ["api#77"]) },
};
var planJson = JsonSerializer.Serialize(plan, Storage.Json);
Check(planJson.Contains("\"itemIDs\"") && planJson.Contains("\"accountID\"") && planJson.Contains("\"webURL\"") && planJson.Contains("\"kind\": \"issue\"")
    && planJson.Contains("\"2026-10-27T12:00:00Z\"") && planJson.Contains(colleague.AccountId.ToString().ToUpperInvariant()), "plan JSON matches the macOS app's");
var snapshotJson = JsonSerializer.Serialize(PreviewData.Snapshot(), Storage.Json);
var restored = JsonSerializer.Deserialize<Snapshot>(snapshotJson, Storage.Json)!;
Check(!snapshotJson.Contains("\"nextStep\"") && !snapshotJson.Contains("\"shortReference\"") && restored.Items.Count == 11
    && restored.Items.First(i => i.Iid == 33).MergeRequests[0].HasConflicts, "snapshot keeps data, not computed values");
var issue = Gl(ItemKind.Issue, 1372, 1) with { Title = "Přidat podporu pro \"Šablony\" / v2.0!" };
Check(GitLabClient.BranchName(issue) == "1372-pridat-podporu-pro-sablony-v2-0", $"branch named like GitLab: {GitLabClient.BranchName(issue)}");
Check(GitLabClient.BranchName(issue with { IsConfidential = true }) == "1372-confidential-issue", "confidential issue branch");
Check(GitLabClient.TitleWithoutDraftPrefix("Draft: Resolve \"x\"") == "Resolve \"x\"" && GitLabClient.TitleWithoutDraftPrefix("[Draft] (draft) y") == "y", "draft prefixes removed");
Check(GitLabAccount.NormalizedBaseUrl("gitlab.example.com/api/v4/") == "https://gitlab.example.com" && GitLabAccount.Username(" @jnovak ") == "jnovak", "settings input cleaned up");
Check(Palette.PreferredSlot("x") == Palette.PreferredSlot("x") && Palette.PreferredSlot("6F1C2B9E-3D4A-4E5B-9C8D-7A6B5C4D3E2F:1") is >= 0 and < 6, "project color slot is stable");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/// Answers every question with "no", the tests never get to the network.
sealed class SilentUserInterface : IUserInterface
{
    public Task<bool> Confirm(string title, string message, string action) => Task.FromResult(false);

    public Task<int> Show(string title, string message, IReadOnlyList<string>? buttons = null, bool isWarning = false) => Task.FromResult(0);

    public Task Copy(string text) => Task.CompletedTask;

    public void Open(string url)
    {
    }
}
