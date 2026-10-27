import Foundation

struct GitLabAccount: Codable, Hashable, Identifiable, Sendable {
    var id = UUID()
    var name: String
    var baseURL: String
    var targetBranch = "master"
    var defaultReviewer = ""

    var apiURL: URL? { Self.apiURL(from: baseURL) }

    static func normalizedBaseURL(_ input: String) -> String {
        var value = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if !value.contains("://") {
            value = "https://" + value
        }
        while value.hasSuffix("/") {
            value.removeLast()
        }
        if value.hasSuffix("/api/v4") {
            value.removeLast("/api/v4".count)
        }
        return value
    }

    static func apiURL(from input: String) -> URL? {
        guard let url = URL(string: normalizedBaseURL(input) + "/api/v4"),
              url.scheme == "https" || url.scheme == "http",
              url.host()?.isEmpty == false else { return nil }
        return url
    }

    /// `@jnovak ` → `jnovak`
    static func username(_ input: String) -> String {
        String(input.trimmingCharacters(in: .whitespacesAndNewlines).trimmingPrefix("@"))
    }
}

enum GitLabError: LocalizedError {
    case invalidURL
    case missingToken
    case invalidResponse
    case unauthorized
    case forbidden
    case notFound
    case http(Int, String?)
    case decoding(String)
    case unknownUser(String)

    var errorDescription: String? {
        switch self {
        case .invalidURL: "Neplatná URL GitLabu"
        case .missingToken: "Chybí access token"
        case .invalidResponse: "Neplatná odpověď serveru"
        case .unauthorized: "Neplatný nebo expirovaný token (401)"
        case .forbidden: "Token na to nemá oprávnění (403), akce potřebují scope api"
        case .notFound: "Nenalezeno (404)"
        case .http(let code, let message): "GitLab vrátil HTTP \(code)" + (message.map { ": \($0)" } ?? "")
        case .decoding(let detail): "Nečekaný formát odpovědi: \(detail)"
        case .unknownUser(let username): "Uživatel @\(username) neexistuje"
        }
    }
}

enum BlockerError: LocalizedError {
    case unknownGitLab(String)
    case closed
    case cycle

    var errorDescription: String? {
        switch self {
        case .unknownGitLab(let host): "GitLab \(host) není v nastavení připojený."
        case .closed: "Tohle issue nebo MR je už zavřené, není na co čekat."
        case .cycle: "Tahle položka už čeká na tu, ke které ji přidáváš."
        }
    }
}

struct GitLabClient: Sendable {
    private static let session = URLSession(configuration: .ephemeral)
    private static let maxPages = 10
    private static let mentionActions: Set = ["mentioned", "directly_addressed"]

    let apiBase: URL
    let token: String

    // MARK: Reading

    func currentUser() async throws -> GLUser {
        try await get("user")
    }

    func userID(username: String) async throws -> Int {
        let users: [GLUser] = try await get("users", query: [URLQueryItem(name: "username", value: username)])
        guard let user = users.first else { throw GitLabError.unknownUser(username) }
        return user.id
    }

    func tokenScopes() async throws -> [String] {
        let info: GLTokenInfo = try await get("personal_access_tokens/self")
        return info.scopes
    }

