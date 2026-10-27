import AppKit
import SwiftUI

/// Detail of the selected item: what it is, its next step, its place in the order and what it waits on.
struct ItemInspector: View {
    @Environment(AppModel.self) private var model
    @FocusState private var isSearchFocused: Bool
    let item: TodoItem?

    var body: some View {
        if let item {
            ScrollView {
                InspectorContent(item: item, isSearchFocused: $isSearchFocused)
            }
            .onAppear { focusSearchIfRequested(for: item) }
            .onChange(of: model.focusRequest) { focusSearchIfRequested(for: item) }
        } else {
            ContentUnavailableView("Vyber položku", systemImage: "sidebar.right", description: Text("Tady uvidíš detail a nastavíš, na co čeká."))
        }
    }

    private func focusSearchIfRequested(for item: TodoItem) {
        guard model.focusRequest.searchesBlocker, model.focusRequest.itemID == item.id else { return }
        // The field exists only after this update, focus it in the next one.
        Task { isSearchFocused = true }
    }
}

struct InspectorContent: View {
    let item: TodoItem
    var isSearchFocused: FocusState<Bool>.Binding

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            Header(item: item)
            // A waiting item is not up yet, its actions stay in the context menu.
            if let step = item.nextStep, !item.isWaiting {
                PrimaryStepButton(step: step)
            }
            NoteSection(item: item)
            OrderSection(item: item)
            WaitSection(item: item, isSearchFocused: isSearchFocused)
            if let mergeRequest = item.mergeRequest {
                MergeRequestSection(item: item, mergeRequest: mergeRequest)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct Header: View {
    let item: TodoItem

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                StageCircle(item: item, size: 15)
                Text(item.stage == .issue ? "Issue" : "Merge request")
                    .fontWeight(.semibold)
                    .foregroundStyle(item.stage.color)
                Text("·")
                    .foregroundStyle(.tertiary)
                ProjectLabel(item: item)
                Spacer(minLength: 4)
                Button {
                    NSWorkspace.shared.open(item.webURL)
                } label: {
                    Image(systemName: "arrow.up.forward.square")
                }
                .buttonStyle(.borderless)
                .help(item.kind == .issue ? "Otevřít issue v GitLabu" : "Otevřít MR v GitLabu")
            }
            .font(.caption)
            Text(item.title)
                .font(.title3.weight(.semibold))
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
            ItemMeta(item: item)
        }
    }
}

private struct PrimaryStepButton: View {
    @Environment(AppModel.self) private var model
    let step: ItemStep

    var body: some View {
        Button {
            Task { await model.perform(step.action, onItemWithID: step.target.id) }
        } label: {
            Label(step.action.title, systemImage: step.action.symbol)
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        .tint(step.action.isProminent ? Color.accentColor : ItemKind.mergeRequest.color)
        .disabled(model.runningActions.contains(step.target.id))
    }
}

private struct SectionTitle: View {
    let title: String
    let systemImage: String

    var body: some View {
        Label(title, systemImage: systemImage)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.secondary)
    }
}

/// Rounded background for fields and cards in the inspector.
private struct Box<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        content
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.05)))
    }
}

