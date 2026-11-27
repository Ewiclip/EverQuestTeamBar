using System.Net;
using System.Net.Http;
using System.Text;

namespace TeamBar;

internal sealed partial class GitHubReleaseNotices
{
    private const string CachedInstallerName = "TeamBar-Setup.exe";

    internal async Task<string> Stage(GitHubReleaseNotice notice, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        ValidateInstaller(notice);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        string directory = Path.Combine(Path.GetFullPath(cacheDirectory), "github");
        string destination = Path.Combine(directory, CachedInstallerName);
        RequireOrdinaryPath(destination);
        Directory.CreateDirectory(directory);
        if (File.Exists(destination) && await Task.Run(() => CacheMatches(destination, notice), token).ConfigureAwait(false))
            return destination;

        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        bool created = false;
        try
        {
            using var response = await DownloadResponse(notice.DownloadUri, token).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                created = true;
                await ReleaseInstallerFile.WriteVerified(response.Content, output, notice.Bytes, notice.Sha256, token).ConfigureAwait(false);
            }
            MarkInternetDownload(partial);
            token.ThrowIfCancellationRequested();
            RequireOrdinaryPath(destination);
            // One verified installer per user. A failed transfer leaves the last complete file intact.
            File.Move(partial, destination, overwrite: true);
            created = false;
            return destination;
        }
        finally { if (created) File.Delete(partial); }
    }

    internal FileStream OpenVerifiedInstaller(string path, GitHubReleaseNotice notice)
    {
        ValidateInstaller(notice);
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != CachedInstallerName)
            throw new InvalidDataException("Unexpected staged installer path.");
        RequireOrdinaryPath(path);
        var locked = ReleaseInstallerFile.OpenVerified(path, notice.Bytes, notice.Sha256);
        try
        {
            // A previously cached file can lose its Internet zone after a copy or restore.
            MarkInternetDownload(path);
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }

    private bool CacheMatches(string path, GitHubReleaseNotice notice)
    {
        try { using var verified = OpenVerifiedInstaller(path, notice); return true; }
        catch (InvalidDataException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private static void ValidateInstaller(GitHubReleaseNotice notice)
    {
        if (notice.Version.Length > 80 || !NumberedBeta.IsMatch(notice.Version) ||
            notice.Bytes is <= 0 or > ReleaseUpdateManifest.InstallerLimit || !ReleaseUpdateManifest.IsHash(notice.Sha256))
            throw new InvalidDataException("Invalid GitHub installer metadata.");
    }

    private static void RequireOrdinaryPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The installer cache cannot contain linked files or directories.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void MarkInternetDownload(string path)
    {
        string zonePath = path + ":Zone.Identifier";
        try
        {
            using var stream = File.OpenRead(zonePath);
            if (stream.Length > 8192) throw new IOException("The installer's Windows security zone is too large to verify.");
            using var reader = new StreamReader(stream);
            bool zoneTransfer = false;
            while (reader.ReadLine() is { } raw)
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) zoneTransfer = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase);
                if (!zoneTransfer) continue;
                int equals = line.IndexOf('=');
                if (equals < 0 || !line[..equals].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase)) continue;
                if (line[(equals + 1)..].Trim() is "3" or "4") return;
                break;
            }
        }
        catch (FileNotFoundException) { }
        File.WriteAllText(zonePath, "[ZoneTransfer]\r\nZoneId=3\r\n", Encoding.ASCII);
    }

    private async Task<HttpResponseMessage> DownloadResponse(Uri origin, CancellationToken token)
    {
        async Task<HttpResponseMessage> Get(Uri uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("TeamBar");
            var result = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (result.RequestMessage?.RequestUri == uri) return result;
            result.Dispose();
            throw new HttpRequestException("The download was redirected without verification.");
        }

        var response = await Get(origin).ConfigureAwait(false);
        try
        {
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var uri = response.Headers.Location;
                if (uri is not { IsAbsoluteUri: true } || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                    uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.OriginalString.Contains('\\') ||
                    uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub returned an unexpected installer download location.");
                response.Dispose();
                response = await Get(uri).ConfigureAwait(false);
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"GitHub installer download failed (HTTP {(int)response.StatusCode}).");
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
