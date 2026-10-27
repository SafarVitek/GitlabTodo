namespace GitLabTodo.Core;

/// One block in the timeline: an item of the user's, or someone else's issue or merge request shown for context.
public sealed class TimelineBlock
{
    public TodoItem? Item { get; init; }
    public ExternalBlocker? External { get; init; }
    public required string LaneKey { get; init; }
    /// Blocks this one waits on.
    public required List<string> Blockers { get; init; }
    /// Empty steps the user put before it.
    public int Delay { get; init; }
    public int Slot { get; set; }
    /// Steps before this are empty because of the delay, the ones from here to `Slot` because of a wait.
    public int DelayedUntil { get; set; }

    public string Id => Item?.Id ?? External!.Id;
    public string Title => Item?.Title ?? External?.Title ?? "";
    public string Reference => Item?.References ?? Names.LocalReference(External?.Reference ?? "");
    public string WebUrl => Item?.WebUrl ?? External!.WebUrl;
}

public sealed class TimelineLane
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required int Color { get; init; }
    public required Guid AccountId { get; init; }
    public required int ProjectId { get; init; }
    public List<TimelineBlock> Blocks { get; set; } = [];
}

/// Lanes per project with their items in work order. Every block is one step long and starts only after the block
/// before it in its lane and everything it waits on, which leaves gaps where an item waits on another project.
public sealed class TimelineSchedule
{
    public required List<TimelineLane> Lanes { get; init; }
    public required int StepCount { get; init; }

    public static TimelineSchedule Build(
        IReadOnlyList<TodoItem> items, IReadOnlyList<ExternalBlocker> external, IReadOnlyDictionary<string, int> positions,
        IReadOnlyDictionary<string, int>? delays = null)
    {
        delays ??= new Dictionary<string, int>();
        var lanes = new Dictionary<string, TimelineLane>();
        var keys = new Dictionary<string, double>();
        var unplacedStart = (positions.Count == 0 ? -1 : positions.Values.Max()) + 1;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var laneKey = item.ProjectKey;
            if (!lanes.TryGetValue(laneKey, out var lane))
            {
                lane = new TimelineLane { Key = laneKey, Name = item.ProjectName, Color = item.ProjectColor, AccountId = item.AccountId, ProjectId = item.ProjectId };
                lanes[laneKey] = lane;
            }
            var blockers = (item.Wait?.ItemIds ?? []).Concat((item.Wait?.External ?? []).Select(b => b.Id)).ToList();
            lane.Blocks.Add(new TimelineBlock { Item = item, LaneKey = laneKey, Blockers = blockers, Delay = delays.GetValueOrDefault(item.Id) });
            // Items come in work order already, new ones after the placed.
            keys[item.Id] = positions.TryGetValue(item.Id, out var position) ? position : unplacedStart + index;
        }
        foreach (var blocker in external)
        {
            var laneKey = $"{blocker.AccountId.IdString()}:{blocker.ProjectId}";
            if (!lanes.TryGetValue(laneKey, out var lane))
            {
                lane = new TimelineLane
                {
                    Key = laneKey,
                    Name = Names.ProjectName(blocker.Reference, blocker.Reference),
                    Color = Palette.PreferredSlot(laneKey),
                    AccountId = blocker.AccountId,
                    ProjectId = blocker.ProjectId,
                };
                lanes[laneKey] = lane;
            }
            lane.Blocks.Add(new TimelineBlock { External = blocker, LaneKey = laneKey, Blockers = [], Delay = delays.GetValueOrDefault(blocker.Id) });
            // Someone else's work not placed by the user is probably in progress, it goes first.
            keys[blocker.Id] = positions.TryGetValue(blocker.Id, out var position) ? position : -1;
        }

