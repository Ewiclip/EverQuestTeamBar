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
    private readonly string currentVersion = ReleaseUpdateConfiguration.InstalledVersion;
    private readonly Func<string, Task> launchInstaller = LaunchInteractiveInstaller;
    private UpdatePreferences preferences = new();
    private QueuedUpdate? queued;
    private CancellationTokenSource? downloading;
    private string? promptedVersion;
    internal bool IsConfigured => client is not null || gitHub is not null;
    internal bool Busy { get; private set; }
    internal bool AutoCheck => preferences.AutoCheck;
    internal bool IsQueued => queued is not null;
    internal string Channel => client?.Configuration.Channel ?? (gitHub is not null ? "beta (GitHub Releases)" : "Unconfigured");
    internal string Status { get; private set; } = "This build has no update channel configured.";
    internal ReleaseUpdateManifest? Available { get; private set; }
    internal GitHubReleaseNotice? Notice { get; private set; }
    internal string? AvailableVersion => Available?.Version ?? Notice?.Version;
    internal string? SkippedVersion => preferences.SkippedVersion;
    internal bool RequiresEverQuestClosed => Available is { } release && client is { } updater && release.RequiresEverQuestClosed(updater.Configuration);
    internal event Action? Changed;
    internal event Action<string>? PromptRequested;

    private ReleaseUpdateCoordinator()
    {
        statePath = Path.Combine(InstalledAppData.DataDirectory, "updates", "preferences.json");
        try
        {
            if (!InstalledAppData.IsInstalledBuild) { Status = "Update installation is available in installed builds. This development copy does not check for or install updates."; return; }
            preferences = ReadPreferences();
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

    internal ReleaseUpdateCoordinator(string statePath, GitHubReleaseNotices gitHub, string currentVersion, Func<string, Task> launchInstaller)
    {
        this.statePath = statePath;
        this.gitHub = gitHub;
        this.currentVersion = currentVersion;
        this.launchInstaller = launchInstaller;
        preferences = ReadPreferences();
    }

    private UpdatePreferences ReadPreferences()
    {
        if (!File.Exists(statePath)) return new();
        if (new FileInfo(statePath).Length > 4096) throw new InvalidDataException("Update preferences are too large.");
        var settings = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllBytes(statePath)) ?? new();
        if (settings.SkippedVersion is { } version)
        {
            try { _ = ReleaseUpdateVersion.Parse(version); }
            catch (InvalidDataException) { return settings with { SkippedVersion = null }; }
        }
        return settings;
    }

    internal void SetAutoCheck(bool enabled)
    {
        var updated = preferences with { AutoCheck = enabled };
        SavePreferences(updated); preferences = updated; Changed?.Invoke();
    }

    internal async Task Check(bool manual, bool onOpen = false)
    {
        if (!IsConfigured || Busy || queued is not null) return;
        var now = DateTimeOffset.UtcNow;
        if (!manual && (!preferences.AutoCheck || !onOpen && preferences.LastAttemptUtc <= now && now - preferences.LastAttemptUtc < TimeSpan.FromDays(1))) return;
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
                Notice = await gitHub.FindNewer(currentVersion);
                Status = Notice is null ? "No newer beta installer has been published on GitHub."
                    : $"Team Bar {Notice.Version} is available.";
            }
            if (AvailableVersion is { } offered && IsVersionSkipped(offered))
                Status = $"Reminders are off through Team Bar {SkippedVersion}. You can still install {offered} here.";
        }
        catch (Exception error)
        {
            Available = null;
            Notice = null;
            Status = (gitHub is null ? "Could not verify an update. " : "Could not check GitHub Releases. ") + error.Message;
        }
        finally { Busy = false; Changed?.Invoke(); }
        if (!manual && AutoCheck && AvailableVersion is { } version && !IsVersionSkipped(version) && version != promptedVersion && PromptRequested is { } prompt)
        {
            promptedVersion = version;
            prompt(version);
        }
    }

    internal bool IsVersionSkipped(string version) => SkippedVersion is { } skipped &&
        ReleaseUpdateVersion.Parse(version).CompareTo(ReleaseUpdateVersion.Parse(skipped)) <= 0;

    internal void SkipVersion(string version)
    {
        if (Busy || queued is not null || AvailableVersion != version || IsVersionSkipped(version)) return;
        var updated = preferences with { SkippedVersion = version };
        SavePreferences(updated);
        preferences = updated;
        Status = $"You skipped Team Bar {version}. You can still install it here.";
        Changed?.Invoke();
    }

    internal async Task<bool> InstallNow(string version)
    {
        if (Busy || queued is not null || AvailableVersion != version) return false;
        using var cancel = new CancellationTokenSource();
        downloading = cancel;
        Busy = true;
        Status = $"Downloading and verifying Team Bar {version}…";
        Changed?.Invoke();
        bool launched = false;
        try
        {
            string cache = Path.GetDirectoryName(statePath)!;
            string file;
            FileStream verified;
            if (gitHub is not null && Notice is { } notice)
            {
                file = await gitHub.Stage(notice, cache, cancel.Token);
                verified = await Task.Run(() => gitHub.OpenVerifiedInstaller(file, notice), cancel.Token);
            }
            else if (client is not null && Available is { } manifest)
            {
                EnsureGameClosedIfRequired(manifest, client.Configuration);
                file = await client.Stage(manifest, cache, cancel.Token);
                verified = await Task.Run(() => client.OpenVerifiedInstaller(file, manifest), cancel.Token);
            }
            else return false;
            using (verified)
            {
                cancel.Token.ThrowIfCancellationRequested();
                // Interactive Setup owns its preflight, backup, and graceful close of this running instance.
                Task setupExit = launchInstaller(file);
                launched = true;
                Status = $"Setup is open for Team Bar {version}.";
                Changed?.Invoke();
                _ = ObserveSetupExit(setupExit, version);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Status = "Update download canceled."; }
        catch (Exception error) { Status = "Could not start the update. " + error.Message; }
        finally
        {
            downloading = null;
            if (!launched) { Busy = false; Changed?.Invoke(); }
        }
        return false;
    }

    internal void CancelDownload() => downloading?.Cancel();

    private async Task ObserveSetupExit(Task setupExit, string version)
    {
        try
        {
            await setupExit;
            Status = $"Setup for Team Bar {version} has closed.";
        }
        catch (Exception error) { Status = "Could not track the open installer. " + error.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }

    private static Task LaunchInteractiveInstaller(string path)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        var process = Process.Start(start) ?? throw new IOException("Windows did not start Setup.");
        return WaitForExit(process);

        static async Task WaitForExit(Process process)
        {
            using (process) await process.WaitForExitAsync();
        }
    }

    internal async Task QueueForExit()
    {
        if (client is null || Busy || queued is not null || Available is not { } release) return;
        using var cancel = new CancellationTokenSource();
        downloading = cancel;
        Busy = true; Status = "Downloading and verifying the installer…"; Changed?.Invoke();
        try
        {
            string path = await client.Stage(release, Path.GetDirectoryName(statePath)!, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            queued = new(path, release);
            Status = release.RequiresEverQuestClosed(client.Configuration)
                ? "Ready to install when you exit Team Bar. Close every EverQuest client first; this release changes native components."
                : "Ready to install when you exit Team Bar. Your EverQuest clients can stay open.";
        }
        catch (Exception error) { Status = "The update was not queued. " + error.Message; }
        finally { downloading = null; Busy = false; Changed?.Invoke(); }
    }

    internal void CancelQueued()
    {
        if (Busy) return;
        queued = null; Status = "The queued update was canceled. You can install it later."; Changed?.Invoke();
    }

    // Called only by the existing orderly Exit path after all settings have flushed and input has stopped.
    internal async Task FinishExit(Func<Task> waitForNativeShutdown, Func<bool> installerRequestedExit)
    {
        try { await ReleaseUpdateHandoff.Finish(waitForNativeShutdown, () => ApplyQueued(installerRequestedExit)); }
        catch (Exception error)
        {
            if (queued is not null) { Status = "The update was not installed. " + error.Message; Changed?.Invoke(); }
            throw;
        }
    }

    private void ApplyQueued(Func<bool> installerRequestedExit)
    {
        if (queued is not { } update || client is null) return;
        ReleaseUpdateHandoff.Apply(() => EnsureGameClosedIfRequired(update.Manifest, client.Configuration),
            () => client.OpenVerifiedInstaller(update.Path, update.Manifest),
            () => InstalledAppData.CreatePreUpdateBackup(update.Manifest.Version), () => StartInstaller(update.Path), installerRequestedExit);
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
            if (processes.Length > 0) throw new InvalidOperationException("This update changes native components. Close every EverQuest client before starting the update.");
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
    private sealed record UpdatePreferences(bool AutoCheck = true, DateTimeOffset LastAttemptUtc = default, string? SkippedVersion = null);
    private sealed record QueuedUpdate(string Path, ReleaseUpdateManifest Manifest);
}

internal static class ReleaseUpdateHandoff
{
    internal static async Task Finish(Func<Task> waitForInputShutdown, Action apply)
    {
        await waitForInputShutdown();
        apply();
    }

    internal static void Apply(Action ensureAllowed, Func<IDisposable> openVerifiedInstaller, Action backup, Action launch,
        Func<bool>? installerRequestedExit = null)
    {
        if (installerRequestedExit?.Invoke() == true) return;
        ensureAllowed();
        using var verified = openVerifiedInstaller();
        backup();
        if (installerRequestedExit?.Invoke() == true) return;
        ensureAllowed();
        if (installerRequestedExit?.Invoke() != true) launch();
    }
}
