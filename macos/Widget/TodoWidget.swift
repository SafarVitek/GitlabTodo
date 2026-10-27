import SwiftUI
import WidgetKit

@main
struct GitLabTodoWidgetBundle: WidgetBundle {
    var body: some Widget {
        TodoWidget()
    }
}

struct TodoWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "TodoWidget", provider: TodoProvider()) { entry in
            TodoWidgetView(entry: entry)
                .containerBackground(.background, for: .widget)
        }
        .configurationDisplayName("GitLab Todo")
        .description("Co je na řadě, v pořadí, které si nastavíš v aplikaci.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge, .systemExtraLarge])
        .contentMarginsDisabled()
    }
}

struct TodoProvider: TimelineProvider {
    func placeholder(in context: Context) -> TodoEntry {
        TodoEntry(date: .now, snapshot: .preview, displaySize: context.displaySize)
    }

    func getSnapshot(in context: Context, completion: @escaping (TodoEntry) -> Void) {
        let snapshot = loadSnapshot()
        // The widget gallery shows sample data until the app has fetched something.
        completion(TodoEntry(
            date: .now,
            snapshot: context.isPreview && snapshot.items.isEmpty ? .preview : snapshot,
            state: WidgetState.load(),
            displaySize: context.displaySize))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<TodoEntry>) -> Void) {
        let entry = TodoEntry(date: .now, snapshot: loadSnapshot(), state: WidgetState.load(), displaySize: context.displaySize)
        // The app reloads timelines whenever its data changes, the periodic reload is only a fallback.
        completion(Timeline(entries: [entry], policy: .after(.now.addingTimeInterval(15 * 60))))
    }

    private func loadSnapshot() -> Snapshot {
        SharedStore.load(Snapshot.self, from: SharedStore.snapshotURL) ?? Snapshot()
    }
}

struct TodoWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: TodoEntry

    var body: some View {
        WidgetContent(family: family, entry: entry)
    }
}
