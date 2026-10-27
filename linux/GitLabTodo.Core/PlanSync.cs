using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabTodo.Core;

/// Where the plan is kept in sync between devices: a file in a repository on one of the GitLabs. `Project` is
/// `group/project`.
public sealed record SyncSettings(
    [property: JsonPropertyName("accountID")] Guid AccountId,
    [property: JsonPropertyName("project")] string Project);

/// What this device knows about the last sync, to tell its own changes from those of other devices.
public sealed class SyncState
{
    [JsonPropertyName("settings")]
    public required SyncSettings Settings { get; set; }

    [JsonPropertyName("lastCommitID")]
    public string? LastCommitId { get; set; }

    /// The plan as last synced, with this device's account IDs.
    [JsonPropertyName("base")]
    public Plan? Base { get; set; }

    [JsonPropertyName("syncedAt")]
    public DateTimeOffset? SyncedAt { get; set; }

    /// `private`, `internal` or `public`, as GitLab last reported it.
    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }
}

/// The file in the repository, the same the macOS app reads and writes.
public sealed class SyncDocument
{
    public const int CurrentFormat = 1;

    [JsonPropertyName("format")]
    public int Format { get; set; } = CurrentFormat;

    [JsonPropertyName("plan")]
    public Plan Plan { get; set; } = new();
}

/// The repository file the plan is synced through.
public interface IRemotePlanStore
{
    /// The file's content and the commit that last changed it, null while there is no file yet.
    Task<(byte[] Content, string CommitId)?> Read();

    /// Writes over the file as of `commitId`, null to create it. Throws `PlanSyncConflict` when another device wrote
    /// in between. Returns the new commit ID.
    Task<string> Write(byte[] content, string? commitId);
}

public sealed class PlanSyncConflict() : Exception("Soubor v repozitáři se mezitím změnil");

public static class PlanSync
{
    public const string FilePath = "gitlab-todo.json";

    // MARK: Account IDs

