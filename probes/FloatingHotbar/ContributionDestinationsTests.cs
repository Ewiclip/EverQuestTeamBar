using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TeamBar;

internal static class ContributionDestinationsTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => send(request, cancel);
    }
    private static ContributionRecord Record(int id)
    {
        var payload = JsonSerializer.SerializeToElement(new { itemId = id, name = "Destination fixture " + id });
        return new("item", id, ContributionPayload.Fingerprint(payload), payload, new("Destination fixture", "Live"));
    }
    private static async Task Until(Func<bool> predicate)
    {
        long deadline = Environment.TickCount64 + 6000;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline) throw new InvalidOperationException("Destination fixture timed out.");
            await Task.Delay(10);
        }
    }
    internal static async Task Run(string directory, Action<bool, string> check)
    {
        string dkpEndpoint = "https://dkp-destination.invalid/api/contributions/v1/catalog";
        string eqEndpoint = "https://tradeskills-destination.invalid/api/contributions/v1/catalog";
        string dkpToken = "dkpc_" + new string('a', 32) + "_" + new string('A', 43);
        string eqToken = "dkpc_" + new string('b', 32) + "_" + new string('B', 43);
        var legacy = new ContributionClient(directory, new Handler((_, _) => throw new InvalidOperationException("Disabled fixture sent a request.")));
        try { await legacy.Ready; await legacy.ConfigureAsync(false, dkpEndpoint, dkpToken); }
        finally { legacy.Dispose(); await legacy.Completion; }
        new ContributionQueue(directory, dkpEndpoint, new string('a', 32)).Enqueue([Record(1)]);
        int dkpRequests = 0, eqRequests = 0, allowDkp = 0;
        var manager = new ContributionDestinations(directory, destination => new Handler(async (request, cancel) =>
        {
            bool dkp = destination == "dkp";
            if (dkp) Interlocked.Increment(ref dkpRequests); else Interlocked.Increment(ref eqRequests);
            if (request.RequestUri!.AbsoluteUri != (dkp ? dkpEndpoint : eqEndpoint)
                || request.Headers.Authorization?.Parameter != (dkp ? dkpToken : eqToken))
                throw new InvalidOperationException("A destination received another website's endpoint or credential.");
            if (dkp && Volatile.Read(ref allowDkp) == 0) return new(HttpStatusCode.Unauthorized);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancel));
            var records = body.RootElement.GetProperty("items").EnumerateArray().Select((item, index) => new
            {
                id = (index + 1).ToString("x32"), kind = "item", entityId = item.GetProperty("itemId").GetInt32(), status = "pending", duplicate = false
            }).ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { schemaVersion = 1, accepted = records.Length, duplicates = 0, records }), Encoding.UTF8, "application/json") };
        }));
        try
        {
            await manager.Ready;
            var dkp = manager.All.Single(d => d.Id == "dkp").Client;
            var eq = manager.All.Single(d => d.Id == "eqtradeskills").Client;
            check(dkp.Settings is { Enabled: false, HasApiKey: true } && dkp.Status.PendingCount == 1
                && eq.Settings is { Enabled: false, HasApiKey: false } && eq.Status.PendingCount == 0,
                "destination manager restores legacy DKP credentials and pending history without enabling a second site");
            await dkp.ConfigureAsync(true, dkpEndpoint, null);
            await Until(() => dkp.Status.NeedsAttention);
            await eq.ConfigureAsync(true, eqEndpoint, eqToken);
            var routes = manager.Snapshot();
            var failures = await manager.Enqueue([Record(2)], routes, CancellationToken.None);
            await Until(() => eq.Status.LastUploadAtUtc is not null && eq.Status.PendingCount == 0);
            check(failures.Length == 0 && dkp.Status.PendingCount == 2 && dkp.Status.NeedsAttention
                && eqRequests == 1 && dkpRequests == 1,
                "a rejected DKP key keeps its pending data while EQTradeskills receives its independent copy");
            check(File.Exists(Path.Combine(directory, "key.dpapi")) && File.Exists(Path.Combine(directory, "EQTradeskills", "key.dpapi")),
                "each destination has its own protected credential file");
            var eqSettings = eq.Settings;
            await dkp.ConfigureAsync(false, dkpEndpoint, null);
            await manager.Enqueue([Record(3)], routes, CancellationToken.None);
            await Until(() => eqRequests == 2 && eq.Status.PendingCount == 0);
            check(dkp.Status.PendingCount == 2 && ReferenceEquals(eqSettings, eq.Settings) && manager.Snapshot().Length == 1,
                "changing DKP settings invalidates only its captured route and leaves the other website active");
            await eq.ClearAsync();
            check(!eq.Settings.Enabled && !eq.Settings.HasApiKey && dkp.Settings.HasApiKey && dkp.Status.PendingCount == 2,
                "clearing one destination cannot remove another destination's credential or queue");
            await eq.ConfigureAsync(true, eqEndpoint, eqToken);
            await manager.Enqueue([Record(2), Record(3)], manager.Snapshot(), CancellationToken.None);
            await Task.Delay(80);
            check(eqRequests == 2 && eq.Status.PendingCount == 0, "reconnecting the same website/account preserves its acknowledged history");
            await dkp.ConfigureAsync(true, dkpEndpoint, null);
            await Until(() => dkp.Status.NeedsAttention && dkpRequests == 2);
            Volatile.Write(ref allowDkp, 1); dkp.Retry();
            await Until(() => dkp.Status.PendingCount == 0 && !dkp.Status.IsSending);
            check(dkpRequests == 3 && eqRequests == 2, "retrying one destination delivers its retained records without resending to another website");
        }
        finally { manager.Dispose(); await manager.Completion; }
        using var dkpLease = new FileStream(Path.Combine(directory, "sender.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var eqLease = new FileStream(Path.Combine(directory, "EQTradeskills", "sender.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        check(dkpLease.CanWrite && eqLease.CanWrite, "closing the manager releases both independent sender leases");
    }
}
