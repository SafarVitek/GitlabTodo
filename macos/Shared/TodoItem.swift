import Foundation

enum ItemKind: String, Codable, Sendable {
    case issue
    case mergeRequest

    var symbol: String {
        switch self {
        case .issue: "smallcircle.filled.circle"
        case .mergeRequest: "arrow.triangle.merge"
        }
    }
}

enum ItemRole: String, Codable, Sendable {
    case assigned
    case mentioned
}

enum ItemAction: String, Codable, Sendable {
    case createMergeRequest = "create-mr"
    case markReady = "ready"

    var title: String {
        switch self {
        case .createMergeRequest: "Vytvořit merge request"
        case .markReady: "Označit jako ready a přidat reviewera"
        }
    }

    var shortTitle: String {
        switch self {
        case .createMergeRequest: "MR"
        case .markReady: "Ready"
        }
    }

    var symbol: String {
        switch self {
        case .createMergeRequest: "arrow.triangle.branch"
        case .markReady: "checkmark.seal"
        }
    }

    /// A draft waiting to be marked ready is the step not to forget; creating MRs is offered more quietly.
    var isProminent: Bool {
        self == .markReady
    }
}

/// Why an item cannot be worked on yet: other items in the list, issues or merge requests of other people,
/// something outside GitLab, or any mix of these.
struct Wait: Codable, Hashable, Sendable {
    var itemIDs: [String] = []
    var external: [ExternalBlocker] = []
    var note: String?

    var isEmpty: Bool {
        itemIDs.isEmpty && external.isEmpty && (note ?? "").isEmpty
    }
}

extension Wait {
    /// Plans saved before `external` existed lack the key.
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        itemIDs = try container.decodeIfPresent([String].self, forKey: .itemIDs) ?? []
        external = try container.decodeIfPresent([ExternalBlocker].self, forKey: .external) ?? []
        note = try container.decodeIfPresent(String.self, forKey: .note)
    }
}

/// An issue or merge request that is not in the list, e.g. a colleague's, which an item waits on.
struct ExternalBlocker: Codable, Hashable, Identifiable, Sendable {
    let accountID: UUID
    let projectID: Int
    let kind: ItemKind
    let iid: Int
    var title: String
    var reference: String
    var webURL: URL

    var id: String {
        "\(accountID.uuidString):\(projectID):\(kind.rawValue):\(iid)"
    }

    /// `group/sub/project#12` → `project#12`
    var shortReference: String {
        reference.split(separator: "/").last.map(String.init) ?? reference
    }
}

/// The user's own notes on an item, e.g. from a meeting. Lines written like GitLab's task lists, `- [ ] call Petr`,
/// are tasks to tick off.
struct ItemNote: Codable, Hashable, Sendable {
    struct Task: Hashable, Sendable {
        /// Index of the line in `text`.
        let line: Int
        let text: String
        let isDone: Bool
    }

    var text: String
    var updatedAt: Date

    var lines: [String] {
        text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
    }

    var tasks: [Task] {
        lines.enumerated().compactMap { index, line in
            Self.match(line).map { Task(line: index, text: String($0.text ?? ""), isDone: $0.mark != " ") }
        }
    }

    var openTasks: [Task] {
        tasks.filter { !$0.isDone }
    }

    /// For tooltips: the beginning of the note.
    var preview: String {
        text.count > 400 ? String(text.prefix(399)) + "…" : text
    }

    /// Ticks a task off or back on, leaving the rest of the note as written.
    func toggling(line index: Int, at date: Date = .now) -> ItemNote {
        var lines = lines
        guard lines.indices.contains(index), let match = Self.match(lines[index]) else { return self }
        let line = match.mark.base
        lines[index] = String(line[..<match.mark.startIndex]) + (match.mark == " " ? "x" : " ") + String(line[match.mark.endIndex...])
        return ItemNote(text: lines.joined(separator: "\n"), updatedAt: date)
    }

