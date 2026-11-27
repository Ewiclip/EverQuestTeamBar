using System.Text;

internal sealed record ItemDefinitionLayout(int Size, int ItemId, int Name, int LoreName, int IconId,
    int EquipSlots, int Classes, int Races, int RequiredLevel, int RecommendedLevel, int Tradeskill,
    int QuestItem, int Droppable, int Attuneable, int Heirloom, int LoreGroup, int ArmorClass,
    int HitPoints, int Mana, int Endurance, int Damage, int Delay, int AugmentationType, int StackSize)
{
    internal static readonly ItemDefinitionLayout Live = new(0x640, 0xb8, 0, 0x40, 0xc4,
        0xbc, 0x140, 0x144, 0x110, 0x114, 0xea, 0x5a8, 0xd1, 0xd2, 0xd3, 0xec, 0x10c,
        0x104, 0x108, 0x56c, 0x158, 0x152, 0x1d8, 0x590);

    // 2d97f027...72ac: all 120 initializer and 1442 deserializer instructions
    // match the previously validated ca050 build after address relocation.
    // All 21 exported details retain their member offsets and read widths.
    internal static readonly ItemDefinitionLayout Beta = Live with { };
    internal static ItemDefinitionLayout For(CollectorBuild build) => build == CollectorBuild.Beta ? Beta : Live;
}
internal sealed record InventoryMemoryLayout(ulong ProfileContainer, ulong ChildContainer, ItemMemoryLayout Item)
{
    internal static readonly InventoryMemoryLayout Live = new(0x20, 0x70, ItemMemoryLayout.Live);
    // The ItemClient vtable's first two methods return item+0x80 in this build.
    internal static readonly InventoryMemoryLayout Beta = new(0x20, 0x80, ItemMemoryLayout.Beta);
    internal static InventoryMemoryLayout For(CollectorBuild build) => build == CollectorBuild.Beta ? Beta : Live;
}

internal static class InventoryDecoder
{
    internal const int MaxItems = 2048;
    private const int MaxBytes = 8 * 1024 * 1024;

    internal static InventoryCapture Capture(int pid, long? expectedStartedUtcTicks = null, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.Live;
        using var memory = build.Open(pid, expectedStartedUtcTicks, CollectorCapability.Inventory);
        var snapshot = ReadStable(memory.Read, memory.Base, build);
        return new(CollectorFormat.SchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
            build.Channel, build.BuildSha256, build == CollectorBuild.Live ? CollectorFormat.Decoder : "beta-inventory-research-2", "inventory", CollectorFormat.DetailStatus,
            snapshot.Character.Name, snapshot.Character.Class, snapshot.Items);
    }

    internal static InventorySnapshot ReadStable(Func<ulong, int, byte[]> read, ulong image, CollectorBuild? build = null)
    {
        var first = Decode(read, image, build);
        var second = Decode(read, image, build);
        if (first.Character != second.Character || !first.Items.SequenceEqual(second.Items))
            throw new InvalidDataException("Inventory changed during collection. No partial capture was saved.");
        return second;
    }

