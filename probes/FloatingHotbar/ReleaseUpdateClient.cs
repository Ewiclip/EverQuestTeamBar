using System.Net;
using System.Net.Http;

namespace TeamBar;

internal sealed class ReleaseUpdateClient
{
    private readonly HttpClient http;
    private readonly IReleaseUpdateVerifier verifier;
    internal ReleaseUpdateConfiguration Configuration { get; }

    internal ReleaseUpdateClient(ReleaseUpdateConfiguration configuration)
        : this(configuration, new HttpClient(new HttpClientHandler
        { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false, CheckCertificateRevocationList = true })
        { Timeout = TimeSpan.FromMinutes(10) }, new ReleaseUpdateTrust()) { }

    // Dependencies are internal and supplied only by fixture tests; the production constructor fixes both implementations.
    internal ReleaseUpdateClient(ReleaseUpdateConfiguration configuration, HttpClient http, IReleaseUpdateVerifier verifier)
    {
        configuration.Validate();
        Configuration = configuration; this.http = http; this.verifier = verifier;
    }

    internal async Task<ReleaseUpdateManifest> Check(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        cancellationToken = deadline.Token;
        byte[] manifest = await Read(new Uri(Configuration.FeedBase, "manifest.json"), ReleaseUpdateManifest.ManifestLimit, cancellationToken).ConfigureAwait(false);
        byte[] signature = await Read(new Uri(Configuration.FeedBase, "manifest.json.p7s"), ReleaseUpdateManifest.SignatureLimit, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => verifier.VerifyManifest(manifest, signature, Configuration.ManifestSignerSha256), cancellationToken).ConfigureAwait(false);
        return ReleaseUpdateManifest.Parse(manifest, Configuration, DateTimeOffset.UtcNow);
    }

    internal async Task<string> Stage(ReleaseUpdateManifest manifest, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        cancellationToken = deadline.Token;
        manifest.Validate(Configuration, DateTimeOffset.UtcNow);
        if (ReleaseUpdateVersion.Parse(manifest.Version).CompareTo(ReleaseUpdateVersion.Parse(Configuration.CurrentVersion)) <= 0)
            throw new InvalidDataException("An update must be newer than the installed version.");
        string directory = Path.Combine(Path.GetFullPath(cacheDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, "installer.partial"), destination = Path.Combine(directory, "TeamBar-Setup.exe");
        try
        {
            using var response = await Send(Configuration.AllowedDownload(manifest.Installer.Url), cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await ReleaseInstallerFile.WriteVerified(response.Content, output, manifest.Installer.Bytes, manifest.Installer.Sha256, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => verifier.VerifyInstaller(partial, manifest.Installer.PublisherSha256), cancellationToken).ConfigureAwait(false);
            File.Move(partial, destination);
            return destination;
        }
        catch
        {
            // Only files created by this staging operation are removed; never traverse a feed-provided path.
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    internal FileStream OpenVerifiedInstaller(string path, ReleaseUpdateManifest manifest)
    {
        manifest.Validate(Configuration, DateTimeOffset.UtcNow);
        var locked = ReleaseInstallerFile.OpenVerified(path, manifest.Installer.Bytes, manifest.Installer.Sha256);
        try
        {
            verifier.VerifyInstaller(path, manifest.Installer.PublisherSha256);
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }

    private async Task<byte[]> Read(Uri uri, int maximum, CancellationToken cancellationToken)
    {
        using var response = await Send(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length > maximum) throw new InvalidDataException("The release response exceeds its allowed size.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("The release response exceeds its allowed size.");
            output.Write(buffer, 0, count);
        }
    }

    private async Task<HttpResponseMessage> Send(Uri uri, CancellationToken cancellationToken)
    {
        Configuration.AllowedDownload(uri.AbsoluteUri);
        var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // No redirects, including same-host redirects. A successful request must still terminate at the exact approved URL.
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri)
        { response.Dispose(); throw new HttpRequestException("The trusted update server did not return the requested release file (redirects are not accepted)."); }
        return response;
    }
}
