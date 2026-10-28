using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using GitLabTodo.Core;
using GitLabTodo.Platform;

namespace GitLabTodo.Ui;

/// Looks for newer releases on GitHub and installs them, where the app runs as an AppImage. Other installs only know
/// their version; Nix or the package manager updates them.
public sealed class Updater
{
    private readonly AppState state;
    private readonly Action quit;
    private readonly DispatcherTimer timer;

    public Updater(AppState state, Action quit)
    {
        this.state = state;
        this.quit = quit;
        // It runs for days in the tray, so not only at launch.
        timer = new DispatcherTimer(TimeSpan.FromHours(12), DispatcherPriority.Background, (_, _) =>
        {
            if (state.CheckUpdates)
            {
                _ = Check(quiet: true);
            }
        });
    }

    public event Action? Changed;

    public static bool CanUpdate => AppImage.Path is not null;

    /// `0.2.0` without the commit the build adds.
    public static string Version =>
        (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// A newer release, once one was found.
    public AvailableUpdate? Available { get; private set; }
    /// 0 to 1 while it downloads, null otherwise.
    public double? Progress { get; private set; }
    public bool IsChecking { get; private set; }
    public bool HasChecked { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (!CanUpdate)
        {
            return;
        }
        if (state.CheckUpdates)
        {
            _ = Check(quiet: true);
        }
        timer.Start();
    }

    /// `quiet`, at launch and from the timer: a failed check says nothing, the network may just not be up yet.
    public async Task Check(bool quiet = false)
    {
        if (!CanUpdate || IsChecking || Progress is not null)
        {
            return;
        }
        IsChecking = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            Available = Updates.Platform is { } platform ? await Updates.Check(Version, platform) : null;
            HasChecked = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (!quiet)
            {
                Error = $"Aktualizace nejde zjistit: {exception.Message}";
            }
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke();
        }
    }

    /// Downloads, verifies and swaps the AppImage, then starts the new one and quits.
    public async Task InstallAndRestart()
    {
        if (Available is null || Progress is not null)
        {
            return;
        }
        Progress = 0;
        Error = null;
        Changed?.Invoke();
        var download = AppImage.DownloadPath();
        try
        {
            // The release may have been replaced since the check, the app runs for days.
            if (Updates.Platform is not { } platform || await Updates.Check(Version, platform) is not { } fresh)
            {
                Available = null;
                Progress = null;
                Error = "Ta verze už není k dispozici, máš nejnovější.";
                Changed?.Invoke();
                return;
            }
            await Updates.Download(fresh, download, new Progress<double>(value =>
            {
                Progress = value;
                Changed?.Invoke();
            }));
            AppImage.Replace(download);
            AppImage.Relaunch();
            quit();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Progress = null;
            Error = $"Aktualizace se nenainstalovala: {exception.Message}";
            try
            {
                File.Delete(download);
            }
            catch (IOException)
            {
            }
            Changed?.Invoke();
        }
    }
}
