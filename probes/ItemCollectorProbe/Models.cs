using System.Text.Json;
using System.Security.Cryptography;

internal static class CollectorFormat
{
    internal const int SchemaVersion = 1;
    internal const string Decoder = "live-inventory-research-1";
    internal const string DetailStatus = "candidate-client-definition-needs-display-review";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    internal static void WriteNew<T>(string path, T value)
    {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new IOException("Create the output directory first.");
        // Commit a complete file without replacing an earlier capture or catalog.
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            File.Move(temp, full, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Values from a client definition, not a claim about unaugmented/server-canonical stats.
// Keep these separate from the ID/name search index until an item-display check passes.
internal sealed record CandidateDetails(string LoreName, int IconId, uint EquipSlotsMask,
    uint ClassesMask, uint RacesMask, int RequiredLevel, int RecommendedLevel,
    bool Tradeskill, bool QuestItem, bool Droppable, bool Attuneable, bool Heirloom,
    int LoreGroup, int ArmorClass, int HitPoints, int Mana, int Endurance,
    int Damage, int Delay, int AugmentationType, int StackSize);
internal sealed record ItemData(int ItemId, string Name, CandidateDetails CandidateDetails);
internal sealed record InventoryEntry(string SlotPath, ItemData Item);
internal sealed record InventorySnapshot(CharacterIdentity Character, InventoryEntry[] Items);
internal sealed record InventoryCapture(int SchemaVersion, Guid CaptureId, DateTimeOffset ObservedAtUtc,
    string ClientChannel, string BuildSha256, string Decoder, string SourceKind, string DetailStatus,
    string CharacterName, string CharacterClass, InventoryEntry[] Items);
internal sealed record CatalogEvidence(Guid CaptureId, DateTimeOffset ObservedAtUtc, string SourceKind);
internal sealed record CatalogVersion(string Fingerprint, string Name, CandidateDetails CandidateDetails,
    string DetailStatus, string BuildSha256, string Decoder, CatalogEvidence[] Evidence);
internal sealed record CatalogItem(int ItemId, string Name, string[] Names, DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc, CatalogVersion[] Versions);
internal sealed record CatalogDocument(int SchemaVersion, string ClientChannel, string Scope,
    CatalogItem[] Items);

internal static class Catalog
{
    internal static CatalogDocument Build(IEnumerable<InventoryCapture> input)
    {
        var captures = input.ToArray();
        if (captures.Length == 0) throw new InvalidDataException("At least one capture is required.");
        var scope = captures[0] ?? throw new InvalidDataException("An inventory capture is missing.");
        var captureIds = new Dictionary<Guid, string>();
        foreach (var c in captures)
        {
            if (c is null || c.SchemaVersion != CollectorFormat.SchemaVersion
                || !(c.ClientChannel == "Live" && c.BuildSha256 == Identity.BuildHash && c.Decoder == CollectorFormat.Decoder
                    || c.ClientChannel == "Beta" && c.BuildSha256 == CollectorBuild.BetaBuildHash && c.Decoder == "beta-inventory-research-2")
                || c.ClientChannel != scope.ClientChannel || c.BuildSha256 != scope.BuildSha256 || c.Decoder != scope.Decoder
                || c.SourceKind != "inventory" || c.DetailStatus != CollectorFormat.DetailStatus
                || c.CaptureId == Guid.Empty || c.ObservedAtUtc.Offset != TimeSpan.Zero
                || c.ObservedAtUtc.Year < 2026 || c.Items is null || c.Items.Length > InventoryDecoder.MaxItems)
                throw new InvalidDataException("Unsupported or malformed inventory capture.");
            string hash = CollectorFormat.Hash(c);
            if (captureIds.TryGetValue(c.CaptureId, out string? prior) && prior != hash)
                throw new InvalidDataException("Conflicting captures use the same ID.");
            captureIds[c.CaptureId] = hash;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in c.Items)
            {
                if (entry is null || string.IsNullOrEmpty(entry.SlotPath) || !paths.Add(entry.SlotPath))
                    throw new InvalidDataException("Invalid or duplicated inventory slot.");
                InventoryDecoder.ValidateItem(entry.Item);
            }
        }
        var observations = captures.DistinctBy(c => c.CaptureId).SelectMany(c => c.Items.Select(e => (Capture: c, Item: e.Item)));
        var items = observations.GroupBy(x => x.Item.ItemId).OrderBy(g => g.Key).Select(group =>
        {
            var ordered = group.OrderBy(x => x.Capture.ObservedAtUtc).ThenBy(x => x.Capture.CaptureId).ToArray();
            var versions = ordered.GroupBy(x => CollectorFormat.Hash(new { x.Item, x.Capture.BuildSha256, x.Capture.Decoder }))
                .Select(v => new CatalogVersion(v.Key, v.First().Item.Name, v.First().Item.CandidateDetails,
                    CollectorFormat.DetailStatus, v.First().Capture.BuildSha256, v.First().Capture.Decoder,
                    v.Select(x => new CatalogEvidence(x.Capture.CaptureId, x.Capture.ObservedAtUtc, "inventory"))
                        .DistinctBy(e => e.CaptureId).OrderBy(e => e.ObservedAtUtc).ThenBy(e => e.CaptureId).ToArray()))
                .OrderBy(v => v.Fingerprint, StringComparer.Ordinal).ToArray();
            return new CatalogItem(group.Key, ordered[^1].Item.Name,
                ordered.Select(x => x.Item.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                ordered[0].Capture.ObservedAtUtc, ordered[^1].Capture.ObservedAtUtc, versions);
        }).ToArray();
        // No character names, process IDs, memory addresses, inventory slots, or asserted drop sources.
        return new(CollectorFormat.SchemaVersion, scope.ClientChannel, "observed-items-only", items);
    }
}
