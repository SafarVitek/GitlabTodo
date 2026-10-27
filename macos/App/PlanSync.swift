import CryptoKit
import Foundation

/// Where the plan is kept in sync between devices: a file in a repository on one of the GitLabs.
struct SyncSettings: Codable, Hashable, Sendable {
    var accountID: UUID
    /// `group/project`
    var project: String
}

/// What this device knows about the last sync, to tell its own changes from those of other devices.
struct SyncState: Codable, Sendable {
    var settings: SyncSettings
    var lastCommitID: String?
    /// The plan as last synced, with this device's account IDs.
    var base: Plan?
    var syncedAt: Date?
    /// `private`, `internal` or `public`, as GitLab last reported it.
    var visibility: String?
}

/// The file in the repository.
struct SyncDocument: Codable {
    static let currentFormat = 1

    var format = currentFormat
    var plan: Plan
}

/// The repository file the plan is synced through.
protocol RemotePlanStore: Sendable {
    /// The file's content and the commit that last changed it, nil while there is no file yet.
    func read() async throws -> (content: Data, commitID: String)?
    /// Writes over the file as of `commitID`, nil to create it. Throws `PlanSync.Conflict` when another device wrote
    /// in between. Returns the new commit ID.
    func write(_ content: Data, over commitID: String?) async throws -> String
}

enum PlanSyncError: LocalizedError {
    case newerFormat
    case unreadable

    var errorDescription: String? {
        switch self {
        case .newerFormat: "Data v repozitáři uložila novější verze aplikace, aktualizuj ji."
        case .unreadable: "Soubor gitlab-todo.json v repozitáři nejde přečíst."
        }
    }
}

enum PlanSync {
    static let filePath = "gitlab-todo.json"

    struct Conflict: Error {}

    // MARK: Account IDs

    /// The same for a GitLab on every device, unlike the random account IDs, so that plans of two devices match.
    /// A hash of the normalized URL in the shape of a UUID; the Linux app computes the same.
    static func stableID(forBaseURL baseURL: String) -> String {
        let normalized = GitLabAccount.normalizedBaseURL(baseURL).lowercased()
        var bytes = Array(SHA256.hash(data: Data("gitlab-todo:\(normalized)".utf8)).prefix(16))
        bytes[6] = (bytes[6] & 0x0F) | 0x50
        bytes[8] = (bytes[8] & 0x3F) | 0x80
        let hex = bytes.map { String(format: "%02X", $0) }.joined()
        let groups = [8, 4, 4, 4, 12]
        var parts: [String] = []
        var index = hex.startIndex
        for length in groups {
            let end = hex.index(index, offsetBy: length)
            parts.append(String(hex[index..<end]))
            index = end
        }
        return parts.joined(separator: "-")
    }

    /// This device's account IDs → the stable ones.
    static func stableIDs(of accounts: [GitLabAccount]) -> [String: String] {
        Dictionary(accounts.map { ($0.id.uuidString, stableID(forBaseURL: $0.baseURL)) }, uniquingKeysWith: { first, _ in first })
    }

    /// Every account ID in the plan replaced, e.g. this device's by the stable ones before writing to the repository.
    /// IDs of GitLabs this device does not know stay as they are.
    static func translate(_ plan: Plan, _ mapping: [String: String]) throws -> Plan {
        guard !mapping.isEmpty else { return plan }
        var text = String(decoding: try encoder().encode(plan), as: UTF8.self)
        for (from, to) in mapping where from != to {
            text = text.replacingOccurrences(of: from, with: to)
        }
        return try decoder().decode(Plan.self, from: Data(text.utf8))
    }

    // MARK: Merging

    /// Three-way merge against the plan both sides started from. A change on one side wins over no change on the
    /// other; where both changed the same thing, this device wins, except for notes, where the newer one does.
    static func merge(base: Plan, local: Plan, remote: Plan) -> Plan {
        Plan(
            order: mergeOrder(base: base.order, local: local.order, remote: remote.order),
            waits: mergeEntries(base: base.waits, local: local.waits, remote: remote.waits),
            released: mergeEntries(base: base.released, local: local.released, remote: remote.released),
            tracked: mergeTracked(base: base.tracked, local: local.tracked, remote: remote.tracked),
            delays: mergeEntries(base: base.delays, local: local.delays, remote: remote.delays),
            notes: mergeEntries(base: base.notes, local: local.notes, remote: remote.notes) { local, remote in
                // Text is never dropped for a deletion on the other side.
                guard let local, let remote else { return local ?? remote }
                return local.updatedAt >= remote.updatedAt ? local : remote
            })
    }

    static func mergeEntries<Value: Equatable>(
        base: [String: Value], local: [String: Value], remote: [String: Value],
        resolve: (Value?, Value?) -> Value? = { local, _ in local }
    ) -> [String: Value] {
        var merged: [String: Value] = [:]
        for key in Set(base.keys).union(local.keys).union(remote.keys) {
            let (before, mine, theirs) = (base[key], local[key], remote[key])
            merged[key] = if mine == theirs {
                mine
            } else if mine == before {
                theirs
            } else if theirs == before {
                mine
            } else {
                resolve(mine, theirs)
            }
        }
        return merged
    }

