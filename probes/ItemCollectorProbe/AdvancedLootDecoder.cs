internal sealed record LootSource(uint CorpseId, string Name, short StackCount, string Evidence);
internal sealed record ObservedLootItem(string List, int ItemId, string Name, int IconId, uint LootId, LootSource[] Sources);
internal sealed record AdvancedLootState(CharacterIdentity Character, ObservedLootItem[] Items);
internal sealed record AdvancedLootCapture(int SchemaVersion, Guid CaptureId, DateTimeOffset ObservedAtUtc,
    string ClientChannel, string BuildSha256, string Decoder, string SourceKind, string CharacterName,
    ObservedLootItem[] Items, string Completeness, LootContext? Context = null, string? ContextError = null);

internal sealed record AdvancedLootMemoryLayout(ulong WindowRoot, ulong WindowVtable, ulong Lists,
    ulong ListVtable, ulong SourceVtable)
{
    internal const string NovemberBuildHash = "0a1d49e9d97f64406f16e06a4a8be167469c831a90afa5341776c57c1e97bf3d";
    internal static readonly AdvancedLootMemoryLayout Legacy = new(0xe03498, 0x875458, 0x3f0, 0x875438, 0x875418);
    // Exact-image constructor evidence: a4f40 (AdvancedLootWnd), a4fc6 (vtable),
    // a57cd/a5929 (personal/shared lists), 1a398c (root). Independently decoded
    // target layout supplies identity, zone and corpse fields. No other build is admitted.
    internal static readonly AdvancedLootMemoryLayout November = new(0xe125b0, 0x881460, 0x3e8, 0x881440, 0x881420);
    internal static AdvancedLootMemoryLayout ForHash(string hash) => hash switch
    {
        Identity.BuildHash => Legacy,
        NovemberBuildHash => November,
        _ => throw new InvalidDataException("Advanced Loot collection is not validated for this build.")
    };
    internal static bool SupportsHash(string hash) => hash is Identity.BuildHash or NovemberBuildHash;
    internal static readonly (uint Rva, int Length)[] NovemberEvidence =
    [
        (0xa4f40, 0x9e), (0xa56ef, 14), (0xa57b1, 0x23), (0xa590d, 0x23),
        (0x1a3969, 0x2a), (0xa3c50, 0x39), (0xa44b0, 0x9b)
    ];
    internal static void VerifyCode(IdentityResolver.Resolved image, Func<ulong, int, byte[]> read, ulong imageBase)
    {
        _ = ForHash(image.Hash);
        if (image.Hash != NovemberBuildHash) return;
        foreach (var (rva, length) in NovemberEvidence)
            if (!read(imageBase + rva, length).AsSpan().SequenceEqual(image.FileBytes(rva, length)))
                throw new InvalidDataException("Loaded Advanced Loot code differs from the validated executable. Nothing was accepted.");
    }
}

