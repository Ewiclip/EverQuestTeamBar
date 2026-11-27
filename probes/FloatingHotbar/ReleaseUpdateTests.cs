using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TeamBar;

internal static class ReleaseUpdateTests
{
    internal static void Run(string outputDirectory) => RunAsync(outputDirectory).GetAwaiter().GetResult();

    private static async Task RunAsync(string outputDirectory)
    {
        string directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory)) throw new IOException("Updater fixtures require a new output directory.");
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); results.Add(reason); }
        static async Task Reject(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or HttpRequestException or CryptographicException or JsonException or IOException) { return; }
            throw new InvalidOperationException("An untrusted update unexpectedly passed verification.");
        }
        static Task RejectSync(Action action) => Reject(() => { action(); return Task.CompletedTask; });
        string hash = new('A', 64), otherHash = new('B', 64);
        var configuration = new ReleaseUpdateConfiguration(new("https://updates.example.com/team-bar/beta/"), hash, "beta", "0.1.0-beta.1", hash, hash);
        byte[] installer = Encoding.UTF8.GetBytes("An inert fixture, not an executable.");
        var release = new ReleaseUpdateManifest(1, "team-bar", "beta", "0.1.0-beta.2", DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1), "bridge-1", 1, "Fixture release notes.",
            new("https://updates.example.com/team-bar/beta/TeamBar-Setup.exe", Convert.ToHexString(SHA256.HashData(installer)), installer.Length, otherHash),
            [new("TeamBar.exe", hash, 1), new("TeamBar.dll", hash, 1), new("TeamBridge.dll", hash, 1), new("TeamControllerHost.dll", hash, 1)]);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(release, ReleaseUpdateManifest.JsonOptions);

        configuration.Validate();
        Check(ReleaseUpdateManifest.Parse(manifest, configuration, DateTimeOffset.UtcNow).Version == release.Version, "Valid signed-schema fixture parses.");
        var noRuntimeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Check(!release.RequiresEverQuestClosed(configuration, noRuntimeFiles), "Identical native hashes preserve the bridge during a managed-only update.");
        Check((release with { Files = release.Files.Select(file => file.Path == "TeamBridge.dll" ? file with { Sha256 = otherHash } : file).ToArray() }).RequiresEverQuestClosed(configuration, noRuntimeFiles),
            "Changing the injected native bridge requires all EverQuest clients closed.");
        Check((release with { Files = release.Files.Select(file => file.Path == "TeamControllerHost.dll" ? file with { Sha256 = otherHash } : file).ToArray() }).RequiresEverQuestClosed(configuration, noRuntimeFiles),
            "Changing the native controller requires all EverQuest clients closed.");
        var runtimeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["vcruntime140.dll"] = hash };
        var withRuntime = release with { Files = release.Files.Append(new("vcruntime140.dll", hash, 1)).ToArray() };
        Check(!withRuntime.RequiresEverQuestClosed(configuration, runtimeFiles), "Identical app-local C runtime files preserve the loaded bridge.");
        Check(withRuntime.RequiresEverQuestClosed(configuration, noRuntimeFiles), "Adding an app-local C runtime requires EverQuest closed.");
        Check(release.RequiresEverQuestClosed(configuration, runtimeFiles), "Removing an app-local C runtime requires EverQuest closed.");
        Check((release with { Files = release.Files.Append(new("vcruntime140.dll", otherHash, 1)).ToArray() }).RequiresEverQuestClosed(configuration, runtimeFiles),
            "Changing an app-local C runtime requires EverQuest closed even if both Team Bar native DLLs are identical.");

        foreach (string path in new[] { "../TeamBar.exe", "native/../TeamBar.exe", "/TeamBar.exe", "C:/TeamBar.exe", "native\\TeamBar.exe", "A/CON.dll", "aux", "native//file.dll", "file.dll.", "file.dll ", "file:stream", "file%2e.dll" })
            Check(!ReleaseUpdateManifest.IsSafeRelativePath(path), "Reject unsafe manifest path: " + path);
        foreach (string url in new[] { "http://updates.example.com/team-bar/beta/a.exe", "https://evil.example.com/team-bar/beta/a.exe", "https://user@updates.example.com/team-bar/beta/a.exe", "https://updates.example.com/team-bar/beta/a.exe?x=1", "https://updates.example.com/team-bar/beta/a.exe#fragment", "https://updates.example.com:8443/team-bar/beta/a.exe", "https://updates.example.com/team-bar/stable/a.exe", "https://updates.example.com/team-bar/beta/%2e%2e/a.exe", "https://127.0.0.1/a.exe", "file:///C:/a.exe" })
            await RejectSync(() => configuration.AllowedDownload(url));
        results.Add("Reject insecure, foreign-origin, credentialed, encoded, query-bearing, and off-channel URLs.");
        foreach (var invalid in new[]
        {
            release with { SchemaVersion = 2 }, release with { Product = "another-product" }, release with { Channel = "stable" },
            release with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, release with { PublishedAt = DateTimeOffset.UtcNow.AddHours(1) },
            release with { PublishedAt = DateTimeOffset.UtcNow.AddMinutes(6) }, release with { ExpiresAt = release.PublishedAt.AddDays(30).AddTicks(1) },
            release with { ExpiresAt = DateTimeOffset.UtcNow.AddYears(1) }, release with { NativeProtocol = 2 },
            release with { Installer = release.Installer with { Bytes = ReleaseUpdateManifest.InstallerLimit + 1 } },
            release with { Installer = release.Installer with { PublisherSha256 = "" } },
            release with { Files = release.Files.Append(release.Files[0] with { Path = "TEAMBAR.EXE" }).ToArray() },
            release with { Files = release.Files.Take(3).ToArray() }, release with { Version = "01.2.3" }
        }) await RejectSync(() => invalid.Validate(configuration, DateTimeOffset.UtcNow));
        await RejectSync(() => ReleaseUpdateManifest.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")), configuration, DateTimeOffset.UtcNow));
        results.Add("Reject expired, future, oversized, incompatible, incomplete and ambiguous manifests.");
        foreach (var (older, newer) in new[] { ("0.1.0-beta.2", "0.1.0-beta.10"), ("0.1.0-beta.10", "0.1.0"), ("1.9.9", "2.0.0-beta.1"), ("0.1.0-beta.a", "0.1.0-beta.b") })
            Check(ReleaseUpdateVersion.Parse(older).CompareTo(ReleaseUpdateVersion.Parse(newer)) < 0, "Correct semantic-version ordering: " + older + " < " + newer);

        var verifier = new FixtureVerifier();
        using var http = new HttpClient(new FixtureHttp(request => Response(request, request.RequestUri!.AbsolutePath.EndsWith("manifest.json") ? manifest
            : request.RequestUri.AbsolutePath.EndsWith(".p7s") ? [1, 2, 3] : installer)));
        var client = new ReleaseUpdateClient(configuration, http, verifier);
        var checkedRelease = await client.Check();
        Check(verifier.ManifestChecks == 1 && verifier.ManifestPin == hash, "A metadata signature is verified with the compiled manifest key before accepting a release.");
        string staged = await client.Stage(checkedRelease, directory);
        Check(File.ReadAllBytes(staged).SequenceEqual(installer) && verifier.InstallerChecks == 1 && verifier.InstallerPin == otherHash,
            "Only exact-sized, hash-matching installers are staged; their signed-manifest leaf pin is independent of the metadata signer.");
        using (client.OpenVerifiedInstaller(staged, release)) { }
        Check(verifier.InstallerChecks == 2, "The staged installer is checked again immediately before handoff.");
        File.WriteAllBytes(staged, Encoding.UTF8.GetBytes("tampered"));
        await RejectSync(() => { using var locked = client.OpenVerifiedInstaller(staged, release); });
        results.Add("A tampered staged installer cannot be launched.");
        await Reject(() => client.Stage(release with { Version = "0.1.0-beta.1" }, directory));
        await Reject(() => client.Stage(release with { Version = "0.0.1" }, directory));
        results.Add("Already-installed and downgrade releases cannot be staged.");

        foreach (var responseBytes in new[] { installer[..^1], installer.Concat(new byte[] { 4 }).ToArray(), Enumerable.Repeat((byte)0, installer.Length).ToArray() })
        {
            using var brokenHttp = new HttpClient(new FixtureHttp(request => Response(request, responseBytes)));
            await Reject(() => new ReleaseUpdateClient(configuration, brokenHttp, verifier).Stage(release, directory));
        }
        results.Add("Truncated, oversized, and wrong-hash downloads fail without leaving a partial executable.");
        Check(!Directory.EnumerateFiles(directory, "*.partial", SearchOption.AllDirectories).Any(), "Failed downloads remove owned partial files.");
        using (var redirectHttp = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.Redirect) { RequestMessage = request, Headers = { Location = new("https://evil.example.com/a.exe") } })))
            await Reject(() => new ReleaseUpdateClient(configuration, redirectHttp, verifier).Check());
        using (var oversizedHttp = new HttpClient(new FixtureHttp(request => Response(request, new byte[ReleaseUpdateManifest.ManifestLimit + 1]))))
            await Reject(() => new ReleaseUpdateClient(configuration, oversizedHttp, verifier).Check());
        using (var rejectedSignature = new HttpClient(new FixtureHttp(request => Response(request, manifest))))
            await Reject(() => new ReleaseUpdateClient(configuration, rejectedSignature, new FixtureVerifier { RejectManifest = true }).Check());
        results.Add("Redirects, response-size limits, and rejected signatures all fail closed.");
        using (var rejectedInstaller = new HttpClient(new FixtureHttp(request => Response(request, installer))))
            await Reject(() => new ReleaseUpdateClient(configuration, rejectedInstaller, new FixtureVerifier { RejectInstaller = true }).Stage(release, directory));
        results.Add("An installer rejected by Authenticode verification is never staged for execution.");

        using (var stalledHttp = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(new StalledStream()) })))
        {
            var stalledClient = new ReleaseUpdateClient(configuration, stalledHttp, verifier);
            foreach (bool downloadInstaller in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                bool canceled = false;
                try
                {
                    if (downloadInstaller) await stalledClient.Stage(release, directory, cancellation.Token);
                    else await stalledClient.Check(cancellation.Token);
                }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, downloadInstaller ? "Cancellation interrupts a stalled installer response body."
                    : "Cancellation interrupts a stalled manifest response body.");
            }
            Check(!Directory.EnumerateFiles(directory, "*.partial", SearchOption.AllDirectories).Any(), "Cancellation removes an owned partial download.");
        }

        using (RSA key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=Team Bar updater fixture only", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            var cms = new SignedCms(new ContentInfo(manifest), detached: true);
            cms.ComputeSignature(new CmsSigner(certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") });
            byte[] signature = cms.Encode();
            string pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            var trust = new ReleaseUpdateTrust();
            trust.VerifyManifest(manifest, signature, pin);
            await RejectSync(() => trust.VerifyManifest(manifest, signature, hash));
            await RejectSync(() => trust.VerifyManifest([1, 2, 3], signature, pin));
            var attached = new SignedCms(new ContentInfo([8, 9, 10]), detached: false);
            attached.ComputeSignature(new CmsSigner(certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") });
            await RejectSync(() => trust.VerifyManifest(manifest, attached.Encode(), pin));
            await RejectSync(() => trust.VerifyInstaller(staged, pin));
            results.Add("Real CMS verification accepts only the pinned metadata signer and exact signed bytes; Windows rejects an unsigned installer fixture.");
        }

        var inputStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool launched = false;
        var handoff = ReleaseUpdateHandoff.Finish(() => inputStopped.Task, () => launched = true);
        Check(!launched && !handoff.IsCompleted, "Installer handoff waits for graceful native input shutdown.");
        inputStopped.SetResult(); await handoff;
        Check(launched, "Installer handoff proceeds only after input shutdown completes.");
        launched = false;
        await Reject(() => ReleaseUpdateHandoff.Finish(() => Task.FromException(new IOException("Fixture shutdown failed.")), () => launched = true));
        Check(!launched, "An input shutdown failure prevents installer launch.");
        var order = new List<string>();
        ReleaseUpdateHandoff.Apply(() => order.Add("game check"), () => { order.Add("verify"); return new MemoryStream(); },
            () => order.Add("backup"), () => order.Add("launch"));
        Check(order.SequenceEqual(new[] { "game check", "verify", "backup", "game check", "launch" }),
            "Handoff verifies the installer, completes a profile backup, and rechecks native eligibility before launch.");
        await RejectSync(() => ReleaseUpdateHandoff.Apply(() => { }, () => new MemoryStream(),
            () => throw new IOException("Fixture backup failed."), () => launched = true));
        Check(!launched, "Backup failure prevents installer launch.");
        await RejectSync(() => ReleaseUpdateHandoff.Apply(() => { }, () => throw new CryptographicException("Fixture tampered installer."),
            () => throw new InvalidOperationException("Backup must not run."), () => launched = true));
        Check(!launched, "Failed launch-time verification prevents both backup and installer launch.");
        await RejectSync(() => ReleaseUpdateHandoff.Apply(() => throw new IOException("Fixture EQ still open."),
            () => throw new InvalidOperationException("Verification must not run."), () => { }, () => launched = true));
        Check(!launched, "A native update blocked by an open game never launches the installer.");
        order.Clear();
        ReleaseUpdateHandoff.Apply(() => order.Add("check"), () => new MemoryStream(), () => order.Add("backup"),
            () => order.Add("launch"), () => true);
        Check(order.Count == 0, "An external Setup waiting for exit supersedes a queued update before its game checks or backup.");
        bool externalSetup = false;
        ReleaseUpdateHandoff.Apply(() => order.Add("check"), () => new MemoryStream(), () => externalSetup = true,
            () => order.Add("launch"), () => externalSetup);
        Check(order.SequenceEqual(["check"]), "A Setup request received during backup prevents a competing installer launch.");
        int eligibilityChecks = 0;
        ReleaseUpdateHandoff.Apply(() => eligibilityChecks++, () => new MemoryStream(), () => { },
            () => launched = true, () => eligibilityChecks == 2);
        Check(eligibilityChecks == 2 && !launched, "A Setup request received during the last eligibility check also supersedes the queued update.");
        await GitHubReleaseNoticesTests.Run(Check, directory);
        await ReleaseUpdateCoordinatorTests.Run(directory, Check);
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { passed = true, checks = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    private sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
    private sealed class FixtureVerifier : IReleaseUpdateVerifier
    {
        internal int ManifestChecks, InstallerChecks;
        internal string? ManifestPin, InstallerPin;
        internal bool RejectManifest, RejectInstaller;
        public void VerifyManifest(byte[] manifest, byte[] signature, string pin)
        { ManifestChecks++; ManifestPin = pin; if (RejectManifest) throw new CryptographicException("Fixture rejects metadata signature."); }
        public void VerifyInstaller(string path, string pin)
        { InstallerChecks++; InstallerPin = pin; if (RejectInstaller) throw new CryptographicException("Fixture rejects installer signature."); }
    }
}
