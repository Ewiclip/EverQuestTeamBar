using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

// Read-only Live identity adapter. The signatures describe previously reviewed
// instructions; every referenced root/member is derived and cross-checked.
// This grants no capability to item, target, spell or other passive decoders.
internal static class IdentityResolver
{
    internal sealed record Pattern(string Id, string PatternText, Dictionary<string, int[]> Fields);
    private sealed record PatternJson(string Id, string Pattern, Dictionary<string, int[]> Fields);
    internal sealed record Evidence(string Id, uint Rva, byte[] Bytes);
    internal sealed record Section(uint Rva, uint Size, int Raw, int RawSize, bool Code, bool Writable, bool Readable);
    internal sealed record Resolved(string Hash, byte[] Image, uint ImageSize, Section[] Sections,
        Dictionary<string, uint> Fields, Evidence[] Evidence)
    {
        internal bool Contains(uint rva, uint size, Func<Section, bool> select) => Sections.Any(s => select(s)
            && rva >= s.Rva && size <= s.Size && rva - s.Rva <= s.Size - size);
        internal byte[] FileBytes(uint rva, int size)
        {
            var section = Sections.SingleOrDefault(s => rva >= s.Rva && size <= s.RawSize && rva - s.Rva <= s.RawSize - size)
                ?? throw new InvalidDataException("Identity reference has no file-backed data.");
            return Image.AsSpan(checked(section.Raw + (int)(rva - section.Rva)), size).ToArray();
        }
        internal void VerifyLoaded(Func<ulong, int, byte[]> read, ulong image)
        {
            foreach (var item in Evidence)
                if (!read(image + item.Rva, item.Bytes.Length).AsSpan().SequenceEqual(item.Bytes))
                    throw new InvalidDataException("The running EQ image differs from the resolved identity code. Restart or update the detector.");
        }
        internal IdentityLayout Bind(Func<ulong, int, byte[]> read, ulong image)
        {
            ulong pc = BitConverter.ToUInt64(read(image + Fields["PcRoot"], 8));
            if (!MemoryReader.Pointer(pc) || (pc & 7) != 0) throw new InvalidDataException("Character data is unavailable.");
            // Both the name comparison and profile access code explicitly use
            // Pc+8 -> signed DWORD at table+4 to locate the virtual base.
            ulong table = BitConverter.ToUInt64(read(pc + 8, 8));
            if (table < image || table - image > uint.MaxValue || (table & 3) != 0
                || !Contains((uint)(table - image), 8, s => s.Readable && !s.Code && !s.Writable))
                throw new InvalidDataException("Character virtual-base metadata is unavailable.");
            byte[] metadata = read(table, 8);
            if (!metadata.AsSpan().SequenceEqual(FileBytes((uint)(table - image), 8)) || BitConverter.ToInt32(metadata) != -8)
                throw new InvalidDataException("Character virtual-base metadata disagrees with the executable.");
            int delta = BitConverter.ToInt32(metadata, 4);
            if (delta is < 0x100 or > 0x10000 || (delta & 7) != 0
                || BitConverter.ToUInt64(read(image + Fields["PcRoot"], 8)) != pc
                || BitConverter.ToUInt64(read(pc + 8, 8)) != table)
                throw new InvalidDataException("Character layout is changing or unsupported.");
            return new(Fields["PlayerRoot"], Fields["PcRoot"], Fields["GameRoot"], Fields["GameState"], Fields["PcPlayer"],
                Fields["PlayerType"], Fields["PlayerName"], (ulong)delta + Fields["PcNameRelative"],
                (ulong)delta + 0x18, (ulong)delta + 0x10, Fields["PlayerClass"], Fields["ProfileClass"], Fields["ProfileLevel"]);
        }
    }

