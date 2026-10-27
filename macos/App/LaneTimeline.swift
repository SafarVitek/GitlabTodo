import AppKit
import os
import SwiftUI

// MARK: - Schedule

/// One block in the timeline: an item of the user's, or someone else's issue or merge request shown for context.
struct TimelineBlock: Identifiable, Equatable {
    enum Content: Equatable {
        case own(TodoItem)
        case external(ExternalBlocker)
    }

    let content: Content
    let laneKey: String
    /// Blocks this one waits on.
    let blockers: [String]
    /// Empty steps the user put before it.
    var delay = 0
    var slot = 0
    /// Steps before this are empty because of the delay, the ones from here to `slot` because of a wait.
    var delayedUntil = 0

    var id: String {
        switch content {
        case .own(let item): item.id
        case .external(let blocker): blocker.id
        }
    }

    var item: TodoItem? {
        if case .own(let item) = content { item } else { nil }
    }

    var external: ExternalBlocker? {
        if case .external(let blocker) = content { blocker } else { nil }
    }

    var title: String {
        item?.title ?? external?.title ?? ""
    }

    var reference: String {
        if let item {
            return item.references
        }
        let reference = external?.reference ?? ""
        return reference.firstIndex { $0 == "#" || $0 == "!" }.map { String(reference[$0...]) } ?? reference
    }

    var webURL: URL? {
        item?.webURL ?? external?.webURL
    }
}

struct TimelineLane: Identifiable {
    let key: String
    let name: String
    let color: Int
    let accountID: UUID
    let projectID: Int
    var blocks: [TimelineBlock] = []

    var id: String { key }
}

/// Lanes per project with their items in work order. Every block is one step long and starts only after the block
/// before it in its lane and everything it waits on, which leaves gaps where an item waits on another project.
struct TimelineSchedule {
    var lanes: [TimelineLane]
    var stepCount: Int

    static func build(items: [TodoItem], external: [ExternalBlocker], positions: [String: Int], delays: [String: Int] = [:]) -> TimelineSchedule {
        var lanes: [String: TimelineLane] = [:]
        var keys: [String: Double] = [:]
        let unplacedStart = (positions.values.max() ?? -1) + 1

        for (index, item) in items.enumerated() {
            let laneKey = item.projectKey
            if lanes[laneKey] == nil {
                lanes[laneKey] = TimelineLane(key: laneKey, name: item.projectName, color: item.projectColor, accountID: item.accountID, projectID: item.projectID)
            }
            let blockers = (item.wait?.itemIDs ?? []) + (item.wait?.external.map(\.id) ?? [])
            lanes[laneKey]?.blocks.append(TimelineBlock(content: .own(item), laneKey: laneKey, blockers: blockers, delay: delays[item.id] ?? 0))
            // Items come in work order already, new ones after the placed.
            keys[item.id] = Double(positions[item.id] ?? unplacedStart + index)
        }
        for blocker in external {
            let laneKey = "\(blocker.accountID.uuidString):\(blocker.projectID)"
            if lanes[laneKey] == nil {
                let name = blocker.reference.prefix { $0 != "#" && $0 != "!" }.split(separator: "/").last.map(String.init) ?? blocker.reference
                lanes[laneKey] = TimelineLane(
                    key: laneKey, name: name, color: Palette.preferredSlot(forProject: laneKey),
                    accountID: blocker.accountID, projectID: blocker.projectID)
            }
            lanes[laneKey]?.blocks.append(TimelineBlock(content: .external(blocker), laneKey: laneKey, blockers: [], delay: delays[blocker.id] ?? 0))
            // Someone else's work not placed by the user is probably in progress, it goes first.
            keys[blocker.id] = positions[blocker.id].map(Double.init) ?? -1
        }

        var sorted = lanes.values.map { lane in
            var lane = lane
            lane.blocks.sort { (keys[$0.id] ?? 0, $0.title) < (keys[$1.id] ?? 0, $1.title) }
            keepBehindBlockers(&lane.blocks)
            return lane
        }
        // The project with the most urgent own item first, projects of other people only after the user's.
        let laneRank = { (lane: TimelineLane) in lane.blocks.compactMap { $0.item.flatMap { keys[$0.id] } }.min() ?? .infinity }
        sorted.sort { (laneRank($0), $0.name) < (laneRank($1), $1.name) }

        let (steps, delayedUntil) = schedule(sorted)
        for laneIndex in sorted.indices {
            for blockIndex in sorted[laneIndex].blocks.indices {
                let id = sorted[laneIndex].blocks[blockIndex].id
                sorted[laneIndex].blocks[blockIndex].slot = steps[id] ?? 0
                sorted[laneIndex].blocks[blockIndex].delayedUntil = delayedUntil[id] ?? 0
            }
        }
        return TimelineSchedule(lanes: sorted, stepCount: (steps.values.max() ?? -1) + 1)
    }

