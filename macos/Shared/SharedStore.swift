import Foundation
import SwiftUI

/// Files in `~/Library/Application Support/GitLabTodo`, shared by the app and the widget.
enum SharedStore {
    /// Inside the sandboxed widget `NSHomeDirectory()` points into its container, so resolve the real home.
    static let directoryURL: URL = {
        let home = getpwuid(getuid()).map { String(cString: $0.pointee.pw_dir) } ?? NSHomeDirectory()
        return URL(filePath: home, directoryHint: .isDirectory)
            .appending(path: "Library/Application Support/GitLabTodo", directoryHint: .isDirectory)
    }()

    static let snapshotURL = directoryURL.appending(path: "snapshot.json")
    static let planURL = directoryURL.appending(path: "plan.json")
    static let widgetStateURL = directoryURL.appending(path: "widget-state.json")
    static let syncURL = directoryURL.appending(path: "sync.json")

    static func load<Value: Decodable>(_ type: Value.Type, from url: URL) -> Value? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(type, from: data)
    }

    static func save(_ value: some Encodable, to url: URL) throws {
        if !FileManager.default.fileExists(atPath: directoryURL.path(percentEncoded: false)) {
            try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true)
        }
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(value).write(to: url, options: .atomic)
    }
}

/// URLs the widget uses to hand clicks over to the app.
enum AppLink {
    static let scheme = "gitlabtodo"

    case open(URL)
    case show(itemID: String?)
    case action(ItemAction, itemID: String)

    init?(url: URL) {
        guard url.scheme == Self.scheme else { return nil }
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        let value = { (name: String) in query.first { $0.name == name }?.value }
        switch url.host() {
        case "open":
            guard let target = value("url").flatMap(URL.init(string:)) else { return nil }
            self = .open(target)
        case "show":
            self = .show(itemID: value("item"))
        case "action":
            guard let action = value("type").flatMap(ItemAction.init(rawValue:)), let itemID = value("item") else { return nil }
            self = .action(action, itemID: itemID)
        default:
            return nil
        }
    }

    var url: URL {
        var components = URLComponents()
        components.scheme = Self.scheme
        switch self {
        case .open(let target):
            components.host = "open"
            components.queryItems = [URLQueryItem(name: "url", value: target.absoluteString)]
        case .show(let itemID):
            components.host = "show"
            components.queryItems = itemID.map { [URLQueryItem(name: "item", value: $0)] }
        case .action(let action, let itemID):
            components.host = "action"
            components.queryItems = [URLQueryItem(name: "type", value: action.rawValue), URLQueryItem(name: "item", value: itemID)]
        }
        return components.url!
    }
}