        foreach (var lane in lanes.Values)
        {
            lane.Blocks = lane.Blocks
                .OrderBy(b => keys.GetValueOrDefault(b.Id))
                .ThenBy(b => b.Title, StringComparer.Ordinal)
                .ToList();
            KeepBehindBlockers(lane.Blocks);
        }
        // The project with the most urgent own item first, projects of other people only after the user's.
        double LaneRank(TimelineLane lane) =>
            lane.Blocks.Where(b => b.Item is not null).Select(b => keys.GetValueOrDefault(b.Id)).DefaultIfEmpty(double.PositiveInfinity).Min();
        var sorted = lanes.Values.OrderBy(LaneRank).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();

        var (steps, delayedUntil) = Schedule(sorted);
        foreach (var block in sorted.SelectMany(l => l.Blocks))
        {
            block.Slot = steps.GetValueOrDefault(block.Id);
            block.DelayedUntil = delayedUntil.GetValueOrDefault(block.Id);
        }
        return new TimelineSchedule { Lanes = sorted, StepCount = (steps.Count == 0 ? -1 : steps.Values.Max()) + 1 };
    }

    /// A block waiting on a later block of its own lane moves right behind it, whatever the stored order says.
    private static void KeepBehindBlockers(List<TimelineBlock> blocks)
    {
        bool WaitsOn(int index, int other) => other > index && blocks[index].Blockers.Contains(blocks[other].Id);
        for (var round = 0; round < blocks.Count * blocks.Count; round++)
        {
            var index = Enumerable.Range(0, blocks.Count).FirstOrDefault(i => Enumerable.Range(0, blocks.Count).Any(o => WaitsOn(i, o)), -1);
            if (index < 0)
            {
                return;
            }
            var blocker = Enumerable.Range(0, blocks.Count).Last(o => WaitsOn(index, o));
            var block = blocks[index];
            blocks.RemoveAt(index);
            // The blocker moved one up with the removal, so this lands right after it.
            blocks.Insert(blocker, block);
        }
    }

    /// Assigns steps lane by lane as far as blockers allow, repeating until nothing moves. A dependency cycle would
    /// stall that, then the first stuck block is placed ignoring the blockers that never got a step.
    private static (Dictionary<string, int> Steps, Dictionary<string, int> DelayedUntil) Schedule(List<TimelineLane> lanes)
    {
        var shown = lanes.SelectMany(l => l.Blocks.Select(b => b.Id)).ToHashSet();
        var steps = new Dictionary<string, int>();
        var delayedUntil = new Dictionary<string, int>();

        int Start(TimelineBlock block, int previous)
        {
            var blockers = block.Blockers.Where(shown.Contains).Where(steps.ContainsKey).Select(id => steps[id]);
            var delayed = previous + 1 + block.Delay;
            delayedUntil[block.Id] = delayed;
            return Math.Max(delayed, blockers.DefaultIfEmpty(-1).Max() + 1);
        }

        while (steps.Count < shown.Count)
        {
            var progressed = true;
            while (progressed)
            {
                progressed = false;
                foreach (var lane in lanes)
                {
                    var previous = -1;
                    foreach (var block in lane.Blocks)
                    {
                        if (steps.TryGetValue(block.Id, out var step))
                        {
                            previous = step;
                            continue;
                        }
                        if (!block.Blockers.Where(shown.Contains).All(steps.ContainsKey))
                        {
                            break;
                        }
                        steps[block.Id] = Start(block, previous);
                        previous = steps[block.Id];
                        progressed = true;
                    }
                }
            }
            // Break a cycle at the first block that is still waiting.
            foreach (var lane in lanes)
            {
                var previous = -1;
                var placed = false;
                foreach (var block in lane.Blocks)
                {
                    if (steps.TryGetValue(block.Id, out var step))
                    {
                        previous = step;
                        continue;
                    }
                    steps[block.Id] = Start(block, previous);
                    placed = true;
                    break;
                }
                if (placed)
                {
                    break;
                }
            }
        }
        return (steps, delayedUntil);
    }
}
