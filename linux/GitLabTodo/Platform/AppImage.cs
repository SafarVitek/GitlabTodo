using System.Diagnostics;
using Avalonia.Platform;

namespace GitLabTodo.Platform;

/// Running from an AppImage: the one kind of Linux install that can replace itself with a newer release. Nix and
/// .deb installs are updated the way they were installed.
public static class AppImage
{
    /// The AppImage file itself, not the folder it is mounted at while it runs.
    public static string? Path => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// Replaces the AppImage with the downloaded and verified file. The running copy keeps what it has open; the
    /// rename is atomic, so a crash never leaves half an AppImage.
    public static void Replace(string downloaded)
    {
        var target = Path ?? throw new InvalidOperationException("Neběží jako AppImage");
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        File.SetUnixFileMode(downloaded, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(downloaded, target, overwrite: true);
    }

    /// A file next to the AppImage to download into, so that the final rename stays on one file system.
    public static string DownloadPath() =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path!)!, $".gitlab-todo-update-{Environment.ProcessId}");

    /// Starts the new AppImage a moment after this process has ended, so it does not find this one running.
    public static void Relaunch()
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", "sleep 1; exec \"$0\"", Path! })
        {
            start.ArgumentList.Add(argument);
        }
        Process.Start(start)?.Dispose();
    }

    /// An entry in the desktop's app menu pointing at this AppImage, written again when the file has moved. Its own
    /// name, so that it does not hide the entry of a .deb install.
    public static void AddToMenu()
    {
        if (Path is not { } path)
        {
            return;
        }
        try
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } custom
                ? custom
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share");
            var icon = System.IO.Path.Combine(data, "icons/hicolor/512x512/apps/gitlab-todo.png");
            if (!File.Exists(icon))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(icon)!);
                using var source = AssetLoader.Open(new Uri("avares://gitlab-todo/Assets/icon.png"));
                using var target = File.Create(icon);
                source.CopyTo(target);
            }
            var entry = System.IO.Path.Combine(data, "applications/gitlab-todo-appimage.desktop");
            var content = $"""
                [Desktop Entry]
                Type=Application
                Name=GitLab Todo
                Comment=Issues a merge requesty z GitLabu v pořadí, ve kterém na nich chceš pracovat
                Exec="{path}"
                Icon=gitlab-todo
                Terminal=false
                Categories=Development;ProjectManagement;
                Keywords=gitlab;todo;merge request;

                """;
            if (!File.Exists(entry) || File.ReadAllText(entry) != content)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(entry)!);
                File.WriteAllText(entry, content);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Položka v menu aplikací nejde zapsat: {exception.Message}");
        }
    }
}
