import AppKit
import Observation
import WidgetKit

/// The user's own ordering of the work, kept locally.
struct Plan: Codable, Sendable {
    /// Item IDs in the order the user wants to work on them.
    var order: [String] = []
    var waits: [String: Wait] = [:]
    /// Items whose wait ended because what they waited on got done, until the user looks at them.
    var released: [String: Release] = [:]
    /// Issues and merge requests of other people added to the timeline with „+“.
    var tracked: [ExternalBlocker] = []
    /// Empty timeline steps the user put before an item, to start it later.
    var delays: [String: Int] = [:]
}

extension Plan {
    /// Plans saved by older versions lack the newer keys.
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        order = try container.decodeIfPresent([String].self, forKey: .order) ?? []
        waits = try container.decodeIfPresent([String: Wait].self, forKey: .waits) ?? [:]
        released = try container.decodeIfPresent([String: Release].self, forKey: .released) ?? [:]
        tracked = try container.decodeIfPresent([ExternalBlocker].self, forKey: .tracked) ?? []
        delays = try container.decodeIfPresent([String: Int].self, forKey: .delays) ?? [:]
    }
}

/// What got done so that an item can start.
struct Release: Codable, Hashable, Sendable {
    var date: Date
    var finished: [String]
}

@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()
    static let refreshIntervals: [TimeInterval] = [60, 120, 300, 600, 900, 1800]

    struct FocusRequest: Equatable {
        var itemID: String?
        /// Puts the cursor into the item's blocker search.
        var searchesBlocker = false
        var serial = 0
    }

    private enum Keys {
        static let accounts = "accounts"
        static let refreshInterval = "refreshInterval"
    }

    private(set) var accounts: [GitLabAccount]
    /// All items in work order: placed ones first, then the new ones.
    private(set) var items: [TodoItem] = []
    private(set) var accountErrors: [UUID: String] = [:]
    private(set) var lastRefresh: Date?
    private(set) var isRefreshing = false
    private(set) var refreshInterval: TimeInterval
    private(set) var runningActions: Set<String> = []
    /// Asks the main window to show an item, e.g. after a click in the widget.
    private(set) var focusRequest = FocusRequest()
    /// Other people's issues and merge requests in the timeline: added with „+“ or waited on.
    private(set) var timelineExternal: [ExternalBlocker] = []
    /// Place of each item, own or not, in the work order.
    private(set) var orderPositions: [String: Int] = [:]
    private(set) var timelineDelays: [String: Int] = [:]

    @ObservationIgnored private let isPreview: Bool
    @ObservationIgnored private var plan: Plan
    @ObservationIgnored private var tokens: [UUID: String] = [:]
    @ObservationIgnored private var userIDs: [UUID: Int] = [:]
    @ObservationIgnored private var fetchedItems: [UUID: [TodoItem]] = [:]
    @ObservationIgnored private var refreshPending = false
    @ObservationIgnored private var publishedHash: Int?
    @ObservationIgnored private var timerTask: Task<Void, Never>?

    private init() {
        isPreview = false
        let defaults = UserDefaults.standard
        accounts = defaults.data(forKey: Keys.accounts).flatMap { try? JSONDecoder().decode([GitLabAccount].self, from: $0) } ?? []
        let interval = defaults.double(forKey: Keys.refreshInterval)
        refreshInterval = interval > 0 ? interval : 300
        plan = SharedStore.load(Plan.self, from: SharedStore.planURL) ?? Plan()
        for account in accounts {
            tokens[account.id] = Keychain.token(for: account.id)
        }

        // Start from the last known state, so the list is not empty until the first refresh finishes.
        if let snapshot = SharedStore.load(Snapshot.self, from: SharedStore.snapshotURL) {
            fetchedItems = Dictionary(grouping: snapshot.items, by: \.accountID)
            lastRefresh = snapshot.refreshedAt
        }
        rebuildItems()

        observeWake()
        restartTimer()
    }

    /// In-memory model over sample data for rendering views and tests; touches neither files, keychain nor network.
    init(preview snapshot: Snapshot) {
        isPreview = true
        accounts = Set(snapshot.items.map(\.accountID)).map { GitLabAccount(id: $0, name: "GitLab", baseURL: "https://gitlab.example.com") }
        refreshInterval = 300
        plan = Plan(
            order: snapshot.items.filter(\.isPlaced).map(\.id),
            waits: Dictionary(snapshot.items.compactMap { item in item.wait.map { (item.id, $0) } }, uniquingKeysWith: { first, _ in first }),
            released: Dictionary(snapshot.items.compactMap { item in item.releasedBy.map { (item.id, Release(date: .now, finished: [$0])) } }, uniquingKeysWith: { first, _ in first }))
        fetchedItems = Dictionary(grouping: snapshot.items, by: \.accountID)
        lastRefresh = snapshot.refreshedAt
        rebuildItems()
    }

    func token(for accountID: UUID) -> String? {
        tokens[accountID]
    }

    func requestFocus(on itemID: String?) {
        // A merge request shown as part of its issue is selected through the issue.
        let topLevelID = itemID.flatMap { id in items.first { $0.id == id || $0.mergeRequests.contains { $0.id == id } }?.id }
        focusRequest = FocusRequest(itemID: topLevelID ?? itemID, serial: focusRequest.serial + 1)
    }

    func requestBlockerSearch(for itemID: String) {
        focusRequest = FocusRequest(itemID: itemID, searchesBlocker: true, serial: focusRequest.serial + 1)
    }

    /// Finds top-level items as well as merge requests grouped under their issue.
    func item(withID id: String) -> TodoItem? {
        for item in items {
            if item.id == id {
                return item
            }
            if let mergeRequest = item.mergeRequests.first(where: { $0.id == id }) {
                return mergeRequest
            }
        }
        return nil
    }

    // MARK: Refreshing

    func refresh() async {
        guard !isPreview else { return }
        guard !isRefreshing else {
            refreshPending = true
            return
        }
        isRefreshing = true
        repeat {
            refreshPending = false
            await fetchAll()
        } while refreshPending
        isRefreshing = false
    }

    private func fetchAll() async {
        let jobs = accounts.map { account in (account, Result { try client(for: account) }) }
        let results = await withTaskGroup(of: (UUID, Result<[TodoItem], any Error>).self) { group in
            for (account, client) in jobs {
                group.addTask {
                    do {
                        return (account.id, .success(try await client.get().todoItems(for: account)))
                    } catch {
                        return (account.id, .failure(error))
                    }
                }
            }
            var results: [(UUID, Result<[TodoItem], any Error>)] = []
            for await result in group {
                results.append(result)
            }
            return results
        }

        for (accountID, result) in results {
            switch result {
            case .success(let fetched):
                let previous = fetchedItems[accountID] ?? []
                fetchedItems[accountID] = fetched
                accountErrors[accountID] = nil
                // Merge requests grouped under their issue are still there, they just moved.
                prunePlan(accountID: accountID, keeping: Set(fetched.flatMap { [$0.id] + $0.mergeRequests.map(\.id) }), previous: previous)
            case .failure(let error):
                // Keep the last known items, a VPN hiccup should not empty the list.
                accountErrors[accountID] = error.localizedDescription
            }
        }
        await refreshExternalBlockers()
        lastRefresh = .now
        rebuildItems()
    }

    /// Checks the issues and merge requests of other people that items wait on. Closed and merged ones end the wait,
    /// the ones that cannot be checked right now stay.
    private func refreshExternalBlockers() async {
        let blockers = Array(Set(plan.waits.values.flatMap(\.external) + plan.tracked))
        let jobs = blockers.compactMap { blocker in
            accounts.first { $0.id == blocker.accountID }.flatMap { try? client(for: $0) }.map { (blocker, $0) }
        }
        guard !jobs.isEmpty else { return }
        let current = await withTaskGroup(of: (String, GLWorkItem?).self) { group in
            for (blocker, client) in jobs {
                group.addTask {
                    (blocker.id, try? await client.workItem(project: String(blocker.projectID), kind: blocker.kind, iid: blocker.iid))
                }
            }
            var current: [String: GLWorkItem] = [:]
            for await (blockerID, workItem) in group {
                current[blockerID] = workItem
            }
            return current
        }

        applyExternalStates(
            openTitles: current.filter(\.value.isOpen).mapValues(\.title),
            finished: Set(current.filter { !$0.value.isOpen }.keys))
    }

    /// Drops finished external blockers and keeps titles of open ones current.
    func applyExternalStates(openTitles: [String: String], finished: Set<String>) {
        let before = (plan.waits, plan.released, plan.tracked)
        plan.tracked = plan.tracked.compactMap { blocker in
            guard !finished.contains(blocker.id) else { return nil }
            var blocker = blocker
            blocker.title = openTitles[blocker.id] ?? blocker.title
            return blocker
        }
        plan.order.removeAll { finished.contains($0) }
        plan.delays = plan.delays.filter { !finished.contains($0.key) }
        for (itemID, wait) in plan.waits {
            var wait = wait
            let done = wait.external.filter { finished.contains($0.id) }
            wait.external = wait.external.compactMap { blocker in
                guard !finished.contains(blocker.id) else { return nil }
                var blocker = blocker
                blocker.title = openTitles[blocker.id] ?? blocker.title
                return blocker
            }
            if wait.isEmpty, !done.isEmpty {
                release(itemID, finished: done.map(\.shortReference))
            }
            plan.waits[itemID] = wait.isEmpty ? nil : wait
        }
        if before.0 != plan.waits || before.1 != plan.released || before.2 != plan.tracked {
            planChanged()
        }
    }

    /// Everything `itemID` waited on got done: it shows as ready to start until the user looks at it.
    private func release(_ itemID: String, finished: [String]) {
        plan.released[itemID] = Release(date: .now, finished: finished)
    }

    /// The user has seen the item, it no longer needs to stand out as ready.
    func acknowledgeRelease(of itemID: String) {
        guard plan.released.removeValue(forKey: itemID) != nil else { return }
        planChanged()
    }

    private func rebuildItems() {
        // A release nobody looked at for a week is old news.
        let fresh = plan.released.filter { $0.value.date > .now.addingTimeInterval(-7 * 24 * 3600) }
        if fresh.count != plan.released.count {
            plan.released = fresh
            savePlan()
        }
        let accountIDs = Set(accounts.map(\.id))
        fetchedItems = fetchedItems.filter { accountIDs.contains($0.key) }
        accountErrors = accountErrors.filter { accountIDs.contains($0.key) }

        let byID = Dictionary(fetchedItems.values.joined().map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let issueOfMergeRequest = Dictionary(
            byID.values.flatMap { issue in issue.mergeRequests.map { ($0.id, issue.id) } },
            uniquingKeysWith: { first, _ in first })
        foldGroupedMergeRequests(into: issueOfMergeRequest)
        let position = Dictionary(plan.order.enumerated().map { ($1, $0) }, uniquingKeysWith: { first, _ in first })
        items = byID.values
            .map { item in
                var item = item
                item.isPlaced = position[item.id] != nil
                item.wait = plan.waits[item.id]
                item.waitDescription = item.wait.map { describe($0, of: item, in: byID) }
                item.releasedBy = plan.released[item.id]?.finished.joined(separator: ", ")
                return item
            }
            .sorted { lhs, rhs in
                switch (position[lhs.id], position[rhs.id]) {
                case let (left?, right?): left < right
                case (.some, nil): true
                case (nil, .some): false
                case (nil, nil): lhs.activityAt > rhs.activityAt
                }
            }
            .withProjectColors()
        var external = plan.tracked
        for blocker in plan.waits.values.flatMap(\.external) where !external.contains(where: { $0.id == blocker.id }) {
            external.append(blocker)
        }
        timelineExternal = external
        orderPositions = position
        timelineDelays = plan.delays
        publishSnapshot()
    }

    /// Once a merge request is grouped under its issue, the issue takes the earlier of their two places,
    /// and waiting on the merge request means waiting on the issue.
    private func foldGroupedMergeRequests(into issueOf: [String: String]) {
        guard !issueOf.isEmpty else { return }
        var order: [String] = []
        for id in plan.order.map({ issueOf[$0] ?? $0 }) where !order.contains(id) {
            order.append(id)
        }
        var waits: [String: Wait] = [:]
        for (itemID, wait) in plan.waits {
            let owner = issueOf[itemID] ?? itemID
            var merged = waits[owner] ?? Wait()
            for blocker in wait.itemIDs.map({ issueOf[$0] ?? $0 }) where blocker != owner && !merged.itemIDs.contains(blocker) {
                merged.itemIDs.append(blocker)
            }
            for blocker in wait.external where !merged.external.contains(where: { $0.id == blocker.id }) {
                merged.external.append(blocker)
            }
            merged.note = merged.note ?? wait.note
            waits[owner] = merged.isEmpty ? nil : merged
        }
        if order != plan.order || waits != plan.waits {
            plan.order = order
            plan.waits = waits
            savePlan()
        }
    }

    private func describe(_ wait: Wait, of item: TodoItem, in items: [String: TodoItem]) -> String {
        var parts = wait.itemIDs.compactMap { items[$0] }.map { blocker in
            blocker.projectKey == item.projectKey ? blocker.localReference : blocker.shortReference
        }
        parts += wait.external.map(\.shortReference)
        if let note = wait.note, !note.isEmpty {
            parts.append(note)
        }
        return parts.joined(separator: ", ")
    }

    private func publishSnapshot() {
        guard !isPreview else { return }
        let failing = accounts.filter { accountErrors[$0.id] != nil }.map(\.name)
        try? SharedStore.save(Snapshot(items: items, refreshedAt: lastRefresh, failingAccounts: failing), to: SharedStore.snapshotURL)

        // Widget reloads are budgeted by the system, so only ask for one when the content changed.
        var hasher = Hasher()
        hasher.combine(items)
        hasher.combine(failing)
        let hash = hasher.finalize()
        if hash != publishedHash {
            publishedHash = hash
            WidgetCenter.shared.reloadAllTimelines()
        }
    }

    private func restartTimer(after delay: Duration = .zero) {
        guard !isPreview else { return }
        timerTask?.cancel()
        timerTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            while !Task.isCancelled {
                await self?.refresh()
                guard let interval = self?.refreshInterval else { return }
                try? await Task.sleep(for: .seconds(interval))
            }
        }
    }

    private func observeWake() {
        _ = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                // Give the network and VPN a moment to come back first.
                self?.restartTimer(after: .seconds(15))
            }
        }
    }

    // MARK: Work order

    /// Applies a drag in a possibly filtered list; items hidden by the filter keep their places.
    func move(_ visibleIDs: [String], fromOffsets source: IndexSet, toOffset destination: Int) {
        var visible = visibleIDs
        visible.move(fromOffsets: source, toOffset: destination)
        let moved = source.map { visibleIDs[$0] }
        var order = plan.order.filter { !moved.contains($0) }
        for id in visible where moved.contains(id) {
            let index = visible.firstIndex(of: id)!
            // Anchor on the nearest placed neighbour in the visible list.
            if let next = visible[(index + 1)...].first(where: { order.contains($0) }), let at = order.firstIndex(of: next) {
                order.insert(id, at: at)
            } else if let previous = visible[..<index].last(where: { order.contains($0) }), let at = order.firstIndex(of: previous) {
                order.insert(id, at: at + 1)
            } else {
                order.append(id)
            }
        }
        plan.order = order
        planChanged()
    }

    func moveToTop(_ item: TodoItem) {
        plan.order.removeAll { $0 == item.id }
        plan.order.insert(item.id, at: 0)
        planChanged()
    }

    func moveToEnd(_ item: TodoItem) {
        plan.order.removeAll { $0 == item.id }
        plan.order.append(item.id)
        planChanged()
    }

    func toggleBlocker(_ blocker: TodoItem, for item: TodoItem) {
        var wait = plan.waits[item.id] ?? Wait()
        if let index = wait.itemIDs.firstIndex(of: blocker.id) {
            wait.itemIDs.remove(at: index)
        } else {
            wait.itemIDs.append(blocker.id)
        }
        updateWait(wait, forItemID: item.id)
    }

    func setWaitNote(_ note: String, forItemID itemID: String) {
        var wait = plan.waits[itemID] ?? Wait()
        let trimmed = note.trimmingCharacters(in: .whitespacesAndNewlines)
        wait.note = trimmed.isEmpty ? nil : trimmed
        guard wait != plan.waits[itemID] ?? Wait() else { return }
        updateWait(wait, forItemID: itemID)
    }

    func stopWaiting(_ item: TodoItem) {
        updateWait(Wait(), forItemID: item.id)
    }

    /// Finds open issues and merge requests `item` could wait on: by words in the title, by number, or by a pasted
    /// URL or `group/project#12`. Leaves out the item itself and what it already waits on.
    func searchBlockers(_ text: String, for item: TodoItem) async throws -> [BlockerCandidate] {
        var found: [(GitLabAccount, ItemKind, GLWorkItem)] = []
        if let reference = WorkItemReference(text), reference.host != nil || reference.path != nil {
            var account = accounts.first { $0.id == item.accountID }
            var project = reference.path ?? String(item.projectID)
            if let host = reference.host {
                account = accounts.first { URL(string: $0.baseURL)?.host() == host }
                // A GitLab served under a sub-path has it in front of the project path.
                if let account, let basePath = URL(string: account.baseURL)?.path(), basePath.count > 1 {
                    project = String(project.trimmingPrefix(basePath.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/"))
                }
            }
            guard let account else { throw BlockerError.unknownGitLab(reference.host ?? "") }
            let workItem = try await client(for: account).workItem(project: project, kind: reference.kind, iid: reference.iid)
            guard workItem.isOpen else { throw BlockerError.closed }
            found = [(account, reference.kind, workItem)]
        } else {
            found = try await searchEverywhere(text, preferring: item)
        }

        let own = Set([item.id] + item.mergeRequests.map(\.id))
        var seen = Set<String>()
        return found.compactMap { account, kind, workItem in
            let listedID = TodoItem.makeID(accountID: account.id, kind: kind, gitLabID: workItem.id)
            guard workItem.isOpen, !own.contains(listedID), seen.insert(listedID).inserted else { return nil }
            let listed = items.first { $0.id == listedID || $0.mergeRequests.contains { $0.id == listedID } }
            var candidate = BlockerCandidate(accountID: account.id, kind: kind, workItem: workItem, listedItemID: listed?.id)
            if account.id != item.accountID {
                candidate.gitLabName = account.name
            }
            let waitsAlready = listed.map { item.wait?.itemIDs.contains($0.id) ?? false }
                ?? (item.wait?.external.contains { $0.id == candidate.blocker.id } ?? false)
            return waitsAlready ? nil : candidate
        }
    }

    /// Searches every connected GitLab at once, the item's own first. Only the item's own GitLab can fail the search,
    /// the others just contribute nothing when they are unreachable.
    private func searchEverywhere(_ text: String, preferring item: TodoItem) async throws -> [(GitLabAccount, ItemKind, GLWorkItem)] {
        let jobs = accounts.compactMap { account in (try? client(for: account)).map { (account, $0) } }
        let results = await withTaskGroup(of: (GitLabAccount, Result<[(ItemKind, GLWorkItem)], any Error>).self) { group in
            for (account, client) in jobs {
                // A project ID means nothing in another GitLab.
                let projectID = account.id == item.accountID ? item.projectID : nil
                group.addTask {
                    do {
                        return (account, .success(try await client.searchWorkItems(text, projectID: projectID)))
                    } catch {
                        return (account, .failure(error))
                    }
                }
            }
            var results: [(GitLabAccount, Result<[(ItemKind, GLWorkItem)], any Error>)] = []
            for await result in group {
                results.append(result)
            }
            return results
        }
        var found: [(GitLabAccount, ItemKind, GLWorkItem)] = []
        for (account, result) in results.sorted(by: { $0.0.id == item.accountID && $1.0.id != item.accountID }) {
            switch result {
            case .success(let hits):
                found += hits.map { (account, $0.0, $0.1) }
            case .failure(let error):
                if account.id == item.accountID {
                    throw error
                }
            }
        }
        return found
    }

    /// One that is in the list already becomes an ordinary blocker, anything else an external one.
    func addBlocker(_ candidate: BlockerCandidate, for item: TodoItem) throws {
        guard let listedID = candidate.listedItemID else {
            addBlocker(candidate.blocker, for: item)
            return
        }
        guard let listed = blockerCandidates(for: item).first(where: { $0.id == listedID }) else { throw BlockerError.cycle }
        if !(item.wait?.itemIDs.contains(listed.id) ?? false) {
            toggleBlocker(listed, for: item)
        }
    }

    func addBlocker(_ blocker: ExternalBlocker, for item: TodoItem) {
        var wait = plan.waits[item.id] ?? Wait()
        guard !wait.external.contains(where: { $0.id == blocker.id }) else { return }
        wait.external.append(blocker)
        updateWait(wait, forItemID: item.id)
    }

    func removeBlocker(_ blocker: ExternalBlocker, for item: TodoItem) {
        var wait = plan.waits[item.id] ?? Wait()
        wait.external.removeAll { $0.id == blocker.id }
        updateWait(wait, forItemID: item.id)
    }

    /// Items `item` can wait on: anything but itself and items that already wait on it.
    func blockerCandidates(for item: TodoItem) -> [TodoItem] {
        items.filter { $0.id != item.id && !blockers(of: $0.id).contains(item.id) }
    }

    /// Everything `itemID` waits on, directly or through other items.
    private func blockers(of itemID: String) -> Set<String> {
        var found = Set<String>()
        var queue = plan.waits[itemID]?.itemIDs ?? []
        while let next = queue.popLast() {
            if found.insert(next).inserted {
                queue += plan.waits[next]?.itemIDs ?? []
            }
        }
        return found
    }

    private func updateWait(_ wait: Wait, forItemID itemID: String) {
        plan.waits[itemID] = wait.isEmpty ? nil : wait
        if !wait.isEmpty {
            plan.released[itemID] = nil
        }
        planChanged()
    }

    /// Closed issues and merged MRs leave the plan, and whatever waited on them can go on.
    private func prunePlan(accountID: UUID, keeping ids: Set<String>, previous: [TodoItem]) {
        let prefix = accountID.uuidString + ":"
        // Only own items can be gone: `account:kind:id`. Other people's ones (`account:project:kind:iid`) are checked separately.
        let isGone = { (id: String) in id.hasPrefix(prefix) && id.split(separator: ":").count == 3 && !ids.contains(id) }
        let before = (plan.order, plan.waits, plan.released, plan.delays)
        let previousByID = Dictionary(previous.flatMap { [$0] + $0.mergeRequests }.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        plan.order.removeAll(where: isGone)
        for (itemID, wait) in plan.waits {
            var wait = wait
            let done = wait.itemIDs.filter(isGone)
            wait.itemIDs.removeAll(where: isGone)
            if !isGone(itemID), wait.isEmpty, !done.isEmpty {
                release(itemID, finished: done.map { previousByID[$0]?.shortReference ?? "položka" })
            }
            plan.waits[itemID] = isGone(itemID) || wait.isEmpty ? nil : wait
        }
        plan.released = plan.released.filter { !isGone($0.key) }
        plan.delays = plan.delays.filter { !isGone($0.key) }
        if before.0 != plan.order || before.1 != plan.waits || before.2 != plan.released || before.3 != plan.delays {
            savePlan()
        }
    }

    private func planChanged() {
        savePlan()
        rebuildItems()
    }

    private func savePlan() {
        guard !isPreview else { return }
        try? SharedStore.save(plan, to: SharedStore.planURL)
    }

    // MARK: Timeline

    /// Open issues and merge requests of one project that are not in the timeline yet, for its „+“.
    func searchProject(_ text: String, accountID: UUID, projectID: Int) async throws -> [BlockerCandidate] {
        guard let account = accounts.first(where: { $0.id == accountID }) else { return [] }
        let found = try await client(for: account).searchWorkItems(text, projectID: projectID, onlyInProject: true)
        let shown = Set(timelineExternal.map(\.id))
        return found.compactMap { kind, workItem in
            let listedID = TodoItem.makeID(accountID: account.id, kind: kind, gitLabID: workItem.id)
            let isListed = items.contains { $0.id == listedID || $0.mergeRequests.contains { $0.id == listedID } }
            let candidate = BlockerCandidate(accountID: account.id, kind: kind, workItem: workItem, listedItemID: nil)
            return workItem.isOpen && !isListed && !shown.contains(candidate.blocker.id) ? candidate : nil
        }
    }

    /// Someone else's issue or merge request at the end of its project's lane.
    func addToTimeline(_ blocker: ExternalBlocker) {
        guard !plan.tracked.contains(where: { $0.id == blocker.id }) else { return }
        plan.tracked.append(blocker)
        if !plan.order.contains(blocker.id) {
            plan.order.append(blocker.id)
        }
        planChanged()
    }

    /// Whatever waits on it keeps it in the timeline.
    func removeFromTimeline(_ blocker: ExternalBlocker) {
        plan.tracked.removeAll { $0.id == blocker.id }
        if !isWaitedOn(blocker) {
            plan.order.removeAll { $0 == blocker.id }
        }
        planChanged()
    }

    func isWaitedOn(_ blocker: ExternalBlocker) -> Bool {
        plan.waits.values.contains { $0.external.contains { $0.id == blocker.id } }
    }

    /// Drag within a lane: onto a block moves it there, onto the lane's empty end moves it last.
    func placeInLane(_ draggedID: String, onto targetID: String?, laneIDs: [String]) {
        guard let from = laneIDs.firstIndex(of: draggedID) else { return }
        let destination: Int
        if let targetID, let target = laneIDs.firstIndex(of: targetID) {
            // Moving right lands after the target, moving left before it.
            destination = from < target ? target + 1 : target
        } else {
            destination = laneIDs.count
        }
        move(laneIDs, fromOffsets: IndexSet(integer: from), toOffset: destination)
    }

    func timelineSchedule() -> TimelineSchedule {
        TimelineSchedule.build(items: items, external: timelineExternal, positions: orderPositions, delays: plan.delays)
    }

    /// Drag onto an empty step of the lane: the item goes behind the blocks starting before that step and waits there,
    /// as long as nothing it waits on keeps it later anyway.
    func placeInTimeline(_ id: String, atStep step: Int, laneIDs: [String]) {
        guard let lane = timelineSchedule().lanes.first(where: { $0.blocks.contains { $0.id == id } }),
              let from = laneIDs.firstIndex(of: id) else { return }
        let previous = lane.blocks.last { $0.id != id && $0.slot < step }
        // Never ahead of what it waits on in the same lane.
        let waitedOn = Set(lane.blocks.first { $0.id == id }?.blockers ?? [])
        let earliest = laneIDs.lastIndex { waitedOn.contains($0) }.map { $0 + 1 } ?? 0
        let destination = max(earliest, previous.flatMap { laneIDs.firstIndex(of: $0.id) }.map { $0 + 1 } ?? 0)
        plan.delays[id] = nil
        if destination == from || destination == from + 1 {
            planChanged()
        } else {
            move(laneIDs, fromOffsets: IndexSet(integer: from), toOffset: destination)
        }
        // Where it starts without a delay decides how long the delay is.
        let natural = timelineSchedule().lanes.flatMap(\.blocks).first { $0.id == id }?.slot ?? step
        plan.delays[id] = step > natural ? step - natural : nil
        planChanged()
    }

    func delay(_ id: String, by steps: Int) {
        let delay = max(0, (plan.delays[id] ?? 0) + steps)
        plan.delays[id] = delay > 0 ? delay : nil
        planChanged()
    }

    /// Drop onto a block of another project: the dragged item waits on it. Someone else's item cannot wait,
    /// so dragging it onto one of the user's makes the user's item wait on it instead.
    func linkInTimeline(_ draggedID: String, onto targetID: String) {
        if let dragged = items.first(where: { $0.id == draggedID }) {
            addTimelineBlocker(targetID, for: dragged)
        } else if let target = items.first(where: { $0.id == targetID }) {
            addTimelineBlocker(draggedID, for: target)
        }
    }

    /// Whether `dependentID` can wait on `blockerID`: only the user's own items wait, and never in a loop.
    func canLink(_ dependentID: String, waitsOn blockerID: String) -> Bool {
        guard dependentID != blockerID, let dependent = items.first(where: { $0.id == dependentID }) else { return false }
        if items.contains(where: { $0.id == blockerID }) {
            return blockerCandidates(for: dependent).contains { $0.id == blockerID }
        }
        return timelineExternal.contains { $0.id == blockerID }
    }

    func removeLink(_ dependentID: String, waitsOn blockerID: String) {
        guard let dependent = items.first(where: { $0.id == dependentID }) else { return }
        if let blocker = items.first(where: { $0.id == blockerID }) {
            if dependent.wait?.itemIDs.contains(blockerID) == true {
                toggleBlocker(blocker, for: dependent)
            }
        } else if let external = dependent.wait?.external.first(where: { $0.id == blockerID }) {
            removeBlocker(external, for: dependent)
        }
    }

    /// Items waiting on `id`, own or someone else's.
    func dependents(of id: String) -> [TodoItem] {
        items.filter { ($0.wait?.itemIDs.contains(id) ?? false) || ($0.wait?.external.contains { $0.id == id } ?? false) }
    }

    /// An arrow drawn from one block to another: the one it points at waits on the one it starts from.
    func link(_ dependentID: String, waitsOn blockerID: String) {
        guard let dependent = items.first(where: { $0.id == dependentID }) else { return }
        addTimelineBlocker(blockerID, for: dependent)
    }

    private func addTimelineBlocker(_ blockerID: String, for item: TodoItem) {
        if let listed = items.first(where: { $0.id == blockerID }) {
            guard blockerCandidates(for: item).contains(where: { $0.id == listed.id }) else {
                Alerts.show(title: "Tahle vazba nejde", message: "\(listed.shortReference) už čeká na \(item.shortReference), vznikl by kruh.", style: .warning)
                return
            }
            if !(item.wait?.itemIDs.contains(listed.id) ?? false) {
                toggleBlocker(listed, for: item)
            }
        } else if let external = timelineExternal.first(where: { $0.id == blockerID }) {
            addBlocker(external, for: item)
        }
    }

    // MARK: Actions


    func perform(_ action: ItemAction, onItemWithID itemID: String) async {
        guard !runningActions.contains(itemID) else { return }
        guard let item = item(withID: itemID),
              let account = accounts.first(where: { $0.id == item.accountID }) else {
            Alerts.show(title: "Položka nenalezena", message: "Seznam se mezitím změnil, zkus to po obnovení znovu.")
            return
        }

        runningActions.insert(itemID)
        defer { runningActions.remove(itemID) }
        do {
            let client = try client(for: account)
            switch action {
            case .createMergeRequest:
                try await createMergeRequest(for: item, account: account, client: client)
            case .markReady:
                try await markReady(item, account: account, client: client)
            }
        } catch {
            Alerts.show(title: "Akce se nepovedla", message: error.localizedDescription, style: .warning)
        }
    }

    private func createMergeRequest(for issue: TodoItem, account: GitLabAccount, client: GitLabClient) async throws {
        let branch = GitLabClient.branchName(for: issue)
        let confirmed = Alerts.confirm(
            title: "Vytvořit merge request?",
            message: "\(issue.reference)\n\(issue.title)\n\nVětev: \(branch) → \(account.targetBranch)\nAssignee: ty",
            action: "Vytvořit MR")
        guard confirmed else { return }

        let assigneeID = try await currentUserID(of: account, client: client)
        // The next refresh groups the new merge request under the issue, keeping its place in the work order.
        let mergeRequest = try await client.createMergeRequest(for: issue, branch: branch, targetBranch: account.targetBranch, assigneeID: assigneeID)
        let command = "git fetch origin && git checkout \(mergeRequest.sourceBranch)"
        Pasteboard.copy(command)
        Task { await refresh() }

        let choice = Alerts.show(
            title: "Merge request \(mergeRequest.references?.short ?? "!\(mergeRequest.iid)") je připravený",
            message: "Větev: \(mergeRequest.sourceBranch)\n\nPříkaz pro checkout je ve schránce:\n\(command)",
            buttons: ["OK", "Otevřít MR"])
        if choice == 1 {
            NSWorkspace.shared.open(mergeRequest.webUrl)
        }
    }

    private func markReady(_ mergeRequest: TodoItem, account: GitLabAccount, client: GitLabClient) async throws {
        let reviewer = account.defaultReviewer
        let reviewerLine = reviewer.isEmpty ? "Výchozí reviewer není nastavený, zruší se jen draft." : "Reviewer: @\(reviewer)"
        let confirmed = Alerts.confirm(
            title: "Označit MR jako ready?",
            message: "\(mergeRequest.reference)\n\(mergeRequest.title)\n\n\(reviewerLine)",
            action: "Označit jako ready")
        guard confirmed else { return }

        var reviewerID: Int?
        if !reviewer.isEmpty {
            reviewerID = try await client.userID(username: reviewer)
        }
        _ = try await client.markReady(projectID: mergeRequest.projectID, iid: mergeRequest.iid, addingReviewer: reviewerID)
        await refresh()
    }

    private func currentUserID(of account: GitLabAccount, client: GitLabClient) async throws -> Int {
        if let id = userIDs[account.id] {
            return id
        }
        let id = try await client.currentUser().id
        userIDs[account.id] = id
        return id
    }

    private func client(for account: GitLabAccount) throws -> GitLabClient {
        guard let apiURL = account.apiURL else { throw GitLabError.invalidURL }
        guard let token = tokens[account.id], !token.isEmpty else { throw GitLabError.missingToken }
        return GitLabClient(apiBase: apiURL, token: token)
    }

    // MARK: Settings

    func saveAccount(_ account: GitLabAccount, token: String?) {
        if let index = accounts.firstIndex(where: { $0.id == account.id }) {
            accounts[index] = account
        } else {
            accounts.append(account)
        }
        if let token {
            Keychain.setToken(token, for: account.id)
            tokens[account.id] = token
        }
        userIDs[account.id] = nil
        persistAccounts()
        restartTimer()
    }

    func removeAccount(_ account: GitLabAccount) {
        accounts.removeAll { $0.id == account.id }
        Keychain.deleteToken(for: account.id)
        tokens[account.id] = nil
        userIDs[account.id] = nil
        prunePlan(accountID: account.id, keeping: [], previous: [])
        persistAccounts()
        rebuildItems()
    }

    func setRefreshInterval(_ interval: TimeInterval) {
        refreshInterval = interval
        UserDefaults.standard.set(interval, forKey: Keys.refreshInterval)
        restartTimer()
    }

    private func persistAccounts() {
        UserDefaults.standard.set(try? JSONEncoder().encode(accounts), forKey: Keys.accounts)
    }
}
