internal sealed record ObservedTarget(uint SpawnId, string Name, int Level, byte RawType, string Type);
internal sealed record TargetState(CharacterIdentity Character, ObservedZone Zone, ObservedTarget? Target);
internal sealed record TargetCapture(int SchemaVersion, Guid CaptureId, DateTimeOffset ObservedAtUtc,
    string ClientChannel, string BuildSha256, string Decoder, string SourceKind,
    int ProcessId, long ProcessStartedUtcTicks, string CharacterName, string CharacterClass,
    ObservedZone Zone, ObservedTarget? Target, string Completeness, TargetBuffCapture? Buffs = null, string? BuffsError = null);

// Passive, one-shot research reader. Targeting is observation evidence, never loot/kill evidence.
internal static class TargetDecoder
{
    internal const ulong TargetRoot = 0xeb64e8, SpawnVtable = 0x967808;
    internal static TargetCapture Capture(int pid, long? expectedStartedUtcTicks = null, MemoryFeature feature = MemoryFeature.DataCollection)
    {
        using var session = new TargetLayoutResolver.Session(pid, expectedStartedUtcTicks, feature);
        var memory = session.Memory;
        var state = ObservedSurfaceReader.Stable(() => Decode(memory.Read, memory.Base, session.Target));
        session.Verify();
        return new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Live", session.Resolved.Image.Hash, "live-selected-target-resolved-1",
            "selected-target", memory.ProcessId, memory.StartedUtcTicks, state.Character.Name, state.Character.Class,
            state.Zone, state.Target,
            "Current selected spawn only. Spawn IDs are session-scoped, not NPC database IDs. NPC type may include pets or other non-player entities; corpse type does not identify the original player/NPC. No distance, coordinates, loot attribution or kill count is collected.");
    }
    internal static TargetState Decode(Func<ulong, int, byte[]> read, ulong image, TargetMemoryLayout? layout = null, bool includeTarget = true)
    {
        layout ??= TargetMemoryLayout.Legacy;
        var r = new ObservedSurfaceReader(read, image);
        var identity = Identity.Decode(r.Read, image, layout.Identity);
        if (r.U64(identity.Player) != image + layout.SpawnVtable || r.Read(identity.Player + layout.Identity.PlayerType, 1)[0] != 0
            || r.Read(identity.Player + layout.SpawnLevel, 1)[0] != identity.Character.Level)
            throw new InvalidDataException("Local spawn type or level failed validation.");
        byte[] zoneBytes = r.Read(image + layout.ZoneHeader, 0x188);
        int zoneId = BitConverter.ToInt32(zoneBytes, 0x184);
        var zone = new ObservedZone(zoneId, ObservedSurfaceReader.Text(zoneBytes, 0, 128),
            ObservedSurfaceReader.Text(zoneBytes, 128, 128));
        if (zoneId is < 1 or > 32767 || zone.ShortName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Target zone failed validation.");
        ulong pointer = includeTarget ? r.U64(image + layout.TargetRoot) : 0;
        ObservedTarget? target = null;
        if (pointer != 0)
        {
            ObservedSurfaceReader.Pointer(pointer);
            ObservedTarget ReadTarget()
            {
                if (r.U64(pointer) != image + layout.SpawnVtable) throw new InvalidDataException("Unsupported target object.");
                string name = ObservedSurfaceReader.Text(r.Read(pointer + layout.DisplayName, 64), 0, 64);
                byte type = r.Read(pointer + layout.Identity.PlayerType, 1)[0], level = r.Read(pointer + layout.SpawnLevel, 1)[0];
                uint id = unchecked((uint)r.I32(pointer + layout.SpawnId));
                if (id == 0 || level == 0) throw new InvalidDataException("Invalid target identity or level.");
                return new(id, name, level, type, type switch { 0 => "Player", 1 => "NPC", 2 => "Corpse", _ => "Unknown" });
            }
            target = ReadTarget();
            if (ReadTarget() != target) throw new InvalidDataException("Selected target data changed.");
        }
        if (includeTarget && r.U64(image + layout.TargetRoot) != pointer) throw new InvalidDataException("Selected target changed.");
        r.Unchanged(image + layout.ZoneHeader, zoneBytes);
        if (Identity.Decode(r.Read, image, layout.Identity) != identity) throw new InvalidDataException("Character changed during target reading.");
        return new(identity.Character, zone, target);
    }
}
