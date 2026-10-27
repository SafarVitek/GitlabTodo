namespace GitLabTodo.Core;

/// Visual language shared with the macOS app, loosely after Todoist: a circle per item, colored projects.
public static class Palette
{
    /// Title color of the lists.
    public const string Headline = "#DB4D3D";
    public const string Waiting = "#FF9500";
    public const string Issue = "#34C759";
    public const string MergeRequest = "#AF52DE";
    public const string Ready = "#34C759";
    public const string Threads = "#FF3B30";
    /// Open tasks in the user's notes.
    public const string Note = "#007AFF";

    /// Green and purple belong to issues and merge requests, orange to waiting, red to review threads.
    /// Blue, pink, yellow, teal, brown and indigo, in the macOS app's order so a project gets the same color.
    public static readonly string[] ProjectColors = ["#007AFF", "#FF2D55", "#FFCC00", "#30B0C7", "#A2845E", "#5856D6"];

    public static string Project(int index) => ProjectColors[index % ProjectColors.Length];

    public static string Color(this ItemKind kind) => kind == ItemKind.Issue ? Issue : MergeRequest;

    /// Stable across launches, unlike `GetHashCode`, so a project keeps its color. FNV-1a like the macOS app.
    public static int PreferredSlot(string projectKey)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var value in System.Text.Encoding.UTF8.GetBytes(projectKey))
        {
            hash = unchecked((hash ^ value) * 0x100000001b3UL);
        }
        return (int)(hash % (ulong)ProjectColors.Length);
    }

    /// Gives each project its own color. A project takes the slot its key hashes to unless another one has it already.
    public static List<TodoItem> WithProjectColors(this IEnumerable<TodoItem> source)
    {
        var items = source.ToList();
        var keys = items.SelectMany(i => i.MergeRequests.Select(m => m.ProjectKey).Prepend(i.ProjectKey))
            .Distinct().Order(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        var colors = new Dictionary<string, int>();
        foreach (var key in keys)
        {
            var slot = PreferredSlot(key);
            while (taken.Contains(slot) && taken.Count < ProjectColors.Length)
            {
                slot = (slot + 1) % ProjectColors.Length;
            }
            taken.Add(slot);
            colors[key] = slot;
        }
        return items.Select(item => item with
        {
            ProjectColor = colors.GetValueOrDefault(item.ProjectKey),
            MergeRequests = item.MergeRequests.Select(m => m with { ProjectColor = colors.GetValueOrDefault(m.ProjectKey) }).ToList(),
        }).ToList();
    }
}

/// Zoom of the timeline, in steps of 10 %.
public static class Zoom
{
    public const double Min = 0.3;
    public const double Max = 1.5;

    public static double Step(double zoom, double step) => Math.Clamp(Math.Round((zoom + step) * 10, MidpointRounding.AwayFromZero) / 10, Min, Max);
}
