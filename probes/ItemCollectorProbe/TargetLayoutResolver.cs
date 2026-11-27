using System.Text;
using System.Text.Json;

internal sealed record TargetMemoryLayout(IdentityLayout Identity, ulong TargetRoot, ulong SpawnVtable,
    ulong ZoneHeader, ulong SpawnLevel, ulong SpawnId, ulong DisplayName)
{
    internal static readonly TargetMemoryLayout Legacy = new(global::Identity.LiveLayout, TargetDecoder.TargetRoot,
        TargetDecoder.SpawnVtable, LootContextDecoder.ZoneHeader, 0x270, 0x168, 0xf4);
}
internal sealed record TargetEffectLayout(ulong WindowRoot, ulong WindowVtable, ulong ArrayVtable, ulong CasterVtable,
    ulong EffectArray, ulong PendingData, ulong PreviousTarget, ulong FirstUpdate, ulong IconVtable = 0xaf2ab8)
{
    internal static readonly TargetEffectLayout Legacy = new(TargetBuffDecoder.WindowRoot, TargetBuffDecoder.WindowVtable,
        TargetBuffDecoder.ArrayVtable, TargetBuffDecoder.CasterVtable, 0x300, 0x330, 0x3c0, 0x350);
}

// Passive adapter only. Static instructions identify roots and member displacements;
// live reads must agree with those instructions, local identity and object metadata.
internal static class TargetLayoutResolver
{
    internal sealed record Definition(string Id, string Pattern, Dictionary<string, int[]> Fields);
    internal sealed record Resolved(IdentityResolver.Resolved Image, Dictionary<string, uint> Fields,
        IdentityResolver.Evidence[] Evidence)
    {
        internal TargetEffectLayout Effects => new(Fields["WindowRoot"], Fields["WindowVtable"], Fields["ArrayVtable"],
            Fields["CasterVtable"], Fields["EffectArray"], Fields["PendingData"], Fields["PreviousTarget"], Fields["FirstUpdate"], Fields["IconVtable"]);
        internal void VerifyLoaded(Func<ulong, int, byte[]> read, ulong image)
        {
            Image.VerifyLoaded(read, image);
            foreach (var e in Evidence)
                if (!read(image + e.Rva, e.Bytes.Length).AsSpan().SequenceEqual(e.Bytes))
                    throw new InvalidDataException("The running client differs from the resolved target reader. Restart or update the reader.");
        }
        internal ulong Vtable(Func<ulong, int, byte[]> read, ulong image, ulong table)
        {
            if (table < image || table - image > uint.MaxValue || (table & 7) != 0
                || !Image.Contains((uint)(table - image), 24, s => s.Readable && !s.Writable && !s.Code))
                throw new InvalidDataException("Target object metadata is outside read-only image data.");
            uint rva = (uint)(table - image);
            var disk = Image.FileBytes(rva, 24); var loaded = read(table, 24);
            int pe = BitConverter.ToInt32(Image.Image, 0x3c);
            ulong preferred = BitConverter.ToUInt64(Image.Image, pe + 48);
            for (int i = 0; i < 24; i += 8)
            {
                ulong function = BitConverter.ToUInt64(disk, i);
                if (function < preferred || function - preferred > uint.MaxValue
                    || !Image.Contains((uint)(function - preferred), 1, s => s.Code && s.Readable && !s.Writable)
                    || BitConverter.ToUInt64(loaded, i) != image + function - preferred)
                    throw new InvalidDataException("Target object metadata differs from the executable.");
            }
            return rva;
        }
        internal TargetMemoryLayout Bind(Func<ulong, int, byte[]> read, ulong image)
        {
            var identity = Image.Bind(read, image);
            var local = Identity.Decode(read, image, identity);
            ulong table = BitConverter.ToUInt64(read(local.Player, 8));
            // The spawn prefix has two adjacent 64-byte names followed by the
            // type byte at +0x81. Both endpoints come from identity code; the
            // local display name provides a runtime cross-check of this schema.
            if (identity.PlayerType != identity.PlayerName + 0x81
                || ObservedSurfaceReader.Text(read(local.Player + identity.PlayerName + 64, 64), 0, 64) != local.Character.Name)
                throw new InvalidDataException("Unsupported spawn-name prefix layout.");
            var result = new TargetMemoryLayout(identity, Fields["TargetRoot"], Vtable(read, image, table),
                Fields["ZoneHeader"], Fields["SpawnLevel"], Fields["SpawnId"], identity.PlayerName + 64);
            if (read(local.Player + result.SpawnLevel, 1)[0] != local.Character.Level
                || BitConverter.ToUInt64(read(local.Player, 8)) != table)
                throw new InvalidDataException("Resolved spawn level disagrees with the current character profile.");
            return result;
        }
    }
    internal static readonly Definition[] Definitions = Load();
    private static Definition[] Load()
    {
        using var stream = typeof(TargetLayoutResolver).Assembly.GetManifestResourceStream("TeamBar.TargetPatterns.json")
            ?? throw new InvalidDataException("Target reader definitions are missing.");
        return JsonSerializer.Deserialize<Definition[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid target reader definitions.");
    }
    private static readonly object Sync = new();
    private static Resolved? cached;
    internal static Resolved FileLayout(string path)
    {
        var identity = IdentityResolver.FileLayout(path);
        lock (Sync)
        {
            if (cached is not null && ReferenceEquals(cached.Image, identity)) return cached;
            return cached = Resolve(identity);
        }
    }
    internal static Resolved Resolve(IdentityResolver.Resolved image)
    {
        var fields = new Dictionary<string, uint>(image.Fields);
        var evidence = new List<IdentityResolver.Evidence>();
        foreach (var definition in Definitions)
        {
            var bytes = definition.Pattern.Split(' ').Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
            var hits = new List<(uint Rva, byte[] Bytes, Dictionary<string, uint> Fields)>();
            foreach (var section in image.Sections.Where(s => s.Code && s.Readable && !s.Writable))
            {
                int end = section.Raw + section.RawSize - bytes.Length;
                for (int at = section.Raw; at <= end; at++)
                {
                    int next = image.Image.AsSpan(at, end - at + 1).IndexOf(bytes[0]!.Value);
                    if (next < 0) break; at += next;
                    bool match = true;
                    for (int i = 1; i < bytes.Length && match; i++)
                        if (bytes[i] is byte expected && image.Image[at + i] != expected) match = false;
                    if (!match) continue;
                    uint rva = section.Rva + (uint)(at - section.Raw);
                    var values = new Dictionary<string, uint>();
                    foreach (var (name, operand) in definition.Fields)
                    {
                        int offset = operand[0], relative = operand[1];
                        long value = offset < 0 ? rva : relative == 0 ? BitConverter.ToUInt32(image.Image, at + offset)
                            : (long)rva + relative + BitConverter.ToInt32(image.Image, at + offset);
                        if (value is < 0 or > uint.MaxValue) { match = false; break; }
                        values[name] = (uint)value;
                        // E.g. a generic window registration must call THIS uniquely
                        // resolved target constructor, not a different UI constructor.
                        if (fields.TryGetValue(name, out uint prior) && prior != value) { match = false; break; }
                    }
                    if (match) hits.Add((rva, image.Image.AsSpan(at, bytes.Length).ToArray(), values));
                    if (hits.Count > 1) break;
                }
                if (hits.Count > 1) break;
            }
            if (hits.Count != 1) throw new InvalidDataException($"Target reader resolution is inconclusive ({definition.Id}: {hits.Count} matches). Nothing was inferred.");
            var hit = hits[0]; evidence.Add(new(definition.Id, hit.Rva, hit.Bytes));
            foreach (var field in hit.Fields) fields[field.Key] = field.Value;
        }
        void Equal(string a, string b, uint add = 0)
        { if ((ulong)fields[a] + add != fields[b]) throw new InvalidDataException($"Target reader evidence disagrees ({a}, {b})."); }
        Equal("WindowRoot", "WindowRootStored"); Equal("WindowRoot", "WindowRootRegistered");
        Equal("TargetRoot", "TargetRootAgain"); Equal("PlayerRoot", "PlayerRootAgain");
        Equal("SpawnLevel", "SpawnLevelAgain"); Equal("PendingData", "PendingDataAgain");
        Equal("EffectArray", "EffectData", 8); Equal("ZoneHeader", "ZoneHeaderAgain");
        Equal("ZoneHeader", "ZoneLongName", 128); Equal("ZoneHeader", "ZoneId", 0x184);
        Equal("CasterVtable", "CasterVtableAgain"); Equal("CasterVtable", "CasterVtableThird");
        foreach (var name in new[] { "TargetRoot", "WindowRoot" })
            if ((fields[name] & 7) != 0 || !image.Contains(fields[name], 8, s => s.Readable && s.Writable && !s.Code))
                throw new InvalidDataException("Invalid target root section.");
        if (!image.Contains(fields["ZoneHeader"], 0x188, s => s.Readable && s.Writable && !s.Code))
            throw new InvalidDataException("Invalid target zone section.");
        foreach (var name in new[] { "WindowVtable", "ArrayVtable", "CasterVtable", "IconVtable", "WindowName" })
            if (!image.Contains(fields[name], 24, s => s.Readable && !s.Writable && !s.Code))
                throw new InvalidDataException("Invalid target metadata section.");
        if (Encoding.ASCII.GetString(image.FileBytes(fields["WindowName"], 13)) != "TargetWindow\0")
            throw new InvalidDataException("The resolved constructor does not identify the target window.");
        foreach (var name in new[] { "SpawnLevel", "SpawnId", "EffectArray", "PendingData", "PreviousTarget", "FirstUpdate" })
            if (fields[name] is < 1 or > 0x10000) throw new InvalidDataException("Target member outside bounds.");
        if (fields["WindowSize"] is < 0x100 or > 0x10000) throw new InvalidDataException("Target window allocation outside bounds.");
        var members = new[] { (fields["EffectArray"], 24u), (fields["PendingData"], 4u),
            (fields["PreviousTarget"], 8u), (fields["FirstUpdate"], 1u) }.OrderBy(m => m.Item1).ToArray();
        for (int i = 0; i < members.Length; i++)
            if ((ulong)members[i].Item1 + members[i].Item2 > fields["WindowSize"]
                || (i > 0 && members[i - 1].Item1 + members[i - 1].Item2 > members[i].Item1))
                throw new InvalidDataException("Target window fields overlap or exceed its allocation.");
        return new(image, fields, evidence.ToArray());
    }
    internal sealed class Session : IDisposable
    {
        internal MemoryReader Memory { get; }
        internal Resolved Resolved { get; }
        internal TargetMemoryLayout Target { get; }
        internal Session(int pid, long? started, MemoryFeature feature = MemoryFeature.DataCollection)
        {
            Memory = new(pid, feature);
            try
            {
                if (started is not null && Memory.StartedUtcTicks != started)
                    throw new InvalidDataException("The selected client restarted. Refresh the character list.");
                Resolved = FileLayout(Memory.Path);
                Resolved.VerifyLoaded(Memory.Read, Memory.Base);
                Target = Resolved.Bind(Memory.Read, Memory.Base);
            }
            catch { Memory.Dispose(); throw; }
        }
        internal void Verify()
        {
            if (!ReferenceEquals(Resolved, FileLayout(Memory.Path)) || Target != Resolved.Bind(Memory.Read, Memory.Base))
                throw new InvalidDataException("The target layout changed during reading.");
        }
        public void Dispose() => Memory.Dispose();
    }
    internal static object Describe(string path)
    {
        var result = FileLayout(path);
        return new { result.Image.Hash, fields = result.Fields.ToDictionary(p => p.Key, p => $"0x{p.Value:x}"),
            evidence = result.Evidence.Select(e => new { e.Id, rva = $"0x{e.Rva:x}", length = e.Bytes.Length }),
            processMemoryRead = false, gameInputSent = false };
    }
}
