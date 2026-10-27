import SwiftUI

/// Visual language shared by the app and the widget, loosely after Todoist: a circle per item, colored projects.
enum Palette {
    /// Title color of the widget and the panel.
    static let headline = Color(red: 0.86, green: 0.30, blue: 0.24)
    static let waiting = Color.orange

    /// Green and purple belong to issues and merge requests, orange to waiting, red to review threads.
    static let projectColors: [Color] = [.blue, .pink, .yellow, .teal, .brown, .indigo]

    static func project(_ index: Int) -> Color {
        projectColors[index % projectColors.count]
    }

    /// Stable across launches, unlike `hashValue`, so a project keeps its color.
    static func preferredSlot(forProject key: String) -> Int {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in key.utf8 {
            hash = (hash ^ UInt64(byte)) &* 0x100_0000_01b3
        }
        return Int(hash % UInt64(projectColors.count))
    }
}

extension [TodoItem] {
    /// Gives each project its own color. A project takes the slot its key hashes to unless another one has it already.
    func withProjectColors() -> [TodoItem] {
        let keys = Set(flatMap { [$0.projectKey] + $0.mergeRequests.map(\.projectKey) }).sorted()
        var taken = Set<Int>()
        var colors: [String: Int] = [:]
        for key in keys {
            var slot = Palette.preferredSlot(forProject: key)
            while taken.contains(slot) && taken.count < Palette.projectColors.count {
                slot = (slot + 1) % Palette.projectColors.count
            }
            taken.insert(slot)
            colors[key] = slot
        }
        return map { item in
            var item = item
            item.projectColor = colors[item.projectKey] ?? 0
            item.mergeRequests = item.mergeRequests.map { mergeRequest in
                var mergeRequest = mergeRequest
                mergeRequest.projectColor = colors[mergeRequest.projectKey] ?? 0
                return mergeRequest
            }
            return item
        }
    }
}

extension ItemKind {
    var color: Color {
        switch self {
        case .issue: .green
        case .mergeRequest: .purple
        }
    }

    var label: String {
        switch self {
        case .issue: "Issue"
        case .mergeRequest: "MR"
        }
    }
}

/// The circle in front of every item: a green ring for an issue, a purple one with the merge glyph for a merge request.
/// A dashed ring is a draft. Shape and glyph keep them apart even in the monochrome desktop style.
struct StageCircle: View {
    let item: TodoItem
    var size: CGFloat = 18

    var body: some View {
        let stage = item.stage
        ZStack {
            Circle()
                .fill(stage.color.opacity(0.12))
            if item.isDraftStage {
                Circle()
                    .strokeBorder(stage.color, style: StrokeStyle(lineWidth: max(1.4, size / 12), dash: [size / 7, size / 10]))
            } else {
                Circle()
                    .strokeBorder(stage.color, lineWidth: max(1.4, size / 12))
            }
            if stage == .mergeRequest {
                Image(systemName: "arrow.triangle.merge")
                    .font(.system(size: size * 0.46, weight: .bold))
                    .foregroundStyle(stage.color)
            }
        }
        .frame(width: size, height: size)
        .overlay(alignment: .topTrailing) {
            if item.hasMergeConflict {
                // A merge conflict needs fixing before anything else happens with the MR.
                Image(systemName: "exclamationmark")
                    .font(.system(size: size * 0.4, weight: .black))
                    .foregroundStyle(.white)
                    .frame(width: size * 0.58, height: size * 0.58)
                    .background(Circle().fill(Color.orange))
                    .offset(x: size * 0.24, y: -size * 0.2)
            }
        }
    }
}

struct ProjectLabel: View {
    let item: TodoItem
    var maxWidth: CGFloat?
    var showsName = true

    var body: some View {
        HStack(spacing: 4) {
            if showsName {
                Text(item.projectName)
                    .lineLimit(1)
                    .frame(maxWidth: maxWidth, alignment: .trailing)
                    .fixedSize(horizontal: maxWidth == nil, vertical: false)
            }
            Circle()
                .fill(Palette.project(item.projectColor))
                .frame(width: 7, height: 7)
        }
        .foregroundStyle(.secondary)
    }
}

/// What the item waited on got done, it can start now.
struct ReadyMark: View {
    let finished: String
    var showsLabel = true

    var body: some View {
        HStack(spacing: 2) {
            Image(systemName: "play.circle.fill")
            if showsLabel {
                Text("připraveno")
            }
        }
        .fontWeight(.semibold)
        .foregroundStyle(.green)
        .help("\(finished) je hotové, můžeš začít")
    }
}

