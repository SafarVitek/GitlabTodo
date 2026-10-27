import AppKit
import SwiftUI

enum ItemFilter: Hashable {
    case all
    case waiting
    case project(String)
}

struct MainWindow: View {
    enum Mode: String, CaseIterable, Identifiable {
        case order = "Pořadí"
        case timeline = "Timeline"

        var id: Self { self }
    }

    @Environment(AppModel.self) private var model
    @State private var filter: ItemFilter? = .all
    @State private var mode = Mode.order
    @State private var kind = KindFilter.all
    @State private var selection: String?
    @State private var showsInspector = true
    @AppStorage(LaneTimeline.zoomKey) private var zoom = 1.0

    private var filteredItems: [TodoItem] {
        let items = switch filter ?? .all {
        case .all: model.items
        case .waiting: model.items.filter(\.isWaiting)
        case .project(let key): model.items.filter { $0.projectKey == key }
        }
        return items.filter(kind.includes)
    }

    private var title: String {
        switch filter ?? .all {
        case .all: "Vše"
        case .waiting: "Čeká"
        case .project(let key): model.items.first { $0.projectKey == key }?.projectName ?? "Projekt"
        }
    }

    var body: some View {
        NavigationSplitView {
            Sidebar(filter: $filter)
                .navigationSplitViewColumnWidth(min: 180, ideal: 210, max: 280)
        } detail: {
            VStack(spacing: 0) {
                ErrorBanner()
                switch mode {
                case .order:
                    OrderList(items: filteredItems, showsProject: !isProjectFilter, selection: $selection)
                case .timeline:
                    // An overview across projects, the sidebar and kind filters do not apply.
                    LaneTimeline(selection: $selection)
                }
            }
            .navigationTitle(title)
            .navigationSubtitle(subtitle)
            .toolbar { toolbar }
            .inspector(isPresented: $showsInspector) {
                ItemInspector(item: model.items.first { $0.id == selection })
                    .inspectorColumnWidth(min: 260, ideal: 300, max: 400)
            }
        }
        .frame(minWidth: 760, minHeight: 420)
        .onAppear {
            applyFocusRequest()
        }
        .onChange(of: model.focusRequest) {
            applyFocusRequest()
        }
        .onChange(of: selection) {
            if let selection {
                // Picking an item shows its detail, even when the inspector was closed.
                showsInspector = true
                model.acknowledgeRelease(of: selection)
            }
        }
    }

    private var isProjectFilter: Bool {
        if case .project = filter { true } else { false }
    }

    private var subtitle: String {
        let waiting = filteredItems.filter(\.isWaiting).count
        let new = filteredItems.filter { !$0.isPlaced }.count
        var parts = ["\(filteredItems.count) položek"]
        if waiting > 0 {
            parts.append("\(waiting) čeká")
        }
        if new > 0 {
            parts.append("\(new) nezařazených")
        }
        return parts.joined(separator: " · ")
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .principal) {
            Picker("Zobrazení", selection: $mode) {
                ForEach(Mode.allCases) { mode in
                    Text(mode.rawValue).tag(mode)
                }
            }
            .pickerStyle(.segmented)
            .fixedSize()
            Picker("Typ", selection: $kind) {
                Text("Vše").tag(KindFilter.all)
                Label("Issue", systemImage: "circle").tag(KindFilter.issues)
                Label("MR", systemImage: "arrow.triangle.merge").tag(KindFilter.mergeRequests)
            }
            .pickerStyle(.segmented)
            .labelStyle(.titleAndIcon)
            .fixedSize()
            .disabled(mode == .timeline)
            .help(mode == .timeline ? "Timeline ukazuje všechno napříč projekty" : "Zobrazit issue, merge requesty nebo obojí")
        }
        ToolbarItemGroup(placement: .primaryAction) {
            if mode == .timeline {
                ControlGroup {
                    Button {
                        zoom = LaneTimeline.zoom(zoom, by: -0.1)
                    } label: {
                        Label("Oddálit", systemImage: "minus.magnifyingglass")
                    }
                    .keyboardShortcut("-", modifiers: .command)
                    .disabled(zoom <= LaneTimeline.zoomRange.lowerBound)
                    Button {
                        zoom = 1
                    } label: {
                        Text("\(Int((zoom * 100).rounded())) %")
                            .monospacedDigit()
                    }
                    .keyboardShortcut("0", modifiers: .command)
                    .help("Zpět na 100 %")
                    Button {
                        zoom = LaneTimeline.zoom(zoom, by: 0.1)
                    } label: {
                        Label("Přiblížit", systemImage: "plus.magnifyingglass")
                    }
                    .keyboardShortcut("+", modifiers: .command)
                    .disabled(zoom >= LaneTimeline.zoomRange.upperBound)
                }
                .help("Zoom timeline, jde i gestem na trackpadu")
            }
            if model.isRefreshing {
                ProgressView()
                    .controlSize(.small)
            }
            Button {
                Task { await model.refresh() }
            } label: {
                Label("Obnovit", systemImage: "arrow.clockwise")
            }
            .disabled(model.isRefreshing)
            Button {
                Windows.showSettings()
            } label: {
                Label("Nastavení", systemImage: "gearshape")
            }
            Button {
                showsInspector.toggle()
            } label: {
                Label("Detail", systemImage: "sidebar.right")
            }
        }
    }

    private func applyFocusRequest() {
        guard let itemID = model.focusRequest.itemID, let item = model.items.first(where: { $0.id == itemID }) else { return }
        if !filteredItems.contains(where: { $0.id == itemID }) {
            filter = .project(item.projectKey)
            kind = .all
        }
        selection = itemID
        showsInspector = true
    }
}

