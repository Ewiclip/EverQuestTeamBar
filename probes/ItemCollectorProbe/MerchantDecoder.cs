internal sealed record MerchantItem(int ItemId, string Name);
internal sealed record ObservedMerchant(string Name, int Level);
internal sealed record MerchantState(CharacterIdentity Character, ObservedZone Zone, bool IsOpen,
    ObservedMerchant? Merchant, MerchantItem[] Items, int ExcludedLimitedStock);
internal sealed record MerchantCapture(int SchemaVersion, DateTimeOffset ObservedAtUtc, string ClientChannel,
    string BuildSha256, string Decoder, string SourceKind, string CharacterName, string CharacterClass,
    ObservedZone Zone, bool IsOpen, ObservedMerchant? Merchant, MerchantItem[] Items, int ExcludedLimitedStock,
    string Completeness);

// The purchase page and active interaction NPC are independent of the current target.
// Only the explicit -1 sale-quantity sentinel qualifies. Stack size and price are not read.
internal static class MerchantDecoder
{
    internal const string Decoder = "live-unlimited-merchant-1";
    internal const ulong WindowRoot = 0xe120b0, WindowVtable = 0xa6dff8, ActiveNpcRoot = 0xec2fb8;
    internal const ulong PageVtable = 0xa6ddf8, ArrayVtable = 0xa6ddd8, Pages = 0x2e8, Visible = 0x1c1;
    internal const string Completeness = "Unlimited stock observed in the open merchant's loaded purchase list only. Limited stock is excluded. This is not a complete merchant catalog or evidence that an item was removed. No prices, purchases, personal inventory or guild identity are collected.";
    // Registration + named constructor; purchase page; stock getter, list display,
    // finite purchase limit; visibility; active interaction NPC used by merchant UI.
    internal static readonly (uint Rva, int Length)[] Evidence =
    [(0x1a1ee6, 0x40), (0x49b25a, 0x9d), (0x49c243, 0x46), (0x49e518, 0x43),
     (0x36fb90, 4), (0x4b0ca5, 12), (0x4b0d16, 0x42), (0x4a5022, 0x2a),
     (0x5dc8f4, 13), (0x5dca49, 7), (0x49f740, 0x23), (0x4a673e, 13)];

    internal static void RequireBuild(string hash)
    {
        if (hash != AdvancedLootMemoryLayout.NovemberBuildHash)
            throw new InvalidDataException("Merchant reading is not validated for this game build.");
    }
    private static void VerifyCode(IdentityResolver.Resolved image, MemoryReader memory)
    {
        RequireBuild(image.Hash);
        foreach (var (rva, count) in Evidence)
            if (!memory.Read(memory.Base + rva, count).AsSpan().SequenceEqual(image.FileBytes(rva, count)))
                throw new InvalidDataException("Loaded merchant code differs from the validated executable.");
    }
    internal static MerchantCapture Capture(int pid, long? expectedStartedUtcTicks = null, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.ForClient(CollectorIdentity.Read(pid));
        build.Require(CollectorCapability.Merchants);
        RequireBuild(build.BuildSha256);
        using var session = new TargetLayoutResolver.Session(pid, expectedStartedUtcTicks);
        if (session.Resolved.Image.Hash != build.BuildSha256) throw new InvalidDataException("Merchant build changed.");
        VerifyCode(session.Resolved.Image, session.Memory);
        var state = ReadStable(session.Memory.Read, session.Memory.Base, session.Target);
        VerifyCode(session.Resolved.Image, session.Memory); session.Verify();
        return new(1, DateTimeOffset.UtcNow, "Live", build.BuildSha256, Decoder, "merchant-unlimited",
            state.Character.Name, state.Character.Class, state.Zone, state.IsOpen, state.Merchant,
            state.Items, state.ExcludedLimitedStock, Completeness);
    }
    private sealed record StableState(MerchantState State, ulong Window, ulong Page, ulong Array,
        ulong NpcPointer, uint NpcId, string RowsHash);
    private sealed record ItemReference(ObservedItemIdentity Item, int Quantity);
    internal static MerchantState ReadStable(Func<ulong, int, byte[]> read, ulong image, TargetMemoryLayout targetLayout) =>
        ObservedSurfaceReader.Stable(() => Decode(read, image, targetLayout)).State;

