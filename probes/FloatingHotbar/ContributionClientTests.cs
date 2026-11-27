using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamBar;

internal static class ContributionClientTests
{
    private const string Endpoint = "https://contribution.invalid/api/contributions/v1/catalog";
    private static readonly string Token = "dkpc_" + new string('a', 32) + "_" + new string('A', 43);
    private static readonly string OtherToken = "dkpc_" + new string('b', 32) + "_" + new string('B', 43);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => send(request, cancel);
    }
    private static ContributionRecord Record(int id = 1)
    {
        var payload = JsonSerializer.SerializeToElement(new { itemId = id, name = "Fixture item " + id });
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText()))).ToLowerInvariant();
        return new("item", id, hash, payload, new("Contribution test fixture", "Live", "1"));
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate, int milliseconds = 4000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!predicate())
        { if (Environment.TickCount64 > deadline) throw new InvalidOperationException("Timed out waiting for fixture state."); await Task.Delay(5); }
    }
    private static Task<HttpResponseMessage> Receipt(HttpRequestMessage request, CancellationToken cancel) => BuildReceipt(request, cancel);
    private static async Task<HttpResponseMessage> BuildReceipt(HttpRequestMessage request, CancellationToken cancel)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancel));
        var records = new List<object>();
        int index = 0;
        foreach (string kind in ContributionKinds.All)
            foreach (var record in body.RootElement.TryGetProperty(ContributionKinds.ArrayName(kind), out var entries) ? entries.EnumerateArray() : Enumerable.Empty<JsonElement>())
                records.Add(new { id = (++index).ToString("x32"), kind, entityId = ContributionKinds.IsSource(kind) ? ContributionKinds.SourceId(record) : record.GetProperty(kind + "Id").GetInt32(), status = "pending", duplicate = false });
        return JsonResponse(new { schemaVersion = 1, accepted = records.Count, duplicates = 0, records });
    }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static async Task Stop(ContributionClient client) { client.Dispose(); await client.Completion; }

    internal static async Task Run(string output)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(output))!;
        string root = Path.Combine(parent, "contribution-client-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        string Area(string name) => Path.Combine(root, name);
        await SettingsAndQueue(Area("storage")); checks.Add("DPAPI, atomic queue, endpoint and account isolation");
        LegacyUnicodeQueue(Area("unicode")); checks.Add("legacy Unicode fingerprints migrate without losing pending or acknowledged records");
        await UploadAndRestart(Area("upload")); checks.Add("opt-in HTTP, complete receipt, persisted deduplication");
        await StopsAndRetry(Area("stops")); checks.Add("authentication, format, redirect and receipt failures keep pending data");
        await TransientRetry(Area("transient")); checks.Add("network and server backoff with minimum batch interval");
        await RateLimit(Area("rate")); checks.Add("Retry-After survives manual retry and restart");
        await LockAndScope(Area("lock")); checks.Add("exclusive sender lease and capture scope guard");
        await StalledBody(Area("timeout")); checks.Add("whole-response timeout and cancellation");
        await ContributionDestinationsTests.Run(Area("destinations"), (ok, name) => { Check(ok, name); checks.Add(name); });
        await SharingChoicesAndHistory(Area("sharing"), (ok, name) => { Check(ok, name); checks.Add(name); });
        Directory.CreateDirectory(Area("sources"));
        var sources = ContributionSourcesTests.Run(Area("sources"));
        var sourceClient = new ContributionClient(Area("source-upload"), new Handler(Receipt));
        try
        {
            await sourceClient.Ready;
            await sourceClient.ConfigureAsync(true, Endpoint, Token);
            await sourceClient.Enqueue(sources);
            Check(sourceClient.Status.PendingCount == 0 && sourceClient.Recent.Length == 0, "New source types must remain off after upgrade");
            await sourceClient.ConfigureAsync(true, Endpoint, null, data: new(false, false, false, true, true, true, true));
            await sourceClient.Enqueue(sources);
            await Until(() => sourceClient.Recent.Length == 4 && sourceClient.Status.PendingCount == 0);
            Check(sourceClient.Recent.Select(r => r.Kind).ToHashSet().SetEquals(["npc", "drop", "foraged", "fished"]), "All source types must reach confirmed history");
            await sourceClient.Enqueue(sources);
            Check(sourceClient.Status.PendingCount == 0, "Repeated sources must remain acknowledged");
            checks.Add("source types are opt-in, use complete receipts and persist source-existence deduplication");
        }
        finally { await Stop(sourceClient); }
        ContributionStorage.Save(Path.GetFullPath(output), new { passed = true, checks, network = "in-memory handlers only", userSettingsTouched = false });
    }

    private static async Task SharingChoicesAndHistory(string directory, Action<bool, string> check)
    {
        const string genericEndpoint = "https://receiver.invalid/custom/team-bar";
        const string genericKey = "generic-bearer-token.with-no-dkp-prefix";
        string account = ContributionStorage.AccountId(genericKey);
        check(account.StartsWith("token-") && ContributionStorage.AccountId(Token) == new string('a', 32),
            "generic bearer keys work while legacy account and rotation identities stay intact");
        ContributionRecord Kind(string kind, int id)
        {
            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [kind + "Id"] = id, ["name"] = "Fixture " + kind });
            return new(kind, id, ContributionPayload.Fingerprint(payload), payload, new("Fixture", "Live"));
        }
        var records = new[] { Kind("item", 51), Kind("spell", 52), Kind("recipe", 53) };
        // Pre-existing observations must obey newly narrowed sharing choices too.
        var queue = new ContributionQueue(directory, genericEndpoint, account); queue.Enqueue(records);
        var sent = new List<string>();
        var client = new ContributionClient(directory, new Handler(async (request, token) =>
        {
            check(request.RequestUri!.AbsoluteUri == genericEndpoint && request.Headers.Authorization?.Parameter == genericKey,
                "generic endpoint receives the exact configured path and its own bearer key");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            lock (sent) foreach (string kind in new[] { "item", "spell", "recipe" })
                if (doc.RootElement.TryGetProperty(kind + "s", out var rows) && rows.GetArrayLength() > 0) sent.Add(kind);
            return await Receipt(request, token);
        }));
        try
        {
            await client.Ready;
            await client.ConfigureAsync(true, genericEndpoint, genericKey, data: new(true, false, false));
            await Until(() => client.Recent.Length == 1 && !client.Status.IsSending);
            check(client.Status.PendingCount == 2 && client.Recent.Single().Kind == "item" && sent.SequenceEqual(["item"]),
                "new sharing choices send only selected types and keep previously queued unselected types local");
            check(client.Recent[0].Name == "Fixture item" && client.Recent[0].Receipt.Status == "pending"
                && client.Recent[0].Result == "Received" && client.Recent[0].Record.Payload.GetProperty("itemId").GetInt32() == 51,
                "recent sharing records the confirmed name, kind, exact payload and review status from the receipt");
            await client.Enqueue([Kind("spell", 62)]);
            check(client.Status.PendingCount == 2, "unselected newly observed data is not queued for upload");
            await client.ConfigureAsync(true, genericEndpoint, null, data: new(false, false, true));
            await Until(() => client.Recent.Length == 2 && !client.Status.IsSending);
            check(client.Status.PendingCount == 1 && sent.SequenceEqual(["item", "recipe"]),
                "selecting a previously held type resumes only that type and leaves other choices untouched");
            bool rejected = false;
            try { await client.ConfigureAsync(true, genericEndpoint, null, data: new(false, false, false)); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && client.Settings.Data.Recipes, "enabled sharing requires a data choice and preserves the previous valid settings on failure");
            await client.ConfigureAsync(false, genericEndpoint, null, data: new(false, false, true));
        }
        finally { await Stop(client); }
        var reopened = new ContributionClient(directory, new Handler((_, _) => throw new InvalidOperationException("Disabled history must not upload")));
        try
        {
            await reopened.Ready;
            check(!reopened.Settings.Enabled && reopened.Settings.Data == new ContributionDataSelection(false, false, true)
                && reopened.Recent.Length == 2 && reopened.Status.PendingCount == 1,
                "sharing choices, held records and confirmed history survive reopening without enabling uploads");
            await reopened.ConfigureAsync(false, "https://another.invalid/receive", "another-generic-token", data: new());
            check(reopened.Recent.Length == 0, "a new endpoint or account never displays another connection's receipt history");
            await reopened.ConfigureAsync(false, genericEndpoint, genericKey);
            check(reopened.Recent.Length == 2, "returning to a previous connection restores its isolated delivery history");
        }
        finally { await Stop(reopened); }
        var historyQueue = new ContributionQueue(Path.Combine(directory, "bounded"), genericEndpoint, account);
        var many = Enumerable.Range(1000, 105).Select(id => Kind("item", id)).ToArray(); historyQueue.Enqueue(many);
        while (historyQueue.Next() is { } batch)
            historyQueue.Acknowledge(batch, batch.Records.Select(r => new ContributionReceipt(r.Kind, r.EntityId, r.EntityId.ToString("x32"), "approved", true)).ToArray());
        historyQueue = new(Path.Combine(directory, "bounded"), genericEndpoint, account);
        check(historyQueue.Recent.Length == 100 && historyQueue.Recent.All(entry => entry.Result == "Already received" && entry.Receipt.Status == "approved"),
            "receipt history is bounded and distinguishes duplicate delivery from the website's review status");
        string historyPath = Directory.GetFiles(Path.Combine(directory, "bounded"), "queue-*.json").Single();
        var savedHistory = ContributionStorage.Read<ContributionQueueFile>(historyPath)!;
        var badReceipt = savedHistory.Recent![0] with { Receipt = savedHistory.Recent[0].Receipt with { EntityId = -1 } };
        ContributionStorage.Save(historyPath, savedHistory with { Recent = [badReceipt] });
        bool corruptRejected = false;
        try { _ = new ContributionQueue(Path.Combine(directory, "bounded"), genericEndpoint, account); }
        catch (InvalidDataException) { corruptRejected = true; }
        check(corruptRejected, "a malformed saved receipt cannot be displayed as a confirmed delivery");

        string rollbackDirectory = Path.Combine(directory, "rollback");
        var rollback = new ContributionQueue(rollbackDirectory, genericEndpoint, account); rollback.Enqueue([records[0]]);
        var rollbackBatch = rollback.Next()!;
        string blockedPath = Directory.GetFiles(rollbackDirectory, "queue-*.json").Single();
        File.Delete(blockedPath); Directory.CreateDirectory(blockedPath);
        bool saveFailed = false;
        try { rollback.Acknowledge(rollbackBatch, [new("item", records[0].EntityId, new string('a', 32), "pending", false)]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveFailed = true; }
        check(saveFailed && rollback.Count == 1 && rollback.Recent.Length == 0 && rollback.LastUploadAtUtc is null,
            "a failed receipt save restores the pending record and does not expose an unpersisted delivery");
    }

    private static Task SettingsAndQueue(string directory)
    {
        Directory.CreateDirectory(directory);
        using var handler = ContributionClient.CreateHandler();
        Check(!handler.AllowAutoRedirect && !handler.UseCookies, "Transport must reject redirects and ambient cookies.");
        Check(ContributionStorage.NormalizeEndpoint("https://contribution.invalid/") == Endpoint, "Website root must normalize to the contribution route.");
        Check(ContributionStorage.NormalizeEndpoint("https://contribution.invalid/team-bar/receive") == "https://contribution.invalid/team-bar/receive", "Custom HTTPS API paths are preserved.");
        foreach (string invalid in new[] { "http://contribution.invalid", "https://user@contribution.invalid", "https://contribution.invalid?key=x", "https://contribution.invalid#fragment", "https://contribution.invalid/" + new string('x', 2048) })
        {
            bool rejected = false; try { ContributionStorage.NormalizeEndpoint(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Unsafe endpoint was accepted.");
        }
        bool badToken = false; try { ContributionStorage.AccountId(Token + "\n"); } catch (ArgumentException) { badToken = true; }
        Check(badToken, "Token validation accepted trailing control text.");
        string path = Path.Combine(directory, "key.dpapi");
        var credential = new ContributionCredential(Endpoint, new string('a', 32), Token);
        ContributionStorage.SaveCredential(path, credential);
        Check(ContributionStorage.ReadCredential(path) == credential, "DPAPI roundtrip failed.");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(Token, StringComparison.Ordinal), "DPAPI file contains a plaintext key.");
        var queue = new ContributionQueue(directory, Endpoint, credential.AccountId);
        queue.Enqueue([Record(), Record()]); Check(queue.Count == 1, "Duplicate observations should collapse.");
        queue = new(directory, Endpoint, credential.AccountId); Check(queue.Count == 1, "Pending data must survive restart.");
        queue.Acknowledge(queue.Next()!);
        queue = new(directory, Endpoint, credential.AccountId); queue.Enqueue([Record()]); Check(queue.Count == 0, "Acknowledged fingerprints must survive restart.");
        var otherAccount = new ContributionQueue(directory, Endpoint, new string('b', 32)); otherAccount.Enqueue([Record()]);
        Check(otherAccount.Count == 1, "Account histories must be isolated.");
        var otherEndpoint = new ContributionQueue(directory, "https://other.invalid/api/contributions/v1/catalog", credential.AccountId); otherEndpoint.Enqueue([Record()]);
        Check(otherEndpoint.Count == 1, "Endpoint histories must be isolated.");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic-save temporary files were not cleaned.");
        var live = Record(901);
        var beta = live with { Source = new("Fixture", "Beta", "1", new string('a', 64), "beta-fixture") };
        var patchedBeta = beta with { Source = beta.Source with { BuildSha256 = new string('b', 64) } };
        queue.Enqueue([live, beta, patchedBeta, beta]);
        Check(queue.Count == 3, "Live/Beta definitions or Beta build versions were lost in queue deduplication.");
        queue = new(directory, Endpoint, credential.AccountId);
        var liveBatch = queue.Next()!;
        Check(liveBatch.Records.Length == 1 && liveBatch.Records[0].Source.ClientChannel == "Live", "Channel batches mixed.");
        queue.Acknowledge(liveBatch);
        queue.Acknowledge(queue.Next()!);
        queue = new(directory, Endpoint, credential.AccountId);
        queue.Enqueue([live, beta, patchedBeta]);
        Check(queue.Count == 1 && queue.Next()!.Records[0].Source.BuildSha256 == new string('b', 64),
            "Channel/build acknowledgements affected another provenance scope.");
        var recipe = ContributionPayload.Recipes([new LoadedRecipe(2, "Fixture recipe", 50, 19, 0, 2, true,
            [new(1, 4, 0, "Fixture ingredient")])], live.Source).Single();
        var recipeBatch = ContributionPayload.Batches([recipe]).Single();
        var recipeReceipt = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, accepted = 1, duplicates = 0,
            records = new[] { new { id = new string('e', 32), kind = "recipe", entityId = 2, status = "pending", duplicate = false } } });
        Check(ContributionClient.ValidReceipt(recipeReceipt, recipeBatch), "Recipe receipt was not acknowledged.");
        queue.Enqueue([recipe]);
        Check(new ContributionQueue(directory, Endpoint, credential.AccountId).Count == 2, "Recipe queue did not survive restart.");
        return Task.CompletedTask;
    }

    private static void LegacyUnicodeQueue(string directory)
    {
        var payload = JsonSerializer.SerializeToElement(new { itemId = 912, name = "Fixture Élan 雪 🛡" });
        var current = new ContributionRecord("item", 912, ContributionPayload.Fingerprint(payload), payload, new("Fixture", "Live"));
        var legacy = current with { Fingerprint = ContributionPayload.LegacyFingerprint(payload) };
        Check(legacy.Fingerprint != current.Fingerprint, "Unicode fixture must exercise the legacy encoding difference.");
        string pendingDirectory = Path.Combine(directory, "pending"), acknowledgedDirectory = Path.Combine(directory, "acknowledged");
        var pendingQueue = new ContributionQueue(pendingDirectory, Endpoint, new string('a', 32));
        pendingQueue.Enqueue([legacy]);
        pendingQueue = new(pendingDirectory, Endpoint, new string('a', 32));
        pendingQueue.Enqueue([current]);
        Check(pendingQueue.Count == 1 && pendingQueue.Next()!.Records.Single().Fingerprint == current.Fingerprint,
            "Old pending Unicode observation was lost or duplicated during upgrade.");
        var acknowledgedQueue = new ContributionQueue(acknowledgedDirectory, Endpoint, new string('a', 32));
        acknowledgedQueue.Enqueue([legacy]);
        acknowledgedQueue.Acknowledge(new([legacy], []));
        acknowledgedQueue = new(acknowledgedDirectory, Endpoint, new string('a', 32));
        acknowledgedQueue.Enqueue([current]);
        Check(acknowledgedQueue.Count == 0, "Old acknowledgement history resent unchanged Unicode data.");
        acknowledgedQueue.Enqueue([current with { Source = new("Fixture", "Beta", "1", new string('a', 64), "fixture") }]);
        Check(acknowledgedQueue.Count == 1, "Legacy Live history incorrectly suppressed Beta evidence.");
    }

    private static async Task UploadAndRestart(string directory)
    {
        int sent = 0;
        var client = new ContributionClient(directory, new Handler(async (request, cancel) =>
        {
            Interlocked.Increment(ref sent);
            Check(request.RequestUri!.AbsoluteUri == Endpoint && request.Method == HttpMethod.Post, "Wrong contribution endpoint or method.");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Token, "Missing bearer authorization.");
            Check(!request.Headers.Contains("Cookie"), "Upload included browser identity.");
            Check(request.Content!.Headers.ContentType?.MediaType == "application/json", "Upload requires JSON content type.");
            return await Receipt(request, cancel);
        }));
        await client.Ready;
        await client.Enqueue([Record()]); Check(sent == 0 && !client.Settings.Enabled, "Sending must be opt-in.");
        await client.ConfigureAsync(true, Endpoint, Token);
        await client.Enqueue([Record()]); await Until(() => sent == 1 && client.Status.PendingCount == 0);
        Check(client.Status.LastUploadAtUtc is not null, "Successful receipt did not record upload time.");
        await Stop(client);
        var resumed = new ContributionClient(directory, new Handler((_, _) => throw new InvalidOperationException("Acknowledged record was resent after restart.")));
        await resumed.Ready; await resumed.Enqueue([Record()]); await Task.Delay(30);
        Check(resumed.Status.PendingCount == 0, "Acknowledged record reentered queue.");
        await resumed.ClearAsync(); Check(!resumed.Settings.Enabled && !File.Exists(Path.Combine(directory, "key.dpapi")), "Clear must remove credentials and disable uploads.");
        await Stop(resumed);
        foreach (string json in Directory.GetFiles(directory, "*.json"))
            Check(!File.ReadAllText(json).Contains(Token, StringComparison.Ordinal), "Plain JSON contains the API key.");
    }

    private static async Task StopsAndRetry(string directory)
    {
        foreach (int code in new[] { 401, 400, 413, 415, 302, 200 })
        {
            int sent = 0;
            var client = new ContributionClient(Path.Combine(directory, code.ToString()), new Handler((_, _) =>
            {
                Interlocked.Increment(ref sent);
                return Task.FromResult(code == 200 ? JsonResponse(new { schemaVersion = 1, accepted = 1, duplicates = 0, records = Array.Empty<object>() })
                    : new HttpResponseMessage((HttpStatusCode)code));
            }));
            await client.Ready; await client.ConfigureAsync(true, Endpoint, Token); await client.Enqueue([Record()]);
            await Until(() => client.Status.NeedsAttention);
            Check(sent == 1 && client.Status.PendingCount == 1 && client.Recent.Length == 0, "Permanent failures must stop without acknowledging data or claiming a recent delivery.");
            await client.Enqueue([Record()]); Check(client.Status.NeedsAttention, "Duplicate capture masked the actionable failure.");
            await Stop(client);
            Check(new ContributionQueue(Path.Combine(directory, code.ToString()), Endpoint, new string('a', 32)).Count == 1, "Failed upload was not durable.");
        }
    }

    private static async Task TransientRetry(string directory)
    {
        int sent = 0;
        var waits = new List<TimeSpan>();
        var client = new ContributionClient(directory, new Handler((request, cancel) =>
        {
            int count = Interlocked.Increment(ref sent);
            if (count == 1) throw new HttpRequestException("Fixture network interruption");
            return count == 2 ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : Receipt(request, cancel);
        }), (wait, token) => { lock (waits) waits.Add(wait); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await client.Ready; await client.ConfigureAsync(true, Endpoint, Token); await client.Enqueue([Record()]);
        await Until(() => sent == 3 && client.Status.PendingCount == 0);
        Check(waits.Any(wait => wait.TotalSeconds >= 4) && waits.All(wait => wait.TotalSeconds <= 300), "Transient backoff was not bounded.");
        await Stop(client);
        Check(ContributionClient.Backoff(20).TotalSeconds <= 300, "Long retry sequence exceeded backoff bound.");
        Check(ContributionClient.Backoff(1, TimeSpan.FromMinutes(10)) >= TimeSpan.FromMinutes(10), "Server Retry-After minimum was shortened.");
    }

    private static async Task RateLimit(string directory)
    {
        int sent = 0;
        var client = new ContributionClient(directory, new Handler((_, _) =>
        {
            Interlocked.Increment(ref sent);
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }));
        await client.Ready; await client.ConfigureAsync(true, Endpoint, Token); await client.Enqueue([Record()]);
        await Until(() => client.Status.Message.Contains("rate limit", StringComparison.Ordinal));
        client.Retry(); await Task.Delay(60); Check(sent == 1, "Manual retry bypassed server throttling.");
        await Stop(client);
        var queue = new ContributionQueue(directory, Endpoint, new string('a', 32));
        Check(queue.RetryNotBeforeUtc > DateTimeOffset.UtcNow.AddMinutes(9), "Server throttle was not persisted.");
        var restarted = new ContributionClient(directory, new Handler((_, _) => throw new InvalidOperationException("Restart bypassed persisted throttling.")));
        await restarted.Ready; await Task.Delay(40); Check(restarted.Status.PendingCount == 1, "Restart lost throttled data.");
        await Stop(restarted);
    }

    private static async Task LockAndScope(string directory)
    {
        var owner = new ContributionClient(directory, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        await owner.Ready;
        var other = new ContributionClient(directory, new Handler((_, _) => throw new InvalidOperationException("Second sender transmitted.")));
        await other.Ready; Check(other.Status.NeedsAttention, "Second instance acquired the writer lease.");
        bool denied = false; try { await other.ConfigureAsync(true, Endpoint, Token); } catch (IOException) { denied = true; }
        Check(denied, "Second instance could overwrite settings."); await Stop(other);
        await owner.ConfigureAsync(true, Endpoint, Token);
        var captureSettings = owner.Settings;
        await owner.ConfigureAsync(true, Endpoint, OtherToken);
        await owner.Enqueue([Record()], expectedSettings: captureSettings);
        Check(owner.Status.PendingCount == 0, "Old capture crossed an account configuration boundary.");
        await Stop(owner);
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static async Task StalledBody(string directory)
    {
        int sent = 0;
        var client = new ContributionClient(directory, new Handler((request, cancel) =>
        {
            if (Interlocked.Increment(ref sent) > 1) return Receipt(request, cancel);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
            response.Content.Headers.ContentType = new("application/json"); return Task.FromResult(response);
        }), (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }) { RequestTimeout = TimeSpan.FromMilliseconds(50) };
        await client.Ready; await client.ConfigureAsync(true, Endpoint, Token); await client.Enqueue([Record()]);
        await Until(() => sent == 2 && client.Status.PendingCount == 0);
        await Stop(client);
    }
}