private struct Sidebar: View {
    @Environment(AppModel.self) private var model
    @Binding var filter: ItemFilter?

    var body: some View {
        let tabs = ProjectTab.tabs(for: model.items)
        let accountNames = Array(Set(tabs.map(\.accountName))).sorted()
        List(selection: $filter) {
            Label("Vše", systemImage: "tray.full")
                .badge(model.items.count)
                .tag(ItemFilter.all)
            Label("Čeká", systemImage: "pause.circle")
                .badge(model.items.filter(\.isWaiting).count)
                .tag(ItemFilter.waiting)
            ForEach(accountNames, id: \.self) { accountName in
                Section(accountNames.count > 1 ? accountName : "Projekty") {
                    ForEach(tabs.filter { $0.accountName == accountName }) { tab in
                        Label {
                            Text(tab.name)
                        } icon: {
                            Circle()
                                .fill(Palette.project(tab.color))
                                .frame(width: 9, height: 9)
                        }
                        .badge(tab.count)
                        .tag(ItemFilter.project(tab.key))
                    }
                }
            }
        }
        .listStyle(.sidebar)
    }
}

private struct OrderList: View {
    @Environment(AppModel.self) private var model
    let items: [TodoItem]
    let showsProject: Bool
    @Binding var selection: String?

    var body: some View {
        let firstNewID = items.first { !$0.isPlaced }?.id
        ScrollViewReader { proxy in
            List(selection: $selection) {
                ForEach(items) { item in
                    OrderRow(item: item, showsProject: showsProject, startsNewItems: item.id == firstNewID, isSelected: item.id == selection)
                        .tag(item.id)
                        .id(item.id)
                }
                .onMove { source, destination in
                    model.move(items.map(\.id), fromOffsets: source, toOffset: destination)
                }
            }
            .listStyle(.inset)
            .contextMenu(forSelectionType: String.self) { ids in
                if let item = items.first(where: { ids.contains($0.id) }) {
                    ItemMenu(item: item)
                }
            } primaryAction: { ids in
                for item in items where ids.contains(item.id) {
                    NSWorkspace.shared.open(item.webURL)
                }
            }
            .overlay {
                if items.isEmpty {
                    ContentUnavailableView("Nic tu není", systemImage: "checkmark.circle")
                }
            }
            .onChange(of: selection) {
                if let selection {
                    withAnimation { proxy.scrollTo(selection) }
                }
            }
        }
    }
}

private struct OrderRow: View {
    @State private var isHovered = false
    let item: TodoItem
    let showsProject: Bool
    let startsNewItems: Bool
    let isSelected: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if startsNewItems {
                Text("Nezařazené – přetáhni nahoru do pořadí")
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(Palette.headline)
                    .padding(.top, 12)
            }
            HStack(alignment: .top, spacing: 10) {
                StageButton(item: item, size: 19)
                    .padding(.top, 1)
                VStack(alignment: .leading, spacing: 3) {
                    Text(item.title)
                        .lineLimit(1)
                    ItemMeta(item: item)
                }
                Spacer(minLength: 8)
                if let step = item.nextStep, step.action.isProminent || isHovered || isSelected {
                    ActionButton(step: step)
                }
                if showsProject {
                    ProjectLabel(item: item, maxWidth: 140)
                        .font(.caption)
                        .padding(.top, 2)
                }
            }
            .opacity(item.isWaiting ? 0.7 : 1)
        }
        .padding(.vertical, 5)
        .frame(minHeight: 38)
        .onHover { isHovered = $0 }
    }
}
