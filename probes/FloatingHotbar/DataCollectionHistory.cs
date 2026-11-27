internal sealed record InspectedObservation(string Key, ItemData Item, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, ItemSpellReference[]? Effects = null)
{
    public string Name => Item.Name;
    public int ItemId => Item.ItemId;
    public string LastSeen => LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
}
internal sealed record NpcDropObservation(string Key, string List, uint LootId, uint? CorpseId, string NpcName,
    int ItemId, string ItemName, int? Quantity, ObservedZone? Zone, ObservedZone? ObservationZone, int? Level,
    string Evidence, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc)
{
    public string ZoneLabel => Zone?.Name ?? "Unknown";
    public string LevelLabel => Level?.ToString() ?? "Unknown";
    public string QuantityLabel => Quantity?.ToString() ?? "Unknown";
    public string LastSeen => LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
    public string SourceLabel => CorpseId is null ? "No source association" : $"Corpse {CorpseId} · {List} loot";
}
internal sealed record InspectionHistorySnapshot(int SchemaVersion, Guid SessionId, string SourceKind,
    string CharacterName, string BuildSha256, string Decoder, string DetailStatus, InspectedObservation[] Items, string ClientChannel = "Live");
internal sealed record DropHistorySnapshot(int SchemaVersion, Guid SessionId, string SourceKind, string CharacterName,
    string BuildSha256, string Decoder, NpcDropObservation[] Drops, string Completeness, string ClientChannel = "Live");

// History belongs to one selected client for one viewer session. It does not count encounters or drops globally.
internal sealed partial class DataCollectionHistory
{
    internal Guid SessionId { get; } = Guid.NewGuid();
    private readonly Dictionary<string, InspectedObservation> inspections = [];
    private readonly Dictionary<string, NpcDropObservation> drops = [];
    private readonly Dictionary<string, string> activeLootKeys = [];
    private int zoneEpoch, generation;
    private int? priorZone;
    private (string Channel, string Build, string Character)? source;
    private string? inspectionDecoder, inspectionDetailStatus, lootDecoder;
    internal bool HasInspectionSource => source is not null && inspectionDecoder is not null;
    internal bool HasLootSource => source is not null && lootDecoder is not null;
    internal InspectedObservation[] Inspections => inspections.Values.OrderBy(i => i.Item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    internal NpcDropObservation[] Drops => drops.Values.OrderByDescending(d => d.LastSeenUtc).ThenBy(d => d.NpcName).ThenBy(d => d.ItemName).ToArray();
    internal void Accept(InspectionCapture capture)
    {
        if (string.IsNullOrWhiteSpace(capture.DetailStatus) || inspectionDetailStatus is not null && inspectionDetailStatus != capture.DetailStatus)
            throw new InvalidDataException("Inspection detail provenance changed. Start a new viewer session.");
        AcceptSource(capture.ClientChannel, capture.BuildSha256, capture.CharacterName, capture.Decoder, inspectionDecoder);
        inspectionDecoder = capture.Decoder; inspectionDetailStatus = capture.DetailStatus;
        foreach (var display in capture.Items)
        {
            string key = display.Effects is null ? CollectorFormat.Hash(display.Item) : CollectorFormat.Hash(new { display.Item, display.Effects });
            inspections[key] = inspections.TryGetValue(key, out var old)
                ? old with { LastSeenUtc = capture.ObservedAtUtc }
                : new(key, display.Item, capture.ObservedAtUtc, capture.ObservedAtUtc, display.Effects);
        }
    }
    internal void Accept(AdvancedLootCapture capture)
    {
        AcceptSource(capture.ClientChannel, capture.BuildSha256, capture.CharacterName, capture.Decoder, lootDecoder);
        lootDecoder = capture.Decoder;
        var observationZone = capture.Context?.ObservationZone;
        if (observationZone is not null && priorZone != observationZone.Id)
        { zoneEpoch++; activeLootKeys.Clear(); priorZone = observationZone.Id; }
        var present = new HashSet<string>();
        foreach (var item in capture.Items)
        {
            LootSource?[] sources = item.Sources.Length == 0 ? new LootSource?[] { null } : item.Sources;
            foreach (var source in sources)
            {
                string identity = $"{zoneEpoch}:{item.List}:{item.LootId}:{source?.CorpseId}:{item.ItemId}";
                present.Add(identity);
                if (!activeLootKeys.TryGetValue(identity, out string? key)) activeLootKeys[identity] = key = $"{identity}:{++generation}";
                var matched = source is null ? null : capture.Context?.Npcs.SingleOrDefault(n => n.CorpseId == source.CorpseId && n.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
                string name = source?.Name ?? "Unknown source";
                var next = new NpcDropObservation(key, item.List, item.LootId, source?.CorpseId, name,
                    item.ItemId, item.Name, source?.StackCount, matched?.Zone, observationZone, matched?.Level,
                    matched is not null ? "direct-loot-source-and-matched-corpse" : source?.Evidence ?? "unknown",
                    capture.ObservedAtUtc, capture.ObservedAtUtc);
                if (drops.TryGetValue(key, out var old)) next = next with
                {
                    FirstSeenUtc = old.FirstSeenUtc,
                    // The remaining stack may shrink while looting; retain the largest observed quantity, never sum polls.
                    Quantity = old.Quantity is null ? next.Quantity : next.Quantity is null ? old.Quantity : Math.Max(old.Quantity.Value, next.Quantity.Value),
                    Zone = next.Zone ?? old.Zone, Level = next.Level ?? old.Level,
                    Evidence = matched is not null || old.Zone is null ? next.Evidence : old.Evidence
                };
                drops[key] = next;
            }
        }
        foreach (string missing in activeLootKeys.Keys.Except(present).ToArray()) activeLootKeys.Remove(missing);
    }
    private void AcceptSource(string channel, string build, string character, string decoder, string? previousDecoder)
    {
        if (channel is not ("Live" or "Beta") || build is null || build.Length != 64 || !build.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(decoder)
            || previousDecoder is not null && previousDecoder != decoder)
            throw new InvalidDataException("Invalid or changed observation provenance. Start a new viewer session.");
        var next = (channel, build, character);
        if (source is not null && source.Value != next)
            throw new InvalidDataException("Observations from different channels, builds or characters cannot share viewer history.");
        source = next;
    }
    private (string Channel, string Build, string Character) SnapshotSource(string character, string? decoder)
    {
        if (source is not { } captured || captured.Character != character || decoder is null)
            throw new InvalidDataException("No matching observation source is available for export.");
        return captured;
    }
    internal InspectionHistorySnapshot InspectionSnapshot(string character)
    {
        var captured = SnapshotSource(character, inspectionDecoder);
        return new(1, SessionId, "item-display-session", captured.Character, captured.Build, inspectionDecoder!,
            inspectionDetailStatus!, Inspections, captured.Channel);
    }
    internal DropHistorySnapshot DropSnapshot(string character)
    {
        var captured = SnapshotSource(character, lootDecoder);
        return new(1, SessionId, "npc-drops-session", captured.Character, captured.Build, lootDecoder!,
            Drops, "Observed loot associations only. Quantity is the largest stack count observed for a continuously present entry. Repeated polls are merged; an entry seen again after absence is a new observation. Server/instance identity, cross-client deduplication, drop rates and kills without drops are not recorded.", captured.Channel);
    }
}
