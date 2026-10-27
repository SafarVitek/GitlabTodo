import AppKit
import Foundation
import Security
import SwiftUI

enum Keychain {
    private static let service = "local.gitlabtodo"

    static func token(for accountID: UUID) -> String? {
        var query = baseQuery(accountID)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func setToken(_ token: String, for accountID: UUID) {
        let data = Data(token.utf8)
        let status = SecItemUpdate(baseQuery(accountID) as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var query = baseQuery(accountID)
            query[kSecValueData as String] = data
            SecItemAdd(query as CFDictionary, nil)
        }
    }

    static func deleteToken(for accountID: UUID) {
        SecItemDelete(baseQuery(accountID) as CFDictionary)
    }

    private static func baseQuery(_ accountID: UUID) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: accountID.uuidString,
        ]
    }
}

/// The app's windows, managed by AppKit so that widget clicks can open them without a SwiftUI view around.
@MainActor
enum Windows {
    private static var main: NSWindow?
    private static var settings: NSWindow?

    static func showMain() {
        let window = main ?? makeWindow(MainWindow(), title: "GitLab Todo", size: NSSize(width: 1040, height: 660), autosaveName: "MainWindow", isResizable: true)
        main = window
        present(window)
    }

    static func showSettings() {
        let window = settings ?? makeWindow(SettingsView(), title: "Nastavení", size: NSSize(width: 500, height: 420), autosaveName: "SettingsWindow", isResizable: false)
        settings = window
        present(window)
    }

    private static func makeWindow(_ view: some View, title: String, size: NSSize, autosaveName: String, isResizable: Bool) -> NSWindow {
        let controller = NSHostingController(rootView: view.environment(AppModel.shared))
        controller.sceneBridgingOptions = [.toolbars, .title]
        controller.sizingOptions = isResizable ? [.minSize] : [.preferredContentSize]
        let window = NSWindow(contentViewController: controller)
        window.title = title
        window.styleMask = isResizable ? [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView] : [.titled, .closable]
        window.isReleasedWhenClosed = false
        window.setContentSize(size)
        if !window.setFrameUsingName(autosaveName) {
            window.center()
        }
        window.setFrameAutosaveName(autosaveName)
        let windowID = ObjectIdentifier(window)
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { _ in
            MainActor.assumeIsolated {
                updateActivationPolicy(closing: windowID)
            }
        }
        return window
    }

    private static func present(_ window: NSWindow) {
        // Show up in the Dock and the app switcher while a window is open.
        NSApp.setActivationPolicy(.regular)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    private static func updateActivationPolicy(closing closingID: ObjectIdentifier) {
        let stillOpen = [main, settings].compactMap { $0 }.contains { ObjectIdentifier($0) != closingID && $0.isVisible }
        if !stillOpen {
            NSApp.setActivationPolicy(.accessory)
        }
    }
}

enum Pasteboard {
    static func copy(_ string: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(string, forType: .string)
    }
}

@MainActor
enum Alerts {
    /// Returns the index of the clicked button.
    @discardableResult
    static func show(title: String, message: String, buttons: [String] = ["OK"], style: NSAlert.Style = .informational) -> Int {
        let alert = makeAlert(title: title, message: message, buttons: buttons, style: style)
        return alert.runModal().rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
    }

    static func confirm(title: String, message: String, action: String) -> Bool {
        let alert = makeAlert(title: title, message: message, buttons: [action, "Zrušit"], style: .informational)
        alert.buttons[1].keyEquivalent = "\u{1b}"
        return alert.runModal() == .alertFirstButtonReturn
    }

    private static func makeAlert(title: String, message: String, buttons: [String], style: NSAlert.Style) -> NSAlert {
        // A menu bar app is usually inactive, the alert would otherwise open behind other windows.
        NSApp.activate()
        let alert = NSAlert()
        alert.alertStyle = style
        alert.messageText = title
        alert.informativeText = message
        for button in buttons {
            alert.addButton(withTitle: button)
        }
        return alert
    }
}

/// Creates a token with GitLab Shell's `personal_access_token` command, authenticated by the user's SSH key.
enum SSHTokenCreator {
    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    static func create(target: String, days: Int = 365) async throws -> String {
        try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            process.executableURL = URL(filePath: "/usr/bin/ssh")
            // BatchMode: fail instead of waiting for a password prompt nobody can answer.
            process.arguments = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", target, "personal_access_token", "gitlab-todo", "api", String(days)]
            let output = Pipe()
            let errors = Pipe()
            process.standardOutput = output
            process.standardError = errors
            let outputHandle = output.fileHandleForReading
            let errorHandle = errors.fileHandleForReading
            process.terminationHandler = { _ in
                let stdout = String(decoding: outputHandle.readDataToEndOfFile(), as: UTF8.self)
                let stderr = String(decoding: errorHandle.readDataToEndOfFile(), as: UTF8.self)
                let token = stdout.split(whereSeparator: \.isNewline)
                    .first { $0.hasPrefix("Token:") }
                    .map { $0.dropFirst("Token:".count).trimmingCharacters(in: .whitespaces) }
                if let token, !token.isEmpty {
                    continuation.resume(returning: token)
                } else {
                    let detail = (stderr.isEmpty ? stdout : stderr).trimmingCharacters(in: .whitespacesAndNewlines)
                    continuation.resume(throwing: Failure(message: detail.isEmpty ? "SSH nevrátilo token" : detail))
                }
            }
            do {
                try process.run()
            } catch {
                continuation.resume(throwing: error)
            }
        }
    }
}
