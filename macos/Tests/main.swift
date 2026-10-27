import Foundation
import SwiftUI
setvbuf(stdout, nil, _IONBF, 0)

var failures = 0
@MainActor func check(_ condition: Bool, _ message: String) {
    print((condition ? "ok   " : "FAIL ") + message)
    if !condition { failures += 1 }
}

MainActor.assumeIsolated {
    let model = AppModel(preview: .preview)
    func refs(_ items: [TodoItem]) -> [String] { items.map(\.shortReference) }
    let item = { (ref: String) in model.items.first { $0.shortReference == ref }! }
    let id = { (ref: String) in item(ref).id }

    check(refs(model.items) == ["backend#12", "backend#33", "frontend#7", "backend#21", "frontend!52", "infra#5", "infra#9", "frontend#18", "frontend#27", "docs#3", "backend#44"],
          "initial order \(refs(model.items))")
    check(item("backend#33").stage == .mergeRequest && item("backend#33").references == "#33 → !34", "issue with MR is in review: \(item("backend#33").references)")
    check(item("backend#33").nextStep?.action == .markReady && item("backend#33").nextStep?.target.kind == .mergeRequest, "Ready targets the merge request")
    check(item("frontend#7").nextStep?.action == .createMergeRequest, "issue without MR offers MR creation")
    check(item("frontend!52").nextStep == nil, "ready MR with a reviewer has no step")
    check(model.item(withID: item("backend#33").mergeRequests[0].id)?.kind == .mergeRequest, "nested MR is found by ID")
    check(model.items.filter(KindFilter.issues.includes).count == 9 && model.items.filter(KindFilter.mergeRequests.includes).count == 2, "kind filter counts")

    var all = model.items.map(\.id)
    model.move(all, fromOffsets: IndexSet(integer: all.firstIndex(of: id("infra#5"))!), toOffset: 0)
    check(refs(model.items).prefix(2) == ["infra#5", "backend#12"], "move to top")

    let backend = model.items.filter { $0.projectKey == item("backend#12").projectKey }.map(\.id)
    model.move(backend, fromOffsets: IndexSet(integer: backend.firstIndex(of: id("backend#33"))!), toOffset: backend.firstIndex(of: id("backend#12"))!)
    check(refs(model.items).prefix(4) == ["infra#5", "backend#33", "backend#12", "frontend#7"], "filtered move keeps hidden items \(refs(model.items).prefix(4))")

    all = model.items.map(\.id)
    model.move(all, fromOffsets: IndexSet(integer: all.firstIndex(of: id("docs#3"))!), toOffset: 1)
    check(item("docs#3").isPlaced && refs(model.items)[1] == "docs#3", "new item placed by drag")
    check(!item("backend#44").isPlaced, "other new item stays new")

    model.toggleBlocker(item("frontend#7"), for: item("docs#3"))
    model.toggleBlocker(item("infra#5"), for: item("docs#3"))
    check(item("docs#3").waitDescription == "frontend#7, infra#5", "waits on two items")
    check(!model.blockerCandidates(for: item("frontend#7")).contains { $0.id == id("docs#3") }, "no direct cycle")
    check(!model.blockerCandidates(for: item("backend#12")).contains { $0.id == id("backend#21") }, "no indirect cycle")

    // Timeline: lanes per project, one step per item, waiting pushes items back.
    let tm = AppModel(preview: .preview)
    @MainActor func schedule() -> TimelineSchedule { TimelineSchedule.build(items: tm.items, external: tm.timelineExternal, positions: tm.orderPositions) }
    @MainActor func step(_ ref: String) -> Int? {
        schedule().lanes.flatMap(\.blocks).first { ($0.item?.shortReference ?? $0.external?.shortReference) == ref }?.slot
    }
    @MainActor func laneIDs(_ name: String) -> [String] { schedule().lanes.first { $0.name == name }!.blocks.map(\.id) }
    let tid = { (ref: String) in tm.items.first { $0.shortReference == ref }!.id }
    check(schedule().lanes.map(\.name) == ["backend", "frontend", "infra", "docs", "platform"], "a lane per project, colleagues' last: \(schedule().lanes.map(\.name))")
    check(step("backend#21") == 2 && step("infra#9") == 1 && step("frontend#27") == 3 && step("platform#500") == 0, "steps follow order and waits")
    tm.placeInLane(tid("backend#33"), onto: tid("backend#12"), laneIDs: laneIDs("backend"))
    check(schedule().lanes[0].blocks.prefix(2).compactMap(\.item?.shortReference) == ["backend#33", "backend#12"], "drag reorders within a lane")
    tm.linkInTimeline(tid("infra#5"), onto: tid("backend#12"))
    check(tm.items.first { $0.shortReference == "infra#5" }?.wait?.itemIDs == [tid("backend#12")], "drag onto another project makes it wait")
    check(step("infra#5") == 2 && step("infra#9") == 3, "waiting on another project leaves a gap: \(step("infra#5") ?? -1), \(step("infra#9") ?? -1)")
    let api = ExternalBlocker(accountID: UUID(), projectID: 901, kind: .issue, iid: 77, title: "Kolegovo API", reference: "team/api#77", webURL: URL(string: "https://x")!)
    tm.addToTimeline(api)
    check(schedule().lanes.contains { $0.name == "api" } && step("api#77") != nil, "+ adds a colleague's issue")
    tm.linkInTimeline(api.id, onto: tid("docs#3"))
    check(tm.items.first { $0.shortReference == "docs#3" }?.wait?.external.map(\.id) == [api.id], "dragging a colleague's issue onto own makes own wait")
    tm.removeFromTimeline(api)
    check(schedule().lanes.contains { $0.name == "api" }, "stays while something waits on it")
    tm.stopWaiting(tm.items.first { $0.shortReference == "docs#3" }!)
    tm.removeFromTimeline(api)
    check(!schedule().lanes.contains { $0.name == "api" }, "removed once nothing waits on it")
    // #12 waiting on #9 closes a loop through lane order (#9 after #5, #5 waits on #12): must still finish.
    tm.toggleBlocker(tm.items.first { $0.shortReference == "infra#9" }!, for: tm.items.first { $0.shortReference == "backend#12" }!)
    let looped = schedule()
    check(looped.lanes.flatMap(\.blocks).count == tm.items.count + tm.timelineExternal.count && looped.stepCount < 40, "a dependency loop does not hang the schedule")

    // Arrows from a block's dot, also between two GitLabs.
    let second = UUID()
    let foreign = TodoItem(
        id: TodoItem.makeID(accountID: second, kind: .issue, gitLabID: 9001), accountID: second, accountName: "Druhý GitLab", kind: .issue,
        role: .assigned, projectID: 55, iid: 3, title: "Issue ve druhém GitLabu", reference: "lab/other#3", webURL: URL(string: "https://y")!,
        activityAt: .now, isPlaced: true)
    var twoLabs = Snapshot.preview
    twoLabs.items.append(foreign)
    let lm = AppModel(preview: twoLabs)
    let lid = { (ref: String) in lm.items.first { $0.shortReference == ref }!.id }
    check(lm.accounts.count == 2, "two GitLabs")
    check(lm.canLink(lid("backend#12"), waitsOn: lid("other#3")), "own item can wait on one in the other GitLab")
    check(!lm.canLink(lid("backend#12"), waitsOn: lid("backend#12")), "not on itself")
    check(!lm.canLink(lid("backend#12"), waitsOn: lid("backend#21")), "not in a loop (#21 waits on #12)")
    let labColleague = lm.timelineExternal.first!
    check(!lm.canLink(labColleague.id, waitsOn: lid("backend#12")), "someone else's issue cannot wait")
    check(lm.canLink(lid("docs#3"), waitsOn: labColleague.id), "own item can wait on someone else's")
    lm.link(lid("backend#12"), waitsOn: lid("other#3"))
    check(lm.items.first { $0.shortReference == "backend#12" }?.wait?.itemIDs == [lid("other#3")], "arrow across GitLabs makes it wait")
    let crossLab = TimelineSchedule.build(items: lm.items, external: lm.timelineExternal, positions: lm.orderPositions)
    let stepOf = { (ref: String) in crossLab.lanes.flatMap(\.blocks).first { $0.item?.shortReference == ref }?.slot }
    check(stepOf("other#3") == 0 && stepOf("backend#12") == 1, "pushed behind its blocker in the other GitLab")

    // Putting items off: empty steps before them, marked as delay rather than wait.
    let dm = AppModel(preview: .preview)
    let did = { (ref: String) in dm.items.first { $0.shortReference == ref }!.id }
    @MainActor func block(_ ref: String) -> TimelineBlock? {
        dm.timelineSchedule().lanes.flatMap(\.blocks).first { $0.item?.shortReference == ref }
    }
    dm.delay(did("frontend#7"), by: 3)
    check(block("frontend#7")?.slot == 3 && block("frontend#7")?.delayedUntil == 3, "put off by three steps")
    check(block("frontend!52")?.slot == 4, "what follows moves along")
    dm.delay(did("frontend#7"), by: -1)
    check(block("frontend#7")?.slot == 2, "one step back")
    dm.delay(did("frontend#7"), by: -10)
    check(block("frontend#7")?.slot == 0 && block("frontend#7")?.delay == 0, "never before its natural place")
    let infra = dm.timelineSchedule().lanes.first { $0.name == "infra" }!.blocks.map(\.id)
    dm.placeInTimeline(did("infra#9"), atStep: 5, laneIDs: infra)
    check(block("infra#9")?.slot == 5 && block("infra#9")?.delay == 4, "dropped onto an empty step: \(block("infra#9")?.slot ?? -1)")
    dm.placeInTimeline(did("infra#9"), atStep: 0, laneIDs: infra)
    check(block("infra#9")?.slot == 1 && block("infra#9")?.delay == 0, "dropped before what it waits on stays right after it")
    dm.link(did("docs#3"), waitsOn: did("infra#5"))
    dm.removeLink(did("docs#3"), waitsOn: did("infra#5"))
    check(!(dm.items.first { $0.shortReference == "docs#3" }!.isWaiting), "link removed")
    check(dm.dependents(of: did("backend#12")).map(\.shortReference) == ["backend#21"], "dependents of an item")
    let legacyDelays = try! JSONDecoder().decode(Plan.self, from: Data(#"{"order":["a"]}"#.utf8))
    check(legacyDelays.delays.isEmpty && legacyDelays.tracked.isEmpty && legacyDelays.notes.isEmpty, "older plans load without delays and notes")

    check(LaneTimeline.zoom(1.0, by: 0.1) == 1.1 && LaneTimeline.zoom(0.3, by: -0.1) == 0.3 && LaneTimeline.zoom(1.47, by: 0.1) == 1.5 && LaneTimeline.zoom(0.83, by: -0.1) == 0.7, "zoom steps by 10 % within its range")

    // A link back to a later item of the same project reorders, rather than drawing an arrow backwards.
    let bm = AppModel(preview: .preview)
    let bid = { (ref: String) in bm.items.first { $0.shortReference == ref }!.id }
    bm.link(bid("backend#12"), waitsOn: bid("backend#33"))
    let backendLane = bm.timelineSchedule().lanes.first { $0.name == "backend" }!.blocks
    check(backendLane.prefix(3).compactMap(\.item?.shortReference) == ["backend#33", "backend#12", "backend#21"], "waiting item moved behind its blocker: \(backendLane.compactMap(\.item?.shortReference))")
    check(backendLane.first { $0.item?.shortReference == "backend#12" }?.slot == 1, "and starts after it")
    check(bm.items.filter { $0.projectName == "backend" }.prefix(2).map(\.shortReference) == ["backend#33", "backend#12"], "the work order in the list follows")
    // Older data where the waiting item comes first: the timeline puts it behind anyway.
    var stale = Snapshot.preview.items
    let sso = stale.firstIndex { $0.shortReference == "backend#12" }!
    stale[sso].wait = Wait(itemIDs: [stale.first { $0.shortReference == "backend#33" }!.id])
    let staleSchedule = TimelineSchedule.build(items: stale, external: [], positions: Dictionary(uniqueKeysWithValues: stale.enumerated().map { ($1.id, $0) }))
    check(staleSchedule.lanes.first { $0.name == "backend" }!.blocks.prefix(2).compactMap(\.item?.shortReference) == ["backend#33", "backend#12"], "timeline straightens stored order")

    // Notes: free text with GitLab-style tasks, kept locally per item.
    let note = ItemNote(text: "Zápis\n- [ ] první\n  * [x] druhý\n[ ] třetí\n- [ ]\n- bod\n[x]neúkol", updatedAt: .now)
    check(note.tasks.map(\.text) == ["první", "druhý", "třetí", ""] && note.tasks.map(\.isDone) == [false, true, false, false], "tasks parsed: \(note.tasks.map(\.text))")
    check(note.openTasks.map(\.line) == [1, 3, 4], "open tasks")
    check(note.toggling(line: 1).text == "Zápis\n- [x] první\n  * [x] druhý\n[ ] třetí\n- [ ]\n- bod\n[x]neúkol", "tick a task off")
    check(note.toggling(line: 2).tasks[1].isDone == false && note.toggling(line: 5) == note, "tick back on, other lines untouched")
    let nm = AppModel(preview: .preview)
    let nid = { (ref: String) in nm.items.first { $0.shortReference == ref }!.id }
    check(nm.items.first { $0.shortReference == "backend#33" }?.note?.openTasks.count == 2, "preview note on the issue with its MR")
    nm.setNote("Meeting\n- [ ] zavolat", forItemID: nid("infra#5"))
    check(nm.items.first { $0.shortReference == "infra#5" }?.note?.tasks.map(\.text) == ["zavolat"], "note saved on the item")
    nm.toggleTask(line: 1, forItemID: nid("infra#5"))
    check(nm.items.first { $0.shortReference == "infra#5" }?.note?.openTasks.isEmpty == true, "task ticked off")
    nm.setNote("  \n", forItemID: nid("infra#5"))
    check(nm.items.first { $0.shortReference == "infra#5" }?.note == nil, "an empty note is removed")
    let mergeRequestID = nm.items.first { $0.shortReference == "backend#33" }!.mergeRequests[0].id
    nm.setNote("z MR", forItemID: mergeRequestID)
    check(nm.items.first { $0.shortReference == "backend#33" }?.note?.text.hasSuffix("\n\nz MR") == true, "a note on a grouped MR joins its issue")
    nm.requestFocus(on: nid("docs#3"), editsNote: true)
    check(nm.takeNoteToEdit(nid("docs#3")) && !nm.takeNoteToEdit(nid("docs#3")), "widget click opens the note once")
    let notedPlan = try! JSONDecoder().decode(Plan.self, from: Data(#"{"order":[],"notes":{"a":{"text":"x","updatedAt":0}}}"#.utf8))
    check(notedPlan.notes["a"]?.text == "x", "notes load from the plan")

    // Grouping: an MR closing an issue, or named after it, joins the issue.
    let account = UUID()
    func gl(_ kind: ItemKind, _ iid: Int, _ gid: Int, branch: String? = nil) -> TodoItem {
        TodoItem(id: TodoItem.makeID(accountID: account, kind: kind, gitLabID: gid), accountID: account, accountName: "a", kind: kind, role: .assigned,
                 projectID: 7, iid: iid, title: "t", reference: "g/p\(kind == .issue ? "#" : "!")\(iid)", webURL: URL(string: "https://x")!, activityAt: .now, sourceBranch: branch)
    }
    let grouped = GitLabClient.group(
        issues: [gl(.issue, 1372, 500), gl(.issue, 10, 501)],
        mergeRequests: [gl(.mergeRequest, 1192, 900, branch: "feature-x"), gl(.mergeRequest, 77, 901, branch: "10-something"), gl(.mergeRequest, 78, 902, branch: "main-fix")],
        closedIssues: [TodoItem.makeID(accountID: account, kind: .mergeRequest, gitLabID: 900): [500]],
        accountID: account)
    check(grouped.count == 3 && grouped[0].mergeRequests.map(\.iid) == [1192], "MR grouped by closes_issues")
    check(grouped[1].mergeRequests.map(\.iid) == [77], "MR grouped by branch name")
    check(grouped[2].iid == 78 && grouped[2].kind == .mergeRequest, "unrelated MR stays on its own")
    let threads = try! JSONDecoder().decode([GLDiscussion].self, from: Data(#"[{"notes":[{"resolvable":false}]},{"notes":[{"resolvable":true,"resolved":false},{"resolvable":true,"resolved":true}]},{"notes":[{"resolvable":true,"resolved":true}]}]"#.utf8))
    check(threads.filter(\.isResolvable).count == 2 && threads.filter(\.isUnresolved).count == 1, "threads counted like GitLab")
    check(item("backend#33").reviewThreads.map { "\($0.resolved)/\($0.total)" } == "1/3", "x/y shown on the issue of the MR")
    check(item("frontend!52").reviewThreads.map { $0.resolved == $0.total } == true, "all resolved")
    check(item("frontend#7").reviewThreads == nil, "no threads without an MR")
    // Waiting on other people's issues.
    let url = WorkItemReference("https://gitlab.example.com/group/sub/project/-/issues/123")
    check(url?.host == "gitlab.example.com" && url?.path == "group/sub/project" && url?.kind == .issue && url?.iid == 123, "URL reference")
    check(WorkItemReference("https://g.example.com/a/b/-/merge_requests/45")?.kind == .mergeRequest, "MR URL")
    check(WorkItemReference(" group/project!45 ").map { "\($0.path ?? "-") \($0.kind) \($0.iid)" } == "group/project mergeRequest 45", "path!iid")
    check(WorkItemReference("#12")?.path == nil && WorkItemReference("#12")?.iid == 12, "#iid in the same project")
    check(WorkItemReference("hello") == nil && WorkItemReference("https://example.com/foo") == nil, "garbage rejected")
    let legacyPlan = try! JSONDecoder().decode(Plan.self, from: Data(#"{"order":["a","b"],"waits":{"b":{"itemIDs":["a"]}}}"#.utf8))
    check(legacyPlan.order == ["a", "b"] && legacyPlan.waits["b"]?.itemIDs == ["a"] && legacyPlan.released.isEmpty, "plan saved by an older version still loads")

    check(item("frontend#27").waitDescription == "platform#500" && item("frontend#27").isWaiting, "waits on a colleague's issue")
    check(item("frontend#7").releasedBy == "platform#480", "released item marked")
    let colleague = ExternalBlocker(accountID: UUID(), projectID: 901, kind: .issue, iid: 77, title: "x", reference: "team/api#77", webURL: URL(string: "https://x")!)
    model.addBlocker(colleague, for: item("infra#5"))
    model.toggleBlocker(item("backend#12"), for: item("infra#5"))
    model.applyExternalStates(openTitles: [:], finished: [colleague.id])
    check(item("infra#5").isWaiting && item("infra#5").releasedBy == nil, "still waits on its own item, not released yet")
    model.applyExternalStates(openTitles: [:], finished: [item("frontend#27").wait!.external[0].id])
    check(!item("frontend#27").isWaiting && item("frontend#27").releasedBy == "platform#500", "colleague's issue done: ready to start")
    model.acknowledgeRelease(of: id("frontend#27"))
    check(item("frontend#27").releasedBy == nil, "ready mark gone once looked at")
    let labels = try! JSONDecoder.gitLabTest.decode([GLLabel].self, from: Data(##"["plain", {"name":"bug","color":"#dc143c","text_color":"#FFFFFF"}]"##.utf8))
    check(labels.map(\.itemLabel) == [ItemLabel(name: "plain", color: "#6699cc", textColor: "#FFFFFF"), ItemLabel(name: "bug", color: "#dc143c", textColor: "#FFFFFF")], "labels with and without details")
    check(Color(hex: "#dc143c") != nil && Color(hex: "red") == nil, "label colors parsed")
    check(GitLabClient.issueIID(fromBranch: "1372-remove-timeline") == 1372 && GitLabClient.issueIID(fromBranch: "feature-1372") == nil, "branch prefix parsing")
}
print(failures == 0 ? "ALL PASSED" : "\(failures) FAILED")
exit(failures == 0 ? 0 : 1)

extension JSONDecoder {
    static var gitLabTest: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }
}
