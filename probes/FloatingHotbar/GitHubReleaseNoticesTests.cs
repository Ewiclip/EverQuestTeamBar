using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TeamBar;

internal static class GitHubReleaseNoticesTests
{
    internal static async Task Run(Action<bool, string> check, string directory)
    {
        static object Release(string version, string state = "uploaded", long size = 1024, bool draft = false,
            bool prerelease = true, string? assetName = null, string? digest = null, string? url = null) => new
        {
            tag_name = "v" + version, draft, prerelease,
            html_url = "https://example.invalid/release",
            assets = new[] { new { name = assetName ?? $"TeamBar-{version}-win-x64-Setup.exe", state, size,
                digest = digest ?? ("sha256:" + new string('a', 64)),
                browser_download_url = url ?? $"https://github.com/Ewiclip/EverQuestTeamBar/releases/download/v{version}/TeamBar-{version}-win-x64-Setup.exe" } }
        };
        static HttpResponseMessage Page(HttpRequestMessage request, object releases, bool more = false)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json") };
            if (more) response.Headers.Add("Link", "<https://example.invalid/other>; rel=\"next\"");
            return response;
        }

        var addresses = new List<Uri>();
        using (var http = new HttpClient(new Handler(request =>
        {
            addresses.Add(request.RequestUri!);
            return addresses.Count switch
            {
                1 => Page(request, new[] { Release("1.2.3-beta.7"), Release("4.0.0-beta.1", draft: true),
                    Release("3.0.0-beta.1", prerelease: false), Release("2.0.0-rc.1"), Release("2.00.0-beta.1"),
                    Release("1.2.3-beta.50", size: 0), Release("1.2.3-beta.20", state: "starter"),
                    Release("1.2.3-beta.300", assetName: "TeamBar-other.exe") }, more: true),
                2 => Page(request, new[] { Release("1.2.3-beta.12"), Release("1.2.3-beta.9") }),
                _ => throw new InvalidOperationException("Unexpected request after the last release page.")
            };
        })))
        {
            var notice = await new GitHubReleaseNotices(http).FindNewer("1.2.3-beta.4");
            check(notice is { Version: "1.2.3-beta.12" } &&
                notice.ReleasePage.AbsoluteUri == "https://github.com/Ewiclip/EverQuestTeamBar/releases/tag/v1.2.3-beta.12",
                "Only published numbered betas with a matching uploaded x64 installer can surface a canonical GitHub release page.");
            check(addresses.Select(uri => uri.AbsoluteUri).SequenceEqual(new[] { 1, 2 }.Select(page =>
                $"https://api.github.com/repos/Ewiclip/EverQuestTeamBar/releases?per_page=100&page={page}")),
                "Unsigned notices request only the fixed repository's release metadata, including on subsequent pages.");
        }
        foreach (var releases in new[] { Array.Empty<object>(), new[] { Release("1.2.3-beta.4"), Release("1.2.3-beta.2") } })
        {
            using var http = new HttpClient(new Handler(request => Page(request, releases)));
            check(await new GitHubReleaseNotices(http).FindNewer("1.2.3-beta.4") is null,
                releases.Length == 0 ? "An empty public release list has no update." : "The same or an older beta is never advertised.");
        }
        static async Task<bool> Fails(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            using var http = new HttpClient(new Handler(respond));
            try { await new GitHubReleaseNotices(http).FindNewer("1.2.3-beta.4"); }
            catch (Exception e) when (e is JsonException or InvalidDataException or HttpRequestException) { return true; }
            return false;
        }
        check(await Fails(request => new(HttpStatusCode.Forbidden) { RequestMessage = request }) &&
            await Fails(request => new(HttpStatusCode.Redirect) { RequestMessage = request }) &&
            await Fails(request => Page(request, new[] { new { tag_name = "v2.0.0-beta.4" } })),
            "API failures, redirects and incomplete release data report a failed lookup.");
        check(await Fails(request => Page(request, new[] { Release("1.2.3-beta.8") }, more: true)) &&
            await Fails(request => Page(request, new string('x', 4 * 1024 * 1024))),
            "An oversized response or an unfinished page sequence cannot report a partial update result.");
        check(await Fails(request => Page(request, new[] { Release("1.2.3-beta.8", digest: "sha256:bad") })) &&
            await Fails(request => Page(request, new[] { Release("1.2.3-beta.8", size: ReleaseUpdateManifest.InstallerLimit + 1) })) &&
            await Fails(request => Page(request, new[] { Release("1.2.3-beta.8", url: "https://example.invalid/setup.exe") })),
            "The newest uploaded installer must have a valid GitHub digest, bounded size and exact repository download URL.");
        await GitHubReleaseDownloadTests.Run(check, Path.Combine(directory, "github-download"));
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }
}
