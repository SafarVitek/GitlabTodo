using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GitLabTodo.Core;

/// Written as `issue` and `mergeRequest` by the camelCase enum converter in `Storage.Json`, like the macOS app.
public enum ItemKind
{
    Issue,
    MergeRequest,
}

public enum ItemRole
{
    Assigned,
    Mentioned,
}

public enum ItemAction
{
    CreateMergeRequest,
    MarkReady,
}

public enum KindFilter
{
    All,
    Issues,
    MergeRequests,
}

public static class Names
{
    public static string Key(this ItemKind kind) => kind == ItemKind.Issue ? "issue" : "mergeRequest";

    public static string Title(this ItemAction action) => action switch
    {
        ItemAction.CreateMergeRequest => "Vytvořit merge request",
        _ => "Označit jako ready a přidat reviewera",
    };

    public static string ShortTitle(this ItemAction action) => action == ItemAction.CreateMergeRequest ? "+ MR" : "Ready";

    /// A draft waiting to be marked ready is the step not to forget; creating MRs is offered more quietly.
    public static bool IsProminent(this ItemAction action) => action == ItemAction.MarkReady;

    public static bool Includes(this KindFilter filter, TodoItem item) => filter switch
    {
        KindFilter.Issues => item.Stage == ItemKind.Issue,
        KindFilter.MergeRequests => item.Stage == ItemKind.MergeRequest,
        _ => true,
    };

    /// `group/sub/project#12` → `project#12`
    public static string ShortReference(string reference) => reference.Split('/')[^1];

    /// `group/sub/project#12` → `#12`
    public static string LocalReference(string reference)
    {
        var index = reference.IndexOfAny(['#', '!']);
        return index >= 0 ? reference[index..] : reference;
    }

    /// `group/sub/project#12` → `project`
    public static string ProjectName(string reference, string fallback)
    {
        var index = reference.IndexOfAny(['#', '!']);
        var path = index >= 0 ? reference[..index] : reference;
        var name = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    /// Account IDs are written like Swift's `UUID.uuidString`, so the data matches the macOS app.
    public static string IdString(this Guid id) => id.ToString().ToUpperInvariant();
}

/// A GitLab label with its colors as `#RRGGBB`.
public sealed record ItemLabel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("textColor")] string TextColor);

/// An issue or merge request that is not in the list, e.g. a colleague's, which an item waits on.
public sealed record ExternalBlocker(
    [property: JsonPropertyName("accountID")] Guid AccountId,
    [property: JsonPropertyName("projectID")] int ProjectId,
    [property: JsonPropertyName("kind")] ItemKind Kind,
    [property: JsonPropertyName("iid")] int Iid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("webURL")] string WebUrl)
{
    [JsonIgnore]
    public string Id => $"{AccountId.IdString()}:{ProjectId}:{Kind.Key()}:{Iid}";

    [JsonIgnore]
    public string ShortReference => Names.ShortReference(Reference);
}

/// Why an item cannot be worked on yet: other items in the list that must be done first, other people's
/// issues or merge requests, something outside GitLab, or any mix of these.
public sealed record Wait
{
    [JsonPropertyName("itemIDs")]
    public List<string> ItemIds { get; init; } = [];

    [JsonPropertyName("external")]
    public List<ExternalBlocker> External { get; init; } = [];

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonIgnore]
    public bool IsEmpty => ItemIds.Count == 0 && External.Count == 0 && string.IsNullOrEmpty(Note);

    public bool SameAs(Wait? other) =>
        other is not null && ItemIds.SequenceEqual(other.ItemIds) && External.SequenceEqual(other.External) && Note == other.Note;
}

/// What got done so that an item can start.
public sealed record Release(
    [property: JsonPropertyName("date"), JsonConverter(typeof(WholeSecondsConverter))] DateTimeOffset Date,
    [property: JsonPropertyName("finished")] List<string> Finished);

/// The user's own ordering of the work, kept locally. Same JSON as the macOS app's plan.
public sealed class Plan
{
    /// Item IDs in the order the user wants to work on them.
    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = [];

