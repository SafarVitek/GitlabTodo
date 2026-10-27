namespace GitLabTodo.Platform;

/// Starting after login through an XDG autostart entry, hidden in the tray.
public static class Autostart
{
    private static string FilePath
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config))
            {
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(config, "autostart", "gitlab-todo.desktop");
        }
    }

    public static bool IsEnabled => File.Exists(FilePath);

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $"""
            [Desktop Entry]
            Type=Application
            Name=GitLab Todo
            Comment=Issues a merge requesty z GitLabu v pořadí, ve kterém na nich chceš pracovat
            Exec={Command} --hidden
            Icon=gitlab-todo
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    /// Installed with Nix, the store path changes with every update, the command on the PATH stays.
    private static string Command
    {
        get
        {
            var path = Environment.ProcessPath ?? "gitlab-todo";
            return path.StartsWith("/nix/store/", StringComparison.Ordinal) ? "gitlab-todo" : $"\"{path}\"";
        }
    }
}