    /// The same for a GitLab on every device, unlike the random account IDs, so that plans of two devices match.
    /// A hash of the normalized URL in the shape of a UUID; the macOS app computes the same.
    public static string StableId(string baseUrl)
    {
        var normalized = GitLabAccount.NormalizedBaseUrl(baseUrl).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"gitlab-todo:{normalized}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// This device's account IDs → the stable ones.
    public static Dictionary<string, string> StableIds(IEnumerable<GitLabAccount> accounts) =>
        accounts.DistinctBy(a => a.Id).ToDictionary(a => a.Id.IdString(), a => StableId(a.BaseUrl));

    /// Every account ID in the plan replaced, e.g. this device's by the stable ones before writing to the repository.
    /// IDs of GitLabs this device does not know stay as they are.
    public static Plan Translate(Plan plan, IReadOnlyDictionary<string, string> mapping)
    {
        var text = JsonSerializer.Serialize(plan, Storage.Json);
        foreach (var (from, to) in mapping.Where(m => m.Key != m.Value))
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        return JsonSerializer.Deserialize<Plan>(text, Storage.Json)!;
    }

    // MARK: Merging

    /// Three-way merge against the plan both sides started from. A change on one side wins over no change on the
    /// other; where both changed the same thing, this device wins, except for notes, where the newer one does.
    public static Plan Merge(Plan @base, Plan local, Plan remote) => new()
    {
        Order = MergeOrder(@base.Order, local.Order, remote.Order),
        Waits = MergeEntries(@base.Waits, local.Waits, remote.Waits),
        Released = MergeEntries(@base.Released, local.Released, remote.Released),
        Tracked = MergeTracked(@base.Tracked, local.Tracked, remote.Tracked),
        Delays = MergeEntries(@base.Delays, local.Delays, remote.Delays),
        Notes = MergeEntries(@base.Notes, local.Notes, remote.Notes, (mine, theirs) =>
            // Text is never dropped for a deletion on the other side.
            mine is null || theirs is null ? mine ?? theirs : mine.UpdatedAt >= theirs.UpdatedAt ? mine : theirs),
    };

    public static Dictionary<string, T> MergeEntries<T>(
        Dictionary<string, T> @base, Dictionary<string, T> local, Dictionary<string, T> remote, Func<T?, T?, T?>? resolve = null)
        where T : class?
    {
        var merged = new Dictionary<string, T>();
        foreach (var key in @base.Keys.Union(local.Keys).Union(remote.Keys).Order(StringComparer.Ordinal))
        {
            var before = @base.GetValueOrDefault(key);
            var mine = local.GetValueOrDefault(key);
            var theirs = remote.GetValueOrDefault(key);
            var value = Same(mine, theirs) ? mine
                : Same(mine, before) ? theirs
                : Same(theirs, before) ? mine
                : resolve is null ? mine : resolve(mine, theirs);
            if (value is not null)
            {
                merged[key] = value;
            }
        }
        return merged;
    }

    /// For value entries like delays, through boxes so that a missing entry and a zero differ.
    public static Dictionary<string, int> MergeEntries(Dictionary<string, int> @base, Dictionary<string, int> local, Dictionary<string, int> remote)
    {
        static Dictionary<string, object> Boxed(Dictionary<string, int> values) => values.ToDictionary(v => v.Key, v => (object)v.Value);
        return MergeEntries(Boxed(@base), Boxed(local), Boxed(remote)).ToDictionary(v => v.Key, v => (int)v.Value);
    }

    /// The local order, without what the other device removed and with what it added, each right after the item it
    /// follows there.
    public static List<string> MergeOrder(List<string> @base, List<string> local, List<string> remote)
    {
        if (local.SequenceEqual(remote) || remote.SequenceEqual(@base))
        {
            return local.ToList();
        }
        if (local.SequenceEqual(@base))
        {
            return remote.ToList();
        }
        var removed = @base.Except(remote).ToHashSet();
        var merged = local.Where(id => !removed.Contains(id)).ToList();
        var known = @base.ToHashSet();
        for (var index = 0; index < remote.Count; index++)
        {
            var id = remote[index];
            if (known.Contains(id) || merged.Contains(id))
            {
                continue;
            }
            var previous = remote.Take(index).LastOrDefault(merged.Contains);
            merged.Insert(previous is null ? 0 : merged.IndexOf(previous) + 1, id);
        }
        return merged;
    }

    private static List<ExternalBlocker> MergeTracked(List<ExternalBlocker> @base, List<ExternalBlocker> local, List<ExternalBlocker> remote)
    {
        static Dictionary<string, ExternalBlocker> Keyed(List<ExternalBlocker> blockers) => blockers.DistinctBy(b => b.Id).ToDictionary(b => b.Id);
        var merged = MergeEntries(Keyed(@base), Keyed(local), Keyed(remote));
        var order = MergeOrder(@base.Select(b => b.Id).ToList(), local.Select(b => b.Id).ToList(), remote.Select(b => b.Id).ToList());
        return order.Where(merged.ContainsKey).Select(id => merged[id])
            .Concat(merged.Values.Where(b => !order.Contains(b.Id)).OrderBy(b => b.Id, StringComparer.Ordinal))
            .ToList();
    }

    /// `group/project`, also from a pasted URL of the repository.
    public static string ProjectPath(string input)
    {
        var path = input.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal))
        {
            path = url.AbsolutePath;
        }
        var dash = path.IndexOf("/-/", StringComparison.Ordinal);
        if (dash >= 0)
        {
            path = path[..dash];
        }
        if (path.EndsWith(".git", StringComparison.Ordinal))
        {
            path = path[..^4];
        }
        return path.Trim('/');
    }

    /// A copy that shares nothing with `plan`, which keeps changing.
    public static Plan Copy(Plan plan) => JsonSerializer.Deserialize<Plan>(JsonSerializer.Serialize(plan, Storage.Json), Storage.Json)!;

    /// Equal content, whatever order the entries were added in.
    public static bool Same(Plan a, Plan b) => Json(Sorted(a)) == Json(Sorted(b));

    private static bool Same<T>(T? a, T? b) => Json(a) == Json(b);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Storage.Json);

    /// Entries in key order, for comparing and for a stable file in the repository.
    public static Plan Sorted(Plan plan) => new()
    {
        Order = plan.Order.ToList(),
        Waits = plan.Waits.OrderBy(w => w.Key, StringComparer.Ordinal).ToDictionary(),
        Released = plan.Released.OrderBy(r => r.Key, StringComparer.Ordinal).ToDictionary(),
        Tracked = plan.Tracked.ToList(),
        Delays = plan.Delays.OrderBy(d => d.Key, StringComparer.Ordinal).ToDictionary(),
        Notes = plan.Notes.OrderBy(n => n.Key, StringComparer.Ordinal).ToDictionary(),
    };

    // MARK: Syncing

    /// Brings the plan and the repository file to the same state. Returns the plan to keep on this device and the
    /// state for the next sync. Reads again and merges anew when another device wrote in between.
    public static async Task<(Plan Plan, SyncState State)> Sync(Plan local, SyncState state, IRemotePlanStore store, IEnumerable<GitLabAccount> accounts)
    {
        var toStable = StableIds(accounts);
        var toLocal = toStable.GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.First().Key);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SyncOnce(local, state, store, toStable, toLocal);
            }
            catch (PlanSyncConflict) when (attempt < 2)
            {
            }
        }
    }

    private static async Task<(Plan Plan, SyncState State)> SyncOnce(
        Plan local, SyncState state, IRemotePlanStore store, Dictionary<string, string> toStable, Dictionary<string, string> toLocal)
    {
        var next = new SyncState
        {
            Settings = state.Settings,
            LastCommitId = state.LastCommitId,
            Base = state.Base,
            SyncedAt = state.SyncedAt,
            Visibility = state.Visibility,
        };
        async Task Write(Plan plan, string? commitId)
        {
            var document = new SyncDocument { Plan = Sorted(Translate(plan, toStable)) };
            next.LastCommitId = await store.Write(JsonSerializer.SerializeToUtf8Bytes(document, Storage.Json), commitId);
            next.Base = plan;
        }

        if (await store.Read() is not { } file)
        {
            await Write(local, null);
            return (local, next);
        }
        SyncDocument document;
        try
        {
            document = JsonSerializer.Deserialize<SyncDocument>(file.Content, Storage.Json) ?? throw new InvalidDataException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new GitLabException("Soubor gitlab-todo.json v repozitáři nejde přečíst.");
        }
        if (document.Format > SyncDocument.CurrentFormat)
        {
            throw new GitLabException("Data v repozitáři uložila novější verze aplikace, aktualizuj ji.");
        }

        if (file.CommitId == state.LastCommitId && state.Base is { } @base)
        {
            // Nothing new from other devices.
            if (!Same(local, @base))
            {
                await Write(local, file.CommitId);
            }
            return (local, next);
        }
        var remote = Translate(document.Plan, toLocal);
        var merged = Merge(state.Base ?? new Plan(), local, remote);
        if (Same(merged, remote))
        {
            next.LastCommitId = file.CommitId;
            next.Base = merged;
        }
        else
        {
            await Write(merged, file.CommitId);
        }
        return (merged, next);
    }
}

