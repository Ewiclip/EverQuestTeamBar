using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace TeamBar;

internal static class GitHubReleaseDownloadTests
{
    internal static async Task Run(Action<bool, string> check, string directory)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("Inert Team Bar download fixture.");
        var notice = new GitHubReleaseNotice("1.2.3-beta.5", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        Uri cdn = new("https://release-assets.githubusercontent.com/github-production-release-asset/123/file?token=test");
        static HttpResponseMessage Content(HttpRequestMessage request, byte[] data) =>
            new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(data) };
        static HttpResponseMessage Redirect(HttpRequestMessage request, Uri to)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = to;
            return response;
        }
        var urls = new List<Uri>();
        using var http = new HttpClient(new GitHubReleaseNoticesTests.Handler(request =>
        {
            urls.Add(request.RequestUri!);
            if (request.RequestUri == notice.DownloadUri) return Redirect(request, cdn);
            if (request.RequestUri == cdn) return Content(request, bytes);
            throw new InvalidOperationException("Unexpected download destination.");
        }));
        var client = new GitHubReleaseNotices(http);
        string file = await client.Stage(notice, directory);
        check(urls.SequenceEqual(new[] { notice.DownloadUri, cdn }) && File.ReadAllBytes(file).SequenceEqual(bytes) &&
            File.ReadAllText(file + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal),
            "The official GitHub asset and its authorized CDN redirect produce the expected installer with Windows Internet-zone metadata.");
        File.Delete(file + ":Zone.Identifier");
        check(await client.Stage(notice, directory) == file && urls.Count == 2 && File.ReadAllText(file + ":Zone.Identifier").Contains("ZoneId=3"),
            "A verified cached installer is reused and its Windows Internet zone is restored.");
        const string restricted = "[ZoneTransfer]\r\nZoneId=4\r\nHostUrl=https://example.invalid/asset\r\n";
        File.WriteAllText(file + ":Zone.Identifier", restricted);
        await client.Stage(notice, directory);
        using (client.OpenVerifiedInstaller(file, notice))
        {
            check(File.ReadAllText(file + ":Zone.Identifier") == restricted && urls.Count == 2,
                "Reusing and opening a cached installer preserves its existing Restricted zone and origin details.");
            bool prevented = false;
            try { using var _ = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { prevented = true; }
            check(prevented, "The final hash check holds a read lock against concurrent replacement through installer launch.");
        }
        File.WriteAllBytes(file, new byte[bytes.Length]);
        bool rejected = false;
        try { using var _ = client.OpenVerifiedInstaller(file, notice); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && await client.Stage(notice, directory) == file && urls.Count == 4 &&
            File.ReadAllBytes(file).SequenceEqual(bytes), "A changed installer fails final verification and is repaired on the next download.");

        var offered = notice with { Version = "1.2.3-beta.6", Sha256 = new string('b', 64) };
        async Task<bool> Rejected(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            using var bad = new HttpClient(new GitHubReleaseNoticesTests.Handler(responder));
            try { await new GitHubReleaseNotices(bad).Stage(offered, directory); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException) { return true; }
            return false;
        }
        check(await Rejected(request => Redirect(request, new Uri("https://example.invalid/other.exe"))) &&
            await Rejected(request => Redirect(request, new Uri("http://release-assets.githubusercontent.com/file"))) &&
            await Rejected(request => Redirect(request, new Uri("https://release-assets.githubusercontent.com:777/file"))) &&
            await Rejected(request => Redirect(request, cdn)),
            "Downloads reject foreign hosts, insecure URLs, alternate ports and repeated redirects.");
        check(await Rejected(request => Content(request, bytes)) &&
            await Rejected(request => Content(request, new byte[bytes.Length - 1])) &&
            File.ReadAllBytes(file).SequenceEqual(bytes) && Directory.GetFiles(Path.GetDirectoryName(file)!).Length == 1,
            "A digest mismatch or short response preserves the prior installer and removes partial downloads.");
        // Without a Content-Length the response body itself must still obey the byte limit.
        check(await Rejected(request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
            Content = new StreamContent(new NonSeekable(new byte[bytes.Length + 1])) }),
            "An unbounded HTTP response is stopped as soon as it exceeds the release asset size.");
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