    private static StableState Decode(Func<ulong, int, byte[]> read, ulong image, TargetMemoryLayout targetLayout)
    {
        var r = new ObservedSurfaceReader(read, image);
        // The target decoder validates identity, zone and spawn metadata. Rebind
        // only its root to the NPC the client is actually interacting with.
        var interactionLayout = targetLayout with { TargetRoot = ActiveNpcRoot };
        ulong window = r.U64(image + WindowRoot);
        if (window == 0)
        {
            var closedContext = TargetDecoder.Decode(r.Read, image, targetLayout, includeTarget: false);
            if (r.U64(image + WindowRoot) != 0) throw new InvalidDataException("Merchant window changed.");
            return new(new(closedContext.Character, closedContext.Zone, false, null, [], 0), 0, 0, 0, 0, 0, "");
        }
        if (r.Root(WindowRoot, WindowVtable) != window) throw new InvalidDataException("Merchant window changed.");
        byte visible = r.Read(window + Visible, 1)[0];
        if (visible > 1) throw new InvalidDataException("Unsupported merchant visibility.");
        if (visible == 0)
        {
            var closedContext = TargetDecoder.Decode(r.Read, image, targetLayout, includeTarget: false);
            if (r.Read(window + Visible, 1)[0] != 0 || r.Root(WindowRoot, WindowVtable) != window)
                throw new InvalidDataException("Merchant window changed.");
            return new(new(closedContext.Character, closedContext.Zone, false, null, [], 0), window, 0, 0, 0, 0, "");
        }
        var context = TargetDecoder.Decode(r.Read, image, interactionLayout);
        ulong npcPointer = r.U64(image + ActiveNpcRoot);
        if (context.Target is not { Type: "NPC", RawType: 1 } npc)
            throw new InvalidDataException("The open merchant has no verified NPC interaction.");
        byte[] pageHeader = r.Read(window + Pages, 24);
        ulong pageArray = ObservedSurfaceReader.Pointer(BitConverter.ToUInt64(pageHeader));
        ulong pageCount = BitConverter.ToUInt64(pageHeader, 8), pageCapacity = BitConverter.ToUInt64(pageHeader, 16);
        if (pageCount != 3 || pageCapacity < pageCount || pageCapacity > 16)
            throw new InvalidDataException("Unsupported merchant page collection.");
        ulong page = ObservedSurfaceReader.Pointer(r.U64(pageArray));
        void CheckPage()
        {
            if (r.U64(page) != image + PageVtable || r.U64(page + 0x30) != image + ArrayVtable
                || r.U64(page + 8) != window || r.I32(page + 0x10) != 200)
                throw new InvalidDataException("Unsupported merchant purchase page.");
        }
        CheckPage();
        byte[] header = r.Read(page + 0x38, 16);
        ulong array = BitConverter.ToUInt64(header);
        int count = BitConverter.ToInt32(header, 8), capacity = BitConverter.ToInt32(header, 12);
        if (count is < 0 or > 200 || capacity < count || capacity > 1024)
            throw new InvalidDataException("Merchant list exceeded its validated bounds.");
        byte[] rows = count == 0 ? [] : r.Read(ObservedSurfaceReader.Pointer(array), count * 0x18);
        var items = new Dictionary<int, MerchantItem>();
        var references = new List<ItemReference>();
        int excluded = 0;
        for (int index = 0; index < count; index++)
        {
            ulong pointer = BitConverter.ToUInt64(rows, index * 0x18);
            if (pointer == 0) throw new InvalidDataException("Merchant list is still loading.");
            var item = r.ItemIdentity(pointer, ItemMemoryLayout.November);
            int quantity = r.I32(pointer + 0x40);
            if (quantity < -1) throw new InvalidDataException("Unknown merchant stock value.");
            references.Add(new(item, quantity));
            if (quantity != -1) { excluded++; continue; }
            var next = new MerchantItem(item.ItemId, item.Name);
            if (items.TryGetValue(item.ItemId, out var prior) && prior != next)
                throw new InvalidDataException("Conflicting merchant item identities.");
            items[item.ItemId] = next;
        }
        foreach (var reference in references)
            if (r.I32(reference.Item.Pointer + 0x40) != reference.Quantity
                || r.ItemIdentity(reference.Item.Pointer, ItemMemoryLayout.November) != reference.Item)
                throw new InvalidDataException("Merchant item or stock changed during collection.");
        if (count > 0) r.Unchanged(array, rows);
        r.Unchanged(page + 0x38, header); r.Unchanged(window + Pages, pageHeader); CheckPage();
        if (r.Root(WindowRoot, WindowVtable) != window || r.Read(window + Visible, 1)[0] != 1
            || r.U64(pageArray) != page || r.U64(image + ActiveNpcRoot) != npcPointer
            || TargetDecoder.Decode(r.Read, image, interactionLayout) != context)
            throw new InvalidDataException("Merchant, zone or character changed during collection.");
        return new(new(context.Character, context.Zone, true, new(npc.Name, npc.Level),
            items.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray(), excluded),
            window, page, array, npcPointer, npc.SpawnId, CollectorFormat.Hash(new { rows, references }));
    }
}
