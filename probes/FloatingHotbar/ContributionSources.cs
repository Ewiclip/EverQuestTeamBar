using System.Globalization;
using System.Text.Json;

namespace TeamBar;

// These records describe source existence, never encounter counts or drop rates.
internal static class ContributionKinds
{
    internal static readonly string[] All = ["item", "spell", "recipe", "npc", "drop", "foraged", "fished"];
    internal static bool IsSource(string kind) => kind is "npc" or "drop" or "foraged" or "fished";
    internal static string ArrayName(string kind) => kind is "foraged" or "fished" ? kind : kind + "s";
    // Source observations have no global NPC/encounter ID. Use a deterministic
    // receipt routing key; the full fingerprint remains the deduplication key.
    internal static int SourceId(JsonElement payload) => Math.Max(1,
        (int)(uint.Parse(ContributionPayload.Fingerprint(payload)[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0x7fffffff));
}

internal static class ContributionSources
{
    private sealed record ZoneWire(int? Id, string? ShortName, string Name);
    private sealed record NpcWire(string Name, ZoneWire Zone, int Level, string Evidence);
    private sealed record DropWire(string Name, int ItemId, ZoneWire Zone, string NpcName, int? Level, string Evidence);
    private sealed record GatheringWire(string Name, ZoneWire Zone, string Evidence);

    private static string Text(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')
            || text.Any(char.IsSurrogate)) throw new InvalidDataException("Source text is invalid.");
        return text.Trim();
    }
    private static ZoneWire Zone(ObservedZone zone)
    {
        if (zone.Id is < 1 or > 32767 || zone.ShortName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("The source zone is not verified.");
        return new(zone.Id, Text(zone.ShortName, 128), Text(zone.Name, 128));
    }
    private static ContributionRecord Record<T>(string kind, T wire, ContributionSource source)
    {
        if (source.ClientChannel != "Live") throw new InvalidDataException("NPC and item-source sharing is currently Live-only.");
        var payload = JsonSerializer.SerializeToElement(wire, ContributionPayload.Json);
        return new(kind, ContributionKinds.SourceId(payload), ContributionPayload.Fingerprint(payload), payload, ContributionPayload.NormalizeSource(source));
    }
    internal static ContributionRecord[] Npc(TargetCapture capture)
    {
        if (capture.ClientChannel != "Live" || capture.Target is not { RawType: 1, Type: "NPC", Level: > 0 and <= 255 } npc) return [];
        return [Record("npc", new NpcWire(Text(npc.Name, 64), Zone(capture.Zone), npc.Level, "selected-npc"),
            new("Team Bar", capture.ClientChannel, "1", capture.BuildSha256, capture.Decoder))];
    }
    internal static ContributionRecord[] Drops(IEnumerable<NpcDropObservation> observations, ContributionSource source)
    {
        if (source.ClientChannel != "Live") return [];
        var records = new List<ContributionRecord>();
        foreach (var row in observations)
        {
            // A current observation zone or matching name alone is insufficient.
            if (row.Evidence != "direct-loot-source-and-matched-corpse" || row.Zone is null || row.CorpseId is null
                || row.ItemId is < 1 or > 10_000_000 || row.Level is < 1 or > 255) continue;
            records.Add(Record("drop", new DropWire(Text(row.ItemName, 255), row.ItemId, Zone(row.Zone),
                Text(row.NpcName, 64), row.Level, "matched-corpse"), source));
        }
        return records.DistinctBy(ContributionPayload.RecordKey).ToArray();
    }
    internal static ContributionRecord Gathering(GatheringObservation row, ContributionSource source)
    {
        if (row.Kind is not ("foraged" or "fished")) throw new InvalidDataException("Unknown gathering source.");
        return Record(row.Kind, new GatheringWire(Text(row.Name, 255), Zone(row.Zone), "success-log-and-zone"), source);
    }
}