extension Color {
    /// `#RRGGBB`, the way GitLab sends label colors.
    init?(hex: String) {
        let digits = hex.trimmingPrefix("#")
        guard digits.count == 6, let value = UInt32(digits, radix: 16) else { return nil }
        self.init(
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255)
    }
}

extension EnvironmentValues {
    /// Scales the padding of label chips along with the timeline's zoom.
    @Entry var labelScale: CGFloat = 1
}

/// A label in its GitLab colors.
struct LabelChip: View {
    @Environment(\.labelScale) private var scale
    let label: ItemLabel

    var body: some View {
        Text(label.name.replacingOccurrences(of: "::", with: ": "))
            .fontWeight(.medium)
            .lineLimit(1)
            .foregroundStyle(Color(hex: label.textColor) ?? .white)
            .padding(.horizontal, 5 * scale)
            .padding(.vertical, 1 * scale)
            .background(Capsule().fill(Color(hex: label.color) ?? .gray))
    }
}

/// As many labels as fit the space they get, the rest as „+N“.
struct FittingLabels: View {
    @Environment(\.labelScale) private var scale
    let labels: [ItemLabel]

    var body: some View {
        if !labels.isEmpty {
            ViewThatFits(in: .horizontal) {
                ForEach(Array((0...labels.count).reversed()), id: \.self) { shown in
                    HStack(spacing: 3 * scale) {
                        ForEach(labels.prefix(shown), id: \.self) { label in
                            LabelChip(label: label)
                        }
                        if shown < labels.count {
                            Text("+\(labels.count - shown)")
                                .fontWeight(.medium)
                                .foregroundStyle(.secondary)
                                .padding(.horizontal, 5 * scale)
                                .padding(.vertical, 1 * scale)
                                .background(Capsule().fill(Color.primary.opacity(0.08)))
                                .help(labels.dropFirst(shown).map(\.name).joined(separator: ", "))
                        }
                    }
                    .fixedSize()
                }
            }
        }
    }
}

/// What the item waits on, in the waiting orange.
struct WaitMark: View {
    let description: String
    var showsLabel = true

    var body: some View {
        HStack(spacing: 2) {
            Image(systemName: "pause.circle.fill")
            Text(showsLabel ? "čeká na \(description)" : description)
                .lineLimit(1)
        }
        .fontWeight(.medium)
        .foregroundStyle(Palette.waiting)
    }
}

/// Merge conflict on the item's merge request.
struct ConflictIcon: View {
    var body: some View {
        Image(systemName: "exclamationmark.triangle.fill")
            .foregroundStyle(.orange)
            .help("Merge konflikt, je potřeba ho vyřešit")
    }
}

/// Review threads as resolved/total in red while some are open, a check mark once all are resolved.
struct ThreadsBadge: View {
    let resolved: Int
    let total: Int

    var body: some View {
        let isDone = resolved == total
        HStack(spacing: 2) {
            Image(systemName: "bubble.left.fill")
                .imageScale(.small)
            if isDone {
                Image(systemName: "checkmark")
                    .fontWeight(.bold)
            } else {
                Text("\(resolved)/\(total)")
                    .monospacedDigit()
            }
        }
        .fontWeight(.semibold)
        .foregroundStyle(isDone ? Color.green : Color.red)
        .help(isDone ? "Všechna vlákna jsou vyřešená" : "Vyřešeno \(resolved) z \(total) vláken")
    }
}

/// Position in the timeline's queue: a number, paused when the item waits, an outline when it is new.
struct WorkBadge: View {
    let item: TodoItem
    let number: Int?
    var size: CGFloat = 22

    var body: some View {
        ZStack {
            if item.isWaiting {
                Circle()
                    .fill(Palette.waiting.opacity(0.18))
                Image(systemName: "pause.fill")
                    .font(.system(size: size * 0.4, weight: .bold))
                    .foregroundStyle(Palette.waiting)
            } else if let number {
                Circle()
                    .fill(number == 1 ? Palette.headline : Color.primary.opacity(0.09))
                Text("\(number)")
                    .font(.system(size: size * (number < 10 ? 0.52 : 0.44), weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(number == 1 ? Color.white : Color.primary)
            } else {
                Circle()
                    .strokeBorder(Color.accentColor.opacity(0.75), style: StrokeStyle(lineWidth: 1.5, dash: [2.5, 2]))
                Circle()
                    .fill(Color.accentColor)
                    .frame(width: size * 0.28, height: size * 0.28)
            }
        }
        .frame(width: size, height: size)
    }
}