    /// Assigned issues, assigned merge requests and merge requests with a pending mention.
    func todoItems(for account: GitLabAccount) async throws -> [TodoItem] {
        let opened = [
            URLQueryItem(name: "scope", value: "assigned_to_me"),
            URLQueryItem(name: "state", value: "opened"),
            URLQueryItem(name: "with_labels_details", value: "true"),
        ]
        async let issues: [GLIssue] = getAll("issues", query: opened)
        async let mergeRequests: [GLMergeRequest] = getAll("merge_requests", query: opened)
        // GitLab cannot filter merge requests by mention, only its to-do list knows about them.
        async let todos: [GLTodo] = getAll("todos", query: [URLQueryItem(name: "state", value: "pending"), URLQueryItem(name: "type", value: "MergeRequest")])

        let issueItems = try await issues.map { Self.item(from: $0, account: account) }
        let assigned = try await mergeRequests
        var mergeRequestItems = assigned.map { Self.item(from: $0, role: .assigned, activityAt: $0.updatedAt, account: account) }

        var seen = Set(assigned.map(\.id))
        let mentions = try await todos
            .filter { Self.mentionActions.contains($0.actionName) }
            .sorted { $0.createdAt > $1.createdAt }
        for todo in mentions {
            guard let mergeRequest = todo.target, mergeRequest.state == "opened", seen.insert(mergeRequest.id).inserted else { continue }
            mergeRequestItems.append(Self.item(from: mergeRequest, role: .mentioned, activityAt: todo.createdAt, account: account))
        }

        let details = await details(of: mergeRequestItems)
        for index in mergeRequestItems.indices {
            let mergeRequestDetails = details[mergeRequestItems[index].id] ?? MergeRequestDetails()
            mergeRequestItems[index].resolvableThreads = mergeRequestDetails.resolvableThreads
            mergeRequestItems[index].unresolvedThreads = mergeRequestDetails.unresolvedThreads
        }
        return Self.group(
            issues: issueItems,
            mergeRequests: mergeRequestItems,
            closedIssues: details.mapValues(\.closedIssueIDs),
            accountID: account.id)
    }

    private struct MergeRequestDetails: Sendable {
        var closedIssueIDs: [Int] = []
        var resolvableThreads = 0
        var unresolvedThreads = 0
    }

    /// Issues each merge request closes and its review threads. Best effort: a failed lookup leaves the defaults,
    /// grouping then falls back to the branch name.
    private func details(of mergeRequests: [TodoItem]) async -> [String: MergeRequestDetails] {
        await withTaskGroup(of: (String, MergeRequestDetails).self) { group in
            for mergeRequest in mergeRequests {
                group.addTask {
                    let path = "projects/\(mergeRequest.projectID)/merge_requests/\(mergeRequest.iid)"
                    async let issues: [GLIssueReference]? = try? get("\(path)/closes_issues")
                    // The list's `blocking_discussions_resolved` stays true unless the project requires resolved threads.
                    async let discussions: [GLDiscussion]? = try? getAll("\(path)/discussions", query: [])
                    var details = MergeRequestDetails()
                    details.closedIssueIDs = await issues?.map(\.id) ?? []
                    let threads = await discussions?.filter(\.isResolvable) ?? []
                    details.resolvableThreads = threads.count
                    details.unresolvedThreads = threads.filter(\.isUnresolved).count
                    return (mergeRequest.id, details)
                }
            }
            var details: [String: MergeRequestDetails] = [:]
            for await (mergeRequestID, mergeRequestDetails) in group {
                details[mergeRequestID] = mergeRequestDetails
            }
            return details
        }
    }

    /// Puts each merge request under the issue it closes, so an issue and its MR show up as one item.
    static func group(issues: [TodoItem], mergeRequests: [TodoItem], closedIssues: [String: [Int]], accountID: UUID) -> [TodoItem] {
        var issues = issues
        var standalone: [TodoItem] = []
        for mergeRequest in mergeRequests {
            let closed = Set((closedIssues[mergeRequest.id] ?? []).map { TodoItem.makeID(accountID: accountID, kind: .issue, gitLabID: $0) })
            let branchIID = mergeRequest.sourceBranch.flatMap(issueIID(fromBranch:))
            let index = issues.firstIndex { closed.contains($0.id) }
                ?? issues.firstIndex { $0.projectID == mergeRequest.projectID && $0.iid == branchIID }
            if let index {
                issues[index].mergeRequests.append(mergeRequest)
            } else {
                standalone.append(mergeRequest)
            }
        }
        return issues + standalone
    }

    /// GitLab names branches created from an issue `<iid>-<title>`.
    static func issueIID(fromBranch branch: String) -> Int? {
        branch.prefixMatch(of: /(\d+)-/).flatMap { Int($0.1) }
    }

    /// Any issue or merge request, assigned to the user or not. `project` is a numeric ID or a `group/project` path.
    func workItem(project: String, kind: ItemKind, iid: Int) async throws -> GLWorkItem {
        let encodedProject = project.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? project
        return try await get("projects/\(encodedProject)/\(kind == .issue ? "issues" : "merge_requests")/\(iid)")
    }