    [JsonPropertyName("waits")]
    public Dictionary<string, Wait> Waits { get; set; } = [];

    /// Items whose wait ended because what they waited on got done, until the user looks at them.
    [JsonPropertyName("released")]
    public Dictionary<string, Release> Released { get; set; } = [];

    /// Issues and merge requests of other people added to the timeline with „+“.
    [JsonPropertyName("tracked")]
    public List<ExternalBlocker> Tracked { get; set; } = [];

    /// Empty timeline steps the user put before an item, to start it later.
    [JsonPropertyName("delays")]
    public Dictionary<string, int> Delays { get; set; } = [];
}

/// An action and the item it runs on, which for an issue with a merge request is the merge request.
public sealed record ItemStep(ItemAction Action, TodoItem Target);

public sealed record TodoItem
{
    /// `<account>:<kind>:<GitLab id>`; the local plan is keyed by it.
    public required string Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string AccountName { get; init; }
    public required ItemKind Kind { get; init; }
    public required ItemRole Role { get; init; }
    public required int ProjectId { get; init; }
    public required int Iid { get; init; }
    public required string Title { get; init; }
    public required string Reference { get; init; }
    public required string WebUrl { get; init; }
    public required DateTimeOffset ActivityAt { get; init; }
    public DateTimeOffset? DueDate { get; init; }
    public bool IsConfidential { get; init; }
    public bool HasMergeRequest { get; init; }
    public bool IsDraft { get; init; }
    public string? SourceBranch { get; init; }
    public int ReviewerCount { get; init; }
    /// Review threads on a merge request that can be resolved, and how many of them are still open.
    public int ResolvableThreads { get; init; }
    public int UnresolvedThreads { get; init; }
    public bool HasConflicts { get; init; }
    public List<ItemLabel> Labels { get; init; } = [];
    /// Placed by the user in the work order. Unplaced items are new and come after the placed ones.
    public bool IsPlaced { get; init; }
    public Wait? Wait { get; init; }
    /// `Wait` for display, e.g. `#12` or the note.
    public string? WaitDescription { get; init; }
    /// What got done so that this item can start, shown until the user looks at the item.
    public string? ReleasedBy { get; init; }
    /// Merge requests that close this issue, shown as part of it rather than on their own.
    public List<TodoItem> MergeRequests { get; init; } = [];
    /// Index into the project palette, assigned so that projects do not share colors.
    public int ProjectColor { get; init; }

    public static string MakeId(Guid accountId, ItemKind kind, long gitLabId) => $"{accountId.IdString()}:{kind.Key()}:{gitLabId}";

    [JsonIgnore]
    public bool IsWaiting => Wait is not null;
    [JsonIgnore]
    public string ShortReference => Names.ShortReference(Reference);
    [JsonIgnore]
    public string LocalReference => Names.LocalReference(Reference);
    [JsonIgnore]
    public string ProjectKey => $"{AccountId.IdString()}:{ProjectId}";
    [JsonIgnore]
    public string ProjectName => Names.ProjectName(Reference, AccountName);

    /// The issue's merge request once there is one, otherwise the item itself.
    [JsonIgnore]
    public TodoItem? MergeRequest => Kind == ItemKind.MergeRequest ? this : MergeRequests.FirstOrDefault();

    /// An issue with a merge request is in review, so it counts and looks like a merge request.
    [JsonIgnore]
    public ItemKind Stage => MergeRequest is null ? ItemKind.Issue : ItemKind.MergeRequest;
    [JsonIgnore]
    public bool IsDraftStage => MergeRequest?.IsDraft ?? false;
    [JsonIgnore]
    public bool HasMergeConflict => MergeRequest?.HasConflicts ?? false;

    /// `#12`, or `#12 → !34` for an issue with its merge request.
    [JsonIgnore]
    public string References => string.Join(" → ", new[] { LocalReference }.Concat(MergeRequests.Select(m => m.LocalReference)));

    /// Resolved and all resolvable review threads of the merge request, `null` when it has none.
    [JsonIgnore]
    public (int Resolved, int Total)? ReviewThreads =>
        MergeRequest is { ResolvableThreads: > 0 } mr ? (mr.ResolvableThreads - mr.UnresolvedThreads, mr.ResolvableThreads) : null;

