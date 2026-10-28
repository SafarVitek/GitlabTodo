using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabTodo.Core;

/// Account IDs as `6F1C2B9E-…`, the way Swift writes a UUID.
public sealed class UppercaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Guid.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.IdString());
}

/// Dates as `2026-10-27T12:00:00Z`, which the macOS app reads too.
public sealed class WholeSecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

public sealed class GitLabAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string TargetBranch { get; set; } = "master";
    public string DefaultReviewer { get; set; } = "";

    public Uri? ApiUrl => ApiUrlFrom(BaseUrl);

    public static string NormalizedBaseUrl(string input)
    {
        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }
        value = value.TrimEnd('/');
        if (value.EndsWith("/api/v4", StringComparison.Ordinal))
        {
            value = value[..^"/api/v4".Length];
        }
        return value;
    }

    public static Uri? ApiUrlFrom(string input) =>
        Uri.TryCreate(NormalizedBaseUrl(input) + "/api/v4", UriKind.Absolute, out var url)
        && url.Scheme is "https" or "http" && url.Host.Length > 0 ? url : null;

    /// `@jnovak ` → `jnovak`
    public static string Username(string input) => input.Trim().TrimStart('@');
}

public sealed class Settings
{
    public List<GitLabAccount> Accounts { get; set; } = [];
    public double RefreshMinutes { get; set; } = 5;
    public double TimelineZoom { get; set; } = 1;
    /// Closing the window keeps the app running in the system tray.
    public bool KeepInTray { get; set; } = true;
    /// `system`, `light` or `dark`.
    public string Theme { get; set; } = "system";
    /// Looks for a newer release at launch, where the app can update itself.
    public bool CheckUpdates { get; set; } = true;
    /// The desktop's own window frames instead of the app's title bars.
    public bool SystemFrame { get; set; }
}

/// Files under the XDG folders: settings and tokens in `~/.config/gitlab-todo`, the plan and the last refresh in
/// `~/.local/share/gitlab-todo`.
public static class Storage
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new UppercaseGuidConverter() },
    };

    public static string ConfigDirectory { get; set; } = Folder("XDG_CONFIG_HOME", ".config");
    public static string DataDirectory { get; set; } = Folder("XDG_DATA_HOME", ".local/share");

    public static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");
    public static string TokensPath => Path.Combine(ConfigDirectory, "tokens.json");
    public static string PlanPath => Path.Combine(DataDirectory, "plan.json");
    public static string SnapshotPath => Path.Combine(DataDirectory, "snapshot.json");
    public static string SyncPath => Path.Combine(DataDirectory, "sync.json");

    private static string Folder(string variable, string fallback)
    {
        var root = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
        }
        return Path.Combine(root, "gitlab-todo");
    }

    public static T? Load<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return default;
        }
    }

    /// Written to a temporary file first, so a crash never leaves half a plan behind.
    public static void Save<T>(string path, T value, bool isPrivate = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        if (isPrivate && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