    /// A block waiting on a later block of its own lane moves right behind it, whatever the stored order says.
    private static func keepBehindBlockers(_ blocks: inout [TimelineBlock]) {
        for _ in 0..<blocks.count * blocks.count {
            guard let index = blocks.indices.first(where: { index in
                blocks.indices.contains { $0 > index && blocks[index].blockers.contains(blocks[$0].id) }
            }) else { return }
            let blocker = blocks.indices.last { $0 > index && blocks[index].blockers.contains(blocks[$0].id) }!
            let block = blocks.remove(at: index)
            // The blocker moved one up with the removal, so this lands right after it.
            blocks.insert(block, at: blocker)
        }
    }

    /// Assigns steps lane by lane as far as blockers allow, repeating until nothing moves. A dependency cycle would
    /// stall that, then the first stuck block is placed ignoring the blockers that never got a step.
    private static func schedule(_ lanes: [TimelineLane]) -> (steps: [String: Int], delayedUntil: [String: Int]) {
        let shown = Set(lanes.flatMap { $0.blocks.map(\.id) })
        var steps: [String: Int] = [:]
        var delayedUntil: [String: Int] = [:]
        func start(of block: TimelineBlock, after previous: Int) -> Int {
            let blockers = block.blockers.filter(shown.contains).compactMap { steps[$0] }
            let delayed = previous + 1 + block.delay
            delayedUntil[block.id] = delayed
            return max(delayed, (blockers.max() ?? -1) + 1)
        }
        while steps.count < shown.count {
            var progressed = true
            while progressed {
                progressed = false
                for lane in lanes {
                    var previous = -1
                    for block in lane.blocks {
                        if let step = steps[block.id] {
                            previous = step
                            continue
                        }
                        guard block.blockers.filter(shown.contains).allSatisfy({ steps[$0] != nil }) else { break }
                        steps[block.id] = start(of: block, after: previous)
                        previous = steps[block.id]!
                        progressed = true
                    }
                }
            }
            // Break a cycle at the first block that is still waiting.
            outer: for lane in lanes {
                var previous = -1
                for block in lane.blocks {
                    if let step = steps[block.id] {
                        previous = step
                        continue
                    }
                    steps[block.id] = start(of: block, after: previous)
                    break outer
                }
            }
        }
        return (steps, delayedUntil)
    }
}

// MARK: - View

/// Sizes of the timeline at a zoom level. Everything is laid out and drawn at its final size, text included,
/// so the timeline stays sharp at any zoom rather than being a scaled picture.
struct TimelineMetrics: Equatable {
    var zoom: CGFloat = 1

    var padding: CGFloat { 20 * zoom }
    var headerWidth: CGFloat { 170 * zoom }
    var blockWidth: CGFloat { 200 * zoom }
    var blockHeight: CGFloat { 72 * zoom }
    var columnGap: CGFloat { 20 * zoom }
    var laneGap: CGFloat { 14 * zoom }
    var rulerHeight: CGFloat { 26 * zoom }

    func scaled(_ value: CGFloat) -> CGFloat {
        value * zoom
    }

    /// Lines thinner than this get hard to see when zoomed out.
    func line(_ width: CGFloat) -> CGFloat {
        max(1, width * zoom)
    }

    func font(_ size: CGFloat, weight: Font.Weight = .regular) -> Font {
        .system(size: size * zoom, weight: weight)
    }

    func x(_ step: Int) -> CGFloat {
        padding + headerWidth + CGFloat(step) * (blockWidth + columnGap)
    }

    func y(_ lane: Int) -> CGFloat {
        padding + rulerHeight + CGFloat(lane) * (blockHeight + laneGap)
    }

    /// The step whose column, including half of the gaps around it, contains `x`.
    func step(at x: CGFloat) -> Int {
        max(0, Int(((x - padding - headerWidth + columnGap / 2) / (blockWidth + columnGap)).rounded(.down)))
    }

    func size(steps: Int, lanes: Int) -> CGSize {
        CGSize(width: x(steps) + padding, height: y(lanes) + padding)
    }
}

/// Scrolls the timeline sideways while something is dragged near or past its left or right edge. A system drag
/// session delivers no mouse events to the app, so the pointer is read from the window 60 times a second, and the
/// AppKit scroll view under the SwiftUI one is scrolled directly: SwiftUI reports its scroll offset a frame late,
/// which made a loop built on it jump back and forth.
@MainActor
@Observable
final class TimelineAutoScroller {
    private static let edge: CGFloat = 56
    private static let maxStep: CGFloat = 16