    /// The next step, shown as a button. For an issue with a merge request it is the merge request's step.
    [JsonIgnore]
    public ItemStep? NextStep
    {
        get
        {
            if (MergeRequests.Count > 0)
            {
                return MergeRequests[0].NextStep;
            }
            return Kind switch
            {
                ItemKind.Issue => HasMergeRequest ? null : new ItemStep(ItemAction.CreateMergeRequest, this),
                _ => Role == ItemRole.Assigned && IsDraft ? new ItemStep(ItemAction.MarkReady, this) : null,
            };
        }
    }

    /// Offered in the context menu even when the button is hidden.
    [JsonIgnore]
    public List<ItemStep> Steps
    {
        get
        {
            if (MergeRequests.Count > 0)
            {
                return MergeRequests.SelectMany(m => m.Steps).ToList();
            }
            return (Kind, Role) switch
            {
                (ItemKind.Issue, _) => [new ItemStep(ItemAction.CreateMergeRequest, this)],
                (ItemKind.MergeRequest, ItemRole.Assigned) => [new ItemStep(ItemAction.MarkReady, this)],
                _ => [],
            };
        }
    }

    [JsonIgnore]
    public string? CheckoutCommand => MergeRequest?.SourceBranch is { } branch ? $"git fetch origin && git checkout {branch}" : null;
}

public static class ItemListExtensions
{
    /// Position in the work order. Only placed items that are not waiting get one.
    public static Dictionary<string, int> WorkNumbers(this IEnumerable<TodoItem> items)
    {
        var numbers = new Dictionary<string, int>();
        foreach (var item in items.Where(i => i.IsPlaced && !i.IsWaiting))
        {
            numbers[item.Id] = numbers.Count + 1;
        }
        return numbers;
    }
}

/// What the app keeps of the last refresh, to show something right away on the next start.
public sealed class Snapshot
{
    public List<TodoItem> Items { get; set; } = [];
    public DateTimeOffset? RefreshedAt { get; set; }
}

public sealed record ProjectTab(string Key, string Name, string AccountName, int Count, int Color)
{
    /// Biggest projects first.
    public static List<ProjectTab> Tabs(IEnumerable<TodoItem> items) => items
        .GroupBy(i => i.ProjectKey)
        .Select(g => new ProjectTab(g.Key, g.First().ProjectName, g.First().AccountName, g.Count(), g.First().ProjectColor))
        .OrderByDescending(t => t.Count)
        .ThenBy(t => t.Name, StringComparer.Ordinal)
        .ToList();
}

/// What the user types to point at an issue or merge request: a GitLab URL, `group/project#12`, `group/project!34`,
/// or `#12` / `!34` within the item's own project.
public sealed partial record WorkItemReference(string? Host, string? Path, ItemKind Kind, int Iid)
{
    [GeneratedRegex(@"^([\w.\-/]*)([#!])(\d+)$")]
    private static partial Regex ShortForm();

    public static WorkItemReference? Parse(string input)
    {
        var text = input.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal))
        {
            // https://gitlab.example.com/group/project/-/issues/12
            var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dash = Array.IndexOf(parts, "-");
            if (dash <= 0 || dash + 2 >= parts.Length || !int.TryParse(parts[dash + 2], out var iid))
            {
                return null;
            }
            ItemKind? kind = parts[dash + 1] switch
            {
                "issues" or "work_items" => ItemKind.Issue,
                "merge_requests" => ItemKind.MergeRequest,
                _ => null,
            };
            return kind is null ? null : new WorkItemReference(url.Host, string.Join('/', parts[..dash]), kind.Value, iid);
        }
        var match = ShortForm().Match(text);
        if (!match.Success)
        {
            return null;
        }
        var path = match.Groups[1].Value;
        return new WorkItemReference(
            null,
            path.Length == 0 ? null : path,
            match.Groups[2].Value == "#" ? ItemKind.Issue : ItemKind.MergeRequest,
            int.Parse(match.Groups[3].Value));
    }
}