    /// Open issues and merge requests the user can see: by number, the given project first, or by words in the title.
    /// `onlyInProject` keeps the search inside that project; without a project it searches the whole GitLab.
    func searchWorkItems(_ text: String, projectID: Int?, onlyInProject: Bool = false) async throws -> [(ItemKind, GLWorkItem)] {
        let opened = [URLQueryItem(name: "state", value: "opened")]
        let everywhere = [URLQueryItem(name: "scope", value: "all"), URLQueryItem(name: "per_page", value: "10")]
        if let iid = Int(text.trimmingCharacters(in: CharacterSet(charactersIn: "#! "))) {
            let number = [URLQueryItem(name: "iids[]", value: String(iid))]
            var local: [(ItemKind, GLWorkItem)] = []
            if let projectID {
                async let issues: [GLWorkItem] = get("projects/\(projectID)/issues", query: opened + number)
                async let mergeRequests: [GLWorkItem] = get("projects/\(projectID)/merge_requests", query: opened + number)
                local = try await issues.map { (ItemKind.issue, $0) } + mergeRequests.map { (ItemKind.mergeRequest, $0) }
            }
            guard !onlyInProject else { return local }
            let elsewhere: [GLWorkItem] = try await get("issues", query: opened + number + everywhere)
            return local + elsewhere.filter { $0.projectId != projectID }.map { (ItemKind.issue, $0) }
        }
        let words = opened + everywhere + [
            URLQueryItem(name: "search", value: text),
            URLQueryItem(name: "in", value: "title"),
            URLQueryItem(name: "order_by", value: "updated_at"),
        ]
        let scope = onlyInProject ? projectID.map { "projects/\($0)/" } ?? "" : ""
        async let issues: [GLWorkItem] = get("\(scope)issues", query: words)
        async let mergeRequests: [GLWorkItem] = get("\(scope)merge_requests", query: words)
        return try await issues.map { (ItemKind.issue, $0) } + mergeRequests.prefix(5).map { (ItemKind.mergeRequest, $0) }
    }

    // MARK: Actions

    /// Does what the "Create merge request" button on an issue does, reusing a branch or MR left over from an earlier attempt.
    func createMergeRequest(for issue: TodoItem, branch: String, targetBranch: String, assigneeID: Int) async throws -> GLMergeRequest {
        let project = "projects/\(issue.projectID)"
        let existing: [GLMergeRequest] = try await get("\(project)/merge_requests", query: [
            URLQueryItem(name: "source_branch", value: branch),
            URLQueryItem(name: "state", value: "opened"),
        ])
        if let mergeRequest = existing.first {
            return mergeRequest
        }

        do {
            let _: GLBranch = try await get("\(project)/repository/branches/\(branch)")
        } catch GitLabError.notFound {
            let _: GLBranch = try await send("POST", "\(project)/repository/branches", body: NewBranch(branch: branch, ref: targetBranch))
        }

        return try await send("POST", "\(project)/merge_requests", body: NewMergeRequest(
            sourceBranch: branch,
            targetBranch: targetBranch,
            title: "Draft: Resolve \"\(issue.title)\"",
            description: "Closes #\(issue.iid)",
            assigneeId: assigneeID))
    }

    func markReady(projectID: Int, iid: Int, addingReviewer reviewerID: Int?) async throws -> GLMergeRequest {
        let path = "projects/\(projectID)/merge_requests/\(iid)"
        let current: GLMergeRequest = try await get(path)
        let reviewerIDs = (current.reviewers ?? []).map(\.id)

        var update = MergeRequestUpdate()
        let title = Self.titleWithoutDraftPrefix(current.title)
        if title != current.title {
            update.title = title
        }
        if let reviewerID, !reviewerIDs.contains(reviewerID) {
            update.reviewerIds = reviewerIDs + [reviewerID]
        }
        guard update.title != nil || update.reviewerIds != nil else { return current }
        return try await send("PUT", path, body: update)
    }

