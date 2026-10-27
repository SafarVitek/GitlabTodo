import AppIntents
import Foundation

/// What the widget's own buttons change.
struct WidgetState: Codable, Sendable {
    /// `TodoItem.projectKey` of the shown project, `nil` for all projects.
    var project: String?
    var kind = KindFilter.all

    static func load() -> WidgetState {
        SharedStore.load(WidgetState.self, from: SharedStore.widgetStateURL) ?? WidgetState()
    }

    static func update(_ change: (inout WidgetState) -> Void) {
        var state = load()
        change(&state)
        try? SharedStore.save(state, to: SharedStore.widgetStateURL)
    }
}

struct SelectProjectIntent: AppIntent {
    static let title: LocalizedStringResource = "Přepnout projekt"
    static let isDiscoverable = false

    /// Empty for all projects.
    @Parameter(title: "Projekt")
    var projectKey: String

    init() {}

    init(projectKey: String?) {
        self.projectKey = projectKey ?? ""
    }

    func perform() async throws -> some IntentResult {
        WidgetState.update { $0.project = projectKey.isEmpty ? nil : projectKey }
        return .result()
    }
}

struct SelectKindIntent: AppIntent {
    static let title: LocalizedStringResource = "Filtrovat issue a MR"
    static let isDiscoverable = false

    @Parameter(title: "Typ")
    var kind: String

    init() {}

    init(kind: KindFilter) {
        self.kind = kind.rawValue
    }

    func perform() async throws -> some IntentResult {
        WidgetState.update { $0.kind = KindFilter(rawValue: kind) ?? .all }
        return .result()
    }
}
