internal sealed record ItemSpellReference(string Kind, int SpellId, string? Name = null);
internal sealed record InspectedItem(int WindowIndex, ItemData Item, ItemSpellReference[]? Effects = null);
internal sealed record InspectionState(CharacterIdentity Character, InspectedItem[] Items);
internal sealed record InspectionCapture(int SchemaVersion, Guid CaptureId, DateTimeOffset ObservedAtUtc,
    string ClientChannel, string BuildSha256, string Decoder, string SourceKind, string DetailStatus,
    string CharacterName, string CharacterClass, InspectedItem[] Items, string Completeness);

internal sealed record InspectionMemoryLayout(ulong ManagerRoot, ulong ManagerVtable, ulong WindowVtable,
    ulong WindowIndex, ulong ItemReference)
{
    internal static readonly InspectionMemoryLayout Live = new(0xf4b5a0, 0x8dc1e0, 0xa3d8a0, 0xab0, 0x3f8);
    // 2d97f027...72ac: named ItemDisplayWindow constructor 0x482240,
    // item-method calls use +0x5b0; manager insertion writes index +0xd698.
    internal static readonly InspectionMemoryLayout Beta = new(0xfcca00, 0x9511e0, 0xab3738, 0xd698, 0x5b0);
    internal static readonly InspectionMemoryLayout November = new(0xf5a610, 0x8e8180, 0xa498a0, 0xaa8, 0x3f0);
    internal static InspectionMemoryLayout For(CollectorBuild build) => build == CollectorBuild.Beta ? Beta
        : build.BuildSha256 == AdvancedLootMemoryLayout.NovemberBuildHash ? November : Live;
    // Named-window constructor, manager root/insertion, definition getter and ID
    // assignment, and the seven-slot effect initializer. Exact image only.
    internal static readonly (uint Rva, int Length)[] NovemberEvidence =
    [(0x436a21, 10), (0x1a073e, 0x55), (0x44f5a1, 7), (0x2c9f40, 17),
     (0x2ca631, 19), (0x2ca59a, 12), (0x6cc6d0, 0x92), (0x5670b0, 0x91)];
    internal static void VerifyCode(IdentityResolver.Resolved image, Func<ulong, int, byte[]> read, ulong imageBase)
    {
        if (image.Hash != AdvancedLootMemoryLayout.NovemberBuildHash)
            throw new InvalidDataException("Inspection is not validated for this build.");
        foreach (var (rva, length) in NovemberEvidence)
            if (!read(imageBase + rva, length).AsSpan().SequenceEqual(image.FileBytes(rva, length)))
                throw new InvalidDataException("Loaded inspection code differs from the validated executable.");
    }
}

