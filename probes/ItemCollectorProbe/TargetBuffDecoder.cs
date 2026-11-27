using System.Text;

internal sealed record TargetEffect(int Slot, int SpellId, string? SpellName, string? CasterName, int RemainingMilliseconds, int? IconId = null)
{
    public string Remaining => RemainingMilliseconds < 0 ? "No countdown reported"
        : $"{RemainingMilliseconds / 3600000}h {RemainingMilliseconds / 60000 % 60}m {RemainingMilliseconds / 1000 % 60}s";
}
internal sealed record TargetBuffState(string Status, TargetEffect[] Effects);
internal sealed record TargetBuffCapture(int SchemaVersion, Guid CaptureId, DateTimeOffset ObservedAtUtc,
    string ClientChannel, string BuildSha256, string Decoder, string SourceKind, int ProcessId, long ProcessStartedUtcTicks,
    string CharacterName, string CharacterClass, ObservedZone Zone, ObservedTarget? Target, string Status,
    TargetEffect[] Effects, string? SpellNamesSha256, string? SpellNamesError, string Completeness);

// Target-window data received by the client, not all buffs known to the server.
internal static class TargetBuffDecoder
{
    internal const ulong WindowRoot = 0xe03610, WindowVtable = 0xab2b20, ArrayVtable = 0xa73df8, CasterVtable = 0xb215b0;
    internal const int MaxEffects = 400, Stride = 0x78;
    internal static TargetBuffCapture Capture(int pid, long? expectedStartedUtcTicks = null)
    {
        var target = CaptureTarget(pid, expectedStartedUtcTicks);
        return target.Buffs ?? throw new InvalidDataException(target.BuffsError ?? "Target effects unavailable.");
    }
    internal static TargetCapture CaptureTarget(int pid, long? expectedStartedUtcTicks = null, MemoryFeature feature = MemoryFeature.DataCollection)
    {
        // Cached until the installed file changes; never rescan all spell names on each poll.
        var names = SpellNameCatalog.Read();
        using var session = new TargetLayoutResolver.Session(pid, expectedStartedUtcTicks, feature);
        var memory = session.Memory;
        var target = ObservedSurfaceReader.Stable(() => TargetDecoder.Decode(memory.Read, memory.Base, session.Target));
        TargetBuffState? buffs = null; string? error = null;
        try { buffs = Decode(memory.Read, memory.Base, target, session.Target, session.Resolved.Effects); }
        catch (Exception e) when (e is InvalidDataException or IOException or System.ComponentModel.Win32Exception) { error = e.Message; }
        if (TargetDecoder.Decode(memory.Read, memory.Base, session.Target) != target) throw new InvalidDataException("Target changed during effect capture.");
        session.Verify();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        TargetBuffCapture? captured = buffs is null ? null : new(1, Guid.NewGuid(), at, "Live", session.Resolved.Image.Hash, "live-target-effects-resolved-1", "target-buffs",
            memory.ProcessId, memory.StartedUtcTicks, target.Character.Name, target.Character.Class, target.Zone, target.Target,
            buffs.Status, buffs.Effects.Select(e => e with { SpellName = names.Names.GetValueOrDefault(e.SpellId) }).ToArray(), names.Sha256, names.Error,
            "Effects loaded in this client's target window. Spell IDs, caster names, icon cells and remaining milliseconds are memory observations; spell names come from the installed spells_us.txt. Unknown casters stay unknown. Negative duration means no countdown reported. Buff/debuff classification and timer-hold state are not decoded. Not a complete server buff list or proof of a new cast.");
        return new(1, Guid.NewGuid(), at, "Live", session.Resolved.Image.Hash, "live-selected-target-resolved-1", "selected-target",
            memory.ProcessId, memory.StartedUtcTicks, target.Character.Name, target.Character.Class, target.Zone, target.Target,
            "Selected target and available target-window effects. No distance, coordinates, loot attribution or kill count is collected.", captured, error);
    }
    internal static TargetBuffState Decode(Func<ulong, int, byte[]> read, ulong image, TargetState expected,
        TargetMemoryLayout? targetLayout = null, TargetEffectLayout? layout = null)
    {
        targetLayout ??= TargetMemoryLayout.Legacy;
        layout ??= TargetEffectLayout.Legacy;
        var r = new ObservedSurfaceReader(read, image);
        ulong selected = r.U64(image + targetLayout.TargetRoot);
        if (expected.Target is null)
        {
            if (selected != 0 || r.U64(image + targetLayout.TargetRoot) != 0) throw new InvalidDataException("Target changed.");
            return new("no-target", []);
        }
        ObservedSurfaceReader.Pointer(selected);
        void CheckTarget()
        {
            if (r.U64(image + targetLayout.TargetRoot) != selected || r.U64(selected) != image + targetLayout.SpawnVtable
                || unchecked((uint)r.I32(selected + targetLayout.SpawnId)) != expected.Target.SpawnId
                || ObservedSurfaceReader.Text(r.Read(selected + targetLayout.DisplayName, 64), 0, 64) != expected.Target.Name
                || r.Read(selected + targetLayout.Identity.PlayerType, 1)[0] != expected.Target.RawType
                || r.Read(selected + targetLayout.SpawnLevel, 1)[0] != expected.Target.Level)
                throw new InvalidDataException("Target identity changed during effect reading.");
        }
        CheckTarget();
        ulong window = r.Root(layout.WindowRoot, layout.WindowVtable);
        void CheckWindowTarget()
        {
            if (r.Root(layout.WindowRoot, layout.WindowVtable) != window || r.U64(window + layout.PreviousTarget) != selected
                || r.Read(window + layout.FirstUpdate, 1)[0] != 0)
                throw new InvalidDataException("Target window has not caught up with the current selection.");
        }
        CheckWindowTarget();
        byte[] header = r.Read(window + layout.EffectArray, 24);
        int count = BitConverter.ToInt32(header, 16), capacity = BitConverter.ToInt32(header, 20);
        if (BitConverter.ToUInt64(header) != image + layout.ArrayVtable || count is < 0 or > MaxEffects || capacity < count || capacity > MaxEffects)
            throw new InvalidDataException("Unsupported target-effect array.");
        if (r.I32(window + layout.PendingData) == 0)
        {
            CheckWindowTarget(); CheckTarget();
            return new("waiting-for-target-data", []);
        }
        ulong data = count == 0 ? 0 : ObservedSurfaceReader.Pointer(BitConverter.ToUInt64(header, 8));
        TargetEffect[] ReadEffects()
        {
            var effects = new List<TargetEffect>();
            byte[] bytes = count == 0 ? [] : r.Read(data, count * Stride);
            for (int slot = 0; slot < count; slot++)
            {
                int offset = slot * Stride, id = BitConverter.ToInt32(bytes, offset + 0x10);
                if (id is 0 or -1) continue;
                if (id is < 0 or > 1000000 || BitConverter.ToUInt64(bytes, offset + 0x18) != image + layout.CasterVtable)
                    throw new InvalidDataException("Invalid target-effect record.");
                int length = BitConverter.ToInt32(bytes, offset + 0x28);
                if (length is < 0 or >= 64) throw new InvalidDataException("Invalid target-effect caster length.");
                string? caster = null;
                if (length > 0)
                {
                    if (BitConverter.ToUInt64(bytes, offset + 0x20) != data + (ulong)offset + 0x34
                        || BitConverter.ToInt32(bytes, offset + 0x2c) != 64 || bytes[offset + 0x34 + length] != 0
                        || bytes.AsSpan(offset + 0x34, length).ContainsAnyExceptInRange((byte)32, (byte)126))
                        throw new InvalidDataException("Unsupported target-effect caster string.");
                    caster = Encoding.ASCII.GetString(bytes, offset + 0x34, length);
                }
                int? icon = null;
                ulong iconPointer = BitConverter.ToUInt64(bytes, offset + 8);
                if (iconPointer != 0)
                {
                    try
                    {
                        byte[] animation = r.Read(ObservedSurfaceReader.Pointer(iconPointer), 0x60);
                        int cell = BitConverter.ToInt32(animation, 0x48);
                        // SetCurCell at RVA 0x5d4c20: current cell, frame, and 40x40 crop must agree.
                        if (BitConverter.ToUInt64(animation) == image + layout.IconVtable && animation[0x3e] == 1 && animation[0x3f] == 0
                            && BitConverter.ToInt32(animation, 0x40) == 40 && BitConverter.ToInt32(animation, 0x44) == 40
                            && cell is >= 0 and < 9216 && BitConverter.ToInt32(animation, 0x10) is > 0 and <= 256
                            && cell / 36 < BitConverter.ToInt32(animation, 0x10)
                            && BitConverter.ToInt32(animation, 0x2c) == cell / 36
                            && BitConverter.ToInt32(animation, 0x50) == cell % 6 * 40
                            && BitConverter.ToInt32(animation, 0x54) == cell % 36 / 6 * 40
                            && BitConverter.ToInt32(animation, 0x58) == cell % 6 * 40 + 40
                            && BitConverter.ToInt32(animation, 0x5c) == cell % 36 / 6 * 40 + 40) icon = cell;
                    }
                    catch (InvalidDataException) { /* Optional artwork cannot invalidate spell/caster evidence. */ }
                }
                effects.Add(new(slot, id, null, caster, BitConverter.ToInt32(bytes, offset + 0x14), icon));
            }
            return effects.ToArray();
        }
        var first = ReadEffects(); var second = ReadEffects();
        // Remaining milliseconds may tick between reads. Identity and caster must agree.
        if (!first.Select(e => e with { RemainingMilliseconds = 0 }).SequenceEqual(second.Select(e => e with { RemainingMilliseconds = 0 })))
            throw new InvalidDataException("Target effects changed during reading.");
        r.Unchanged(window + layout.EffectArray, header); CheckWindowTarget(); CheckTarget();
        return new("observed-target-window", second);
    }
}