/// The user's notes on the item, e.g. from a meeting. Shown with the tasks as checkboxes, a click on the text edits it.
private struct NoteSection: View {
    @Environment(AppModel.self) private var model
    @FocusState private var isFocused: Bool
    @State private var isEditing = false
    @State private var text = ""
    @State private var selection: TextSelection?
    let item: TodoItem

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                SectionTitle(title: "Poznámky", systemImage: "note.text")
                Spacer(minLength: 4)
                if let note = item.note, !note.tasks.isEmpty {
                    NoteBadge(note: note)
                        .font(.caption)
                }
                Button(action: addTask) {
                    Label("Úkol", systemImage: "plus")
                        .font(.caption.weight(.medium))
                }
                .buttonStyle(.borderless)
                .help("Přidat úkol „- [ ] …“ na konec poznámky")
            }
            if isEditing {
                editor
            } else if let note = item.note {
                NoteView(note: note) { line in
                    model.toggleTask(line: line, forItemID: item.id)
                } onEdit: {
                    startEditing()
                }
                Text("Upraveno \(note.updatedAt.formatted(.relative(presentation: .named)))")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            } else {
                Button(action: startEditing) {
                    Label("Zápis z meetingu, úkoly, cokoli k tomuhle", systemImage: "square.and.pencil")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.05)))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .onAppear(perform: editIfRequested)
        .onChange(of: model.noteToEdit) { editIfRequested() }
        .onChange(of: item.id) { oldID, _ in
            if isEditing {
                model.setNote(text, forItemID: oldID)
                isEditing = false
            }
            editIfRequested()
        }
        .onChange(of: isFocused) {
            if !isFocused {
                finishEditing()
            }
        }
        // Saved a moment after typing stops, so the widget follows without a save button.
        .task(id: text) {
            guard isEditing else { return }
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled, isEditing else { return }
            model.setNote(text, forItemID: item.id)
        }
    }

    private var editor: some View {
        VStack(alignment: .leading, spacing: 6) {
            TextEditor(text: $text, selection: $selection)
                .font(.callout)
                .scrollContentBackground(.hidden)
                .focused($isFocused)
                .frame(minHeight: 120)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.05)))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Color.accentColor.opacity(0.6), lineWidth: 1))
            HStack {
                Text("Úkol napíšeš jako „- [ ] text“, ukládá se průběžně.")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                Spacer(minLength: 4)
                Button("Hotovo", action: finishEditing)
                    .controlSize(.small)
                    .keyboardShortcut(.return, modifiers: .command)
            }
        }
    }

    /// A click on the item in the widget lands here, ready to write.
    private func editIfRequested() {
        if model.takeNoteToEdit(item.id) {
            startEditing()
        }
    }

    /// The note as stored now; `item` can be a moment behind right after a save.
    private var storedText: String {
        model.item(withID: item.id)?.note?.text ?? ""
    }

    private func startEditing() {
        if !isEditing {
            text = storedText
            isEditing = true
        }
        selection = TextSelection(insertionPoint: text.endIndex)
        // The editor exists only after this update, focus it in the next one.
        Task { isFocused = true }
    }

    private func addTask() {
        if !isEditing {
            text = storedText
        }
        if !text.isEmpty, !text.hasSuffix("\n") {
            text += "\n"
        }
        text += "- [ ] "
        startEditing()
    }

    private func finishEditing() {
        guard isEditing else { return }
        model.setNote(text, forItemID: item.id)
        isEditing = false
    }
}

/// A note as text, with its tasks as checkboxes.
private struct NoteView: View {
    let note: ItemNote
    let onToggle: (Int) -> Void
    let onEdit: () -> Void

    var body: some View {
        let tasks = Dictionary(note.tasks.map { ($0.line, $0) }, uniquingKeysWith: { first, _ in first })
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Array(note.lines.enumerated()), id: \.offset) { index, line in
                if let task = tasks[index] {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Button {
                            onToggle(index)
                        } label: {
                            Image(systemName: task.isDone ? "checkmark.square.fill" : "square")
                                .foregroundStyle(task.isDone ? Color.green : Color.secondary)
                        }
                        .buttonStyle(.plain)
                        .help(task.isDone ? "Vrátit jako nehotové" : "Hotovo")
                        Text(Self.inline(task.text))
                            .strikethrough(task.isDone)
                            .foregroundStyle(task.isDone ? .secondary : .primary)
                    }
                } else if line.trimmingCharacters(in: .whitespaces).isEmpty {
                    Color.clear.frame(height: 2)
                } else {
                    Text(Self.inline(Self.bulleted(line)))
                }
            }
        }
        .font(.callout)
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.05)))
        .contentShape(Rectangle())
        .onTapGesture(perform: onEdit)
        .help("Kliknutím upravíš")
    }

    /// `- point` shown as `• point`.
    private static func bulleted(_ line: String) -> String {
        guard let match = line.wholeMatch(of: /(\s*)[-*+]\s+(.*)/) else { return line }
        return "\(match.1)•  \(match.2)"
    }

    /// Bold, italics, code and links as in Markdown, anything else as written.
    private static func inline(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    }
}

private struct OrderSection: View {
    @Environment(AppModel.self) private var model
    let item: TodoItem

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title: "Pořadí", systemImage: "list.number")
            HStack {
                Text(position)
                    .fontWeight(.medium)
                Spacer()
                ControlGroup {
                    Button {
                        model.moveToTop(item)
                    } label: {
                        Image(systemName: "arrow.up.to.line")
                    }
                    .help("Na začátek pořadí")
                    Button {
                        model.moveToEnd(item)
                    } label: {
                        Image(systemName: "arrow.down.to.line")
                    }
                    .help(item.isPlaced ? "Na konec pořadí" : "Zařadit na konec pořadí")
                }
                .fixedSize()
            }
            Text("Jinak pořadí měníš přetažením v seznamu.")
                .font(.caption)
                .foregroundStyle(.tertiary)
        }
    }

    private var position: String {
        if item.isWaiting {
            return "Čeká, zatím není na řadě"
        }
        guard let number = model.items.workNumbers()[item.id] else { return "Nezařazená" }
        return "\(number). na řadě"
    }
}