    @ObservationIgnored weak var scrollView: NSScrollView?
    @ObservationIgnored private var timer: Timer?

    /// Starts following the pointer; stops on its own when the mouse button is released.
    func begin() {
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        // Common modes keep it running inside the drag session's event tracking loop.
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func tick() {
        guard NSEvent.pressedMouseButtons & 1 != 0, let scrollView, let window = scrollView.window, let document = scrollView.documentView else {
            timer?.invalidate()
            timer = nil
            return
        }
        NSCursor.closedHand.set()
        // Window coordinates have their origin at the bottom left.
        let viewport = scrollView.convert(scrollView.bounds, to: nil)
        let pointer = window.mouseLocationOutsideOfEventStream
        // Only while the pointer is level with the timeline, give or take a little.
        let fromTop = viewport.maxY - pointer.y
        guard fromTop >= -40, fromTop <= viewport.height + 40 else { return }
        let dx = speed(pointer.x - viewport.minX, length: viewport.width)
        guard dx != 0 else { return }

        let clip = scrollView.contentView
        var origin = clip.bounds.origin
        origin.x = min(max(0, origin.x + dx), max(0, document.frame.width - clip.bounds.width))
        guard origin != clip.bounds.origin else { return }
        clip.scroll(to: origin)
        scrollView.reflectScrolledClipView(clip)
    }

    /// Faster the closer to the edge, full speed at it and beyond.
    private func speed(_ position: CGFloat, length: CGFloat) -> CGFloat {
        if position < Self.edge {
            let closeness = min(1, (Self.edge - position) / Self.edge)
            return -Self.maxStep * closeness * closeness
        }
        if position > length - Self.edge {
            let closeness = min(1, (position - (length - Self.edge)) / Self.edge)
            return Self.maxStep * closeness * closeness
        }
        return 0
    }
}

/// Hands over the AppKit scroll view that SwiftUI's `ScrollView` is built on.
private struct EnclosingScrollViewReader: NSViewRepresentable {
    let onFind: (NSScrollView) -> Void

    func makeNSView(context: Context) -> NSView {
        NSView()
    }

    func updateNSView(_ view: NSView, context: Context) {
        // Only in the view hierarchy after this update.
        Task { @MainActor in
            if let scrollView = view.enclosingScrollView {
                onFind(scrollView)
            }
        }
    }
}

/// Horizontal timeline: a lane per project, items as equally long steps, arrows where one waits on another.
struct LaneTimeline: View {
    static let zoomKey = "timelineZoom"
    static let zoomRange = 0.3...1.5

    /// The next zoom level, in steps of 10 %.
    static func zoom(_ zoom: Double, by step: Double) -> Double {
        min(zoomRange.upperBound, max(zoomRange.lowerBound, ((zoom + step) * 10).rounded() / 10))
    }

    @Environment(AppModel.self) private var model
    @AppStorage(LaneTimeline.zoomKey) private var zoom = 1.0
    @State private var pinchStart: Double?
    @State private var position = ScrollPosition()
    @State private var visible = CGRect.zero
    @State private var scroller = TimelineAutoScroller()
    @Binding var selection: String?

    var body: some View {
        let schedule = TimelineSchedule.build(items: model.items, external: model.timelineExternal, positions: model.orderPositions, delays: model.timelineDelays)
        // One empty step at the end to drop blocks behind the last one.
        let steps = max(schedule.stepCount + 1, 4)
        let metrics = TimelineMetrics(zoom: zoom)
        ScrollView([.horizontal, .vertical]) {
            TimelineContent(schedule: schedule, steps: steps, metrics: metrics, selection: $selection)
                .environment(scroller)
                .background(EnclosingScrollViewReader { scroller.scrollView = $0 })
        }
        .scrollPosition($position)
        .onScrollGeometryChange(for: CGRect.self, of: { $0.visibleRect }) { _, rect in
            visible = rect
        }
        .simultaneousGesture(
            MagnifyGesture()
                .onChanged { value in
                    let start = pinchStart ?? zoom
                    pinchStart = start
                    zoom = min(Self.zoomRange.upperBound, max(Self.zoomRange.lowerBound, start * value.magnification))
                }
                .onEnded { _ in pinchStart = nil })
        .onChange(of: zoom) { old, new in
            keepCenter(from: old, to: new)
        }
        .overlay {
            if schedule.lanes.isEmpty {
                ContentUnavailableView("Nic tu není", systemImage: "checkmark.circle")
            }
        }
    }

