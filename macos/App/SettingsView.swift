import ServiceManagement
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var draft: AccountDraft?
    @State private var launchesAtLogin = SMAppService.mainApp.status == .enabled
    @State private var loginItemError: String?

    var body: some View {
        Form {
            Section("GitLab") {
                ForEach(model.accounts) { account in
                    AccountRow(account: account, error: model.accountErrors[account.id]) {
                        draft = AccountDraft(account)
                    } onRemove: {
                        model.removeAccount(account)
                    }
                }
                Button("Přidat GitLab…") {
                    draft = AccountDraft()
                }
            }
            SyncSection()
            Section("Obnovování") {
                Picker("Interval", selection: Binding(get: { model.refreshInterval }, set: { model.setRefreshInterval($0) })) {
                    ForEach(AppModel.refreshIntervals, id: \.self) { interval in
                        Text("\(Int(interval / 60)) min")
                            .tag(interval)
                    }
                }
                Toggle("Spouštět po přihlášení", isOn: Binding(get: { launchesAtLogin }, set: { updateLoginItem($0) }))
                if let loginItemError {
                    Text(loginItemError)
                        .font(.caption)
                        .foregroundStyle(.red)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 640)
        .sheet(item: $draft) { draft in
            AccountEditor(draft: draft)
        }
    }

    private func updateLoginItem(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            loginItemError = nil
        } catch {
            loginItemError = error.localizedDescription
        }
        launchesAtLogin = SMAppService.mainApp.status == .enabled
    }
}

private struct AccountRow: View {
    let account: GitLabAccount
    let error: String?
    let onEdit: () -> Void
    let onRemove: () -> Void

