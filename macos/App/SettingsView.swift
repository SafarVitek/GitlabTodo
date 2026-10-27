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
        .frame(width: 500, height: 420)
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
                    HStack {
                        Spacer()
                        Button("Vytvořit token přes SSH", action: createTokenOverSSH)
                            .disabled(sshTarget == nil || isWorking)
                    }
                } footer: {
                    Text(tokenHint)
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

    private var sshTarget: String? {
        apiURL?.host().map { "git@\($0)" }
    }

    private var tokenHint: String {
        "Personal Access Token se scope api, akce zakládají větve a MR. Tlačítko SSH ho vytvoří příkazem "
            + "„ssh \(sshTarget ?? "git@…") personal_access_token“ pomocí tvého SSH klíče (platnost 365 dní)."
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

    private func createTokenOverSSH() {
        guard let sshTarget else { return }
        status = .working("Vytvářím token přes SSH…")
        Task {
            do {
                token = try await SSHTokenCreator.create(target: sshTarget)
                status = .success("Token vytvořen, nezapomeň uložit")
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
