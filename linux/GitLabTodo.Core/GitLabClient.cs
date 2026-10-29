using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GitLabTodo.Core;

public sealed class GitLabException(string message) : Exception(message)
{
    public static GitLabException InvalidUrl => new("Neplatná URL GitLabu");
    public static GitLabException MissingToken => new("Chybí access token");
    public static GitLabException TokenLoading => new("Token se ještě načítá z klíčenky");
    public static GitLabException Unauthorized => new("Neplatný nebo expirovaný token (401)");
    public static GitLabException Forbidden => new("Token na to nemá oprávnění (403), akce potřebují scope api");
    public static GitLabException NotFound => new("Nenalezeno (404)");
    public static GitLabException UnknownUser(string username) => new($"Uživatel @{username} neexistuje");
    public static GitLabException UnknownGitLab(string host) => new($"GitLab {host} není v nastavení připojený.");
    public static GitLabException Closed => new("Tohle issue nebo MR je už zavřené, není na co čekat.");
    public static GitLabException Cycle => new("Tahle položka už čeká na tu, ke které ji přidáváš.");
}

// MARK: API models

public sealed record GLUser(long Id, string Username);

public sealed record GLReferences(string? Short, string Full);

public sealed record GLTokenInfo(List<string> Scopes);

public sealed record GLIssueReference(long Id);

public sealed record GLBranch(string Name);

public sealed record GLDiscussionNote(bool? Resolvable, bool? Resolved);

public sealed record GLDiscussion(List<GLDiscussionNote> Notes)
{
    public bool IsResolvable => Notes.Any(n => n.Resolvable == true);

    /// A thread is open while any of its resolvable notes is not resolved, the way GitLab counts them.
    public bool IsUnresolved => Notes.Any(n => n.Resolvable == true && n.Resolved != true);
}

/// Labels come as objects with `with_labels_details`, as plain names elsewhere (e.g. in to-dos).
[JsonConverter(typeof(GLLabelConverter))]
public sealed record GLLabel(string Name, string? Color, string? TextColor)
{
    // GitLab's default label colors.
    public ItemLabel ItemLabel => new(Name, Color ?? "#6699cc", TextColor ?? "#FFFFFF");
}

