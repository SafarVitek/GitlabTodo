import AppKit
import SwiftUI

struct MenuContentView: View {
    @Environment(AppModel.self) private var model

    /// The panel shows what can be worked on, waiting items are in the main window.
    private var items: [TodoItem] {
        model.items.filter { !$0.isWaiting }
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ErrorBanner()
            content
            Divider()
            footer
        }
        .frame(width: 420)
    }

    private var header: some View {
        HStack(spacing: 10) {
            Text("Na řadě")
                .font(.headline)
                .foregroundStyle(Palette.headline)
            Spacer()
            if model.isRefreshing {
                ProgressView()
                    .controlSize(.small)
            }
            Button {
                Task { await model.refresh() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .help("Obnovit")
            .disabled(model.isRefreshing)
            Button {
                Windows.showMain()
            } label: {
                Image(systemName: "macwindow")
            }
            .help("Pořadí a timeline")
            Button {
                Windows.showSettings()
            } label: {
                Image(systemName: "gearshape")
            }
            .help("Nastavení")
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    @ViewBuilder
    private var content: some View {
        if model.accounts.isEmpty {
            placeholder(symbol: "server.rack", title: "Není připojený žádný GitLab", showsSettingsButton: true)
        } else if items.isEmpty {
            placeholder(symbol: "checkmark.circle", title: model.lastRefresh == nil ? "Načítám…" : "Nic na práci", showsSettingsButton: false)
        } else {
            let numbers = model.items.workNumbers()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(items) { item in
                        PanelRow(item: item, number: numbers[item.id])
                    }
                }
                .padding(.vertical, 4)
            }
            .frame(height: 440)
        }
    }

    private var footer: some View {
        HStack(spacing: 10) {
            let waiting = model.items.count - items.count
            if waiting > 0 {
                Button {
                    Windows.showMain()
                } label: {
                    Label("\(waiting) čeká", systemImage: "pause.circle.fill")
                        .foregroundStyle(.orange)
                }
            }
            TimelineView(.periodic(from: .now, by: 30)) { _ in
                Text(refreshDescription)
            }
            Spacer()
            Button("Ukončit") {
                NSApplication.shared.terminate(nil)
            }
        }
        .buttonStyle(.borderless)
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var refreshDescription: String {
        guard let lastRefresh = model.lastRefresh else { return "Zatím neaktualizováno" }
        return "Aktualizováno " + lastRefresh.formatted(.relative(presentation: .named))
    }

    private func placeholder(symbol: String, title: String, showsSettingsButton: Bool) -> some View {
        VStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.largeTitle)
                .foregroundStyle(.secondary)
            Text(title)
                .foregroundStyle(.secondary)
            if showsSettingsButton {
                Button("Přidat GitLab…") { Windows.showSettings() }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 40)
    }
}

private struct PanelRow: View {
    @Environment(AppModel.self) private var model
    @State private var isHovered = false
    let item: TodoItem
    let number: Int?

    var body: some View {
        HStack(spacing: 10) {
            StageButton(item: item)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title)
                    .fontWeight(number == 1 ? .semibold : .regular)
                    .lineLimit(2)
                ItemMeta(item: item)
            }
            Spacer(minLength: 4)
            if let step = item.nextStep, step.action.isProminent || isHovered {
                ActionButton(step: step)
            }
            ProjectLabel(item: item, maxWidth: 90)
                .font(.caption)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(isHovered ? Color.primary.opacity(0.06) : Color.clear)
        .contentShape(Rectangle())
        .onHover { isHovered = $0 }
        .onTapGesture {
            model.acknowledgeRelease(of: item.id)
            NSWorkspace.shared.open(item.webURL)
        }
        .contextMenu { ItemMenu(item: item) }
    }
}
