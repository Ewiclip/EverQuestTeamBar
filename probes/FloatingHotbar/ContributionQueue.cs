using System.Security.Cryptography;
using System.Text;

namespace TeamBar;

internal sealed record ContributionQueueFile(int Version, string Endpoint, string AccountId,
    ContributionRecord[] Pending, string[] Acknowledged, DateTimeOffset? LastUploadAtUtc, DateTimeOffset? RetryNotBeforeUtc = null,
    ContributionDelivery[]? Recent = null);
internal sealed record ContributionReceipt(string Kind, int EntityId, string Id, string Status, bool Duplicate);
internal sealed record ContributionDelivery(DateTimeOffset ReceivedAtUtc, ContributionRecord Record, ContributionReceipt Receipt)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Name => Record.Payload.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() ?? "" : "";
    [System.Text.Json.Serialization.JsonIgnore] public string Kind => Record.Kind;
    [System.Text.Json.Serialization.JsonIgnore] public string When => ReceivedAtUtc.ToLocalTime().ToString("g");
    [System.Text.Json.Serialization.JsonIgnore] public string Result => Receipt.Duplicate ? "Already received" : "Received";
    [System.Text.Json.Serialization.JsonIgnore] private string ZoneName => Record.Payload.TryGetProperty("zone", out var zone)
        && zone.ValueKind == System.Text.Json.JsonValueKind.Object && zone.TryGetProperty("name", out var name)
        && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() ?? "Unknown zone" : "Unknown zone";
    [System.Text.Json.Serialization.JsonIgnore] public string Summary => ContributionKinds.IsSource(Kind)
        ? $"{Record.Source.ClientChannel} · {Kind} · {ZoneName} · {Result} · {Receipt.Status} at receipt"
        : $"{Record.Source.ClientChannel} · {Kind} {Record.EntityId} · {Result} · {Receipt.Status} at receipt";
}