    /// Zooming keeps what was in the middle of the view in the middle.
    private func keepCenter(from old: Double, to new: Double) {
        guard old > 0, visible.width > 0 else { return }
        let factor = new / old
        position.scrollTo(point: CGPoint(
            x: max(0, visible.midX * factor - visible.width / 2),
            y: max(0, visible.midY * factor - visible.height / 2)))
    }
}

struct TimelineContent: View {
    static let space = "timeline"

    @Environment(AppModel.self) private var model
    @State private var link: LinkDrag?
    let schedule: TimelineSchedule
    let steps: Int
    var metrics = TimelineMetrics()
    @Binding var selection: String?

    var body: some View {
        let size = metrics.size(steps: steps, lanes: schedule.lanes.count)
        ZStack(alignment: .topLeading) {
            Ruler(steps: steps, height: size.height, metrics: metrics)
            // Lane backgrounds below the arrows, so the arrows can be right-clicked; blocks on top.
            ForEach(Array(schedule.lanes.enumerated()), id: \.element.id) { index, lane in
                LaneBackground(lane: lane, index: index, width: size.width, metrics: metrics)
            }
            DependencyArrows(schedule: schedule, size: size, metrics: metrics)
            ForEach(Array(schedule.lanes.enumerated()), id: \.element.id) { index, lane in
                LaneRow(lane: lane, index: index, metrics: metrics, selection: $selection)
            }
            // The dots on the right edge of every block, dragged out into a „waits on“ arrow.
            ForEach(Array(schedule.lanes.enumerated()), id: \.element.id) { index, lane in
                ForEach(lane.blocks) { block in
                    let start = CGPoint(x: metrics.x(block.slot) + metrics.blockWidth, y: metrics.y(index) + metrics.blockHeight / 2)
                    let handle = LinkHandle.size(metrics)
                    LinkHandle(metrics: metrics) { location in
                        link = LinkDrag(fromID: block.id, fromReference: block.reference, start: start, location: location)
                    } onEnded: { location in
                        finishLink(from: block, at: location)
                    }
                    .offset(x: start.x - handle / 2, y: start.y - handle / 2)
                }
            }
            if let link {
                let target = hit(link.location)
                LinkPreview(
                    link: link,
                    target: target,
                    isValid: target.map { model.canLink($0.block.id, waitsOn: link.fromID) } ?? false,
                    size: size,
                    metrics: metrics)
            }
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .coordinateSpace(.named(Self.space))
    }

    private func hit(_ location: CGPoint) -> (block: TimelineBlock, frame: CGRect)? {
        for (index, lane) in schedule.lanes.enumerated() {
            for block in lane.blocks {
                let frame = CGRect(x: metrics.x(block.slot), y: metrics.y(index), width: metrics.blockWidth, height: metrics.blockHeight)
                if frame.contains(location) {
                    return (block, frame)
                }
            }
        }
        return nil
    }

    private func finishLink(from block: TimelineBlock, at location: CGPoint) {
        defer { link = nil }
        guard let target = hit(location)?.block, model.canLink(target.id, waitsOn: block.id) else { return }
        model.link(target.id, waitsOn: block.id)
    }
}

/// An arrow being drawn from a block's dot.
private struct LinkDrag {
    let fromID: String
    let fromReference: String
    let start: CGPoint
    var location: CGPoint
}

private struct LinkHandle: View {
    /// Small enough to stay out of the way, never too small to grab.
    static func size(_ metrics: TimelineMetrics) -> CGFloat {
        max(12, metrics.scaled(16))
    }

    @State private var isHovered = false
    @Environment(TimelineAutoScroller.self) private var scroller
    let metrics: TimelineMetrics
    let onChanged: (CGPoint) -> Void
    let onEnded: (CGPoint) -> Void

    var body: some View {
        let dot = max(isHovered ? 8 : 6, metrics.scaled(isHovered ? 12 : 9))
        Circle()
            .fill(Palette.waiting.opacity(isHovered ? 1 : 0.75))
            .overlay(Circle().strokeBorder(Color(nsColor: .controlBackgroundColor), lineWidth: metrics.line(1.5)))
            .frame(width: dot, height: dot)
            .frame(width: Self.size(metrics), height: Self.size(metrics))
            .contentShape(Circle())
            .onHover { isHovered = $0 }
            .help("Přidat vazbu")
            .gesture(
                DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineContent.space))
                    .onChanged {
                        scroller.begin()
                        onChanged($0.location)
                    }
                    .onEnded { onEnded($0.location) })
    }
}

/// The arrow following the pointer, the block under it and what letting go would do.
private struct LinkPreview: View {
    let link: LinkDrag
    let target: (block: TimelineBlock, frame: CGRect)?
    let isValid: Bool
    let size: CGSize
    let metrics: TimelineMetrics