    /// The local order, without what the other device removed and with what it added, each right after the item it
    /// follows there.
    static func mergeOrder(base: [String], local: [String], remote: [String]) -> [String] {
        if local == remote || remote == base {
            return local
        }
        if local == base {
            return remote
        }
        let removed = Set(base).subtracting(remote)
        var merged = local.filter { !removed.contains($0) }
        let known = Set(base)
        for (index, id) in remote.enumerated() where !known.contains(id) && !merged.contains(id) {
            let previous = remote[..<index].last { merged.contains($0) }
            let position = previous.flatMap { merged.firstIndex(of: $0) }.map { $0 + 1 } ?? 0
            merged.insert(id, at: position)
        }
        return merged
    }

    private static func mergeTracked(base: [ExternalBlocker], local: [ExternalBlocker], remote: [ExternalBlocker]) -> [ExternalBlocker] {
        func keyed(_ blockers: [ExternalBlocker]) -> [String: ExternalBlocker] {
            Dictionary(blockers.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        }
        let merged = mergeEntries(base: keyed(base), local: keyed(local), remote: keyed(remote))
        let order = mergeOrder(base: base.map(\.id), local: local.map(\.id), remote: remote.map(\.id))
        return order.compactMap { merged[$0] } + merged.values.filter { !order.contains($0.id) }.sorted { $0.id < $1.id }
    }

    // MARK: Syncing

    /// Brings the plan and the repository file to the same state. Returns the plan to keep on this device and the
    /// state for the next sync. Reads again and merges anew when another device wrote in between.
    static func sync(local: Plan, state: SyncState, store: some RemotePlanStore, accounts: [GitLabAccount]) async throws -> (plan: Plan, state: SyncState) {
        let toStable = stableIDs(of: accounts)
        let toLocal = Dictionary(toStable.map { ($1, $0) }, uniquingKeysWith: { first, _ in first })
        var attempt = 0
        while true {
            do {
                return try await syncOnce(local: local, state: state, store: store, toStable: toStable, toLocal: toLocal)
            } catch is Conflict where attempt < 2 {
                attempt += 1
            }
        }
    }

    private static func syncOnce(
        local: Plan, state: SyncState, store: some RemotePlanStore, toStable: [String: String], toLocal: [String: String]
    ) async throws -> (plan: Plan, state: SyncState) {
        var state = state
        func write(_ plan: Plan, over commitID: String?) async throws {
            let document = SyncDocument(plan: try translate(plan, toStable))
            state.lastCommitID = try await store.write(try encoder().encode(document), over: commitID)
            state.base = plan
        }

        guard let file = try await store.read() else {
            try await write(local, over: nil)
            return (local, state)
        }
        let (content, commitID) = file
        guard let document = try? decoder().decode(SyncDocument.self, from: content) else { throw PlanSyncError.unreadable }
        guard document.format <= SyncDocument.currentFormat else { throw PlanSyncError.newerFormat }

        if commitID == state.lastCommitID, let base = state.base {
            // Nothing new from other devices.
            if local != base {
                try await write(local, over: commitID)
            }
            return (local, state)
        }
        let remote = try translate(document.plan, toLocal)
        let merged = merge(base: state.base ?? Plan(), local: local, remote: remote)
        if merged == remote {
            state.lastCommitID = commitID
            state.base = merged
        } else {
            try await write(merged, over: commitID)
        }
        return (merged, state)
    }

    static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        // Stable output, so the repository history shows what changed.
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }

    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}

/// The plan file in a GitLab repository, written with commits on its default branch.
struct GitLabPlanStore: RemotePlanStore {
    let client: GitLabClient
    let projectID: Int
    let branch: String

    func read() async throws -> (content: Data, commitID: String)? {
        guard let file = try await client.repositoryFile(projectID: projectID, path: PlanSync.filePath, ref: branch) else { return nil }
        guard let content = Data(base64Encoded: file.content, options: .ignoreUnknownCharacters) else { throw PlanSyncError.unreadable }
        return (content, file.lastCommitId)
    }

    func write(_ content: Data, over commitID: String?) async throws -> String {
        do {
            try await client.writeRepositoryFile(
                projectID: projectID, path: PlanSync.filePath, branch: branch, content: String(decoding: content, as: UTF8.self),
                message: "GitLab Todo: synchronizace", lastCommitID: commitID)
        } catch GitLabError.http(400, let message?) where message.contains("changed") || message.contains("already exists") {
            throw PlanSync.Conflict()
        }
        guard let file = try await client.repositoryFile(projectID: projectID, path: PlanSync.filePath, ref: branch) else {
            throw PlanSyncError.unreadable
        }
        return file.lastCommitId
    }
}