internal static class InspectionDecoder
{
    internal const string Decoder = "live-item-display-1";
    internal const ulong ManagerRoot = 0xf4b5a0, ManagerVtable = 0x8dc1e0, WindowVtable = 0xa3d8a0;
    internal static InspectionCapture Capture(int pid, long? expectedStartedUtcTicks = null, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.ForClient(CollectorIdentity.Read(pid));
        if (build.UsesResolvedIdentity)
        {
            build.Require(CollectorCapability.Inspection);
            using var session = new TargetLayoutResolver.Session(pid, expectedStartedUtcTicks);
            var m = session.Memory;
            if (session.Resolved.Image.Hash != build.BuildSha256) throw new InvalidDataException("Inspection build changed.");
            InspectionMemoryLayout.VerifyCode(session.Resolved.Image, m.Read, m.Base);
            var current = ObservedSurfaceReader.Stable(() => Decode(m.Read, m.Base, session.Target.Identity,
                InspectionMemoryLayout.For(build), ItemMemoryLayout.For(build), ItemDefinitionLayout.For(build), true));
            InspectionMemoryLayout.VerifyCode(session.Resolved.Image, m.Read, m.Base);
            session.Verify();
            return Result(current, build);
        }
        using var memory = build.Open(pid, expectedStartedUtcTicks, CollectorCapability.Inspection);
        var state = ObservedSurfaceReader.Stable(() => Decode(memory.Read, memory.Base, build.IdentityLayout,
            InspectionMemoryLayout.For(build), ItemMemoryLayout.For(build), ItemDefinitionLayout.For(build), true));
        return Result(state, build);
    }
    private static InspectionCapture Result(InspectionState state, CollectorBuild build)
    {
        var names = SpellNameCatalog.ReadObserved(state.Items.SelectMany(i => i.Effects ?? []).Select(e => e.SpellId),
            Path.Combine(build.InstallationPath, "spells_us.txt"));
        var items = state.Items.Select(i => i with { Effects = i.Effects?.Select(e => e with { Name = e.Name ?? names.Names.GetValueOrDefault(e.SpellId) }).ToArray() }).ToArray();
        return new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, build.Channel, build.BuildSha256,
            build.Channel == "Live" ? "live-item-display-effects-3" : "beta-item-display-effects-3",
            "item-display", CollectorFormat.DetailStatus, state.Character.Name, state.Character.Class, items,
            "Items retained by the item-display manager, including cached windows. Effects belong only to the displayed item's definition; augment slots are not traversed. Inspection is not ownership or loot evidence.");
    }
    internal static InspectionState Decode(Func<ulong, int, byte[]> read, ulong image, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.Live;
        return Decode(read, image, build.IdentityLayout, InspectionMemoryLayout.For(build), ItemMemoryLayout.For(build), ItemDefinitionLayout.For(build));
    }
    internal static InspectionState Decode(Func<ulong, int, byte[]> read, ulong image, IdentityLayout identityLayout,
        InspectionMemoryLayout layout, ItemMemoryLayout itemLayout, ItemDefinitionLayout definitionLayout, bool includeEffects = false)
    {
        var r = new ObservedSurfaceReader(read, image);
        var identity = Identity.Decode(r.Read, image, identityLayout);
        ulong manager = r.Root(layout.ManagerRoot, layout.ManagerVtable);
        byte[] header = r.Read(manager + 8, 0x20);
        int count = BitConverter.ToInt32(header, 0), binSize = BitConverter.ToInt32(header, 4), mask = BitConverter.ToInt32(header, 8), shift = BitConverter.ToInt32(header, 12);
        ulong bins = BitConverter.ToUInt64(header, 16);
        int binCount = BitConverter.ToInt32(header, 24), maximum = r.I32(manager + 0x48);
        if (maximum != 6 || count is < 0 or > 6 || binSize != 32 || mask != 31 || shift != 5
            || binCount is < 0 or > 1 || (count > 0 && binCount != 1))
            throw new InvalidDataException("Unsupported item-display manager layout.");
        var items = new List<InspectedItem>();
        if (count > 0)
        {
            ulong bin = ObservedSurfaceReader.Pointer(r.U64(ObservedSurfaceReader.Pointer(bins)));
            byte[] pointers = r.Read(bin, count * 8);
            var visited = new HashSet<ulong>();
            for (int i = 0; i < count; i++)
            {
                ulong window = ObservedSurfaceReader.Pointer(BitConverter.ToUInt64(pointers, i * 8));
                if (!visited.Add(window) || r.U64(window) != image + layout.WindowVtable || r.I32(window + layout.WindowIndex) != i)
                    throw new InvalidDataException("Item-display window identity mismatch.");
                byte[] reference = r.Read(window + layout.ItemReference, 16);
                ulong item = BitConverter.ToUInt64(reference);
                if (item != 0)
                {
                    var identityBefore = r.ItemIdentity(item, itemLayout);
                    var definition = r.Item(item, itemLayout, definitionLayout);
                    // Read only this definition, never item-window aggregated text
                    // or child/augment containers. Keep effects out of inventory equality.
                    ItemSpellReference[]? effects = includeEffects ? DecodeEffects(r.Read(identityBefore.Definition + 0x220, 7 * 0x64)) : null;
                    if (r.ItemIdentity(item, itemLayout) != identityBefore) throw new InvalidDataException("Inspected item changed.");
                    items.Add(new(i, definition, effects));
                }
                r.Unchanged(window + layout.ItemReference, reference);
                if (r.U64(window) != image + layout.WindowVtable || r.I32(window + layout.WindowIndex) != i)
                    throw new InvalidDataException("Item-display window changed.");
            }
            r.Unchanged(bin, pointers);
            if (r.U64(bins) != bin) throw new InvalidDataException("Item-display array changed.");
        }
        r.Unchanged(manager + 8, header);
        if (r.Root(layout.ManagerRoot, layout.ManagerVtable) != manager || Identity.Decode(r.Read, image, identityLayout) != identity)
            throw new InvalidDataException("Character or item-display manager changed.");
        return new(identity.Character, items.ToArray());
    }
    internal static ItemSpellReference[] DecodeEffects(byte[] slots)
    {
        if (slots.Length != 7 * 0x64) throw new InvalidDataException("Invalid item effect array.");
        string[] kinds = ["Click", "Proc", "Worn", "Focus", "Scroll", "Focus2", "Blessing"];
        var result = new List<ItemSpellReference>();
        for (int slot = 0; slot < kinds.Length; slot++)
        {
            int id = BitConverter.ToInt32(slots, slot * 0x64);
            if (id is 0 or -1) continue;
            if (id is < 1 or > 1_000_000) throw new InvalidDataException("Invalid item spell identity.");
            int start = slot * 0x64 + 0x20;
            string? name = slots[start] == 0 ? null : ObservedSurfaceReader.Text(slots, start, 64);
            result.Add(new(kinds[slot], id, name));
        }
        return result.ToArray();
    }
}
