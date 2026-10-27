import AppIntents
import SwiftUI
import WidgetKit

struct TodoEntry: TimelineEntry {
    let date: Date
    let snapshot: Snapshot
    var state = WidgetState()
    /// Size of the widget on screen, rows are counted from it.
    var displaySize = CGSize.zero
}

struct WidgetContent: View {
    /// The widget draws its own margins, so it knows exactly how much room the rows have.
    nonisolated static let margin: CGFloat = 14

    let family: WidgetFamily
    let entry: TodoEntry

    var body: some View {
        let model = WidgetModel(entry: entry, family: family)
        Group {
            switch family {
            case .systemSmall:
                SmallLayout(model: model)
            case .systemExtraLarge:
                ColumnsLayout(model: model)
            default:
                ListLayout(model: model, isLarge: family == .systemLarge)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(Self.margin)
    }
}

/// Filters the snapshot by the widget's buttons. Waiting items keep their place, dimmed and with what they wait on.
struct WidgetModel {
    let snapshot: Snapshot
    let projects: [ProjectTab]
    let project: ProjectTab?
    let kind: KindFilter
    /// Items of the shown project, any kind.
    let projectItems: [TodoItem]
    let waitingCount: Int
    /// Height left for rows below the header.
    let rowsHeight: CGFloat

    init(entry: TodoEntry, family: WidgetFamily) {
        // Typical macOS sizes for when the system does not say.
        let height = entry.displaySize.height > 0 ? entry.displaySize.height : family == .systemSmall || family == .systemMedium ? 158 : 344
        rowsHeight = height - 2 * WidgetContent.margin - 28
        snapshot = entry.snapshot
        projects = ProjectTab.tabs(for: entry.snapshot.items)
        // Falls back to all projects when the remembered one has nothing left.
        project = projects.first { $0.key == entry.state.project }
        kind = entry.state.kind
        projectItems = entry.snapshot.items.filter { [project] item in project == nil || item.projectKey == project?.key }
        waitingCount = entry.snapshot.items.filter { [project] item in item.isWaiting && (project == nil || item.projectKey == project?.key) }.count
    }

    var items: [TodoItem] {
        projectItems.filter(kind.includes)
    }

    func count(_ filter: KindFilter) -> Int {
        projectItems.filter(filter.includes).count
    }
}

// MARK: - Header

private struct Header: View {
    let model: WidgetModel
    var showsKindFilter = true

    var body: some View {
        HStack(spacing: 6) {
            HStack(spacing: 5) {
                if let project = model.project {
                    Circle()
                        .fill(Palette.project(project.color))
                        .frame(width: 8, height: 8)
                    Text(project.name)
                        .foregroundStyle(.primary)
                } else {
                    Text("Na řadě")
                        .foregroundStyle(Palette.headline)
                }
            }
            .font(.system(size: 14, weight: .bold))
            .lineLimit(1)
            if model.waitingCount > 0 {
                Link(destination: AppLink.show(itemID: nil).url) {
                    HStack(spacing: 2) {
                        Image(systemName: "pause.circle.fill")
                        Text("\(model.waitingCount)")
                    }
                    .font(.system(size: 10.5, weight: .semibold).monospacedDigit())
                    .foregroundStyle(Palette.waiting)
                }
                .buttonStyle(.plain)
            }
            Spacer(minLength: 4)
            ProjectPicker(model: model)
            if showsKindFilter {
                KindFilterBar(model: model)
            }
        }
    }
}

/// Projects picked directly, with names where they fit and as colored dots where not; the grid stands for all.
private struct ProjectPicker: View {
    let model: WidgetModel

    var body: some View {
        ViewThatFits(in: .horizontal) {
            options(showsNames: true)
            options(showsNames: false)
        }
    }

    private func options(showsNames: Bool) -> some View {
        HStack(spacing: showsNames ? 4 : 2) {
            option(key: nil, showsNames: showsNames) {
                Image(systemName: "square.grid.2x2.fill")
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                if showsNames {
                    Text("Vše")
                }
            }
            ForEach(model.projects) { project in
                option(key: project.key, showsNames: showsNames) {
                    Circle()
                        .fill(Palette.project(project.color))
                        .frame(width: 9, height: 9)
                    if showsNames {
                        Text(project.name)
                    }
                }
            }
        }
        .fixedSize()
    }

    private func option(key: String?, showsNames: Bool, @ViewBuilder label: () -> some View) -> some View {
        let isActive = model.project?.key == key
        let shape = Capsule()
        return Button(intent: SelectProjectIntent(projectKey: key)) {
            HStack(spacing: 4) {
                label()
            }
            .font(.system(size: 10.5, weight: isActive ? .semibold : .medium))
            .foregroundStyle(isActive ? Color.primary : Color.secondary)
            .padding(.horizontal, showsNames ? 7 : 4)
            .frame(minWidth: 17, minHeight: 17)
            .background(shape.fill(Color.primary.opacity(isActive ? 0.14 : showsNames ? 0.05 : 0)))
            .overlay(shape.strokeBorder(Color.primary.opacity(isActive && !showsNames ? 0.55 : 0), lineWidth: 1.2))
        }
        .buttonStyle(.plain)
    }
}

private struct KindFilterBar: View {
    let model: WidgetModel

    var body: some View {
        HStack(spacing: 3) {
            chip(.all) {
                Text("Vše")
            }
            chip(.issues) {
                Circle()
                    .strokeBorder(ItemKind.issue.color, lineWidth: 1.5)
                    .frame(width: 9, height: 9)
                Text("\(model.count(.issues))")
            }
            chip(.mergeRequests) {
                Image(systemName: "arrow.triangle.merge")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(ItemKind.mergeRequest.color)
                Text("\(model.count(.mergeRequests))")
            }
        }
        .fixedSize()
    }

    private func chip(_ filter: KindFilter, @ViewBuilder label: () -> some View) -> some View {
        let isActive = model.kind == filter
        let tint = switch filter {
        case .all: Palette.headline
        case .issues: ItemKind.issue.color
        case .mergeRequests: ItemKind.mergeRequest.color
        }
        return Button(intent: SelectKindIntent(kind: filter)) {
            HStack(spacing: 3) {
                label()
            }
            .font(.system(size: 10.5, weight: .semibold).monospacedDigit())
            .foregroundStyle(isActive ? tint : Color.secondary)
            .padding(.horizontal, 7)
            .padding(.vertical, 3)
            .background(Capsule().fill(isActive ? tint.opacity(0.16) : Color.primary.opacity(0.05)))
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Rows

/// The item's circle; like the rest of the row it opens the item in the app.
private struct CircleLink: View {
    let item: TodoItem
    var size: CGFloat = 17

    var body: some View {
        Link(destination: AppLink.show(itemID: item.id).url) {
            StageCircle(item: item, size: size)
                .opacity(item.isWaiting ? 0.5 : 1)
        }
        .buttonStyle(.plain)
    }
}

/// „+ MR“ for an issue, „Ready“ for a draft. Opens the app, which asks for confirmation before touching GitLab.
private struct StepButton: View {
    let step: ItemStep

    var body: some View {
        let isProminent = step.action.isProminent
        Link(destination: AppLink.action(step.action, itemID: step.target.id).url) {
            HStack(spacing: 2) {
                if step.action == .createMergeRequest {
                    Image(systemName: "plus")
                        .font(.system(size: 8, weight: .heavy))
                }
                Text(step.action.shortTitle)
            }
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(isProminent ? Color.white : ItemKind.mergeRequest.color)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(Capsule().fill(isProminent ? Color.accentColor : ItemKind.mergeRequest.color.opacity(0.15)))
        }
        .buttonStyle(.plain)
    }
}

private struct CompactRow: View {
    let item: TodoItem
    let showsProject: Bool

    var body: some View {
        // A waiting item is not up yet, it shows what it waits on instead of a step.
        let step = item.isWaiting ? nil : item.nextStep
        HStack(spacing: 9) {
            CircleLink(item: item)
            Link(destination: AppLink.show(itemID: item.id).url) {
                HStack(spacing: 6) {
                    if !item.isPlaced {
                        NewDot()
                    }
                    if let releasedBy = item.releasedBy {
                        ReadyMark(finished: releasedBy, showsLabel: false)
                            .font(.system(size: 11))
                    }
                    Text(item.title)
                        .font(.system(size: 12.5))
                        .foregroundStyle(item.isWaiting ? .secondary : .primary)
                        .lineLimit(1)
                        // The title comes first, labels get what is left.
                        .layoutPriority(1)
                    FittingLabels(labels: item.labels)
                        .font(.system(size: 9.5))
                    Spacer(minLength: 6)
                    if let waitDescription = item.waitDescription {
                        WaitMark(description: waitDescription.count > 18 ? String(waitDescription.prefix(17)) + "…" : waitDescription, showsLabel: false)
                            .font(.system(size: 10.5))
                    }
                    if item.hasMergeConflict {
                        ConflictIcon()
                            .font(.system(size: 10.5))
                    }
                    if let threads = item.reviewThreads {
                        ThreadsBadge(resolved: threads.resolved, total: threads.total)
                            .font(.system(size: 10.5))
                    }
                    if showsProject {
                        // Next to a button only the project color fits, the title needs the room more.
                        ProjectLabel(item: item, maxWidth: 92, showsName: step == nil && !item.isWaiting)
                            .font(.system(size: 10.5))
                    } else {
                        Text(item.references)
                            .font(.system(size: 10.5).monospacedDigit())
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            if let step {
                StepButton(step: step)
            }
        }
        .frame(height: 22)
    }
}

private struct DetailedRow: View {
    let item: TodoItem
    let showsProject: Bool

    var body: some View {
        let show = AppLink.show(itemID: item.id).url
        HStack(alignment: .top, spacing: 9) {
            CircleLink(item: item)
                .padding(.top, 1)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Link(destination: show) {
                        Text(item.title)
                            .font(.system(size: 12.5))
                            .foregroundStyle(item.isWaiting ? .secondary : .primary)
                            .lineLimit(1)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    if let step = item.nextStep, !item.isWaiting {
                        StepButton(step: step)
                    }
                }
                Link(destination: show) {
                    HStack(spacing: 5) {
                        Text(item.references)
                            .monospacedDigit()
                        if let releasedBy = item.releasedBy {
                            ReadyMark(finished: releasedBy)
                        }
                        if let waitDescription = item.waitDescription {
                            WaitMark(description: waitDescription)
                        }
                        if item.isDraftStage {
                            Text("Draft")
                                .fontWeight(.semibold)
                        }
                        if item.hasMergeConflict {
                            ConflictIcon()
                        }
                        if let threads = item.reviewThreads {
                            ThreadsBadge(resolved: threads.resolved, total: threads.total)
                        }
                        FittingLabels(labels: item.labels)
                            .font(.system(size: 9.5))
                        if !item.isPlaced {
                            Text("nové")
                                .fontWeight(.semibold)
                                .foregroundStyle(Color.accentColor)
                        }
                        Spacer(minLength: 6)
                        if showsProject {
                            ProjectLabel(item: item, maxWidth: 120)
                        }
                    }
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .frame(height: 34, alignment: .top)
    }
}

private struct NewDot: View {
    var body: some View {
        Circle()
            .fill(Color.accentColor)
            .frame(width: 5, height: 5)
    }
}

/// As many rows as fit the given height. Counted from fixed row heights rather than by trying layouts: macOS archives
/// every alternative of a widget, and trying all row counts made it slow to redraw and flicker.
private struct Rows<Row: View>: View {
    let items: [TodoItem]
    let height: CGFloat
    let rowHeight: CGFloat
    /// The medium widget has no room for the "+3 další" line, the counts in the header tell it.
    var showsMore = true
    @ViewBuilder let row: (TodoItem) -> Row

    var body: some View {
        RowList(items: items, limit: limit, showsMore: showsMore, rowHeight: rowHeight, row: row)
            .frame(maxHeight: .infinity, alignment: .top)
            .clipped()
    }

    private var limit: Int {
        let separator = RowList<Row>.separator(showsMore: showsMore)
        let fitting = { (space: CGFloat) in max(1, Int((space + separator) / (rowHeight + separator))) }
        let all = fitting(height)
        guard showsMore, items.count > all else { return min(items.count, all) }
        return fitting(height - RowList<Row>.moreHeight)
    }
}

private struct RowList<Row: View>: View {
    /// The "+3 další" line with the spacing above it.
    static var moreHeight: CGFloat { 18 }

    /// Spacing above and below the 1pt divider between rows.
    static func separator(showsMore: Bool) -> CGFloat {
        showsMore ? 9 : 7
    }

    let items: [TodoItem]
    let limit: Int
    let showsMore: Bool
    let rowHeight: CGFloat
    @ViewBuilder let row: (TodoItem) -> Row

    var body: some View {
        VStack(alignment: .leading, spacing: showsMore ? 4 : 3) {
            ForEach(Array(items.prefix(limit).enumerated()), id: \.element.id) { index, item in
                if index > 0 {
                    Rectangle()
                        .fill(Color.primary.opacity(0.1))
                        .frame(height: 1)
                        .padding(.leading, 26)
                }
                row(item)
                    .frame(height: rowHeight, alignment: .top)
            }
            Spacer(minLength: 0)
            if showsMore, items.count > limit {
                Text(moreLabel(items.count - limit))
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
                    .frame(height: 14)
                    .padding(.leading, 26)
            }
        }
    }
}

// MARK: - Layouts

private struct ListLayout: View {
    let model: WidgetModel
    let isLarge: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Header(model: model)
            if model.items.isEmpty {
                EmptyState(hasData: model.snapshot.refreshedAt != nil)
            } else if isLarge {
                Rows(items: model.items, height: model.rowsHeight, rowHeight: 34) { DetailedRow(item: $0, showsProject: model.project == nil) }
            } else {
                Rows(items: model.items, height: model.rowsHeight, rowHeight: 22, showsMore: false) { CompactRow(item: $0, showsProject: model.project == nil) }
            }
        }
    }
}

/// Extra large: issues to start on the left, merge requests in review on the right.
private struct ColumnsLayout: View {
    let model: WidgetModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Header(model: model, showsKindFilter: false)
            HStack(alignment: .top, spacing: 18) {
                column(.issues, title: "K řešení", kind: .issue)
                column(.mergeRequests, title: "Merge requests", kind: .mergeRequest)
            }
        }
    }

    private func column(_ filter: KindFilter, title: String, kind: ItemKind) -> some View {
        let items = model.projectItems.filter(filter.includes)
        return VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 5) {
                Text(title)
                    .foregroundStyle(kind.color)
                Text("\(items.count)")
                    .foregroundStyle(.secondary)
            }
            .font(.system(size: 11.5, weight: .bold))
            if items.isEmpty {
                Text("Nic")
                    .font(.system(size: 12))
                    .foregroundStyle(.tertiary)
                Spacer(minLength: 0)
            } else {
                // Below the column title.
                Rows(items: items, height: model.rowsHeight - 20, rowHeight: 34) { DetailedRow(item: $0, showsProject: model.project == nil) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

private struct SmallLayout: View {
    let model: WidgetModel

    var body: some View {
        // „Na řadě“ is only what can be worked on now.
        let items = model.items.filter { !$0.isWaiting }
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Na řadě")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(Palette.headline)
                Spacer(minLength: 4)
                Text("\(items.count)")
                    .font(.system(size: 13, weight: .semibold).monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            if items.isEmpty {
                EmptyState(hasData: model.snapshot.refreshedAt != nil)
            } else {
                ForEach(items.prefix(4)) { item in
                    HStack(spacing: 7) {
                        StageCircle(item: item, size: 14)
                        Text(item.title)
                            .font(.system(size: 11.5))
                            .lineLimit(1)
                    }
                    .frame(maxHeight: .infinity)
                }
            }
        }
        // The small widget supports only a single tap target.
        .widgetURL(AppLink.show(itemID: nil).url)
    }
}

private struct EmptyState: View {
    let hasData: Bool

    var body: some View {
        VStack(spacing: 6) {
            Image(systemName: hasData ? "checkmark.circle" : "arrow.triangle.2.circlepath")
                .font(.system(size: 22, weight: .light))
            Text(hasData ? "Nic na práci" : "Otevři GitLab Todo a přidej GitLab")
                .font(.system(size: 12))
        }
        .foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private func moreLabel(_ count: Int) -> String {
    "+\(count) \(count < 5 ? "další" : "dalších")"
}
