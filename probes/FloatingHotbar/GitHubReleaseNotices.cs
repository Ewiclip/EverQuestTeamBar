using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record GitHubReleaseNotice(string Version, long Bytes, string Sha256)
{
    internal string InstallerName => $"TeamBar-{Version}-win-x64-Setup.exe";
    public Uri ReleasePage => new($"https://github.com/{GitHubReleaseNotices.Repository}/releases/tag/v{Version}");
    internal Uri DownloadUri => new($"https://github.com/{GitHubReleaseNotices.Repository}/releases/download/v{Version}/{InstallerName}");
}

internal sealed partial class GitHubReleaseNotices(HttpClient http)
{
    internal const string Repository = "Ewiclip/EverQuestTeamBar";
    private static readonly Regex NumberedBeta = new(@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-beta\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    private const int PageSize = 100, PageLimit = 10, PageByteLimit = 4 * 1024 * 1024;
    private static readonly HttpClient shared = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true
    }) { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, AllowDuplicateProperties = false, MaxDepth = 24
    };

    internal GitHubReleaseNotices() : this(shared) { }

    internal async Task<GitHubReleaseNotice?> FindNewer(string currentVersion, CancellationToken cancellationToken = default)
    {
        var highest = ReleaseUpdateVersion.Parse(currentVersion);
        (string Version, Asset[] Assets)? newest = null;
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
                if (release.Draft || !release.Prerelease || release.TagName.Length > 81 || !release.TagName.StartsWith('v') ||
                    !NumberedBeta.IsMatch(release.TagName[1..]))
                    continue;
                string version = release.TagName[1..];
                var parsed = ReleaseUpdateVersion.Parse(version);
                if (parsed.CompareTo(highest) <= 0) continue;
                string installerName = $"TeamBar-{version}-win-x64-Setup.exe";
                if (release.Assets.Any(asset => asset is null))
                    throw new InvalidDataException("GitHub returned an empty release asset entry.");
                var assets = release.Assets.Where(asset => asset.State == "uploaded" && asset.Size > 0 && asset.Name == installerName).ToArray();
                if (assets.Length == 0) continue;
                highest = parsed;
                newest = (version, assets);
            }

            // A next-page header can occur even on a short page. Request our own
            // numbered repository URL rather than navigating to a supplied link.
            bool hasNext = response.Headers.TryGetValues("Link", out var links) &&
                links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
            if (!hasNext && releases.Length < PageSize)
            {
                if (newest is not { } selected) return null;
                if (selected.Assets.Length != 1) throw new InvalidDataException("The GitHub release lists more than one matching installer.");
                var asset = selected.Assets[0];
                if (asset.Size > ReleaseUpdateManifest.InstallerLimit || asset.Digest is not { } digest ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || !ReleaseUpdateManifest.IsHash(digest[7..]))
                    throw new InvalidDataException("GitHub did not provide a valid installer size and SHA-256 digest for this release.");
                var notice = new GitHubReleaseNotice(selected.Version, asset.Size, digest[7..]);
                if (asset.BrowserDownloadUrl != notice.DownloadUri.AbsoluteUri)
                    throw new InvalidDataException("The installer is outside the expected Team Bar release.");
                return notice;
            }
        }
        throw new InvalidDataException("The GitHub release list exceeded the maximum number of pages.");
    }

    private sealed record Release(string TagName, bool Draft, bool Prerelease, Asset[] Assets);
    private sealed record Asset(string Name, string State, long Size, string? Digest = null, string? BrowserDownloadUrl = null);
}
