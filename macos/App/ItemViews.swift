import AppKit
import SwiftUI

/// References, merge request state and waiting in one secondary line.
struct ItemMeta: View {
    let item: TodoItem

    var body: some View {
        HStack(spacing: 6) {
            Text(item.references)
                .monospacedDigit()
            if let releasedBy = item.releasedBy {
                ReadyMark(finished: releasedBy)
            }
            if item.isDraftStage {
                Tag(title: "Draft", color: .secondary)
            }
            if item.hasMergeConflict {
                ConflictIcon()
            }
            if let threads = item.reviewThreads {
                ThreadsBadge(resolved: threads.resolved, total: threads.total)
            }
            FittingLabels(labels: item.labels)
                .font(.caption2)
            if item.role == .mentioned {
                Tag(title: "zmínka", color: .purple)
            }
            if !item.isPlaced {
                Tag(title: "nové", color: .accentColor)
            }
            if let waitDescription = item.waitDescription {
                WaitMark(description: waitDescription)
            }
            if let note = item.note {
                NoteBadge(note: note)
            }
            if let branch = item.mergeRequest?.sourceBranch, item.mergeRequest?.role == .assigned {
                Label(branch, systemImage: "arrow.triangle.branch")
            }
            if let dueDate = item.dueDate {
                Label {
                    Text(dueDate, format: .dateTime.day().month())
                } icon: {
                    Image(systemName: "calendar")
                }
                .foregroundStyle(dueDate < Calendar.current.startOfDay(for: .now) ? Color.red : Color.green)
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .lineLimit(1)
    }
}

struct Tag: View {
    let title: String
    let color: Color

    var body: some View {
        Text(title)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .background(Capsule().fill(color.opacity(0.14)))
    }
}

/// The item's circle; clicking it runs the next step, like ticking off a task.
struct StageButton: View {
    @Environment(AppModel.self) private var model
    let item: TodoItem
    var size: CGFloat = 18

    var body: some View {
        if let step = item.nextStep {
            Button {
                Task { await model.perform(step.action, onItemWithID: step.target.id) }
            } label: {
                StageCircle(item: item, size: size)
            }
            .buttonStyle(.plain)
            .disabled(model.runningActions.contains(step.target.id))
            .help(step.action.title)
        } else {
            StageCircle(item: item, size: size)
                .help(item.stage == .issue ? "Issue" : "Merge request")
        }
    }
}

struct ActionButton: View {
    @Environment(AppModel.self) private var model
    let step: ItemStep

    var body: some View {
        Button {
            Task { await model.perform(step.action, onItemWithID: step.target.id) }
        } label: {
            Label(step.action.shortTitle, systemImage: step.action.symbol)
        }
        .controlSize(.small)
        .buttonStyle(.borderedProminent)
        .tint(step.action.isProminent ? Color.accentColor : ItemKind.mergeRequest.color)
        .disabled(model.runningActions.contains(step.target.id))
        .help(step.action.title)
    }
}

/// Context menu shared by the list, the timeline and the menu bar panel.
struct ItemMenu: View {
    @Environment(AppModel.self) private var model
    let item: TodoItem

    var body: some View {
        Button("Na začátek pořadí") { model.moveToTop(item) }
        Button(item.isPlaced ? "Na konec pořadí" : "Zařadit na konec") { model.moveToEnd(item) }
        Divider()
        Menu("Čeká na") {
            Button("Hledat issue nebo MR…") {
                model.requestBlockerSearch(for: item.id)
                Windows.showMain()
            }
            Divider()
            ForEach(model.blockerCandidates(for: item)) { candidate in
                Toggle(isOn: Binding(
                    get: { item.wait?.itemIDs.contains(candidate.id) ?? false },
                    set: { _ in model.toggleBlocker(candidate, for: item) })) {
                    Text("\(candidate.shortReference)  \(candidate.title)")
                }
            }
        }
        LinkRemovalMenu(id: item.id, reference: item.references, blockers: blockerLinks)
        if item.isWaiting {
            Button("Už nečeká") { model.stopWaiting(item) }
        }
        Divider()
        ForEach(item.steps) { step in
            Button(step.action.title) {
                Task { await model.perform(step.action, onItemWithID: step.target.id) }
            }
        }
        if let command = item.checkoutCommand {
            Button("Kopírovat git checkout") { Pasteboard.copy(command) }
        }
        Button("Kopírovat odkaz") { Pasteboard.copy(item.webURL.absoluteString) }
        Button(item.kind == .issue ? "Otevřít issue" : "Otevřít MR") { NSWorkspace.shared.open(item.webURL) }
        ForEach(item.mergeRequests) { mergeRequest in
            Button("Otevřít MR \(mergeRequest.localReference)") { NSWorkspace.shared.open(mergeRequest.webURL) }
        }
    }

}

extension ItemMenu {
    /// What the item waits on, as (ID, reference) for the removal menu.
    private var blockerLinks: [(id: String, reference: String)] {
        let listed = (item.wait?.itemIDs ?? []).compactMap { id in model.items.first { $0.id == id }.map { (id, $0.shortReference) } }
        return listed + (item.wait?.external ?? []).map { ($0.id, $0.shortReference) }
    }
}

/// „Odebrat vazbu“ with every link of an item: what it waits on and what waits on it.
struct LinkRemovalMenu: View {
    @Environment(AppModel.self) private var model
    let id: String
    let reference: String
    let blockers: [(id: String, reference: String)]

    var body: some View {
        let dependents = model.dependents(of: id)
        if !blockers.isEmpty || !dependents.isEmpty {
            Menu("Odebrat vazbu") {
                ForEach(blockers, id: \.id) { blocker in
                    Button("\(reference) čeká na \(blocker.reference)") { model.removeLink(id, waitsOn: blocker.id) }
                }
                ForEach(dependents) { dependent in
                    Button("\(dependent.shortReference) čeká na \(reference)") { model.removeLink(dependent.id, waitsOn: id) }
                }
            }
        }
    }
}

struct ErrorBanner: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let failing = model.accounts.filter { model.accountErrors[$0.id] != nil }
        if !failing.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(failing) { account in
                    Label("\(account.name): \(model.accountErrors[account.id] ?? "")", systemImage: "exclamationmark.triangle.fill")
                }
            }
            .font(.caption)
            .foregroundStyle(.orange)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(Color.orange.opacity(0.08))
        }
    }
}