private struct WaitSection: View {
    @Environment(AppModel.self) private var model
    @FocusState private var isEditingNote: Bool
    @State private var note = ""
    let item: TodoItem
    var isSearchFocused: FocusState<Bool>.Binding

    var body: some View {
        let listed = model.items.filter { item.wait?.itemIDs.contains($0.id) ?? false }
        let external = item.wait?.external ?? []
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title: "Čeká na", systemImage: "pause.circle")
            if !listed.isEmpty || !external.isEmpty {
                VStack(spacing: 2) {
                    ForEach(listed) { blocker in
                        BlockerRow(
                            kind: blocker.stage,
                            reference: blocker.projectKey == item.projectKey ? blocker.localReference : blocker.shortReference,
                            title: blocker.title,
                            url: blocker.webURL,
                            onRemove: { model.toggleBlocker(blocker, for: item) })
                    }
                    ForEach(external) { blocker in
                        BlockerRow(
                            kind: blocker.kind,
                            reference: blocker.shortReference,
                            title: blocker.title,
                            url: blocker.webURL,
                            onRemove: { model.removeBlocker(blocker, for: item) })
                    }
                }
            }
            Box {
                BlockerSearch(item: item, isFocused: isSearchFocused)
            }
            Box {
                HStack(spacing: 6) {
                    Image(systemName: "text.bubble")
                        .foregroundStyle(.secondary)
                    TextField("Jiný důvod", text: $note, prompt: Text("Jiný důvod, např. odpověď zákazníka"))
                        .textFieldStyle(.plain)
                        .focused($isEditingNote)
                        .onSubmit { model.setWaitNote(note, forItemID: item.id) }
                }
            }
            Text("Až se všechno dokončí, čekání skončí samo a položka se ukáže jako připravená.")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .onAppear { note = item.wait?.note ?? "" }
        .onChange(of: item.id) { oldID, _ in
            model.setWaitNote(note, forItemID: oldID)
            note = item.wait?.note ?? ""
        }
        .onChange(of: isEditingNote) {
            if !isEditingNote {
                model.setWaitNote(note, forItemID: item.id)
            }
        }
    }
}

private struct BlockerRow: View {
    @State private var isHovered = false
    let kind: ItemKind
    let reference: String
    let title: String
    let url: URL
    let onRemove: () -> Void

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
            Button(action: onRemove) {
                Image(systemName: "xmark.circle.fill")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .opacity(isHovered ? 1 : 0.4)
            .help("Už na to nečeká")
        }
        .font(.callout)
        .padding(.vertical, 4)
        .padding(.horizontal, 6)
        .background(RoundedRectangle(cornerRadius: 6).fill(Palette.waiting.opacity(isHovered ? 0.14 : 0.08)))
        .contentShape(Rectangle())
        .onHover { isHovered = $0 }
        .onTapGesture(count: 2) { NSWorkspace.shared.open(url) }
        .help("Dvojklikem otevřeš v GitLabu")
    }
}

private struct MergeRequestSection: View {
    @Environment(AppModel.self) private var model
    let item: TodoItem
    let mergeRequest: TodoItem

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title: "Merge request", systemImage: "arrow.triangle.merge")
            Box {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 6) {
                        Text(mergeRequest.shortReference)
                            .monospacedDigit()
                            .fontWeight(.medium)
                        Tag(title: state, color: mergeRequest.isDraft ? .secondary : .green)
                        Spacer()
                        if mergeRequest.id != item.id {
                            Button("Otevřít") { NSWorkspace.shared.open(mergeRequest.webURL) }
                                .controlSize(.small)
                        }
                    }
                    if mergeRequest.hasConflicts {
                        Label("Merge konflikt, je potřeba ho vyřešit", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                    }
                    if let threads = item.reviewThreads {
                        Label(
                            threads.resolved == threads.total ? "Všechna vlákna vyřešená" : "Vyřešeno \(threads.resolved) z \(threads.total) vláken",
                            systemImage: "bubble.left.fill")
                            .foregroundStyle(threads.resolved == threads.total ? Color.green : Color.red)
                    }
                    if let branch = mergeRequest.sourceBranch {
                        HStack(spacing: 6) {
                            Label(branch, systemImage: "arrow.triangle.branch")
                                .lineLimit(1)
                                .textSelection(.enabled)
                            Spacer(minLength: 4)
                            if let command = item.checkoutCommand {
                                Button {
                                    Pasteboard.copy(command)
                                } label: {
                                    Image(systemName: "doc.on.doc")
                                }
                                .buttonStyle(.borderless)
                                .help("Kopírovat „\(command)“")
                            }
                        }
                        .foregroundStyle(.secondary)
                    }
                }
                .font(.callout)
            }
        }
    }

    private var state: String {
        mergeRequest.isDraft ? "Draft" : "Ready"
    }
}