internal static class AdvancedLootDecoder
{
    internal const string Decoder = "live-advanced-loot-2";
    internal const ulong WindowRoot = 0xe03498, WindowVtable = 0x875458;
    internal const int MaxEntries = 256, MaxSources = 128, ItemStride = 0xb0, SourceStride = 0x50;
    internal static AdvancedLootCapture Capture(int pid, long? expectedStartedUtcTicks = null)
    {
        using var session = new TargetLayoutResolver.Session(pid, expectedStartedUtcTicks);
        var memory = session.Memory;
        string hash = session.Resolved.Image.Hash;
        var layout = AdvancedLootMemoryLayout.ForHash(hash);
        Identity.VerifyFile(memory.Path, hash);
        AdvancedLootMemoryLayout.VerifyCode(session.Resolved.Image, memory.Read, memory.Base);
        foreach (ulong table in new[] { layout.WindowVtable, layout.ListVtable, layout.SourceVtable })
            session.Resolved.Vtable(memory.Read, memory.Base, memory.Base + table);
        var state = ObservedSurfaceReader.Stable(() => Decode(memory.Read, memory.Base, layout, session.Target.Identity));
        LootContext? context = null; string? contextError = null;
        try
        {
            context = ObservedSurfaceReader.Stable(() => LootContextDecoder.Decode(memory.Read, memory.Base, state.Items.SelectMany(i => i.Sources).ToArray(), session.Target));
            if (CollectorFormat.Hash(state) != CollectorFormat.Hash(Decode(memory.Read, memory.Base, layout, session.Target.Identity)))
                throw new InvalidDataException("Loot changed while reading NPC context.");
        }
        catch (InvalidDataException error) { context = null; contextError = error.Message; }
        AdvancedLootMemoryLayout.VerifyCode(session.Resolved.Image, memory.Read, memory.Base);
        session.Verify();
        return new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Live", hash, Decoder,
            "advanced-loot", state.Character.Name, state.Items,
            "Direct loot-source records. Matched corpse context supplies zone and level only when spawn ID, source name and corpse type agree. Observation zone alone is not a proven drop zone. Server/instance and cross-session identity are not decoded. Polls are observations, not new drops.", context, contextError);
    }
    internal static AdvancedLootState Decode(Func<ulong, int, byte[]> read, ulong image,
        AdvancedLootMemoryLayout? layout = null, IdentityLayout? identityLayout = null)
    {
        layout ??= AdvancedLootMemoryLayout.Legacy; identityLayout ??= Identity.LiveLayout;
        var r = new ObservedSurfaceReader(read, image);
        var identity = Identity.Decode(r.Read, image, identityLayout);
        ulong window = r.Root(layout.WindowRoot, layout.WindowVtable);
        byte[] listPointers = r.Read(window + layout.Lists, 16);
        var items = new List<ObservedLootItem>();
        var seenLists = new HashSet<ulong>();
        for (int kind = 0; kind < 2; kind++)
        {
            ulong list = BitConverter.ToUInt64(listPointers, kind * 8);
            if (list == 0) continue;
            ObservedSurfaceReader.Pointer(list);
            if (!seenLists.Add(list)) throw new InvalidDataException("Duplicate loot lists.");
            byte[] header = r.Read(list, 25);
            if (BitConverter.ToUInt64(header) != image + layout.ListVtable || header[24] != kind)
                throw new InvalidDataException("Unsupported Advanced Loot list.");
            var (array, count) = Array(header, MaxEntries);
            var seenIds = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                ulong address = array + (ulong)(i * ItemStride);
                byte[] entry = r.Read(address, 0x60);
                long rawId = BitConverter.ToInt64(entry);
                int icon = BitConverter.ToInt32(entry, 0x48);
                uint lootId = BitConverter.ToUInt32(entry, 0x5c);
                if (rawId is < 1 or > 10_000_000 || icon is < 0 or > 10_000_000 || !seenIds.Add(lootId))
                    throw new InvalidDataException("Invalid or duplicate loot entry identity.");
                string name = ObservedSurfaceReader.Text(entry, 8, 64);
                byte[] details = r.Read(address + 0x70, 24);
                if (BitConverter.ToUInt64(details) != image + layout.SourceVtable)
                    throw new InvalidDataException("Unsupported loot-source array.");
                var (sourceArray, sourceCount) = Array(details, MaxSources);
                var sources = new List<LootSource>();
                var seenCorpses = new HashSet<uint>();
                for (int j = 0; j < sourceCount; j++)
                {
                    ulong sourceAddress = sourceArray + (ulong)(j * SourceStride);
                    byte[] source = r.Read(sourceAddress, SourceStride);
                    uint corpse = BitConverter.ToUInt32(source);
                    short quantity = BitConverter.ToInt16(source, 4);
                    if (corpse == 0 || quantity <= 0 || source[12] > 1 || !seenCorpses.Add(corpse))
                        throw new InvalidDataException("Invalid or repeated loot source.");
                    sources.Add(new(corpse, ObservedSurfaceReader.Text(source, 13, 64), quantity, "direct-loot-entry-source-record"));
                    r.Unchanged(sourceAddress, source);
                }
                items.Add(new(kind == 0 ? "shared" : "personal", (int)rawId, name, icon, lootId, sources.ToArray()));
                r.Unchanged(address, entry); r.Unchanged(address + 0x70, details);
            }
            r.Unchanged(list, header);
        }
        r.Unchanged(window + layout.Lists, listPointers);
        if (r.Root(layout.WindowRoot, layout.WindowVtable) != window || Identity.Decode(r.Read, image, identityLayout) != identity)
            throw new InvalidDataException("Character or Advanced Loot window changed.");
        return new(identity.Character, items.ToArray());
    }
    private static (ulong Array, int Count) Array(byte[] header, int limit)
    {
        ulong array = BitConverter.ToUInt64(header, 8);
        int count = BitConverter.ToInt32(header, 16), capacity = BitConverter.ToInt32(header, 20);
        if (count < 0 || count > limit || capacity < count || capacity > limit)
            throw new InvalidDataException("Loot array exceeds its supported bounds.");
        if (count > 0) ObservedSurfaceReader.Pointer(array);
        return (array, count);
    }
}