public sealed class GLLabelConverter : JsonConverter<GLLabel>
{
    public override GLLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new GLLabel(reader.GetString()!, null, null);
        }
        var node = JsonNode.Parse(ref reader)!.AsObject();
        return new GLLabel(node["name"]!.GetValue<string>(), node["color"]?.GetValue<string>(), node["text_color"]?.GetValue<string>());
    }

    public override void Write(Utf8JsonWriter writer, GLLabel value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

public sealed record GLIssue(
    long Id, int Iid, int ProjectId, string Title, string WebUrl, DateTimeOffset UpdatedAt, string? DueDate,
    bool? Confidential, int? MergeRequestsCount, GLReferences? References, List<GLLabel>? Labels);

public sealed record GLMergeRequest(
    long Id, int Iid, int ProjectId, string Title, string? State, string WebUrl, DateTimeOffset UpdatedAt,
    bool? Draft, bool? WorkInProgress, string SourceBranch, List<GLUser>? Reviewers, bool? HasConflicts,
    string? DetailedMergeStatus, GLReferences? References, List<GLLabel>? Labels)
{
    public bool IsDraft => Draft ?? WorkInProgress ?? false;
}

public sealed record GLProject(long Id, string PathWithNamespace, string? DefaultBranch, string? Visibility);

/// `Content` is Base64.
public sealed record GLRepositoryFile(string Content, string LastCommitId);

public sealed record GLFileWrite(string FilePath);

public sealed record GLTodo(string ActionName, GLMergeRequest? Target, DateTimeOffset CreatedAt);

/// The fields issues and merge requests share.
public sealed record GLWorkItem(long Id, int Iid, int ProjectId, string Title, string State, string WebUrl, GLReferences? References)
{
    public bool IsOpen => State == "opened";
}

/// A search hit an item can be set to wait on.
public sealed record BlockerCandidate(Guid AccountId, ItemKind Kind, GLWorkItem WorkItem, string? ListedItemId)
{
    /// Name of the GitLab when it is not the one of the item searched for.
    public string? GitLabName { get; init; }

    public string Id => Blocker.Id;

    public ExternalBlocker Blocker => new(
        AccountId, WorkItem.ProjectId, Kind, WorkItem.Iid, WorkItem.Title,
        WorkItem.References?.Full ?? $"{(Kind == ItemKind.Issue ? "#" : "!")}{WorkItem.Iid}", WorkItem.WebUrl);
}

public sealed partial class GitLabClient(Uri apiBase, string token)
{
    private const int MaxPages = 10;
    private static readonly HashSet<string> MentionActions = ["mentioned", "directly_addressed"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // MARK: Reading

    public Task<GLUser> CurrentUser() => Get<GLUser>("user");

    public async Task<long> UserId(string username)
    {
        var users = await Get<List<GLUser>>("users", [("username", username)]);
        return users.FirstOrDefault()?.Id ?? throw GitLabException.UnknownUser(username);
    }

    public async Task<List<string>> TokenScopes() => (await Get<GLTokenInfo>("personal_access_tokens/self")).Scopes;

    /// Assigned issues, assigned merge requests and merge requests with a pending mention.
    public async Task<List<TodoItem>> TodoItems(GitLabAccount account)
    {
        List<(string, string)> opened = [("scope", "assigned_to_me"), ("state", "opened"), ("with_labels_details", "true")];
        var issuesTask = GetAll<GLIssue>("issues", opened);
        var mergeRequestsTask = GetAll<GLMergeRequest>("merge_requests", opened);
        // GitLab cannot filter merge requests by mention, only its to-do list knows about them.
        var todosTask = GetAll<GLTodo>("todos", [("state", "pending"), ("type", "MergeRequest")]);

        var issueItems = (await issuesTask).Select(issue => Item(issue, account)).ToList();
        var assigned = await mergeRequestsTask;
        var mergeRequestItems = assigned.Select(mr => Item(mr, ItemRole.Assigned, mr.UpdatedAt, account)).ToList();

        var seen = assigned.Select(mr => mr.Id).ToHashSet();
        foreach (var todo in (await todosTask).Where(t => MentionActions.Contains(t.ActionName)).OrderByDescending(t => t.CreatedAt))
        {
            if (todo.Target is { State: "opened" } target && seen.Add(target.Id))
            {
                mergeRequestItems.Add(Item(target, ItemRole.Mentioned, todo.CreatedAt, account));
            }
        }

        var details = await Task.WhenAll(mergeRequestItems.Select(Details));
        var byId = details.ToDictionary(d => d.Id);
        mergeRequestItems = mergeRequestItems
            .Select(mr => mr with { ResolvableThreads = byId[mr.Id].Resolvable, UnresolvedThreads = byId[mr.Id].Unresolved })
            .ToList();
        return Group(issueItems, mergeRequestItems, byId.ToDictionary(d => d.Key, d => d.Value.ClosedIssueIds), account.Id);
    }

    private sealed record MergeRequestDetails(string Id, List<long> ClosedIssueIds, int Resolvable, int Unresolved);

    /// Issues a merge request closes and its review threads. Best effort: a failed lookup leaves them empty,
    /// grouping then falls back to the branch name.
    private async Task<MergeRequestDetails> Details(TodoItem mergeRequest)
    {
        var path = $"projects/{mergeRequest.ProjectId}/merge_requests/{mergeRequest.Iid}";
        var issuesTask = Try(() => Get<List<GLIssueReference>>($"{path}/closes_issues"));
        // The list's `blocking_discussions_resolved` stays true unless the project requires resolved threads.
        var discussionsTask = Try(() => GetAll<GLDiscussion>($"{path}/discussions", []));
        var threads = (await discussionsTask ?? []).Where(d => d.IsResolvable).ToList();
        return new MergeRequestDetails(
            mergeRequest.Id, (await issuesTask ?? []).Select(i => i.Id).ToList(), threads.Count, threads.Count(t => t.IsUnresolved));
    }

    private static async Task<T?> Try<T>(Func<Task<T>> request) where T : class
    {
        try
        {
            return await request();
        }
        catch (Exception exception) when (exception is GitLabException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// Puts each merge request under the issue it closes, so an issue and its MR show up as one item.
    public static List<TodoItem> Group(List<TodoItem> issues, List<TodoItem> mergeRequests, Dictionary<string, List<long>> closedIssues, Guid accountId)
    {
        var grouped = issues.ToList();
        var standalone = new List<TodoItem>();
        foreach (var mergeRequest in mergeRequests)
        {
            var closed = (closedIssues.GetValueOrDefault(mergeRequest.Id) ?? [])
                .Select(id => TodoItem.MakeId(accountId, ItemKind.Issue, id)).ToHashSet();
            var branchIid = mergeRequest.SourceBranch is { } branch ? IssueIidFromBranch(branch) : null;
            var index = grouped.FindIndex(i => closed.Contains(i.Id));
            if (index < 0)
            {
                index = grouped.FindIndex(i => i.ProjectId == mergeRequest.ProjectId && i.Iid == branchIid);
            }
            if (index >= 0)
            {
                grouped[index] = grouped[index] with { MergeRequests = [.. grouped[index].MergeRequests, mergeRequest] };
            }
            else
            {
                standalone.Add(mergeRequest);
            }
        }
        return [.. grouped, .. standalone];
    }

    [GeneratedRegex(@"^(\d+)-")]
    private static partial Regex BranchPrefix();

    /// GitLab names branches created from an issue `<iid>-<title>`.
    public static int? IssueIidFromBranch(string branch) =>
        BranchPrefix().Match(branch) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// Any issue or merge request, assigned to the user or not. `project` is a numeric ID or a `group/project` path.
    public Task<GLWorkItem> WorkItem(string project, ItemKind kind, int iid) =>
        Get<GLWorkItem>($"projects/{Uri.EscapeDataString(project)}/{(kind == ItemKind.Issue ? "issues" : "merge_requests")}/{iid}");

    /// Open issues and merge requests the user can see: by number, the given project first, or by words in the title.
    /// `onlyInProject` keeps the search inside that project; without a project it searches the whole GitLab.
    public async Task<List<(ItemKind Kind, GLWorkItem Item)>> SearchWorkItems(string text, int? projectId, bool onlyInProject = false)
    {
        List<(string, string)> opened = [("state", "opened")];
        List<(string, string)> everywhere = [("scope", "all"), ("per_page", "10")];
        if (int.TryParse(text.Trim('#', '!', ' '), out var iid))
        {
            List<(string, string)> number = [("iids[]", iid.ToString(CultureInfo.InvariantCulture))];
            var local = new List<(ItemKind, GLWorkItem)>();
            if (projectId is { } project)
            {
                var issues = Get<List<GLWorkItem>>($"projects/{project}/issues", [.. opened, .. number]);
                var mergeRequests = Get<List<GLWorkItem>>($"projects/{project}/merge_requests", [.. opened, .. number]);
                local.AddRange((await issues).Select(i => (ItemKind.Issue, i)));
                local.AddRange((await mergeRequests).Select(m => (ItemKind.MergeRequest, m)));
            }
            if (onlyInProject)
            {
                return local;
            }
            var elsewhere = await Get<List<GLWorkItem>>("issues", [.. opened, .. number, .. everywhere]);
            return [.. local, .. elsewhere.Where(i => i.ProjectId != projectId).Select(i => (ItemKind.Issue, i))];
        }
        List<(string, string)> words = [.. opened, .. everywhere, ("search", text), ("in", "title"), ("order_by", "updated_at")];
        var scope = onlyInProject && projectId is { } scoped ? $"projects/{scoped}/" : "";
        var issueHits = Get<List<GLWorkItem>>($"{scope}issues", words);
        var mergeRequestHits = Get<List<GLWorkItem>>($"{scope}merge_requests", words);
        return [.. (await issueHits).Select(i => (ItemKind.Issue, i)), .. (await mergeRequestHits).Take(5).Select(m => (ItemKind.MergeRequest, m))];
    }

    // MARK: Repository for syncing

    /// `path` is `group/project` or a numeric ID.
    public Task<GLProject> Project(string path) => Get<GLProject>($"projects/{Uri.EscapeDataString(path)}");

    /// A new private repository in the user's namespace, with a README so that it has a default branch.
    public Task<GLProject> CreatePrivateProject(string name) =>
        Send<GLProject>(HttpMethod.Post, "projects", new { name, visibility = "private", initialize_with_readme = true });

    /// The file on the branch or commit `reference`, null when it is not there yet.
    public async Task<GLRepositoryFile?> RepositoryFile(long projectId, string path, string reference)
    {
        try
        {
            return await Get<GLRepositoryFile>($"projects/{projectId}/repository/files/{Uri.EscapeDataString(path)}", [("ref", reference)]);
        }
        catch (GitLabException exception) when (exception.Message == GitLabException.NotFound.Message)
        {
            return null;
        }
    }

    /// Creates the file, or updates it when `lastCommitId` is given; GitLab refuses the update when the file changed
    /// after that commit.
    public Task WriteRepositoryFile(long projectId, string path, string branch, string content, string message, string? lastCommitId)
    {
        var url = $"projects/{projectId}/repository/files/{Uri.EscapeDataString(path)}";
        return lastCommitId is null
            ? Send<GLFileWrite>(HttpMethod.Post, url, new { branch, content, commit_message = message, encoding = "text" })
            : Send<GLFileWrite>(HttpMethod.Put, url, new { branch, content, commit_message = message, encoding = "text", last_commit_id = lastCommitId });
    }

    // MARK: Actions

    /// Does what the "Create merge request" button on an issue does, reusing a branch or MR left over from an earlier attempt.
    public async Task<GLMergeRequest> CreateMergeRequest(TodoItem issue, string branch, string targetBranch, long assigneeId)
    {
        var project = $"projects/{issue.ProjectId}";
        var existing = await Get<List<GLMergeRequest>>($"{project}/merge_requests", [("source_branch", branch), ("state", "opened")]);
        if (existing.FirstOrDefault() is { } mergeRequest)
        {
            return mergeRequest;
        }
        try
        {
            await Get<GLBranch>($"{project}/repository/branches/{Uri.EscapeDataString(branch)}");
        }
        catch (GitLabException exception) when (exception.Message == GitLabException.NotFound.Message)
        {
            await Send<GLBranch>(HttpMethod.Post, $"{project}/repository/branches", new { branch, @ref = targetBranch });
        }
        return await Send<GLMergeRequest>(HttpMethod.Post, $"{project}/merge_requests", new
        {
            source_branch = branch,
            target_branch = targetBranch,
            title = $"Draft: Resolve \"{issue.Title}\"",
            description = $"Closes #{issue.Iid}",
            assignee_id = assigneeId,
        });
    }

    public async Task<GLMergeRequest> MarkReady(int projectId, int iid, long? reviewerId)
    {
        var path = $"projects/{projectId}/merge_requests/{iid}";
        var current = await Get<GLMergeRequest>(path);
        var reviewerIds = (current.Reviewers ?? []).Select(r => r.Id).ToList();
        var update = new Dictionary<string, object>();
        var title = TitleWithoutDraftPrefix(current.Title);
        if (title != current.Title)
        {
            update["title"] = title;
        }
        if (reviewerId is { } reviewer && !reviewerIds.Contains(reviewer))
        {
            update["reviewer_ids"] = reviewerIds.Append(reviewer).ToList();
        }
        return update.Count == 0 ? current : await Send<GLMergeRequest>(HttpMethod.Put, path, update);
    }

    [GeneratedRegex(@"[^a-z0-9_-]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex Dashes();

    [GeneratedRegex(@"-[^-]*$")]
    private static partial Regex LastWord();

    /// Lowercase Latin letters without their accents, like Rails' transliteration. A table rather than Unicode
    /// normalization, which needs ICU the app runs without.
    private static readonly Dictionary<char, string> Latin = new (string Letters, string Plain)[]
    {
        ("àáâãäåāăą", "a"), ("çćĉċč", "c"), ("ďđð", "d"), ("èéêëēĕėęě", "e"), ("ĝğġģ", "g"), ("ĥħ", "h"),
        ("ìíîïĩīĭįı", "i"), ("ĵ", "j"), ("ķĸ", "k"), ("ĺļľŀł", "l"), ("ñńņňŉŋ", "n"), ("òóôõöøōŏő", "o"),
        ("ŕŗř", "r"), ("śŝşšș", "s"), ("ţťŧț", "t"), ("ùúûüũūŭůűų", "u"), ("ŵ", "w"), ("ýÿŷ", "y"), ("źżž", "z"),
        ("æ", "ae"), ("œ", "oe"), ("ß", "ss"), ("þ", "th"), ("ĳ", "ij"),
    }.SelectMany(group => group.Letters.Select(letter => (letter, group.Plain))).ToDictionary(p => p.letter, p => p.Plain);

    /// Mirrors GitLab's `Issue.to_branch_name`: `<iid>-<parameterized title>`, at most 100 characters.
    public static string BranchName(TodoItem issue)
    {
        if (issue.IsConfidential)
        {
            return $"{issue.Iid}-confidential-issue";
        }
        var folded = string.Concat(issue.Title.ToLowerInvariant().Select(c => Latin.GetValueOrDefault(c) ?? c.ToString()));
        var slug = Dashes().Replace(NonSlug().Replace(folded, "-"), "-").Trim('-');
        var name = slug.Length == 0 ? $"{issue.Iid}" : $"{issue.Iid}-{slug}";
        if (name.Length > 100)
        {
            // Like GitLab, drop the word cut in half by the truncation.
            name = LastWord().Replace(name[..100], "");
        }
        return name;
    }

    [GeneratedRegex(@"^(?:\s*(?:\[draft\]|\(draft\)|draft:|draft\s-\s))+\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DraftPrefix();

    /// Removes the prefixes GitLab treats as draft markers.
    public static string TitleWithoutDraftPrefix(string title) => DraftPrefix().Replace(title, "");

    // MARK: Mapping

    private static TodoItem Item(GLIssue issue, GitLabAccount account) => new()
    {
        Id = TodoItem.MakeId(account.Id, ItemKind.Issue, issue.Id),
        AccountId = account.Id,
        AccountName = account.Name,
        Kind = ItemKind.Issue,
        Role = ItemRole.Assigned,
        ProjectId = issue.ProjectId,
        Iid = issue.Iid,
        Title = issue.Title,
        Reference = issue.References?.Full ?? $"#{issue.Iid}",
        WebUrl = issue.WebUrl,
        ActivityAt = issue.UpdatedAt,
        DueDate = DateTimeOffset.TryParse(issue.DueDate, CultureInfo.InvariantCulture, out var due) ? due : null,
        IsConfidential = issue.Confidential ?? false,
        HasMergeRequest = (issue.MergeRequestsCount ?? 0) > 0,
        Labels = (issue.Labels ?? []).Select(l => l.ItemLabel).ToList(),
    };

    private static TodoItem Item(GLMergeRequest mergeRequest, ItemRole role, DateTimeOffset activityAt, GitLabAccount account) => new()
    {
        Id = TodoItem.MakeId(account.Id, ItemKind.MergeRequest, mergeRequest.Id),
        AccountId = account.Id,
        AccountName = account.Name,
        Kind = ItemKind.MergeRequest,
        Role = role,
        ProjectId = mergeRequest.ProjectId,
        Iid = mergeRequest.Iid,
        Title = mergeRequest.Title,
        Reference = mergeRequest.References?.Full ?? $"!{mergeRequest.Iid}",
        WebUrl = mergeRequest.WebUrl,
        ActivityAt = activityAt,
        IsDraft = mergeRequest.IsDraft,
        SourceBranch = mergeRequest.SourceBranch,
        ReviewerCount = mergeRequest.Reviewers?.Count ?? 0,
        // A draft reports `draft_status` even with a conflict, `has_conflicts` is the reliable signal.
        HasConflicts = mergeRequest.HasConflicts == true || mergeRequest.DetailedMergeStatus == "conflict",
        Labels = (mergeRequest.Labels ?? []).Select(l => l.ItemLabel).ToList(),
    };

    // MARK: HTTP

    private Task<T> Get<T>(string path, List<(string Name, string Value)>? query = null) =>
        Fetch<T>(Request(HttpMethod.Get, path, query ?? []));

    private async Task<List<T>> GetAll<T>(string path, List<(string Name, string Value)> query)
    {
        var all = new List<T>();
        var page = 1;
        for (var count = 0; count < MaxPages; count++)
        {
            using var response = await Perform(Request(HttpMethod.Get, path, [.. query, ("per_page", "100"), ("page", page.ToString(CultureInfo.InvariantCulture))]));
            all.AddRange(await Decode<List<T>>(response));
            if (!response.Headers.TryGetValues("x-next-page", out var next) || !int.TryParse(next.FirstOrDefault(), out page))
            {
                break;
            }
        }
        return all;
    }

    private Task<T> Send<T>(HttpMethod method, string path, object body)
    {
        var request = Request(method, path, []);
        request.Content = JsonContent.Create(body);
        return Fetch<T>(request);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, List<(string Name, string Value)> query)
    {
        var url = new StringBuilder(apiBase.AbsoluteUri.TrimEnd('/')).Append('/').Append(path);
        if (query.Count > 0)
        {
            url.Append('?').AppendJoin('&', query.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
        }
        var request = new HttpRequestMessage(method, url.ToString());
        request.Headers.Add("PRIVATE-TOKEN", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<T> Fetch<T>(HttpRequestMessage request)
    {
        using var response = await Perform(request);
        return await Decode<T>(response);
    }

    private static async Task<HttpResponseMessage> Perform(HttpRequestMessage request)
    {
        var response = await Http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        var body = await response.Content.ReadAsStringAsync();
        response.Dispose();
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => GitLabException.Unauthorized,
            HttpStatusCode.Forbidden => GitLabException.Forbidden,
            HttpStatusCode.NotFound => GitLabException.NotFound,
            var code => new GitLabException($"GitLab vrátil HTTP {(int)code}" + (ErrorMessage(body) is { } message ? $": {message}" : "")),
        };
    }

    private static async Task<T> Decode<T>(HttpResponseMessage response)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<T>(Json))!;
        }
        catch (JsonException exception)
        {
            throw new GitLabException($"Nečekaný formát odpovědi: {exception.Message}");
        }
    }

    /// GitLab returns `message` as a string, a list or a field → errors map.
    private static string? ErrorMessage(string body)
    {
        try
        {
            var json = JsonNode.Parse(body) as JsonObject;
            var messages = Flatten(json?["message"] ?? json?["error"]).ToList();
            return messages.Count == 0 ? null : string.Join(", ", messages);
        }
        catch (JsonException)
        {
            return null;
        }

        static IEnumerable<string> Flatten(JsonNode? node) => node switch
        {
            JsonValue value => [value.ToString()],
            JsonArray list => list.SelectMany(Flatten),
            JsonObject map => map.SelectMany(pair => Flatten(pair.Value).Select(m => $"{pair.Key} {m}")),
            _ => [],
        };
    }
}
