import AppKit
import SwiftUI

/// Search for what an item waits on: words from the title, a number or a pasted link.
struct BlockerSearch: View {
    @Environment(AppModel.self) private var model
    let item: TodoItem
    var isFocused: FocusState<Bool>.Binding

    var body: some View {
        WorkItemSearch(
            prompt: "slovo z názvu, číslo nebo odkaz",
            ownProjectID: item.projectID,
            isFocused: isFocused,
            suggestions: model.blockerCandidates(for: item).filter { !(item.wait?.itemIDs.contains($0.id) ?? false) },
            otherGitLab: { $0.accountID == item.accountID ? nil : $0.accountName },
            search: { try await model.searchBlockers($0, for: item) },
            pickSuggestion: { model.toggleBlocker($0, for: item) },
            pick: { try model.addBlocker($0, for: item) })
            .id(item.id)
    }
}

/// Search field with suggestions while typing. Before typing it offers `suggestions`, the user's own items.
struct WorkItemSearch: View {
    @State private var query = ""
    @State private var results: [BlockerCandidate] = []
    @State private var isSearching = false
    @State private var error: String?
    let prompt: String
    let ownProjectID: Int
    var isFocused: FocusState<Bool>.Binding
    var suggestions: [TodoItem] = []
    /// Name of the GitLab a suggestion comes from when it is not the item's own.
    var otherGitLab: (TodoItem) -> String? = { _ in nil }
    let search: (String) async throws -> [BlockerCandidate]
    var pickSuggestion: (TodoItem) -> Void = { _ in }
    let pick: (BlockerCandidate) throws -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass")
                    .foregroundStyle(.secondary)
                TextField("Hledat issue nebo MR", text: $query, prompt: Text(prompt))
                    .textFieldStyle(.plain)
                    .focused(isFocused)
                    .onSubmit {
                        if let first = results.first {
                            add(first)
                        }
                    }
                if isSearching {
                    ProgressView()
                        .controlSize(.small)
                }
            }
            if query.isEmpty, isFocused.wrappedValue, !suggestions.isEmpty {
                Text("Z tvého seznamu")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.top, 4)
                ForEach(suggestions.prefix(6)) { listed in
                    Button {
                        pickSuggestion(listed)
                    } label: {
                        SuggestionRow(
                            kind: listed.stage,
                            reference: listed.projectID == ownProjectID ? listed.localReference : listed.shortReference,
                            title: listed.title,
                            gitLabName: otherGitLab(listed))
                    }
                    .buttonStyle(.plain)
                }
            }
            ForEach(results) { candidate in
                Button {
                    add(candidate)
                } label: {
                    let blocker = candidate.blocker
                    SuggestionRow(
                        kind: candidate.kind,
                        reference: blocker.projectID == ownProjectID ? String(blocker.reference.drop { $0 != "#" && $0 != "!" }) : blocker.shortReference,
                        title: blocker.title,
                        isListed: candidate.listedItemID != nil,
                        gitLabName: candidate.gitLabName)
                }
                .buttonStyle(.plain)
                .help(candidate.blocker.reference)
            }
            if let error {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
            } else if isLongEnough, !isSearching, results.isEmpty {
                Text("Nic otevřeného jsem nenašel")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .task(id: query) {
            await runSearch()
        }
    }

    /// Words need three letters for GitLab's search, a number or a link works right away.
    private var isLongEnough: Bool {
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return text.count >= 3 || Int(text.trimmingCharacters(in: CharacterSet(charactersIn: "#!"))) != nil
    }

    private func runSearch() async {
        guard isLongEnough else {
            results = []
            error = nil
            return
        }
        // Wait until the user stops typing.
        try? await Task.sleep(for: .milliseconds(300))
        guard !Task.isCancelled else { return }
        isSearching = true
        defer { isSearching = false }
        do {
            let found = try await search(query.trimmingCharacters(in: .whitespacesAndNewlines))
            guard !Task.isCancelled else { return }
            results = found
            error = nil
        } catch {
            guard !Task.isCancelled else { return }
            results = []
            self.error = error.localizedDescription
        }
    }

    private func add(_ candidate: BlockerCandidate) {
        do {
            try pick(candidate)
            query = ""
        } catch {
            self.error = error.localizedDescription
        }
    }
}

struct SuggestionRow: View {
    @State private var isHovered = false
    let kind: ItemKind
    let reference: String
    let title: String
    var isListed = false
    var gitLabName: String?

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: kind == .issue ? "circle" : "arrow.triangle.merge")
                .foregroundStyle(kind.color)
                .frame(width: 14)
            Text(reference)
                .monospacedDigit()
                .foregroundStyle(.secondary)
            Text(title)
                .lineLimit(1)
            Spacer(minLength: 4)
            if let gitLabName {
                Tag(title: gitLabName, color: .blue)
            }
            if isListed {
                Tag(title: "v seznamu", color: .secondary)
            }
        }
        .font(.callout)
        .padding(.vertical, 3)
        .padding(.horizontal, 4)
        .background(RoundedRectangle(cornerRadius: 5).fill(isHovered ? Color.accentColor.opacity(0.15) : Color.clear))
        .contentShape(Rectangle())
        .onHover { isHovered = $0 }
    }
}
