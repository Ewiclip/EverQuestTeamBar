using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record GitHubReleaseNotice(string Version, Uri ReleasePage);

// The unsigned beta only announces published releases. It returns a browser page
// constructed from the repository and tag; release asset URLs are never opened.
internal sealed class GitHubReleaseNotices(HttpClient http)
{
    internal const string Repository = "Ewiclip/EverQuestTeamBar";
    private const int PageSize = 100, PageLimit = 10, PageByteLimit = 4 * 1024 * 1024;
    private static readonly HttpClient shared = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true
    });
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, AllowDuplicateProperties = false, MaxDepth = 24
    };

    internal GitHubReleaseNotices() : this(shared) { }

    internal async Task<GitHubReleaseNotice?> FindNewer(string currentVersion, CancellationToken cancellationToken = default)
    {
        var highest = ReleaseUpdateVersion.Parse(currentVersion);
        GitHubReleaseNotice? notice = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        for (int page = 1; page <= PageLimit; page++)
        {
            var uri = new Uri($"https://api.github.com/repos/{Repository}/releases?per_page={PageSize}&page={page}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("TeamBar/" + currentVersion);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri)
                throw new HttpRequestException($"GitHub release lookup failed (HTTP {(int)response.StatusCode}).");
            await response.Content.LoadIntoBufferAsync(PageByteLimit, deadline.Token).ConfigureAwait(false);
            using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize<Release[]>(body, json);
            if (releases is null || releases.Length > PageSize)
                throw new InvalidDataException("GitHub returned an invalid release list.");
            foreach (var release in releases)
            {
                if (release is null) throw new InvalidDataException("GitHub returned an empty release entry.");
                if (release.Draft || !release.Prerelease || !Regex.IsMatch(release.TagName,
                    @"\Av(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-beta\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant))
                    continue;
                string version = release.TagName[1..];
                var parsed = ReleaseUpdateVersion.Parse(version);
                if (parsed.CompareTo(highest) <= 0) continue;
                string installerName = $"TeamBar-{version}-win-x64-Setup.exe";
                if (release.Assets.Any(asset => asset is null))
                    throw new InvalidDataException("GitHub returned an empty release asset entry.");
                if (!release.Assets.Any(asset => asset.State == "uploaded" && asset.Size > 0 && asset.Name == installerName))
                    continue;
                highest = parsed;
                notice = new(version, new Uri($"https://github.com/{Repository}/releases/tag/{release.TagName}"));
            }

            // A next-page header can occur even on a short page. Request our own
            // numbered repository URL rather than navigating to a supplied link.
            bool hasNext = response.Headers.TryGetValues("Link", out var links) &&
                links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
            if (!hasNext && releases.Length < PageSize) return notice;
        }
        throw new InvalidDataException("The GitHub release list exceeded the maximum number of pages.");
    }

    private sealed record Release(string TagName, bool Draft, bool Prerelease, Asset[] Assets);
    private sealed record Asset(string Name, string State, long Size);
}