    var body: some View {
        let color = target == nil ? Palette.waiting : isValid ? Color.accentColor : Color.red
        // Over a block the arrow snaps to where the finished one will end.
        let end = target.map { CGPoint(x: $0.frame.minX - 2, y: $0.frame.midY) } ?? link.location
        let shape = RoundedRectangle(cornerRadius: metrics.scaled(9), style: .continuous)
        ZStack(alignment: .topLeading) {
            if let target {
                shape
                    .fill(color.opacity(0.12))
                    .overlay(shape.strokeBorder(color, lineWidth: 2))
                    .frame(width: target.frame.width, height: target.frame.height)
                    .offset(x: target.frame.minX, y: target.frame.minY)
            }
            Canvas { context, _ in
                let bend = max(30 * metrics.zoom, abs(end.x - link.start.x) / 2)
                var path = Path()
                path.move(to: link.start)
                path.addCurve(to: end, control1: CGPoint(x: link.start.x + bend, y: link.start.y), control2: CGPoint(x: end.x - bend, y: end.y))
                context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: 2, lineCap: .round, dash: [6, 4]))
                context.fill(Path(ellipseIn: CGRect(x: end.x - 4, y: end.y - 4, width: 8, height: 8)), with: .color(color))
            }
            .frame(width: size.width, height: size.height)
            // The hint stays readable at any zoom.
            Text(label)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.white)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(Capsule().fill(color))
                .fixedSize()
                .offset(x: link.location.x + 14, y: link.location.y + 14)
        }
        .allowsHitTesting(false)
    }

    private var label: String {
        guard let target else { return "Pusť na položku, která na \(link.fromReference) čeká" }
        return isValid ? "\(target.block.reference) čeká na \(link.fromReference)" : "Sem vazba nejde"
    }
}

private struct Ruler: View {
    let steps: Int
    let height: CGFloat
    let metrics: TimelineMetrics

    var body: some View {
        ForEach(0..<steps, id: \.self) { step in
            Text(step == 0 ? "Teď" : "+\(step)")
                .font(metrics.font(11, weight: .semibold))
                .foregroundStyle(step == 0 ? Palette.headline : Color.secondary)
                .frame(width: metrics.blockWidth)
                .offset(x: metrics.x(step), y: metrics.padding)
            Rectangle()
                .fill(Color.primary.opacity(0.06))
                .frame(width: 1, height: height - metrics.padding * 2 - metrics.rulerHeight)
                .offset(x: metrics.x(step) - metrics.columnGap / 2, y: metrics.padding + metrics.rulerHeight)
        }
    }
}

/// A „waits on“ link between two blocks, routed through the free space between columns and lanes.
private struct DependencyEdge: Identifiable {
    let dependentID: String
    let blockerID: String
    let dependentReference: String
    let blockerReference: String
    let points: [CGPoint]
    let corner: CGFloat
    let headSize: CGFloat

    var id: String { "\(blockerID)>\(dependentID)" }

    static func edges(in schedule: TimelineSchedule, metrics: TimelineMetrics) -> [DependencyEdge] {
        let places = Dictionary(
            schedule.lanes.enumerated().flatMap { index, lane in lane.blocks.map { ($0.id, (lane: index, block: $0)) } },
            uniquingKeysWith: { first, _ in first })
        return schedule.lanes.enumerated().flatMap { laneIndex, lane in
            lane.blocks.flatMap { block in
                block.blockers.compactMap { blockerID -> DependencyEdge? in
                    guard let blocker = places[blockerID] else { return nil }
                    return DependencyEdge(
                        dependentID: block.id,
                        blockerID: blockerID,
                        dependentReference: block.reference,
                        blockerReference: blocker.block.reference,
                        points: route(from: (blocker.lane, blocker.block.slot), to: (laneIndex, block.slot), metrics: metrics),
                        corner: metrics.scaled(8),
                        headSize: max(4, metrics.scaled(7)))
                }
            }
        }
    }