    /// Mirrors GitLab's `Issue.to_branch_name`: `<iid>-<parameterized title>`, at most 100 characters.
    static func branchName(for issue: TodoItem) -> String {
        if issue.isConfidential {
            return "\(issue.iid)-confidential-issue"
        }
        let slug = issue.title
            .folding(options: [.diacriticInsensitive, .widthInsensitive], locale: Locale(identifier: "en_US_POSIX"))
            .lowercased()
            .replacing(/[^a-z0-9_-]+/, with: "-")
            .replacing(/-{2,}/, with: "-")
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        var name = slug.isEmpty ? "\(issue.iid)" : "\(issue.iid)-\(slug)"
        if name.count > 100 {
            // Like GitLab, drop the word cut in half by the truncation.
            name = String(name.prefix(100)).replacing(/-[^-]*$/, with: "")
        }
        return name
    }

    /// Removes the prefixes GitLab treats as draft markers.
    static func titleWithoutDraftPrefix(_ title: String) -> String {
        title.replacing(/^(?:\s*(?:\[draft\]|\(draft\)|draft:|draft\s-\s))+\s*/.ignoresCase(), with: "")
    }

    // MARK: Mapping

    private static func item(from issue: GLIssue, account: GitLabAccount) -> TodoItem {
        TodoItem(
            id: TodoItem.makeID(accountID: account.id, kind: .issue, gitLabID: issue.id),
            accountID: account.id,
            accountName: account.name,
            kind: .issue,
            role: .assigned,
            projectID: issue.projectId,
            iid: issue.iid,
            title: issue.title,
            reference: issue.references?.full ?? "#\(issue.iid)",
            webURL: issue.webUrl,
            activityAt: issue.updatedAt,
            dueDate: issue.dueDate.flatMap(day(from:)),
            isConfidential: issue.confidential ?? false,
            hasMergeRequest: (issue.mergeRequestsCount ?? 0) > 0,
            labels: (issue.labels ?? []).map(\.itemLabel))
    }

    private static func item(from mergeRequest: GLMergeRequest, role: ItemRole, activityAt: Date, account: GitLabAccount) -> TodoItem {
        TodoItem(
            id: TodoItem.makeID(accountID: account.id, kind: .mergeRequest, gitLabID: mergeRequest.id),
            accountID: account.id,
            accountName: account.name,
            kind: .mergeRequest,
            role: role,
            projectID: mergeRequest.projectId,
            iid: mergeRequest.iid,
            title: mergeRequest.title,
            reference: mergeRequest.references?.full ?? "!\(mergeRequest.iid)",
            webURL: mergeRequest.webUrl,
            activityAt: activityAt,
            isDraft: mergeRequest.isDraft,
            sourceBranch: mergeRequest.sourceBranch,
            reviewerCount: mergeRequest.reviewers?.count ?? 0,
            // A draft reports `draft_status` even with a conflict, `has_conflicts` is the reliable signal.
            hasConflicts: mergeRequest.hasConflicts == true || mergeRequest.detailedMergeStatus == "conflict",
            labels: (mergeRequest.labels ?? []).map(\.itemLabel))
    }

