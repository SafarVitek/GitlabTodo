namespace GitLabTodo.Core;

/// Sample data for `--preview`, screenshots and tests, the same as the macOS app's.
public static class PreviewData
{
    public static readonly Guid AccountId = Guid.Parse("6F1C2B9E-3D4A-4E5B-9C8D-7A6B5C4D3E2F");

    public static Snapshot Snapshot()
    {
        var bug = new ItemLabel("bug", "#dc143c", "#FFFFFF");
        var security = new ItemLabel("security", "#8e44ad", "#FFFFFF");
        var customer = new ItemLabel("zákazník", "#f0ad4e", "#1F1E24");

        TodoItem Item(int iid, string project, string title, bool mergeRequest = false, bool placed = true, List<ItemLabel>? labels = null) => new()
        {
            Id = $"preview-{project}-{(mergeRequest ? "mr" : "issue")}-{iid}",
            AccountId = AccountId,
            AccountName = "GitLab",
            Kind = mergeRequest ? ItemKind.MergeRequest : ItemKind.Issue,
            Role = ItemRole.Assigned,
            ProjectId = project.Aggregate(0, (hash, c) => unchecked(hash * 31 + c)),
            Iid = iid,
            Title = title,
            Reference = $"team/{project}{(mergeRequest ? "!" : "#")}{iid}",
            WebUrl = "https://gitlab.example.com",
            ActivityAt = DateTimeOffset.Now.AddHours(-iid),
            HasMergeRequest = iid == 44,
            IsDraft = mergeRequest,
            SourceBranch = mergeRequest ? $"{iid}-export-do-csv" : null,
            Labels = labels ?? [],
            IsPlaced = placed,
        };

        TodoItem Waiting(TodoItem item, List<TodoItem> blockers, string? note = null) => item with
        {
            Wait = new Wait { ItemIds = blockers.Select(b => b.Id).ToList(), Note = note },
            WaitDescription = string.Join(", ", blockers.Select(b => b.LocalReference).Concat(note is null ? [] : [note])),
        };

        TodoItem WaitingOnColleague(TodoItem item)
        {
            var blocker = new ExternalBlocker(
                AccountId, 900, ItemKind.Issue, 500, "Nové API pro synchronizaci", "team/platform#500", "https://gitlab.example.com");
            return item with { Wait = new Wait { External = [blocker] }, WaitDescription = blocker.ShortReference };
        }

        var released = Item(7, "frontend", "Tabulka faktur se nepřekresluje po filtrování", labels: [bug]) with { ReleasedBy = "platform#480" };
        var exportMergeRequest = Item(34, "backend", "Draft: Resolve \"Export faktur do CSV\"", mergeRequest: true) with
        {
            ResolvableThreads = 3,
            UnresolvedThreads = 2,
            HasConflicts = true,
        };
        var export = Item(33, "backend", "Export faktur do CSV") with { HasMergeRequest = true, MergeRequests = [exportMergeRequest] };
        var sso = Item(12, "backend", "Opravit přihlášení přes SSO", labels: [bug, security, customer]);
        var disk = Item(5, "infra", "Zvětšit disk na build serveru");
        var review = Item(52, "frontend", "Přepsat filtr faktur na nové API", mergeRequest: true) with
        {
            IsDraft = false,
            ReviewerCount = 1,
            ResolvableThreads = 2,
        };
        return new Snapshot
        {
            Items = new List<TodoItem>
            {
                sso,
                export,
                released,
                Waiting(Item(21, "backend", "Aktualizovat závislosti na .NET 10"), [sso, export]),
                review,
                disk,
                Waiting(Item(9, "infra", "Rotace certifikátů"), [disk]),
                Waiting(Item(18, "frontend", "Dark mode pro nastavení"), [], "návrh od UX"),
                WaitingOnColleague(Item(27, "frontend", "Zobrazit stav synchronizace")),
                Item(3, "docs", "Doplnit dokumentaci API", placed: false),
                Item(44, "backend", "Logovat neúspěšné webhooky", placed: false),
            }.WithProjectColors(),
            RefreshedAt = DateTimeOffset.Now,
        };
    }
}