    /// Right out of the blocker, down or up in the gap between columns, along the gap between lanes next to the
    /// target and into it from the left. Blocks are never crossed except by links inside one lane.
    private static func route(from: (lane: Int, step: Int), to: (lane: Int, step: Int), metrics: TimelineMetrics) -> [CGPoint] {
        let start = CGPoint(x: metrics.x(from.step) + metrics.blockWidth, y: metrics.y(from.lane) + metrics.blockHeight / 2)
        let end = CGPoint(x: metrics.x(to.step) - 2, y: metrics.y(to.lane) + metrics.blockHeight / 2)
        guard from.lane != to.lane, to.step > from.step else { return [start, end] }
        let lastGap = metrics.x(to.step) - metrics.columnGap / 2
        if to.step == from.step + 1 {
            return [start, CGPoint(x: lastGap, y: start.y), CGPoint(x: lastGap, y: end.y), end]
        }
        let firstGap = start.x + metrics.columnGap / 2
        let channel = to.lane > from.lane
            ? metrics.y(to.lane) - metrics.laneGap / 2
            : metrics.y(to.lane) + metrics.blockHeight + metrics.laneGap / 2
        return [
            start, CGPoint(x: firstGap, y: start.y), CGPoint(x: firstGap, y: channel),
            CGPoint(x: lastGap, y: channel), CGPoint(x: lastGap, y: end.y), end,
        ]
    }

    var path: Path {
        var path = Path()
        guard let first = points.first, let last = points.last else { return path }
        path.move(to: first)
        for index in points.indices.dropFirst().dropLast() {
            let point = points[index]
            let radius = min(corner, distance(points[index - 1], point) / 2, distance(point, points[index + 1]) / 2)
            path.addArc(tangent1End: point, tangent2End: points[index + 1], radius: radius)
        }
        path.addLine(to: last)
        return path
    }

    var head: Path {
        var head = Path()
        guard let end = points.last else { return head }
        head.move(to: CGPoint(x: end.x - headSize, y: end.y - headSize * 0.65))
        head.addLine(to: end)
        head.addLine(to: CGPoint(x: end.x - headSize, y: end.y + headSize * 0.65))
        return head
    }

    private func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
        hypot(a.x - b.x, a.y - b.y)
    }
}

/// Orange arrows from what an item waits on to the item, removable with a right click.
private struct DependencyArrows: View {
    let schedule: TimelineSchedule
    let size: CGSize
    let metrics: TimelineMetrics

    var body: some View {
        ZStack(alignment: .topLeading) {
            ForEach(DependencyEdge.edges(in: schedule, metrics: metrics)) { edge in
                DependencyArrow(edge: edge, metrics: metrics)
            }
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
    }
}

private struct DependencyArrow: View {
    @Environment(AppModel.self) private var model
    @State private var isHovered = false
    let edge: DependencyEdge
    let metrics: TimelineMetrics

    var body: some View {
        let style = StrokeStyle(lineWidth: metrics.line(isHovered ? 2.6 : 1.6), lineCap: .round, lineJoin: .round)
        ZStack {
            edge.path.stroke(Palette.waiting.opacity(isHovered ? 1 : 0.8), style: style)
            edge.head.stroke(Palette.waiting, style: style)
        }
        // A wide band along the line takes the pointer, the rest of the view lets it through.
        .contentShape(edge.path.strokedPath(StrokeStyle(lineWidth: 12, lineCap: .round, lineJoin: .round)))
        .onHover { isHovered = $0 }
        .help("\(edge.dependentReference) čeká na \(edge.blockerReference). Pravým tlačítkem vazbu odebereš.")
        .contextMenu {
            Button("Odebrat vazbu: \(edge.dependentReference) čeká na \(edge.blockerReference)") {
                model.removeLink(edge.dependentID, waitsOn: edge.blockerID)
            }
        }
    }
}

/// The lane's background takes drops onto its empty steps: the block moves there, with a delay if needed.
private struct LaneBackground: View {
    @Environment(AppModel.self) private var model
    @Environment(TimelineAutoScroller.self) private var scroller
    @State private var isTargeted = false
    let lane: TimelineLane
    let index: Int
    let width: CGFloat
    let metrics: TimelineMetrics

    var body: some View {
        let laneIDs = lane.blocks.map(\.id)
        let inset = metrics.scaled(4)
        RoundedRectangle(cornerRadius: metrics.scaled(12), style: .continuous)
            .fill(Color.primary.opacity(isTargeted ? 0.08 : 0.03))
            .frame(width: width - metrics.padding * 2 + inset * 2, height: metrics.blockHeight + inset * 2)
            .dropDestination(for: String.self) { ids, location in
                guard let dragged = ids.first, laneIDs.contains(dragged) else { return false }
                // The location is relative to the background, which starts a little left of the lane.
                model.placeInTimeline(dragged, atStep: metrics.step(at: location.x + metrics.padding - inset), laneIDs: laneIDs)
                return true
            } isTargeted: {
                isTargeted = $0
                if $0 {
                    scroller.begin()
                }
            }
            .offset(x: metrics.padding - inset, y: metrics.y(index) - inset)
    }
}

private struct LaneRow: View {
    @State private var isAdding = false
    let lane: TimelineLane
    let index: Int
    let metrics: TimelineMetrics
    @Binding var selection: String?