    /// `- [ ] text`, `* [x] text` or just `[ ] text`.
    private static func match(_ line: String) -> Regex<(Substring, mark: Substring, text: Substring?)>.Match? {
        line.wholeMatch(of: /\s*(?:[-*+]\s+)?\[(?<mark>[ xX])\](?:\s+(?<text>.*))?/)
    }
}

/// What the user types to point at an issue or merge request: a GitLab URL, `group/project#12`, `group/project!34`,
/// or `#12` / `!34` within the item's own project.
struct WorkItemReference: Equatable, Sendable {
    var host: String?
    var path: String?
    var kind: ItemKind
    var iid: Int

    init?(_ input: String) {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let url = URL(string: text), let host = url.host(), url.scheme?.hasPrefix("http") == true {
            // https://gitlab.example.com/group/project/-/issues/12
            let parts = url.pathComponents.filter { $0 != "/" }
            guard let dash = parts.firstIndex(of: "-"), dash > 0, dash + 2 < parts.count, let iid = Int(parts[dash + 2]) else { return nil }
            switch parts[dash + 1] {
            case "issues", "work_items": kind = .issue
            case "merge_requests": kind = .mergeRequest
            default: return nil
            }
            self.host = host
            path = parts[..<dash].joined(separator: "/")
            self.iid = iid
        } else if let match = text.wholeMatch(of: /([\w.\-\/]*)([#!])(\d+)/), let iid = Int(match.3) {
            host = nil
            path = match.1.isEmpty ? nil : String(match.1)
            kind = match.2 == "#" ? .issue : .mergeRequest
            self.iid = iid
        } else {
            return nil
        }
    }
}

/// A GitLab label with its colors as `#RRGGBB`.
struct ItemLabel: Codable, Hashable, Sendable {
    let name: String
    let color: String
    let textColor: String
}

struct TodoItem: Codable, Hashable, Identifiable, Sendable {
    /// `<account>:<kind>:<GitLab id>`; the local plan is keyed by it.
    let id: String
    let accountID: UUID
    let accountName: String
    let kind: ItemKind
    let role: ItemRole
    let projectID: Int
    let iid: Int
    let title: String
    let reference: String
    let webURL: URL
    let activityAt: Date
    var dueDate: Date?
    var isConfidential = false
    var hasMergeRequest = false
    var isDraft = false
    var sourceBranch: String?
    var reviewerCount = 0
    /// Review threads on a merge request that can be resolved, and how many of them are still open.
    var resolvableThreads = 0
    var unresolvedThreads = 0
    var hasConflicts = false
    var labels: [ItemLabel] = []
    /// Placed by the user in the work order. Unplaced items are new and come after the placed ones.
    var isPlaced = false
    var wait: Wait?
    /// `wait` for display, e.g. `#12` or the note.
    var waitDescription: String?
    /// What got done so that this item can start, shown until the user looks at the item.
    var releasedBy: String?
    /// The user's notes and tasks, kept locally.
    var note: ItemNote?
    /// Merge requests that close this issue, shown as part of it rather than on their own.
    var mergeRequests: [TodoItem] = []
    /// Index into `Palette.projectColors`, assigned by the app so that projects do not share colors.
    var projectColor = 0

    static func makeID(accountID: UUID, kind: ItemKind, gitLabID: Int) -> String {
        "\(accountID.uuidString):\(kind.rawValue):\(gitLabID)"
    }

    var isWaiting: Bool {
        wait != nil
    }

    /// `group/sub/project#12` → `project#12`
    var shortReference: String {
        reference.split(separator: "/").last.map(String.init) ?? reference
    }

    /// `group/sub/project#12` → `#12`
    var localReference: String {
        reference.firstIndex { $0 == "#" || $0 == "!" }.map { String(reference[$0...]) } ?? reference
    }

    var projectKey: String {
        "\(accountID.uuidString):\(projectID)"
    }

    /// `group/sub/project#12` → `project`
    var projectName: String {
        reference.prefix { $0 != "#" && $0 != "!" }.split(separator: "/").last.map(String.init) ?? accountName
    }

    /// The issue's merge request once there is one, otherwise the item itself.
    var mergeRequest: TodoItem? {
        kind == .mergeRequest ? self : mergeRequests.first
    }

    /// An issue with a merge request is in review, so it counts and looks like a merge request.
    var stage: ItemKind {
        mergeRequest == nil ? .issue : .mergeRequest
    }

    var isDraftStage: Bool {
        mergeRequest?.isDraft ?? false
    }

    var hasMergeConflict: Bool {
        mergeRequest?.hasConflicts ?? false
    }

    /// Resolved and all resolvable review threads of the merge request, `nil` when it has none.
    var reviewThreads: (resolved: Int, total: Int)? {
        guard let mergeRequest, mergeRequest.resolvableThreads > 0 else { return nil }
        return (mergeRequest.resolvableThreads - mergeRequest.unresolvedThreads, mergeRequest.resolvableThreads)
    }

    /// `#12`, or `#12 → !34` for an issue with its merge request.
    var references: String {
        ([localReference] + mergeRequests.map(\.localReference)).joined(separator: " → ")
    }

    /// The next step, shown as a button. For an issue with a merge request it is the merge request's step.
    var nextStep: ItemStep? {
        if let mergeRequest = mergeRequests.first {
            return mergeRequest.nextStep
        }
        switch kind {
        case .issue:
            return hasMergeRequest ? nil : ItemStep(action: .createMergeRequest, target: self)
        case .mergeRequest:
            // Adding a reviewer to a merge request that is already ready stays in the context menu.
            return role == .assigned && isDraft ? ItemStep(action: .markReady, target: self) : nil
        }
    }

    /// Offered in the context menu even when the button is hidden.
    var steps: [ItemStep] {
        if !mergeRequests.isEmpty {
            return mergeRequests.flatMap(\.steps)
        }
        switch (kind, role) {
        case (.issue, _): return [ItemStep(action: .createMergeRequest, target: self)]
        case (.mergeRequest, .assigned): return [ItemStep(action: .markReady, target: self)]
        case (.mergeRequest, .mentioned): return []
        }
    }

    var checkoutCommand: String? {
        mergeRequest?.sourceBranch.map { "git fetch origin && git checkout \($0)" }
    }
}

/// An action and the item it runs on, which for an issue with a merge request is the merge request.
struct ItemStep: Identifiable, Sendable {
    let action: ItemAction
    let target: TodoItem

    var id: String { "\(target.id):\(action.rawValue)" }
}

enum KindFilter: String, Codable, CaseIterable, Identifiable, Sendable {
    case all
    case issues
    case mergeRequests

    var id: Self { self }

    var title: String {
        switch self {
        case .all: "Vše"
        case .issues: "Issue"
        case .mergeRequests: "MR"
        }
    }

    func includes(_ item: TodoItem) -> Bool {
        switch self {
        case .all: true
        case .issues: item.stage == .issue
        case .mergeRequests: item.stage == .mergeRequest
        }
    }
}

extension [TodoItem] {
    /// Position in the work order. Only placed items that are not waiting get one.
    func workNumbers() -> [String: Int] {
        var numbers: [String: Int] = [:]
        for item in self where item.isPlaced && !item.isWaiting {
            numbers[item.id] = numbers.count + 1
        }
        return numbers
    }
}

/// What the app hands over to the widget, items in work order.
struct Snapshot: Codable, Sendable {
    var items: [TodoItem] = []
    var refreshedAt: Date?
    var failingAccounts: [String] = []
}

struct ProjectTab: Identifiable, Hashable {
    let key: String
    let name: String
    let accountName: String
    let count: Int
    let color: Int

    var id: String { key }

    /// Biggest projects first.
    static func tabs(for items: [TodoItem]) -> [ProjectTab] {
        Dictionary(grouping: items, by: \.projectKey)
            .map { key, items in ProjectTab(key: key, name: items[0].projectName, accountName: items[0].accountName, count: items.count, color: items[0].projectColor) }
            .sorted { ($0.count, $1.name) > ($1.count, $0.name) }
    }
}