/// The plan file in a GitLab repository, written with commits on its default branch.
public sealed class GitLabPlanStore(GitLabClient client, long projectId, string branch) : IRemotePlanStore
{
    public async Task<(byte[] Content, string CommitId)?> Read()
    {
        if (await client.RepositoryFile(projectId, PlanSync.FilePath, branch) is not { } file)
        {
            return null;
        }
        try
        {
            return (Convert.FromBase64String(file.Content), file.LastCommitId);
        }
        catch (FormatException)
        {
            throw new GitLabException("Soubor gitlab-todo.json v repozitáři nejde přečíst.");
        }
    }

    public async Task<string> Write(byte[] content, string? commitId)
    {
        try
        {
            await client.WriteRepositoryFile(projectId, PlanSync.FilePath, branch, Encoding.UTF8.GetString(content), "GitLab Todo: synchronizace", commitId);
        }
        catch (GitLabException exception) when (exception.Message.Contains("HTTP 400", StringComparison.Ordinal)
            && (exception.Message.Contains("changed", StringComparison.Ordinal) || exception.Message.Contains("already exists", StringComparison.Ordinal)))
        {
            throw new PlanSyncConflict();
        }
        return (await client.RepositoryFile(projectId, PlanSync.FilePath, branch))?.LastCommitId
            ?? throw new GitLabException("Soubor gitlab-todo.json v repozitáři nejde přečíst.");
    }
}
