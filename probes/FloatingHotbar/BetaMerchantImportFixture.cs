using System.Text.Json;

namespace TeamBar;

// Explicit, one-shot research command for LOCAL API verification. It is not a
// polling capability, never initializes contribution settings, and has no sender.
internal static class BetaMerchantImportFixture
{
    internal const string Decoder = "beta-merchant-item-definition-research-1";
    internal const string ValidatedBuildHash = "ca05079325390770ba58390d78d52424d5fe6de15b78786c19255a453ca875a1";
    internal const string ExecutablePath = @"C:\Everquest-Beta\eqgame.exe";
    // This one-off research surface is independent of the collector's current
    // Beta registry. A later supported patch must not inherit these old roots.
    internal static readonly IdentityLayout ValidatedIdentityLayout = new(0xf341d8, 0xf36a78, 0xfb5530,
        0x5e4, 0x2890, 0x135, 0xb4, 0x2fe8, 0x2f58, 0x2f50, 0xfdc, 0x17cc, 0x17e8);
    internal static readonly ItemMemoryLayout ValidatedItemLayout = new(0xa4, 0x120, 0x18, 0x97e2e0);
    internal static readonly ItemDefinitionLayout ValidatedDefinitionLayout = new(0x640, 0xb8, 0, 0x40, 0xc4,
        0xbc, 0x140, 0x144, 0x110, 0x114, 0xea, 0x5a8, 0xd1, 0xd2, 0xd3, 0xec, 0x10c,
        0x104, 0x108, 0x56c, 0x158, 0x152, 0x1d8, 0x590);
    private sealed record Snapshot(CharacterIdentity Character, ulong Player, ulong Profile,
        ulong Window, ulong Page, ulong Array, string HeaderHash, ObservedItemIdentity[] References, ItemData[] Items);

    internal static ItemData ReadDefinition(ObservedSurfaceReader reader, ulong definition)
    {
        var layout = ValidatedDefinitionLayout;
        byte[] bytes = new byte[layout.Size];
        // Fill EVERY field consumed by DecodeItem; unused gaps stay unread.
        // In particular no sale/base value or instance quantity is requested.
        void Fill(int offset, int width) => reader.Read(definition + (uint)offset, width).CopyTo(bytes, offset);
        Fill(layout.Name, 64); Fill(layout.LoreName, 80);
        foreach (int offset in new[] { layout.ItemId, layout.IconId, layout.EquipSlots, layout.Classes, layout.Races,
            layout.RequiredLevel, layout.RecommendedLevel, layout.LoreGroup, layout.ArmorClass, layout.HitPoints,
            layout.Mana, layout.Endurance, layout.Damage, layout.AugmentationType, layout.StackSize }) Fill(offset, 4);
        foreach (int offset in new[] { layout.Tradeskill, layout.QuestItem, layout.Droppable, layout.Attuneable, layout.Heirloom, layout.Delay }) Fill(offset, 1);
        return InventoryDecoder.DecodeItem(bytes, layout);
    }

    internal static ItemData[] ReadStable(Func<ulong, int, byte[]> read, ulong image)
    {
        Snapshot Once()
        {
            var r = new ObservedSurfaceReader(read, image);
            var identity = Identity.Decode(r.Read, image, ValidatedIdentityLayout);
            ulong window = r.Root(0xe83698, 0xad6ce8);
            bool Open() => r.Read(window + 0xa9, 1)[0] == 1 && r.Read(window + 0x234, 1)[0] == 1;
            if (!Open()) throw new InvalidDataException("Open the Beta merchant window before capturing item definitions.");
            byte[] pagesHeader = r.Read(window + 0x2d8, 24);
            ulong pages = ObservedSurfaceReader.Pointer(BitConverter.ToUInt64(pagesHeader));
            ulong pageCount = BitConverter.ToUInt64(pagesHeader, 8), pageCapacity = BitConverter.ToUInt64(pagesHeader, 16);
            if (pageCount != 3 || pageCapacity < pageCount || pageCapacity > 16)
                throw new InvalidDataException("Unsupported item-definition page collection.");
            ulong page = ObservedSurfaceReader.Pointer(r.U64(pages));
            void PageType()
            {
                if (r.U64(page) != image + 0xad6ae8 || r.U64(page + 0x30) != image + 0xad6ac8)
                    throw new InvalidDataException("Item-definition page type changed.");
            }
            PageType();
            byte[] header = r.Read(page + 0x38, 16);
            ulong array = BitConverter.ToUInt64(header);
            int count = BitConverter.ToInt32(header, 8), capacity = BitConverter.ToInt32(header, 12);
            if (count is < 0 or > 200 || capacity < count || capacity > 1024)
                throw new InvalidDataException("Item-definition array exceeded its validated bounds.");
            var references = new List<ObservedItemIdentity>();
            var items = new Dictionary<int, ItemData>();
            for (int index = 0; index < count; index++)
            {
                // The other row members are deliberately never read.
                ulong pointer = r.U64(ObservedSurfaceReader.Pointer(array) + (uint)index * 0x18);
                var reference = r.ItemIdentity(pointer, ValidatedItemLayout);
                var item = ReadDefinition(r, reference.Definition);
                if (item.ItemId != reference.ItemId || item.Name != reference.Name
                    || r.ItemIdentity(pointer, ValidatedItemLayout) != reference)
                    throw new InvalidDataException("Item definition changed during collection.");
                if (items.TryGetValue(item.ItemId, out var prior) && prior != item)
                    throw new InvalidDataException("Conflicting definitions share an item ID in this reading.");
                items[item.ItemId] = item; references.Add(reference);
            }
            for (int index = 0; index < count; index++)
            {
                var reference = references[index];
                if (r.U64(array + (uint)index * 0x18) != reference.Pointer
                    || r.ItemIdentity(reference.Pointer, ValidatedItemLayout) != reference)
                    throw new InvalidDataException("Item-definition list changed during collection.");
            }
            PageType(); r.Unchanged(page + 0x38, header); r.Unchanged(window + 0x2d8, pagesHeader);
            if (!Open() || r.Root(0xe83698, 0xad6ce8) != window || r.U64(pages) != page
                || Identity.Decode(r.Read, image, ValidatedIdentityLayout) != identity)
                throw new InvalidDataException("Character or item-definition window changed during collection.");
            return new(identity.Character, identity.Player, identity.Profile, window, page, array,
                CollectorFormat.Hash(new { pagesHeader, header }), references.ToArray(), items.Values.OrderBy(item => item.ItemId).ToArray());
        }
        return ObservedSurfaceReader.Stable(Once).Items;
    }

