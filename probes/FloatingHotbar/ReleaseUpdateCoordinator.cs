using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace TeamBar;

internal sealed class ReleaseUpdateCoordinator
{
    private static readonly Lazy<ReleaseUpdateCoordinator> shared = new(() => new());
    internal static ReleaseUpdateCoordinator Shared => shared.Value;
    private readonly ReleaseUpdateClient? client;
    private readonly GitHubReleaseNotices? gitHub;
    private readonly DispatcherTimer? timer;
    private readonly string statePath;
    private UpdatePreferences preferences = new();
    private QueuedUpdate? queued;
    internal bool IsConfigured => client is not null || gitHub is not null;
    internal bool CanInstallUpdates => client is not null;
    internal bool Busy { get; private set; }
    internal bool AutoCheck => preferences.AutoCheck;
    internal bool IsQueued => queued is not null;
    internal string Channel => client?.Configuration.Channel ?? (gitHub is not null ? "beta (GitHub Releases)" : "Unconfigured");
    internal string Status { get; private set; } = "This build has no update channel configured.";
    internal ReleaseUpdateManifest? Available { get; private set; }
    internal GitHubReleaseNotice? Notice { get; private set; }
    internal bool RequiresEverQuestClosed => Available is { } release && client is { } updater && release.RequiresEverQuestClosed(updater.Configuration);
    internal event Action? Changed;

    private ReleaseUpdateCoordinator()
    {
        statePath = Path.Combine(InstalledAppData.DataDirectory, "updates", "preferences.json");
        try
        {
            if (File.Exists(statePath))
            {
                if (new FileInfo(statePath).Length > 4096) throw new InvalidDataException("Update preferences are too large.");
                preferences = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllBytes(statePath)) ?? new();
            }
            if (!InstalledAppData.IsInstalledBuild) { Status = "Update installation is available in installed builds. This development copy does not check for or install updates."; return; }
            if (ReleaseUpdateConfiguration.ReadAssembly() is { } configuration)
            {
                client = new(configuration);
                Status = "Ready to check for signed updates.";
            }
            else if (ReleaseUpdateConfiguration.GitHubNoticesEnabled)
            {
                gitHub = new();
                Status = "Ready to check GitHub for beta releases.";
            }
            else return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            timer.Tick += async (_, _) => await Check(manual: false);
            timer.Start();
        }
        catch (Exception error) { Status = "Updates are unavailable: " + error.Message; }
    }

    internal void SetAutoCheck(bool enabled)
    {
        var updated = preferences with { AutoCheck = enabled };
        SavePreferences(updated); preferences = updated; Changed?.Invoke();
    }

    internal async Task Check(bool manual)
    {
        if (!IsConfigured || Busy || queued is not null) return;
        var now = DateTimeOffset.UtcNow;
        if (!manual && (!preferences.AutoCheck || preferences.LastAttemptUtc <= now && now - preferences.LastAttemptUtc < TimeSpan.FromDays(1))) return;
        Busy = true;
        Status = gitHub is null ? "Checking the trusted release feed…" : "Checking GitHub for beta releases…";
        Changed?.Invoke();
        try
        {
            // Persist attempts before networking to avoid repeated automatic retries after a failed check.
            var updated = preferences with { LastAttemptUtc = now };
            SavePreferences(updated); preferences = updated;
            if (client is not null)
            {
                var release = await client.Check();
                bool newer = ReleaseUpdateVersion.Parse(release.Version).CompareTo(ReleaseUpdateVersion.Parse(client.Configuration.CurrentVersion)) > 0;
                Available = newer ? release : null;
                Status = newer ? $"Team Bar {release.Version} is available." : "You have the latest version offered by this channel.";
            }
            else if (gitHub is not null)
            {
                Notice = await gitHub.FindNewer(ReleaseUpdateConfiguration.InstalledVersion);
                Status = Notice is null ? "No newer beta installer has been published on GitHub."
                    : $"Team Bar {Notice.Version} is available. Open the release page to download the installer.";
            }
        }
        catch (Exception error)
        {
            Available = null;
            Notice = null;
            Status = (gitHub is null ? "Could not verify an update. " : "Could not check GitHub Releases. ") + error.Message;
        }
        finally { Busy = false; Changed?.Invoke(); }
    }

    internal async Task QueueForExit()
    {
        if (client is null || Busy || queued is not null || Available is not { } release) return;
        Busy = true; Status = "Downloading and verifying the installer…"; Changed?.Invoke();
        try
        {
            string path = await client.Stage(release, Path.GetDirectoryName(statePath)!);
            queued = new(path, release);
            Status = release.RequiresEverQuestClosed(client.Configuration)
                ? "Ready to install when you exit Team Bar. Close every EverQuest client first; this release changes native components."
                : "Ready to install when you exit Team Bar. Your EverQuest clients can stay open.";
        }
        catch (Exception error) { Status = "The update was not queued. " + error.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }

    internal void CancelQueued()
    {
        if (Busy) return;
        queued = null; Status = "The queued update was canceled. You can install it later."; Changed?.Invoke();
    }

    // Called only by the existing orderly Exit path after all settings have flushed and input has stopped.
    internal async Task FinishExit(Func<Task> waitForNativeShutdown)
    {
        try { await ReleaseUpdateHandoff.Finish(waitForNativeShutdown, ApplyQueued); }
        catch (Exception error)
        {
            if (queued is not null) { Status = "The update was not installed. " + error.Message; Changed?.Invoke(); }
            throw;
        }
    }

    private void ApplyQueued()
    {
        if (queued is not { } update || client is null) return;
        ReleaseUpdateHandoff.Apply(() => EnsureGameClosedIfRequired(update.Manifest, client.Configuration),
            () => client.OpenVerifiedInstaller(update.Path, update.Manifest),
            () => InstalledAppData.CreatePreUpdateBackup(update.Manifest.Version), () => StartInstaller(update.Path));
        queued = null;
    }

    private static void StartInstaller(string path)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", $"/WAITFORPID={Environment.ProcessId}" }) start.ArgumentList.Add(argument);
        using var installer = Process.Start(start) ?? throw new IOException("Windows could not start the verified installer. The update has not been installed.");
    }

    private static void EnsureGameClosedIfRequired(ReleaseUpdateManifest release, ReleaseUpdateConfiguration configuration)
    {
        if (!release.RequiresEverQuestClosed(configuration)) return;
        var processes = Process.GetProcessesByName("eqgame");
        try
        {
            if (processes.Length > 0) throw new InvalidOperationException("This update changes native components. Close every EverQuest client, then choose Exit Team Bar again. Team Bar will not close your game clients.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void SavePreferences(UpdatePreferences value)
    {
        string directory = Path.GetDirectoryName(statePath)!; Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value)); File.Move(temporary, statePath, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record UpdatePreferences(bool AutoCheck = true, DateTimeOffset LastAttemptUtc = default);
    private sealed record QueuedUpdate(string Path, ReleaseUpdateManifest Manifest);
}

internal static class ReleaseUpdateHandoff
{
    internal static async Task Finish(Func<Task> waitForInputShutdown, Action apply)
    {
        await waitForInputShutdown();
        apply();
    }

    internal static void Apply(Action ensureAllowed, Func<IDisposable> openVerifiedInstaller, Action backup, Action launch)
    {
        ensureAllowed();
        using var verified = openVerifiedInstaller();
        backup();
        ensureAllowed();
        launch();
    }
}