internal sealed class ContributionQueue
{
    private readonly string path, endpoint, accountId;
    private readonly Dictionary<string, ContributionRecord> pending;
    private readonly HashSet<string> acknowledged;
    internal ContributionDelivery[] Recent { get; private set; } = [];
    internal int Count => pending.Count;
    internal DateTimeOffset? LastUploadAtUtc { get; private set; }
    internal DateTimeOffset? RetryNotBeforeUtc { get; private set; }
    internal static string Key(ContributionRecord record) => ContributionPayload.RecordKey(record);
    internal ContributionQueue(string directory, string endpoint, string accountId)
    {
        this.endpoint = endpoint; this.accountId = accountId;
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint + "\n" + accountId))).ToLowerInvariant();
        path = Path.Combine(directory, "queue-" + scope + ".json");
        var saved = ContributionStorage.Read<ContributionQueueFile>(path);
        if (saved is not null && (saved.Version != 1 || saved.Endpoint != endpoint || saved.AccountId != accountId
            || saved.Pending is null || saved.Acknowledged is null)) throw new InvalidDataException("The saved contribution queue cannot be opened safely.");
        if (saved?.Pending.Any(InvalidRecord) == true)
            throw new InvalidDataException("The saved contribution queue contains invalid observations.");
        if (saved?.Recent?.Any(entry => entry is null || InvalidRecord(entry.Record) || entry.Receipt is null
            || entry.Receipt.Kind != entry.Record.Kind || entry.Receipt.EntityId != entry.Record.EntityId
            || entry.Receipt.Status is not ("pending" or "approved" or "rejected") || entry.Receipt.Id is null
            || !System.Text.RegularExpressions.Regex.IsMatch(entry.Receipt.Id, @"\A[a-f0-9]{32}\z")) == true)
            throw new InvalidDataException("The saved delivery history contains an invalid receipt.");
        pending = (saved?.Pending ?? []).Select(UpgradeFingerprint).GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        acknowledged = new(saved?.Acknowledged ?? [], StringComparer.Ordinal);
        LastUploadAtUtc = saved?.LastUploadAtUtc;
        RetryNotBeforeUtc = saved?.RetryNotBeforeUtc;
        Recent = saved?.Recent?.Take(100).Select(entry => entry with { Record = UpgradeFingerprint(entry.Record) }).ToArray() ?? [];
    }
    private static bool InvalidRecord(ContributionRecord? record) => record is null || record.Source is null
        || record.Payload.ValueKind != System.Text.Json.JsonValueKind.Object || !ContributionKinds.All.Contains(record.Kind)
        || record.EntityId < 1 || record.Fingerprint is null
        || !System.Text.RegularExpressions.Regex.IsMatch(record.Fingerprint, @"\A[a-f0-9]{64}\z");
    private static ContributionRecord UpgradeFingerprint(ContributionRecord record)
    {
        string current = ContributionPayload.Fingerprint(record.Payload);
        if (record.Fingerprint != current && record.Fingerprint != ContributionPayload.LegacyFingerprint(record.Payload))
            throw new InvalidDataException("The saved contribution fingerprint does not match its observation.");
        return record with { Fingerprint = current };
    }
    private bool IsAcknowledged(ContributionRecord record) => acknowledged.Contains(Key(record))
        || acknowledged.Contains(Key(record with { Fingerprint = ContributionPayload.LegacyFingerprint(record.Payload) }));
    private void Save()
    {
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ContributionQueueFile(1, endpoint, accountId,
            pending.Values.ToArray(), acknowledged.ToArray(), LastUploadAtUtc, RetryNotBeforeUtc, Recent), ContributionStorage.Json);
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Contribution storage is full. Upload pending data before collecting more.");
        ContributionStorage.AtomicWrite(path, bytes);
    }
    internal void DeferUntil(DateTimeOffset deadline)
    {
        var before = RetryNotBeforeUtc;
        RetryNotBeforeUtc = before is { } old && old > deadline ? old : deadline;
        try { Save(); } catch { RetryNotBeforeUtc = before; throw; }
    }
    internal void Enqueue(IEnumerable<ContributionRecord> records)
    {
        // Keep only exporter's data-only records; capture metadata never enters storage.
        var additions = records.Where(record => !IsAcknowledged(record) && !pending.ContainsKey(Key(record)))
            .GroupBy(Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (pending.Count + additions.Length > 50000) throw new InvalidDataException("The contribution queue is full. Upload pending data before collecting more.");
        foreach (var record in additions) pending.Add(Key(record), record);
        try { if (additions.Length > 0) Save(); }
        catch { foreach (var record in additions) pending.Remove(Key(record)); throw; }
    }
    internal ContributionBatch? Next(ContributionDataSelection? selection = null) => ContributionPayload.NextBatch(pending.Values.Where(r => selection?.Includes(r.Kind) ?? true));
    internal void Acknowledge(ContributionBatch batch, ContributionReceipt[]? receipts = null)
    {
        var previousTime = LastUploadAtUtc;
        var previousDelay = RetryNotBeforeUtc;
        var previousRecent = Recent;
        var previousAcknowledged = acknowledged.Count + batch.Records.Length > 100000 ? acknowledged.ToArray() : null;
        var removed = batch.Records.Where(record => pending.ContainsKey(Key(record))).ToArray();
        var receivedAt = DateTimeOffset.UtcNow;
        var deliveries = receipts is null ? [] : removed.Select(record => new ContributionDelivery(receivedAt, record,
            receipts.Single(receipt => receipt.Kind == record.Kind && receipt.EntityId == record.EntityId))).ToArray();
        var nextRecent = Recent;
        if (receipts is not null)
        {
            nextRecent = deliveries.Concat(Recent).Take(100).ToArray();
            // Retain inspectable payloads without allowing history to crowd out
            // the pending queue. A single accepted batch is at most 2 MiB.
            while (nextRecent.Length > 0 && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(nextRecent, ContributionStorage.Json).Length > 4 * 1024 * 1024)
                nextRecent = nextRecent[..^1];
        }
        foreach (var record in removed) { pending.Remove(Key(record)); acknowledged.Add(Key(record)); }
        LastUploadAtUtc = receivedAt;
        Recent = nextRecent;
        RetryNotBeforeUtc = null;
        // Older fingerprints can be safely submitted again: the server deduplicates
        // them. Bound local history so it cannot eventually make the queue unreadable.
        if (acknowledged.Count > 100000)
            foreach (string key in acknowledged.Take(acknowledged.Count - 100000).ToArray()) acknowledged.Remove(key);
        try { Save(); }
        catch
        {
            LastUploadAtUtc = previousTime;
            RetryNotBeforeUtc = previousDelay;
            Recent = previousRecent;
            foreach (var record in removed) { acknowledged.Remove(Key(record)); pending[Key(record)] = record; }
            if (previousAcknowledged is not null) { acknowledged.Clear(); acknowledged.UnionWith(previousAcknowledged); }
            throw;
        }
    }
}
