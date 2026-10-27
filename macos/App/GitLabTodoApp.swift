import AppKit
import os
import SwiftUI

@main
struct GitLabTodoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environment(AppModel.shared)
        } label: {
            MenuBarLabel(model: AppModel.shared)
        }
        .menuBarExtraStyle(.window)
    }
}

private struct MenuBarLabel: View {
    let model: AppModel

    var body: some View {
        let count = model.items.filter { !$0.isWaiting }.count
        Image(systemName: "checklist")
        if count > 0 {
            Text("\(count)")
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let logger = Logger(subsystem: "local.gitlabtodo", category: "links")

    func applicationWillFinishLaunching(_ notification: Notification) {
        registerURLHandler()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // SwiftUI installs its own URL handler while launching, take the URLs back.
        registerURLHandler()
    }

    /// Clicks in the widget arrive as gitlabtodo:// URLs.
    private func registerURLHandler() {
        NSAppleEventManager.shared().setEventHandler(
            self,
            andSelector: #selector(handleGetURL(_:withReply:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL))
    }

    @objc private func handleGetURL(_ event: NSAppleEventDescriptor, withReply reply: NSAppleEventDescriptor) {
        guard let string = event.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue,
              let url = URL(string: string) else { return }
        handle(url)
    }

    private func handle(_ url: URL) {
        guard let link = AppLink(url: url) else { return }
        logger.notice("Handling \(url.host() ?? "?", privacy: .public) link")
        switch link {
        case .open(let target):
            NSWorkspace.shared.open(target)
        case .show(let itemID):
            AppModel.shared.requestFocus(on: itemID)
            Windows.showMain()
        case .action(let action, let itemID):
            Task { await AppModel.shared.perform(action, onItemWithID: itemID) }
        }
    }
}