    private static func day(from string: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withFullDate]
        formatter.timeZone = .current
        return formatter.date(from: string)
    }

    // MARK: Repository for syncing

    /// `path` is `group/project` or a numeric ID.
    func project(path: String) async throws -> GLProject {
        try await get("projects/\(Self.encoded(path))")
    }

    /// A new private repository in the user's namespace, with a README so that it has a default branch.
    func createPrivateProject(name: String) async throws -> GLProject {
        struct Body: Encodable {
            let name: String
            let visibility = "private"
            let initializeWithReadme = true
        }
        return try await send("POST", "projects", body: Body(name: name))
    }

    /// The file on `ref`, nil when it is not there yet.
    func repositoryFile(projectID: Int, path: String, ref: String) async throws -> GLRepositoryFile? {
        do {
            return try await get("projects/\(projectID)/repository/files/\(Self.encoded(path))", query: [URLQueryItem(name: "ref", value: ref)])
        } catch GitLabError.notFound {
            return nil
        }
    }

    /// Creates the file, or updates it when `lastCommitID` is given; GitLab refuses the update when the file changed
    /// after that commit.
    func writeRepositoryFile(projectID: Int, path: String, branch: String, content: String, message: String, lastCommitID: String?) async throws {
        struct Body: Encodable {
            let branch: String
            let content: String
            let commitMessage: String
            let encoding = "text"
            let lastCommitId: String?
        }
        let body = Body(branch: branch, content: content, commitMessage: message, lastCommitId: lastCommitID)
        let _: GLFileWrite = try await send(lastCommitID == nil ? "POST" : "PUT", "projects/\(projectID)/repository/files/\(Self.encoded(path))", body: body)
    }

    private static func encoded(_ component: String) -> String {
        component.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? component
    }

    // MARK: HTTP

    private func get<Response: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> Response {
        let (data, _) = try await perform(makeRequest("GET", path, query: query))
        return try Self.decode(Response.self, from: data)
    }

    private func getAll<Element: Decodable>(_ path: String, query: [URLQueryItem]) async throws -> [Element] {
        var elements: [Element] = []
        var page = 1
        for _ in 0..<Self.maxPages {
            let pageQuery = query + [URLQueryItem(name: "per_page", value: "100"), URLQueryItem(name: "page", value: String(page))]
            let (data, response) = try await perform(makeRequest("GET", path, query: pageQuery))
            elements += try Self.decode([Element].self, from: data)
            guard let next = response.value(forHTTPHeaderField: "x-next-page").flatMap(Int.init) else { break }
            page = next
        }
        return elements
    }

    private func send<Response: Decodable>(_ method: String, _ path: String, body: some Encodable) async throws -> Response {
        var request = makeRequest(method, path)
        let encoder = JSONEncoder()
        encoder.keyEncodingStrategy = .convertToSnakeCase
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let (data, _) = try await perform(request)
        return try Self.decode(Response.self, from: data)
    }

    /// `path` is already percent-encoded, a project path in it has its slashes as `%2F`.
    private func makeRequest(_ method: String, _ path: String, query: [URLQueryItem] = []) -> URLRequest {
        var url = URL(string: apiBase.absoluteString + "/" + path)!
        if !query.isEmpty {
            url.append(queryItems: query)
        }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.httpMethod = method
        request.setValue(token, forHTTPHeaderField: "PRIVATE-TOKEN")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    private func perform(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await Self.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw GitLabError.invalidResponse }
        switch http.statusCode {
        case 200..<300: return (data, http)
        case 401: throw GitLabError.unauthorized
        case 403: throw GitLabError.forbidden
        case 404: throw GitLabError.notFound
        default: throw GitLabError.http(http.statusCode, Self.errorMessage(from: data))
        }
    }

    private static func decode<Value: Decodable>(_ type: Value.Type, from data: Data) throws -> Value {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        decoder.dateDecodingStrategy = .custom { decoder in
            let string = try decoder.singleValueContainer().decode(String.self)
            guard let date = timestamp(from: string) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Neplatné datum \(string)"))
            }
            return date
        }
        do {
            return try decoder.decode(type, from: data)
        } catch let error as DecodingError {
            throw GitLabError.decoding(String(describing: error))
        }
    }

    private static func timestamp(from string: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: string) {
            return date
        }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: string)
    }

    /// GitLab returns `message` as a string, a list or a field → errors map.
    private static func errorMessage(from data: Data) -> String? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        func flatten(_ value: Any?) -> [String] {
            switch value {
            case let string as String: [string]
            case let list as [Any]: list.flatMap { flatten($0) }
            case let map as [String: Any]: map.flatMap { key, value in flatten(value).map { "\(key) \($0)" } }
            default: []
            }
        }
        let messages = flatten(json["message"] ?? json["error"])
        return messages.isEmpty ? nil : messages.joined(separator: ", ")
    }
}

// MARK: - API models

struct GLProject: Decodable, Sendable {
    let id: Int
    let pathWithNamespace: String
    let defaultBranch: String?
    let visibility: String?
}

struct GLRepositoryFile: Decodable, Sendable {
    /// Base64.
    let content: String
    let lastCommitId: String
}

struct GLFileWrite: Decodable, Sendable {
    let filePath: String
}

struct GLUser: Decodable, Sendable {
    let id: Int
    let username: String
}

