internal sealed record ObservedZone(int Id, string ShortName, string Name);
internal sealed record MatchedLootNpc(uint CorpseId, string Name, int Level, ObservedZone Zone);
internal sealed record LootContext(ObservedZone ObservationZone, MatchedLootNpc[] Npcs);

internal static class LootContextDecoder
{
    internal const ulong ZoneHeader = 0xebbce4;
    internal const int MaxSpawns = 4096;
    internal static LootContext Decode(Func<ulong, int, byte[]> read, ulong image, LootSource[] sources, TargetMemoryLayout? layout = null)
    {
        layout ??= TargetMemoryLayout.Legacy;
        var r = new ObservedSurfaceReader(read, image);
        var identity = Identity.Decode(r.Read, image, layout.Identity);
        byte[] zoneBytes = r.Read(image + layout.ZoneHeader, 0x188);
        int zoneId = BitConverter.ToInt32(zoneBytes, 0x184);
        var zone = new ObservedZone(zoneId, ObservedSurfaceReader.Text(zoneBytes, 0, 128), ObservedSurfaceReader.Text(zoneBytes, 128, 128));
        if (zoneId is < 1 or > 32767 || zone.ShortName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Zone context failed validation.");
        // Cross-check the code-derived spawn level against the active player profile.
        if (r.Read(identity.Player + layout.SpawnLevel, 1)[0] != identity.Character.Level)
            throw new InvalidDataException("Spawn level and profile level disagree.");
        var wanted = sources.GroupBy(s => s.CorpseId).ToDictionary(g => g.Key, g => g.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var found = new List<MatchedLootNpc>();
        if (wanted.Count > 0)
        {
            ulong node = identity.Player, expectedVtable = r.U64(identity.Player);
            if (expectedVtable != image + layout.SpawnVtable) throw new InvalidDataException("Unsupported spawn type.");
            var visited = new HashSet<ulong>();
            while (true)
            {
                if (!visited.Add(node) || visited.Count > MaxSpawns) throw new InvalidDataException("Spawn list exceeded its bounds.");
                byte[] link = r.Read(node, 24);
                if (BitConverter.ToUInt64(link) != expectedVtable) throw new InvalidDataException("Spawn list type changed.");
                ulong previous = BitConverter.ToUInt64(link, 16);
                if (previous == 0) break;
                previous = ObservedSurfaceReader.Pointer(previous);
                if (r.U64(previous + 8) != node) throw new InvalidDataException("Spawn list is changing.");
                node = previous;
            }
            visited.Clear(); ulong prior = 0;
            var matchedIds = new HashSet<uint>();
            while (node != 0)
            {
                ObservedSurfaceReader.Pointer(node);
                if (!visited.Add(node) || visited.Count > MaxSpawns) throw new InvalidDataException("Spawn list exceeded its bounds.");
                byte[] link = r.Read(node, 24);
                if (BitConverter.ToUInt64(link) != expectedVtable || BitConverter.ToUInt64(link, 16) != prior)
                    throw new InvalidDataException("Spawn list changed during context reading.");
                uint id = unchecked((uint)r.I32(node + layout.SpawnId));
                if (wanted.TryGetValue(id, out var names))
                {
                    byte[] nameBytes = r.Read(node + layout.DisplayName, 64);
                    string name = ObservedSurfaceReader.Text(nameBytes, 0, 64);
                    byte type = r.Read(node + layout.Identity.PlayerType, 1)[0], level = r.Read(node + layout.SpawnLevel, 1)[0];
                    // ID AND source name AND corpse type must agree. Never match another NPC by name alone.
                    string? match = names.SingleOrDefault(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)
                        || name.Equals(n + "'s corpse", StringComparison.OrdinalIgnoreCase));
                    if (type == 2 && match is not null && level is > 0 and <= 255)
                    {
                        if (!matchedIds.Add(id)) throw new InvalidDataException("Repeated corpse identity.");
                        found.Add(new(id, match, level, zone));
                    }
                    r.Unchanged(node + layout.DisplayName, nameBytes);
                    if (unchecked((uint)r.I32(node + layout.SpawnId)) != id || r.Read(node + layout.Identity.PlayerType, 1)[0] != type || r.Read(node + layout.SpawnLevel, 1)[0] != level)
                        throw new InvalidDataException("Loot source spawn changed.");
                }
                r.Unchanged(node, link); prior = node; node = BitConverter.ToUInt64(link, 8);
            }
        }
        r.Unchanged(image + layout.ZoneHeader, zoneBytes);
        if (Identity.Decode(r.Read, image, layout.Identity) != identity) throw new InvalidDataException("Character changed during zone reading.");
        return new(zone, found.OrderBy(n => n.CorpseId).ToArray());
    }
}