    var body: some View {
        let y = metrics.y(index)
        let laneIDs = lane.blocks.map(\.id)
        HStack(spacing: metrics.scaled(6)) {
            Circle()
                .fill(Palette.project(lane.color))
                .frame(width: metrics.scaled(9), height: metrics.scaled(9))
            Text(lane.name)
                .font(metrics.font(13, weight: .semibold))
                .lineLimit(2)
            Spacer(minLength: 2)
            Button {
                isAdding = true
            } label: {
                Image(systemName: "plus.circle")
                    .font(metrics.font(14))
            }
            .buttonStyle(.borderless)
            .help("Přidat cizí issue nebo MR z \(lane.name)")
            .popover(isPresented: $isAdding, arrowEdge: .bottom) {
                LaneSearch(lane: lane)
            }
        }
        .padding(.leading, metrics.scaled(10))
        .padding(.trailing, metrics.scaled(14))
        .frame(width: metrics.headerWidth, height: metrics.blockHeight)
        .offset(x: metrics.padding, y: y)

        ForEach(gaps, id: \.step) { gap in
            let color = gap.isDelay ? Color.secondary : Palette.waiting
            RoundedRectangle(cornerRadius: metrics.scaled(9), style: .continuous)
                .strokeBorder(color.opacity(0.35), style: StrokeStyle(lineWidth: 1, dash: [metrics.line(4), metrics.line(3)]))
                .overlay {
                    Label(gap.isDelay ? "odloženo" : "čeká", systemImage: gap.isDelay ? "clock" : "pause.circle")
                        .font(metrics.font(11))
                        .foregroundStyle(color.opacity(0.8))
                }
                .frame(width: metrics.blockWidth, height: metrics.blockHeight)
                .offset(x: metrics.x(gap.step), y: y)
                // Drops go through to the lane background below.
                .allowsHitTesting(false)
        }

        ForEach(lane.blocks) { block in
            BlockView(block: block, laneIDs: laneIDs, isSelected: selection == block.id, metrics: metrics) {
                if let item = block.item {
                    selection = item.id
                }
            }
            // Selecting redraws just the two blocks whose selection changed, not the whole timeline.
            .equatable()
            .offset(x: metrics.x(block.slot), y: y)
        }
    }

    /// Steps where the lane stands still: put off by the user, or because the next item waits on something.
    private var gaps: [(step: Int, isDelay: Bool)] {
        var gaps: [(step: Int, isDelay: Bool)] = []
        var next = 0
        for block in lane.blocks {
            for step in next..<max(next, block.slot) {
                gaps.append((step, step < block.delayedUntil))
            }
            next = block.slot + 1
        }
        return gaps
    }
}

private struct BlockView: View, Equatable {
    private static let logger = Logger(subsystem: "local.gitlabtodo", category: "timeline")

    @Environment(AppModel.self) private var model
    @Environment(TimelineAutoScroller.self) private var scroller
    @State private var isTargeted = false
    let block: TimelineBlock
    let laneIDs: [String]
    let isSelected: Bool
    let metrics: TimelineMetrics
    let onSelect: () -> Void

