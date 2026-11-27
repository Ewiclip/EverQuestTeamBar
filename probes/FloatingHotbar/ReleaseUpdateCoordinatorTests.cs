using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamBar;

internal static class ReleaseUpdateCoordinatorTests
{
    internal static async Task Run(string directory, Action<bool, string> check)
    {
        const string installed = "1.0.0-beta.5", first = "1.0.0-beta.6", next = "1.0.0-beta.7";
        string data = Path.Combine(directory, "coordinator");
        Directory.CreateDirectory(data);
        string preferences = Path.Combine(data, "preferences.json");
        File.WriteAllText(preferences, JsonSerializer.Serialize(new { AutoCheck = true, LastAttemptUtc = DateTimeOffset.UtcNow }));
        var feed = new Feed { Version = first };
        using var http = new HttpClient(feed);
        var launches = new List<string>();
        var prompts = new List<string>();
        var setupExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReleaseUpdateCoordinator Open()
        {
            var coordinator = new ReleaseUpdateCoordinator(preferences, new GitHubReleaseNotices(http), installed, path =>
            {
                launches.Add(path);
                return setupExited.Task;
            });
            coordinator.PromptRequested += prompts.Add;
            return coordinator;
        }

        var session = Open();
        await session.Check(manual: false);
        check(feed.Lookups == 0, "A recent persisted beta.5 check still throttles timer checks.");
        await session.Check(manual: false, onOpen: true);
        check(feed.Lookups == 1 && session.AvailableVersion == first && prompts.SequenceEqual([first]) && feed.Downloads == 0,
            "Opening the app checks immediately and offers the newest release before downloading anything.");
        await session.Check(manual: false, onOpen: true);
        check(feed.Lookups == 2 && prompts.SequenceEqual([first]),
            "Reopening in the same session checks GitHub again without repeating the dismissed prompt.");
        session.SkipVersion(first);
        session = Open();
        await session.Check(manual: false, onOpen: true);
        check(feed.Lookups == 3 && session.SkippedVersion == first && prompts.SequenceEqual([first]),
            "The version skipped in the previous session stays quiet after restart.");
        await session.Check(manual: true);
        check(feed.Lookups == 4 && session.AvailableVersion == first && prompts.SequenceEqual([first]),
            "A manual check offers the skipped release in Settings without raising an automatic prompt.");
        feed.Version = next;
        await session.Check(manual: false, onOpen: true);
        check(session.AvailableVersion == next && prompts.SequenceEqual([first, next]),
            "A newer release prompts even with a previously skipped version on disk.");
        await session.InstallNow(first);
        check(launches.Count == 0 && feed.Downloads == 0,
            "Accepting a stale prompt cannot download or launch a different version.");

        var cancelReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.OnDownload = () => { cancelReady.TrySetResult(); return blocked.Task; };
        var canceled = session.InstallNow(next);
        try
        {
            await cancelReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.CancelDownload();
            check(!await canceled.WaitAsync(TimeSpan.FromSeconds(5)) && !session.Busy && launches.Count == 0 && feed.Downloads == 1,
                "Canceling an active transfer clears Busy and prevents the installer from launching.");
        }
        finally { blocked.TrySetResult(); feed.OnDownload = null; }
        feed.DamageDownload = true;
        await session.InstallNow(next);
        check(launches.Count == 0 && !session.Busy && feed.Downloads == 2,
            "Failed verification leaves the app open and permits another attempt.");
        feed.DamageDownload = false;
        var downloadReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.OnDownload = () => { downloadReady.TrySetResult(); return allowDownload.Task; };
        Task<bool> firstAccept = session.InstallNow(next);
        Task<bool> secondAccept;
        try
        {
            await downloadReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondAccept = session.InstallNow(next);
            check(session.Busy && launches.Count == 0, "Setup waits for the accepted download to finish.");
        }
        finally { allowDownload.TrySetResult(); }
        var outcomes = await Task.WhenAll(firstAccept, secondAccept);
        check(outcomes.SequenceEqual([true, false]) && launches.Count == 1 && feed.Downloads == 3 && File.ReadAllBytes(launches[0]).SequenceEqual(Feed.Installer),
            "A retry downloads the correct bytes and concurrent accept actions launch Setup exactly once.");
        check(!await session.InstallNow(next) && launches.Count == 1, "Another click cannot launch a second copy while Setup is running.");
        var readyToRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void AfterSetupExit() { if (!session.Busy) readyToRetry.TrySetResult(); }
        session.Changed += AfterSetupExit;
        setupExited.SetResult();
        await readyToRetry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Changed -= AfterSetupExit;
        check(await session.InstallNow(next) && launches.Count == 2, "If the user closes Setup, the app can start it again.");

        session.SetAutoCheck(false);
        session = Open();
        int before = feed.Lookups;
        await session.Check(manual: false, onOpen: true);
        check(feed.Lookups == before, "A saved auto-check opt-out applies to opening the app too.");
        await session.Check(manual: true);
        check(feed.Lookups == before + 1 && session.AvailableVersion == next, "Manual checking remains available with automatic checking off.");
        session.SkipVersion(next);
        session = Open();
        await session.Check(manual: true);
        await session.InstallNow(next);
        check(launches.Count == 3 && File.ReadAllBytes(launches[2]).SequenceEqual(Feed.Installer),
            "A user can deliberately install a previously skipped release from Settings.");

        session.SetAutoCheck(true);
        session = Open();
        feed.Version = first;
        await session.Check(manual: false, onOpen: true);
        check(session.SkippedVersion == next && prompts.SequenceEqual([first, next]),
            "Withdrawing the skipped release does not automatically prompt for an older available version.");
        await session.Check(manual: true);
        check(session.AvailableVersion == first && await session.InstallNow(first),
            "A manual check can still install the available release below the skipped version and above the installed version.");
        feed.Version = "1.0.0-beta.10";
        await session.Check(manual: false, onOpen: true);
        check(prompts.SequenceEqual([first, next, feed.Version]), "A release newer than the skipped version resumes automatic prompting.");
    }

    private sealed class Feed : HttpMessageHandler
    {
        internal static readonly byte[] Installer = Encoding.UTF8.GetBytes("Inert installer fixture for the coordinator.");
        internal required string Version { get; set; }
        internal bool DamageDownload;
        internal int Lookups, Downloads;
        internal Func<Task>? OnDownload;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string name = $"TeamBar-{Version}-win-x64-Setup.exe";
            string url = $"https://github.com/{GitHubReleaseNotices.Repository}/releases/download/v{Version}/{name}";
            if (request.RequestUri!.AbsoluteUri == $"https://api.github.com/repos/{GitHubReleaseNotices.Repository}/releases?per_page=100&page=1")
            {
                Lookups++;
                var asset = new { name, state = "uploaded", size = Installer.Length,
                    digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Installer)), browser_download_url = url };
                var release = new { tag_name = "v" + Version, draft = false, prerelease = true, assets = new[] { asset } };
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new[] { release }), Encoding.UTF8, "application/json") };
            }
            if (request.RequestUri.AbsoluteUri != url) throw new InvalidOperationException("Unexpected coordinator fixture request.");
            Downloads++;
            if (OnDownload is not null) await OnDownload().WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(DamageDownload ? new byte[Installer.Length] : Installer) };
        }
    }
}