    internal static InventorySnapshot Decode(Func<ulong, int, byte[]> reader, ulong image, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.Live;
        return Decode(reader, image, build.IdentityLayout, InventoryMemoryLayout.For(build), ItemDefinitionLayout.For(build));
    }
    internal static InventorySnapshot Decode(Func<ulong, int, byte[]> reader, ulong image, IdentityLayout identityLayout,
        InventoryMemoryLayout layout, ItemDefinitionLayout definitionLayout)
    {
        var r = new ObservedSurfaceReader(reader, image, MaxBytes);
        byte[] Read(ulong address, int size) => r.Read(address, size);
        ulong Ptr(ulong value)
        {
            if (!MemoryReader.Pointer(value) || (value & 7) != 0)
                throw new InvalidDataException("Invalid item pointer.");
            return value;
        }
        var identity = Identity.Decode(Read, image, identityLayout);
        var entries = new List<InventoryEntry>();
        var visited = new HashSet<ulong>();
        void VisitContainer(ulong address, int depth, string path)
        {
            byte[] header = Read(address, 0x28);
            uint slots = BitConverter.ToUInt32(header, 0), type = BitConverter.ToUInt32(header, 4);
            ulong array = BitConverter.ToUInt64(header, 8), length = BitConverter.ToUInt64(header, 0x10);
            uint capacity = BitConverter.ToUInt32(header, 0x18);
            if (slots > 64 || length > slots || capacity < length || capacity > 64 || type != 0
                || header[0x20] != depth || (depth == 0 && (slots != 36 || length != 36)))
                throw new InvalidDataException("Unsupported inventory-container layout.");
            if (length == 0) return;
            byte[] pointers = Read(Ptr(array), checked((int)length * 16));
            for (int slot = 0; slot < (int)length; slot++)
            {
                ulong item = BitConverter.ToUInt64(pointers, slot * 16);
                if (item == 0) continue;
                Ptr(item);
                if (!visited.Add(item) || entries.Count >= MaxItems)
                    throw new InvalidDataException("Inventory contains a cycle or exceeds the item limit.");
                string slotPath = path.Length == 0 ? slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{path}/{slot}";
                var before = r.ItemIdentity(item, layout.Item);
                ItemData data = r.Item(item, layout.Item, definitionLayout);
                entries.Add(new(slotPath, data));
                if (depth < 2) VisitContainer(item + layout.ChildContainer, depth + 1, slotPath);
                else
                {
                    // EQ's inventory index has three levels. A deeper nonempty container is unsupported.
                    byte[] child = Read(item + layout.ChildContainer, 0x20);
                    if (BitConverter.ToUInt64(child, 0x10) != 0)
                        throw new InvalidDataException("Unexpected nested item container.");
                }
                if (r.ItemIdentity(item, layout.Item) != before)
                    throw new InvalidDataException("Item changed during collection.");
            }
            byte[] after = Read(address, 0x28);
            if (!header.AsSpan(0, 0x21).SequenceEqual(after.AsSpan(0, 0x21))
                || !pointers.AsSpan().SequenceEqual(Read(array, pointers.Length)))
                throw new InvalidDataException("Inventory container changed during collection.");
        }
        VisitContainer(identity.Profile + layout.ProfileContainer, 0, "");
        if (Identity.Decode(Read, image, identityLayout) != identity)
            throw new InvalidDataException("Character changed during collection.");
        return new(identity.Character, entries.ToArray());
    }

    internal static ItemData DecodeItem(byte[] bytes, ItemDefinitionLayout? layout = null)
    {
        layout ??= ItemDefinitionLayout.Live;
        if (bytes.Length != layout.Size) throw new InvalidDataException("Incomplete item definition.");
        int I32(int offset) => BitConverter.ToInt32(bytes, offset);
        uint U32(int offset) => BitConverter.ToUInt32(bytes, offset);
        bool Flag(int offset) => bytes[offset] switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid item flag.") };
        string Text(int offset, int max)
        {
            int length = Array.IndexOf(bytes, (byte)0, offset, max) - offset;
            if (length < 0 || bytes.AsSpan(offset, length).ContainsAnyExceptInRange((byte)32, (byte)126))
                throw new InvalidDataException("Unrecognized item text encoding.");
            return Encoding.ASCII.GetString(bytes, offset, length);
        }
        var result = new ItemData(I32(layout.ItemId), Text(layout.Name, 64), new(Text(layout.LoreName, 80), I32(layout.IconId), U32(layout.EquipSlots),
            U32(layout.Classes), U32(layout.Races), I32(layout.RequiredLevel), I32(layout.RecommendedLevel), Flag(layout.Tradeskill), Flag(layout.QuestItem),
            Flag(layout.Droppable), Flag(layout.Attuneable), Flag(layout.Heirloom), I32(layout.LoreGroup), I32(layout.ArmorClass), I32(layout.HitPoints), I32(layout.Mana),
            I32(layout.Endurance), I32(layout.Damage), bytes[layout.Delay], I32(layout.AugmentationType), I32(layout.StackSize)));
        ValidateItem(result);
        return result;
    }

    internal static void ValidateItem(ItemData item)
    {
        static bool Text(string? value, int max, bool empty = false) => value is not null
            && value.Length <= max && (empty || value.Length > 0) && value.All(c => c is >= ' ' and <= '~');
        if (item is null || item.ItemId is < 1 or > 10_000_000 || !Text(item.Name, 63) || item.CandidateDetails is null)
            throw new InvalidDataException("Invalid item identity.");
        var d = item.CandidateDetails;
        // Retain raw masks: e.g. mercenary gear uses bits outside the 16 player-class bits.
        if (!Text(d.LoreName, 79, true) || d.IconId is < 0 or > 10_000_000
            || d.RequiredLevel is < 0 or > 255 || d.RecommendedLevel is < 0 or > 255
            || d.HitPoints is < -1_000_000 or > 1_000_000 || d.Mana is < -1_000_000 or > 1_000_000
            || d.Endurance is < -1_000_000 or > 1_000_000 || d.ArmorClass is < -100_000 or > 100_000
            || d.Damage is < 0 or > 1_000_000 || d.Delay is < 0 or > 255 || d.StackSize is < 0 or > 1_000_000)
            throw new InvalidDataException($"Item {item.ItemId} ({item.Name}) properties failed structural validation: "
                + System.Text.Json.JsonSerializer.Serialize(d));
    }
}