    nonisolated static func == (lhs: BlockView, rhs: BlockView) -> Bool {
        lhs.block == rhs.block && lhs.laneIDs == rhs.laneIDs && lhs.isSelected == rhs.isSelected && lhs.metrics == rhs.metrics
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: metrics.scaled(9), style: .continuous)
        HStack(spacing: 0) {
            Rectangle()
                .fill(stripe)
                .frame(width: metrics.scaled(4))
            VStack(alignment: .leading, spacing: metrics.scaled(3)) {
                HStack(spacing: metrics.scaled(5)) {
                    if let item = block.item {
                        StageCircle(item: item, size: metrics.scaled(13))
                    } else {
                        Image(systemName: "person.crop.circle")
                            .foregroundStyle(.secondary)
                    }
                    Text(block.reference)
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                    Spacer(minLength: 2)
                    statusIcons
                }
                .font(metrics.font(11))
                Text(block.title)
                    .font(metrics.font(12.5, weight: .medium))
                    .lineLimit(2)
                    .foregroundStyle(block.item?.isWaiting == true ? .secondary : .primary)
                Spacer(minLength: 0)
                if let item = block.item {
                    FittingLabels(labels: item.labels)
                        .font(metrics.font(10))
                        .environment(\.labelScale, metrics.zoom)
                } else {
                    Text("cizí")
                        .font(metrics.font(10, weight: .semibold))
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, metrics.scaled(8))
            .padding(.vertical, metrics.scaled(7))
        }
        .frame(width: metrics.blockWidth, height: metrics.blockHeight, alignment: .topLeading)
        .background(shape.fill(block.item == nil ? Color.primary.opacity(0.04) : Color(nsColor: .controlBackgroundColor)))
        .clipShape(shape)
        .overlay {
            if isTargeted {
                shape.strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [5, 3]))
            } else if isSelected {
                shape.strokeBorder(Color.accentColor, lineWidth: 2)
            } else if block.item == nil {
                shape.strokeBorder(Color.secondary.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [metrics.line(4), metrics.line(3)]))
            } else {
                shape.strokeBorder(Color.primary.opacity(0.1), lineWidth: 1)
            }
        }
        .contentShape(shape)
        .pointerStyle(.grabIdle)
        // One tap gesture, simultaneous so dragging does not swallow it. The event's click count tells a double-click,
        // which opens the item, from a click, which selects it right away.
        .simultaneousGesture(TapGesture().onEnded {
            let clicks = NSApp.currentEvent?.clickCount ?? 1
            Self.logger.notice("Timeline block clicked (\(clicks, privacy: .public)x)")
            if clicks >= 2, let url = block.webURL {
                NSWorkspace.shared.open(url)
            } else {
                onSelect()
            }
        })
        .draggable(block.id)
        .dropDestination(for: String.self) { ids, _ in
            guard let dragged = ids.first, dragged != block.id else { return false }
            if laneIDs.contains(dragged) {
                model.placeInLane(dragged, onto: block.id, laneIDs: laneIDs)
            } else {
                model.linkInTimeline(dragged, onto: block.id)
            }
            return true
        } isTargeted: {
            isTargeted = $0
            // A dragged block is over a drop target right away, which starts scrolling near the edge.
            if $0 {
                scroller.begin()
            }
        }
        .contextMenu {
            Button("O krok později") { model.delay(block.id, by: 1) }
            Button("O krok dřív") { model.delay(block.id, by: -1) }
                .disabled(block.delay == 0)
            if block.delay > 0 {
                Button("Zrušit odklad") { model.delay(block.id, by: -block.delay) }
            }
            Divider()
            if let item = block.item {
                ItemMenu(item: item)
            } else if let external = block.external {
                LinkRemovalMenu(id: external.id, reference: block.reference, blockers: [])
                Button("Otevřít v GitLabu") { NSWorkspace.shared.open(external.webURL) }
                if model.isWaitedOn(external) {
                    Text("Čeká na to některá tvoje položka")
                } else {
                    Button("Odebrat z timeline") { model.removeFromTimeline(external) }
                }
            }
        }
        .help(block.item == nil ? "Cizí: \(block.reference) \(block.title)" : "Přetáhni v řádku pro změnu pořadí, na jiný projekt pro vazbu „čeká na“")
    }

    private var stripe: Color {
        guard let item = block.item else { return .secondary.opacity(0.5) }
        return item.isWaiting ? Palette.waiting : item.stage.color
    }

    @ViewBuilder
    private var statusIcons: some View {
        if block.delay > 0 {
            Image(systemName: "clock")
                .foregroundStyle(.secondary)
                .help("Odloženo o \(block.delay) \(block.delay == 1 ? "krok" : block.delay < 5 ? "kroky" : "kroků")")
        }
        if let item = block.item {
            if item.isWaiting {
                Image(systemName: "pause.circle.fill")
                    .foregroundStyle(Palette.waiting)
            }
            if item.releasedBy != nil {
                Image(systemName: "play.circle.fill")
                    .foregroundStyle(.green)
            }
            if item.hasMergeConflict {
                ConflictIcon()
            }
            if let threads = item.reviewThreads {
                ThreadsBadge(resolved: threads.resolved, total: threads.total)
            }
        }
    }
}

/// „+“ of a lane: someone else's issue or merge request of that project into the timeline.
private struct LaneSearch: View {
    @Environment(AppModel.self) private var model
    @FocusState private var isFocused: Bool
    let lane: TimelineLane

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Přidat do \(lane.name)")
                .font(.headline)
            Text("Cizí issue nebo MR z tohoto projektu. Můžeš ho pak přetahovat a navazovat na něj, po zavření zmizí.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            WorkItemSearch(
                prompt: "slovo z názvu nebo číslo",
                ownProjectID: lane.projectID,
                isFocused: $isFocused,
                search: { try await model.searchProject($0, accountID: lane.accountID, projectID: lane.projectID) },
                pick: { model.addToTimeline($0.blocker) })
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.05)))
        }
        .padding(14)
        .frame(width: 400)
        .onAppear { isFocused = true }
    }
}