    var body: some View {
        HStack {
            Image(systemName: error == nil ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(error == nil ? Color.green : Color.orange)
                .help(error ?? "Připojeno")
            VStack(alignment: .leading, spacing: 2) {
                Text(account.name)
                Text(details)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button("Upravit", action: onEdit)
            Button(role: .destructive, action: onRemove) {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .help("Odebrat")
        }
    }

    private var details: String {
        var parts = [account.baseURL, "→ \(account.targetBranch)"]
        if !account.defaultReviewer.isEmpty {
            parts.append("reviewer @\(account.defaultReviewer)")
        }
        return parts.joined(separator: " · ")
    }
}

private struct AccountDraft: Identifiable {
    let id: UUID
    let isNew: Bool
    var name: String
    var baseURL: String
    var targetBranch: String
    var defaultReviewer: String

    init(_ account: GitLabAccount? = nil) {
        id = account?.id ?? UUID()
        isNew = account == nil
        name = account?.name ?? ""
        baseURL = account?.baseURL ?? ""
        targetBranch = account?.targetBranch ?? "master"
        defaultReviewer = account?.defaultReviewer ?? ""
    }
}

private struct AccountEditor: View {
    private enum Status {
        case idle
        case working(String)
        case success(String)
        case failure(String)
    }

    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var draft: AccountDraft
    @State private var token = ""
    @State private var status = Status.idle

    init(draft: AccountDraft) {
        _draft = State(initialValue: draft)
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("Název", text: $draft.name, prompt: Text("Firemní GitLab"))
                    TextField("URL", text: $draft.baseURL, prompt: Text("https://gitlab.example.com"))
                }
                Section {
                    SecureField("Token", text: $token, prompt: Text(draft.isNew ? "glpat-…" : "Ponechat uložený"))
                } footer: {
                    Text("Personal Access Token musí mít scope api.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Section("Akce") {
                    TextField("Cílová větev MR", text: $draft.targetBranch, prompt: Text("master"))
                    TextField("Výchozí reviewer", text: $draft.defaultReviewer, prompt: Text("uživatelské jméno"))
                }
            }
            .formStyle(.grouped)
            Divider()
            HStack(spacing: 8) {
                statusView
                Spacer()
                Button("Ověřit", action: verify)
                    .disabled(apiURL == nil || effectiveToken.isEmpty || isWorking)
                Button("Zrušit", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Uložit", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canSave)
            }
            .padding(12)
        }
        .frame(width: 500, height: 480)
    }

    private var apiURL: URL? {
        GitLabAccount.apiURL(from: draft.baseURL)
    }

    private var effectiveToken: String {
        let typed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        return typed.isEmpty ? (model.token(for: draft.id) ?? "") : typed
    }

    private var canSave: Bool {
        !draft.name.trimmingCharacters(in: .whitespaces).isEmpty && apiURL != nil && !effectiveToken.isEmpty
    }

    private var isWorking: Bool {
        if case .working = status { true } else { false }
    }

    @ViewBuilder
    private var statusView: some View {
        switch status {
        case .idle:
            EmptyView()
        case .working(let message):
            HStack(spacing: 6) {
                ProgressView()
                    .controlSize(.small)
                Text(message)
            }
            .font(.caption)
        case .success(let message):
            Label(message, systemImage: "checkmark.circle.fill")
                .font(.caption)
                .foregroundStyle(.green)
        case .failure(let message):
            Label(message, systemImage: "xmark.octagon.fill")
                .font(.caption)
                .foregroundStyle(.red)
                .lineLimit(3)
        }
    }

    private func verify() {
        guard let apiURL else { return }
        let client = GitLabClient(apiBase: apiURL, token: effectiveToken)
        let reviewer = GitLabAccount.username(draft.defaultReviewer)
        status = .working("Ověřuji…")
        Task {
            do {
                let user = try await client.currentUser()
                var message = "Přihlášen jako @\(user.username)"
                if let scopes = try? await client.tokenScopes(), !scopes.contains("api") {
                    status = .failure("\(message), ale token nemá scope api, akce nebudou fungovat")
                    return
                }
                if !reviewer.isEmpty {
                    _ = try await client.userID(username: reviewer)
                    message += ", reviewer @\(reviewer) existuje"
                }
                status = .success(message)
            } catch {
                status = .failure(error.localizedDescription)
            }
        }
    }

    private func save() {
        let branch = draft.targetBranch.trimmingCharacters(in: .whitespaces)
        let account = GitLabAccount(
            id: draft.id,
            name: draft.name.trimmingCharacters(in: .whitespaces),
            baseURL: GitLabAccount.normalizedBaseURL(draft.baseURL),
            targetBranch: branch.isEmpty ? "master" : branch,
            defaultReviewer: GitLabAccount.username(draft.defaultReviewer))
        let typed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        model.saveAccount(account, token: typed.isEmpty ? nil : typed)
        dismiss()
    }
}

/// The plan kept in a repository on one of the GitLabs, so that other devices see the same order, links and notes.
private struct SyncSection: View {
    @Environment(AppModel.self) private var model
    @State private var accountID: UUID?
    @State private var project = ""
    @State private var isCreating = false
    @State private var error: String?

    var body: some View {
        Section {
            Picker("GitLab", selection: $accountID) {
                Text("Vypnuto").tag(UUID?.none)
                ForEach(model.accounts) { account in
                    Text(account.name).tag(UUID?.some(account.id))
                }
            }
            if accountID != nil {
                TextField("Repozitář", text: $project, prompt: Text("skupina/gitlab-todo-data"))
                HStack {
                    Button("Vytvořit soukromé repo", action: createRepository)
                        .disabled(isCreating)
                        .help("Založí soukromý repozitář gitlab-todo-data pod tvým účtem")
                    if isCreating {
                        ProgressView()
                            .controlSize(.small)
                    }
                    Spacer()
                    Button("Použít", action: apply)
                        .disabled(!hasChanges || Self.projectPath(project).isEmpty)
                }
            }
            if let state = model.syncState, !hasChanges {
                HStack {
                    status(state)
                    Spacer()
                    Button("Synchronizovat teď") {
                        Task { await model.syncPlan() }
                    }
                    .disabled(model.syncStatus == .syncing)
                }
                if let visibility = state.visibility, visibility != "private" {
                    Label("Repozitář není soukromý, poznámky v něm uvidí i ostatní.", systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                }
            }
            if let error {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        } header: {
            Text("Synchronizace")
        } footer: {
            Text("Pořadí, vazby, odklady a poznámky se ukládají do souboru gitlab-todo.json v tomto repozitáři, změny se odešlou při obnovení. Na dalším počítači zvol stejný GitLab a repozitář. Tokeny ani nastavení se nesynchronizují.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .onAppear {
            accountID = model.syncState?.settings.accountID
            project = model.syncState?.settings.project ?? ""
        }
        .onChange(of: accountID) {
            error = nil
            // Turning it off needs no confirming.
            if accountID == nil, model.syncState != nil {
                model.configureSync(nil)
            }
        }
    }

    private var hasChanges: Bool {
        accountID != model.syncState?.settings.accountID || Self.projectPath(project) != (model.syncState?.settings.project ?? "")
    }

    @ViewBuilder
    private func status(_ state: SyncState) -> some View {
        switch model.syncStatus {
        case .syncing:
            HStack(spacing: 6) {
                ProgressView()
                    .controlSize(.small)
                Text("Synchronizuji…")
            }
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
                .lineLimit(3)
        case .synced(let date):
            Label("Synchronizováno \(date.formatted(.relative(presentation: .named)))", systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
        case .idle:
            if let date = state.syncedAt {
                Text("Synchronizováno \(date.formatted(.relative(presentation: .named)))")
                    .foregroundStyle(.secondary)
            } else {
                Text("Zatím nesynchronizováno")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func apply() {
        guard let accountID else { return }
        error = nil
        model.configureSync(SyncSettings(accountID: accountID, project: Self.projectPath(project)))
    }

    private func createRepository() {
        guard let accountID else { return }
        isCreating = true
        error = nil
        Task {
            do {
                project = try await model.createSyncRepository(accountID: accountID)
                apply()
            } catch {
                self.error = error.localizedDescription
            }
            isCreating = false
        }
    }

    /// `group/project` also from a pasted URL of the repository.
    static func projectPath(_ input: String) -> String {
        var path = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let url = URL(string: path), url.scheme?.hasPrefix("http") == true {
            path = url.path()
        }
        if let range = path.range(of: "/-/") {
            path = String(path[..<range.lowerBound])
        }
        if path.hasSuffix(".git") {
            path.removeLast(4)
        }
        return path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }
}
