using System.Globalization;
using System.Text.Json;
namespace TeamBar;

internal static class ContributionSourcesTests
{
    internal static ContributionRecord[] Run(string directory)
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var now = DateTimeOffset.Now;
        var zone = new ObservedZone(123, "vortex", "The Vortex");
        var source = new ContributionSource("Source fixture", "Live", "1", new string('a', 64), "synthetic");
        var target = new TargetCapture(1, Guid.NewGuid(), now, "Live", new string('a', 64), "synthetic", "selected-target",
            999, 123456789, "PRIVATE_CHARACTER_SENTINEL", "PRIVATE_CLASS_SENTINEL", zone, new(999, "a cotton veilwing", 128, 1, "NPC"), "private");
        var npc = ContributionSources.Npc(target).Single();
        foreach (var type in new[] { (0, "Player"), (2, "Corpse") })
            Check(ContributionSources.Npc(target with { Target = target.Target! with { RawType = (byte)type.Item1, Type = type.Item2 } }).Length == 0, "Only classified NPCs qualify");
        Check(ContributionSources.Npc(target with { ClientChannel = "Beta" }).Length == 0, "Beta source reading is not validated");
        Check(ContributionSources.Npc(target with { Target = target.Target! with { Level = 129 } }).Single().Fingerprint != npc.Fingerprint, "Observed level range must retain both levels");
        var drop = new NpcDropObservation("private", "shared", 345, 999, "a cotton veilwing", 120780, "Riven Arcana Imbued Hide", 2,
            zone, zone, 128, "direct-loot-source-and-matched-corpse", now, now);
        var drops = ContributionSources.Drops([drop, drop with { Quantity = 1, LastSeenUtc = now.AddSeconds(2) }], source);
        Check(drops.Length == 1, "Repeated polls/quantity changes must not create new source associations");
        Check(ContributionSources.Drops([drop with { Zone = null }, drop with { Evidence = "direct-loot-entry-source-record" }], source).Length == 0, "Unmatched zones must not become proven sources");
        var records = new[] { npc with { Source = source }, drops[0],
            ContributionSources.Gathering(new("foraged", "Pod of Water", zone, now), source),
            ContributionSources.Gathering(new("fished", "Fish Scales", zone, now), source) };
        string body = System.Text.Encoding.UTF8.GetString(ContributionPayload.Batches(records).Single().Body);
        foreach (string forbidden in new[] { "PRIVATE_", "corpseId", "spawnId", "quantity", "SeenAt", "LootId", "ProcessId" })
            Check(!body.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Private context or statistics entered source payload: " + forbidden);
        Check(records.All(r => r.EntityId == ContributionKinds.SourceId(r.Payload)), "Receipt identities must be deterministic");
        var old = JsonSerializer.Deserialize<ContributionDataSelection>("{\"items\":true,\"spells\":true,\"recipes\":true}", ContributionStorage.Json)!;
        Check(!old.Npcs && !old.Drops && !old.Foraged && !old.Fished, "Upgrade must not opt into new data types");
        string path = Path.Combine(directory, "eqlog_Fixture_server.txt");
        Check(GatheringLogReader.MatchesClientLog(@"C:\Game\Logs\eqlog_Fixture_server.txt", @"C:\Game", "Fixture")
            && !GatheringLogReader.MatchesClientLog(@"C:\Beta\Logs\eqlog_Fixture_server.txt", @"C:\Game", "Fixture")
            && !GatheringLogReader.MatchesClientLog(@"C:\Game\Logs\eqlog_Other_server.txt", @"C:\Game", "Fixture"), "Manual logs must match both character and client installation");
        string Line(string message, DateTimeOffset? time = null) => $"[{(time ?? now).LocalDateTime.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)}] {message}";
        File.WriteAllText(path, Line("You caught Old Fish!") + "\n");
        var reader = new GatheringLogReader();
        Check(reader.Poll(path, zone, now).Length == 0, "Opening a log must not upload old history");
        File.AppendAllText(path, Line("You have scrounged up Pod of Water.") + "\n" + Line("You caught Fish Scales!") + "\n"
            + Line("Someone tells you, 'You caught Fake Fish!'") + "\n" + Line("You caught nothing!") + "\n");
        var observed = reader.Poll(path, zone, now.AddSeconds(1));
        Check(observed.Length == 2 && observed[0].Kind == "foraged" && observed[1].Kind == "fished", "Only exact successful gathering messages qualify");
        Check(reader.Poll(path, zone, now.AddSeconds(2)).Length == 0, "Rereading must not duplicate log observations");
        File.AppendAllText(path, Line("You caught Partial Fish!"));
        Check(reader.Poll(path, zone, now.AddSeconds(3)).Length == 0, "Incomplete lines must wait");
        File.AppendAllText(path, "\n");
        Check(reader.Poll(path, zone, now.AddSeconds(4)).Length == 1, "Completed partial lines must be read once");
        File.AppendAllText(path, Line("You have entered Another Zone.") + "\n" + Line("You caught Wrong Zone Fish!") + "\n");
        Check(reader.Poll(path, zone, now.AddSeconds(5)).Length == 0, "Zoning invalidates gathering attribution within the poll");
        File.AppendAllText(path, Line("You caught Still Uncertain Fish!") + "\n");
        Check(reader.Poll(path, zone, now.AddSeconds(5.5)).Length == 0, "A stale memory zone must not override an unmatched zone transition");
        File.AppendAllText(path, Line("You caught Stale Fish!", now.AddHours(-1)) + "\n");
        Check(reader.Poll(path, zone, now.AddSeconds(6)).Length == 0, "Historical successes must not use current zone");
        File.AppendAllText(path, Line("You caught Uncertain Fish!") + "\n");
        Check(reader.Poll(path, zone with { Id = 124 }, now.AddSeconds(7)).Length == 0, "Changed zone must not receive previous-zone entries");
        File.WriteAllText(path, Line("You caught Rotated Fish!") + "\n");
        Check(reader.Poll(path, zone, now.AddSeconds(8)).Length == 0, "Truncated logs must rebaseline");
        File.AppendAllText(path, Line("You caught Paused Fish!", now.AddMinutes(1)) + "\n");
        Check(reader.Poll(path, zone, now.AddMinutes(1)).Length == 0, "Polling interruptions must rebaseline");
        reader = new(); reader.Poll(path, zone, now);
        File.AppendAllText(path, Line("You caught Partial Wrong Zone Fish!"));
        reader.Poll(path, zone with { Id = 124 }, now.AddSeconds(1));
        File.AppendAllText(path, "\n");
        Check(reader.Poll(path, zone with { Id = 124 }, now.AddSeconds(2)).Length == 0, "Partial lines must not cross zone boundaries");
        return records;
    }
}
