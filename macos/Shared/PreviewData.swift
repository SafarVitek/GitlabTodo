import Foundation

extension Snapshot {
    /// Sample data for the widget gallery.
    static let preview: Snapshot = {
        let accountID = UUID()
        let bug = ItemLabel(name: "bug", color: "#dc143c", textColor: "#FFFFFF")
        let security = ItemLabel(name: "security", color: "#8e44ad", textColor: "#FFFFFF")
        let customer = ItemLabel(name: "zákazník", color: "#f0ad4e", textColor: "#1F1E24")
        func item(_ iid: Int, _ project: String, _ title: String, mergeRequest: Bool = false, placed: Bool = true, labels: [ItemLabel] = []) -> TodoItem {
            TodoItem(
                id: "preview-\(project)-\(mergeRequest ? "mr" : "issue")-\(iid)",
                accountID: accountID,
                accountName: "GitLab",
                kind: mergeRequest ? .mergeRequest : .issue,
                role: .assigned,
                projectID: project.utf8.reduce(0) { $0 &* 31 &+ Int($1) },
                iid: iid,
                title: title,
                reference: "team/\(project)\(mergeRequest ? "!" : "#")\(iid)",
                webURL: URL(string: "https://gitlab.example.com")!,
                activityAt: .now.addingTimeInterval(-Double(iid) * 3600),
                hasMergeRequest: iid == 44,
                isDraft: mergeRequest,
                sourceBranch: mergeRequest ? "\(iid)-export-do-csv" : nil,
                labels: labels,
                isPlaced: placed)
        }
        func waiting(_ item: TodoItem, on blockers: [TodoItem], note: String? = nil) -> TodoItem {
            var item = item
            item.wait = Wait(itemIDs: blockers.map(\.id), note: note)
            item.waitDescription = (blockers.map(\.localReference) + [note].compactMap { $0 }).joined(separator: ", ")
            return item
        }
        func waitingOnColleague(_ item: TodoItem) -> TodoItem {
            var item = item
            let blocker = ExternalBlocker(
                accountID: accountID, projectID: 900, kind: .issue, iid: 500, title: "Nové API pro synchronizaci",
                reference: "team/platform#500", webURL: URL(string: "https://gitlab.example.com")!)
            item.wait = Wait(external: [blocker])
            item.waitDescription = blocker.shortReference
            return item
        }
        var released = item(7, "frontend", "Tabulka faktur se nepřekresluje po filtrování", labels: [bug])
        released.releasedBy = "platform#480"
        var export = item(33, "backend", "Export faktur do CSV")
        export.hasMergeRequest = true
        var exportMergeRequest = item(34, "backend", "Draft: Resolve \"Export faktur do CSV\"", mergeRequest: true)
        exportMergeRequest.resolvableThreads = 3
        exportMergeRequest.unresolvedThreads = 2
        exportMergeRequest.hasConflicts = true
        export.mergeRequests = [exportMergeRequest]
        let sso = item(12, "backend", "Opravit přihlášení přes SSO", labels: [bug, security, customer])
        let disk = item(5, "infra", "Zvětšit disk na build serveru")
        var review = item(52, "frontend", "Přepsat filtr faktur na nové API", mergeRequest: true)
        review.isDraft = false
        review.reviewerCount = 1
        review.resolvableThreads = 2
        return Snapshot(items: [
            sso,
            export,
            released,
            waiting(item(21, "backend", "Aktualizovat závislosti na .NET 10"), on: [sso, export]),
            review,
            disk,
            waiting(item(9, "infra", "Rotace certifikátů"), on: [disk]),
            waiting(item(18, "frontend", "Dark mode pro nastavení"), on: [], note: "návrh od UX"),
            waitingOnColleague(item(27, "frontend", "Zobrazit stav synchronizace")),
            item(3, "docs", "Doplnit dokumentaci API", placed: false),
            item(44, "backend", "Logovat neúspěšné webhooky", placed: false),
        ].withProjectColors(), refreshedAt: .now)
    }()
}