struct GLTokenInfo: Decodable, Sendable {
    let scopes: [String]
}

struct GLLabel: Decodable, Sendable {
    let name: String
    let color: String?
    let textColor: String?

    private enum CodingKeys: String, CodingKey {
        case name
        case color
        case textColor
    }

    init(from decoder: Decoder) throws {
        // Without `with_labels_details` (e.g. in to-dos) GitLab sends just the names.
        if let name = try? decoder.singleValueContainer().decode(String.self) {
            self.name = name
            color = nil
            textColor = nil
            return
        }
        let container = try decoder.container(keyedBy: CodingKeys.self)
        name = try container.decode(String.self, forKey: .name)
        color = try container.decodeIfPresent(String.self, forKey: .color)
        textColor = try container.decodeIfPresent(String.self, forKey: .textColor)
    }

    var itemLabel: ItemLabel {
        // GitLab's default label colors.
        ItemLabel(name: name, color: color ?? "#6699cc", textColor: textColor ?? "#FFFFFF")
    }
}

struct GLReferences: Decodable, Sendable {
    let short: String?
    let full: String
}

struct GLIssueReference: Decodable, Sendable {
    let id: Int
}

struct GLDiscussion: Decodable, Sendable {
    struct Note: Decodable, Sendable {
        let resolvable: Bool?
        let resolved: Bool?
    }

    let notes: [Note]

    var isResolvable: Bool {
        notes.contains { $0.resolvable == true }
    }

    /// A thread is open while any of its resolvable notes is not resolved, the way GitLab counts them.
    var isUnresolved: Bool {
        notes.contains { $0.resolvable == true && $0.resolved != true }
    }
}

struct GLBranch: Decodable, Sendable {
    let name: String
}

struct GLIssue: Decodable, Sendable {
    let id: Int
    let iid: Int
    let projectId: Int
    let title: String
    let webUrl: URL
    let updatedAt: Date
    let dueDate: String?
    let confidential: Bool?
    let mergeRequestsCount: Int?
    let references: GLReferences?
    let labels: [GLLabel]?
}

/// A search hit an item can be set to wait on.
struct BlockerCandidate: Identifiable, Sendable {
    let accountID: UUID
    let kind: ItemKind
    let workItem: GLWorkItem
    /// Set when the hit is in the user's own list already.
    let listedItemID: String?
    /// Name of the GitLab when it is not the one of the item searched for.
    var gitLabName: String?

    var id: String { blocker.id }

    var blocker: ExternalBlocker {
        ExternalBlocker(
            accountID: accountID,
            projectID: workItem.projectId,
            kind: kind,
            iid: workItem.iid,
            title: workItem.title,
            reference: workItem.references?.full ?? "\(kind == .issue ? "#" : "!")\(workItem.iid)",
            webURL: workItem.webUrl)
    }
}

/// The fields issues and merge requests share.
struct GLWorkItem: Decodable, Sendable {
    let id: Int
    let iid: Int
    let projectId: Int
    let title: String
    let state: String
    let webUrl: URL
    let references: GLReferences?

    var isOpen: Bool {
        state == "opened"
    }
}

struct GLMergeRequest: Decodable, Sendable {
    let id: Int
    let iid: Int
    let projectId: Int
    let title: String
    let state: String?
    let webUrl: URL
    let updatedAt: Date
    let draft: Bool?
    let workInProgress: Bool?
    let sourceBranch: String
    let reviewers: [GLUser]?
    let hasConflicts: Bool?
    let detailedMergeStatus: String?
    let references: GLReferences?
    let labels: [GLLabel]?

    var isDraft: Bool { draft ?? workInProgress ?? false }
}

struct GLTodo: Decodable, Sendable {
    let actionName: String
    let target: GLMergeRequest?
    let createdAt: Date
}

private struct NewBranch: Encodable {
    let branch: String
    let ref: String
}

private struct NewMergeRequest: Encodable {
    let sourceBranch: String
    let targetBranch: String
    let title: String
    let description: String
    let assigneeId: Int
}

private struct MergeRequestUpdate: Encodable {
    var title: String?
    var reviewerIds: [Int]?
}