    // Keep verification ahead of the first process-memory read. The callbacks
    // also let fixtures prove rejection without opening a real game process.
    internal static ItemData[] ReadVerified(Action<string> verifyExecutable, Func<ItemData[]> read)
    {
        verifyExecutable(ValidatedBuildHash);
        return read();
    }

    internal static void Capture(int pid, string baselinePath, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        string fixturesPath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".fixtures");
        if (File.Exists(output) || Directory.Exists(fixturesPath)) throw new IOException("Use a new report filename; previous import fixtures are preserved.");
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
        if (baseline.RootElement.GetProperty("buildSha256").GetString() != ValidatedBuildHash)
            throw new InvalidDataException("The baseline does not belong to the validated Beta build.");
        if (baseline.RootElement.GetProperty("processId").GetInt32() != pid)
            throw new InvalidDataException("The baseline belongs to a different client process.");
        long started = baseline.RootElement.GetProperty("StartedUtcTicks").GetInt64();
        var expected = baseline.RootElement.GetProperty("evidence").GetProperty("observation").GetProperty("itemDefinitions").EnumerateArray()
            .Select(item => (Id: item.GetProperty("itemId").GetInt32(), Name: item.GetProperty("name").GetString(), Icon: item.GetProperty("iconId").GetInt32()))
            .Distinct().OrderBy(item => item.Id).ToArray();
        ItemData[] items;
        using (var memory = new MemoryReader(pid, ExecutablePath, MemoryFeature.DataCollection))
        {
            if (memory.StartedUtcTicks != started)
                throw new InvalidDataException("The selected Beta process restarted. This fixture requires the original baseline session.");
            items = ReadVerified(hash => Identity.VerifyFile(memory.Path, hash), () => ReadStable(memory.Read, memory.Base));
        }
        if (items.Length == 0 || !items.Select(item => (item.ItemId, (string?)item.Name, item.CandidateDetails.IconId)).SequenceEqual(expected))
            throw new InvalidDataException("The open item list differs from the selected baseline. No import fixtures were written.");
        var source = new ContributionSource("Team Bar", "Beta", "1", ValidatedBuildHash, Decoder);
        var rejected = new List<ContributionRejection>();
        var records = ContributionPayload.Items(items, source, rejected.Add);
        if (rejected.Count != 0 || records.Length != items.Length) throw new InvalidDataException("Not every observed definition could be represented by the import schema.");
        var batches = ContributionPayload.Batches(records);
        Directory.CreateDirectory(fixturesPath);
        var fixtures = new List<object>();
        foreach (var batch in batches)
        {
            string file = $"batch-{fixtures.Count + 1:000}.json";
            using (var stream = new FileStream(Path.Combine(fixturesPath, file), FileMode.CreateNew)) stream.Write(batch.Body);
            fixtures.Add(new { file, bytes = batch.Body.Length, source = batch.Records[0].Source,
                records = batch.Records.Select(record => new { record.Kind, record.EntityId, record.Fingerprint }).ToArray() });
        }
        CollectorFormat.WriteNew(Path.Combine(fixturesPath, "observed-items.json"), new { source, items,
            scope = "item-definitions-only", limitation = "Beta observations are not evidence of any seller, price, stock, availability or Live content." });
        CollectorFormat.WriteNew(output, new { passed = true, fixtureCaptured = true, expectedKinds = new[] { "item" },
            tests = new[] { "exact Beta executable", "two complete matching reads", "baseline identities match", "all declared definition fields read individually", "no rejected definitions" },
            fixtures, fixtureDirectory = fixturesPath, privateSentinels = Array.Empty<string>(), itemCount = items.Length,
            processMemoryRead = true, gameplayInput = false, networkRequests = false, deployedApiRequests = false,
            limitation = "Fixture generation only. API verification is reported separately; no merchant collection capability is enabled." });
    }
}