    private sealed record Cached(string Path, long Length, DateTime WriteTime, Resolved Value);
    private static readonly object CacheLock = new();
    private static Cached? cache;
    internal static readonly Pattern[] Patterns = LoadPatterns();
    private static Pattern[] LoadPatterns()
    {
        using var stream = typeof(IdentityResolver).Assembly.GetManifestResourceStream("TeamBar.IdentityPatterns.json")
            ?? throw new InvalidDataException("Identity adapter definitions are missing.");
        return (JsonSerializer.Deserialize<PatternJson[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid identity adapter definitions."))
            .Select(p => new Pattern(p.Id, p.Pattern, p.Fields)).ToArray();
    }
    internal static Resolved FileLayout(string path)
    {
        lock (CacheLock)
        {
            var info = new FileInfo(path);
            if (cache is not null && string.Equals(cache.Path, info.FullName, StringComparison.OrdinalIgnoreCase)
                && cache.Length == info.Length && cache.WriteTime == info.LastWriteTimeUtc) return cache.Value;
            if (info.Length is < 4096 or > 64 * 1024 * 1024) throw new InvalidDataException("Unsupported EQ executable size.");
            var length = info.Length; var time = info.LastWriteTimeUtc;
            var resolved = Resolve(File.ReadAllBytes(path)); info.Refresh();
            if (length != info.Length || time != info.LastWriteTimeUtc) throw new InvalidDataException("The EQ executable changed during resolution.");
            cache = new(info.FullName, length, time, resolved); return resolved;
        }
    }
    internal static DetectedClient Read(MemoryReader memory)
    {
        var resolved = FileLayout(memory.Path);
        resolved.VerifyLoaded(memory.Read, memory.Base);
        var layout = resolved.Bind(memory.Read, memory.Base);
        var client = Identity.Read(memory, layout, "Live", resolved.Hash);
        if (layout != resolved.Bind(memory.Read, memory.Base) || !ReferenceEquals(resolved, FileLayout(memory.Path)))
            throw new InvalidDataException("The client layout changed during detection. Refresh clients.");
        return client;
    }
    internal static Resolved Resolve(byte[] file)
    {
        try { return ResolveCore(file); }
        catch (Exception error) when (error is BadImageFormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidDataException("The EQ executable has an unsupported identity layout.", error); }
    }
    private static Resolved ResolveCore(byte[] file)
    {
        if (file.Length is < 4096 or > 64 * 1024 * 1024) throw new InvalidDataException("Unsupported identity image size.");
        using var pe = new PEReader(new MemoryStream(file, writable: false));
        var h = pe.PEHeaders;
        if (h.CoffHeader.Machine != Machine.Amd64 || h.PEHeader?.Magic != PEMagic.PE32Plus
            || h.PEHeader.SizeOfImage is < 4096 or > 128 * 1024 * 1024 || h.SectionHeaders.Length is < 1 or > 64)
            throw new InvalidDataException("Expected a bounded x64 EQ executable.");
        var sections = h.SectionHeaders.Select(s => new Section(checked((uint)s.VirtualAddress), checked((uint)Math.Max(s.VirtualSize, s.SizeOfRawData)),
            s.PointerToRawData, s.SizeOfRawData, s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute),
            s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite), s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemRead))).ToArray();
        foreach (var section in sections)
            if (section.Raw < 0 || section.RawSize < 0 || section.Raw > file.Length || section.RawSize > file.Length - section.Raw
                || section.Rva > h.PEHeader.SizeOfImage || section.Size > h.PEHeader.SizeOfImage - section.Rva
                || sections.Any(s => !ReferenceEquals(s, section) && section.Rva < (ulong)s.Rva + s.Size && s.Rva < (ulong)section.Rva + section.Size))
                throw new InvalidDataException("Invalid or overlapping identity image sections.");
        var evidence = new List<Evidence>(); var fields = new Dictionary<string, uint>();
        foreach (var definition in Patterns)
        {
            var bytes = definition.PatternText.Split(' ').Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
            var hits = new List<(int Raw, uint Rva)>();
            foreach (var section in sections.Where(s => s.Code && s.Readable && !s.Writable))
            {
                int end = checked(section.Raw + section.RawSize - bytes.Length), at = section.Raw;
                while (at <= end)
                {
                    int next = file.AsSpan(at, end - at + 1).IndexOf(bytes[0]!.Value);
                    if (next < 0) break; at += next;
                    bool match = true;
                    for (int i = 1; i < bytes.Length && match; i++) if (bytes[i] is byte expected && file[at + i] != expected) match = false;
                    if (match)
                    {
                        hits.Add((at, checked(section.Rva + (uint)(at - section.Raw))));
                        if (hits.Count > 1) break;
                    }
                    at++;
                }
                if (hits.Count > 1) break;
            }
            if (hits.Count != 1) throw new InvalidDataException($"Automatic identity resolution is inconclusive ({definition.Id}: {hits.Count} matches). Use Assign windows for input testing.");
            var hit = hits[0]; evidence.Add(new(definition.Id, hit.Rva, file.AsSpan(hit.Raw, bytes.Length).ToArray()));
            foreach (var (name, operand) in definition.Fields)
            {
                int offset = operand[0], next = operand[1];
                uint value = offset < 0 ? hit.Rva : next == 0 ? BitConverter.ToUInt32(file, hit.Raw + offset)
                    : checked((uint)((long)hit.Rva + next + BitConverter.ToInt32(file, hit.Raw + offset)));
                if (fields.TryGetValue(name, out uint prior) && prior != value)
                    throw new InvalidDataException($"Identity code references disagree ({name}).");
                fields[name] = value;
            }
        }
        var result = new Resolved(Convert.ToHexStringLower(SHA256.HashData(file)), file, checked((uint)h.PEHeader.SizeOfImage), sections, fields, evidence.ToArray());
        foreach (var (name, value) in fields)
        {
            if (name.EndsWith("Root", StringComparison.Ordinal))
            {
                if ((value & 7) != 0 || !result.Contains(value, 8, s => s.Readable && s.Writable && !s.Code))
                    throw new InvalidDataException("Identity root is not aligned writable image data.");
            }
            else if (name == "ProfileHelper")
            {
                if (!result.Contains(value, 37, s => s.Code && s.Readable && !s.Writable)) throw new InvalidDataException("Invalid profile helper.");
            }
            else if (value is < 1 or > 0x10000) throw new InvalidDataException($"Identity member is out of bounds ({name}).");
        }
        if (new[] { fields["PlayerRoot"], fields["PcRoot"], fields["GameRoot"] }.Distinct().Count() != 3
            || fields["PlayerClass"] == fields["PlayerType"] || fields["ProfileClass"] == fields["ProfileLevel"])
            throw new InvalidDataException("Identity fields unexpectedly alias.");
        return result;
    }
    internal static object Describe(string path)
    {
        var value = FileLayout(path);
        return new { value.Hash, fields = value.Fields.ToDictionary(p => p.Key, p => $"0x{p.Value:x}"),
            evidence = value.Evidence.Select(e => new { e.Id, rva = $"0x{e.Rva:x}", bytes = e.Bytes.Length }),
            processMemoryRead = false, gameInputSent = false };
    }
}
