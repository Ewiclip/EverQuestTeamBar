using System.Text;

internal sealed record ItemMemoryLayout(ulong InstanceId, ulong Definition, ulong? PreferredDefinition = null, ulong? Vtable = null)
{
    internal static readonly ItemMemoryLayout Live = new(0x11c, 0x128);
    // 2d97f027...72ac: getter 0x30bf30 prefers +0xd8, then +0x118.
    // Deserializer 0x30c620 copies definition ID into +0x0c; vtable +0x28
    // points to that getter. These members changed independently of the UI.
    internal static readonly ItemMemoryLayout Beta = new(0x0c, 0x118, 0xd8, 0x97f2e0);
    internal static readonly ItemMemoryLayout November = new(0xc0, 0x118, 0x58, 0x915c80);
    internal static ItemMemoryLayout For(CollectorBuild build) => build == CollectorBuild.Beta ? Beta
        : build.BuildSha256 == AdvancedLootMemoryLayout.NovemberBuildHash ? November : Live;
}
internal sealed record ObservedItemIdentity(ulong Pointer, ulong Definition, int ItemId, string Name,
    ulong BaseDefinition, ulong PreferredDefinition, int InstanceId);

// Shared bounded, read-only access for the installed-build UI decoders.
internal sealed class ObservedSurfaceReader(Func<ulong, int, byte[]> reader, ulong image, int byteLimit = 4 * 1024 * 1024)
{
    private int bytes;
    internal ulong Image => image;
    internal byte[] Read(ulong address, int count)
    {
        if (!MemoryReader.Pointer(address) || count is <= 0 or > 65536
            || address + (ulong)count >= 0x0000800000000000 || (bytes += count) > byteLimit)
            throw new InvalidDataException("Observation exceeded its read bounds.");
        var value = reader(address, count);
        if (value.Length != count) throw new InvalidDataException("Incomplete observation read.");
        return value;
    }
    internal static ulong Pointer(ulong value) => MemoryReader.Pointer(value) && (value & 7) == 0
        ? value : throw new InvalidDataException("Invalid observation pointer.");
    internal ulong U64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
    internal int I32(ulong address) => BitConverter.ToInt32(Read(address, 4));
    internal ulong Root(ulong rva, ulong vtable)
    {
        ulong value = Pointer(U64(image + rva));
        if (U64(value) != image + vtable) throw new InvalidDataException("Unsupported observation object type.");
        return value;
    }
    internal void Unchanged(ulong address, byte[] before)
    {
        if (!before.AsSpan().SequenceEqual(Read(address, before.Length)))
            throw new InvalidDataException("Observed window data changed during collection.");
    }
    internal static string Text(byte[] value, int offset, int capacity)
    {
        int length = Array.IndexOf(value, (byte)0, offset, capacity) - offset;
        if (length < 1 || value.AsSpan(offset, length).ContainsAnyExceptInRange((byte)32, (byte)126))
            throw new InvalidDataException("Invalid observed name.");
        return Encoding.ASCII.GetString(value, offset, length);
    }
    // Identity-only reads are shared with recipes. Unvalidated equipment fields
    // must not prevent a recipe container from being identified safely.
    internal ObservedItemIdentity ItemIdentity(ulong pointer, ItemMemoryLayout? layout = null, bool allowWorldContainer = false)
    {
        layout ??= ItemMemoryLayout.Live;
        pointer = Pointer(pointer);
        void CheckType()
        {
            if (layout.Vtable is ulong vtable && U64(pointer) != image + vtable)
                throw new InvalidDataException("Unsupported item object type.");
        }
        CheckType();
        int id = I32(pointer + layout.InstanceId);
        ulong baseDefinition = U64(pointer + layout.Definition);
        ulong preferredDefinition = layout.PreferredDefinition is ulong preferred ? U64(pointer + preferred) : 0;
        ulong definition = Pointer(preferredDefinition != 0 ? preferredDefinition : baseDefinition);
        string name = Text(Read(definition, 64), 0, 64);
        int definitionId = I32(definition + 0xb8);
        // Validated standing containers use only a preferred definition with
        // a zero instance ID/fallback (also observed on the November Live forge).
        // Only recipe readers with a verified world location opt into this shape;
        // inventory, inspected items and merchant captures remain strict.
        bool worldContainer = allowWorldContainer && (layout == ItemMemoryLayout.Beta || layout == ItemMemoryLayout.November)
            && id == 0 && preferredDefinition != 0 && baseDefinition == 0;
        if (definitionId is < 1 or > 10_000_000 || (!worldContainer && definitionId != id)
            || I32(pointer + layout.InstanceId) != id || U64(pointer + layout.Definition) != baseDefinition
            || (layout.PreferredDefinition is ulong again && U64(pointer + again) != preferredDefinition))
            throw new InvalidDataException("Observed item identity changed.");
        CheckType();
        return new(pointer, definition, definitionId, name, baseDefinition, preferredDefinition, id);
    }
    internal ItemData Item(ulong pointer, ItemMemoryLayout? memoryLayout = null, ItemDefinitionLayout? definitionLayout = null)
    {
        definitionLayout ??= ItemDefinitionLayout.Live;
        var identity = ItemIdentity(pointer, memoryLayout);
        var item = InventoryDecoder.DecodeItem(Read(identity.Definition, definitionLayout.Size), definitionLayout);
        if (item.ItemId != identity.ItemId || item.Name != identity.Name || ItemIdentity(pointer, memoryLayout) != identity)
            throw new InvalidDataException("Observed item identity changed.");
        return item;
    }
    internal static MemoryReader Open(int pid, long? expectedStartedUtcTicks)
    {
        // Legacy surfaces remain pinned to Live until they gain their own
        // build-aware decoder. This method must not silently admit Beta.
        return CollectorBuild.Live.Open(pid, expectedStartedUtcTicks, CollectorCapability.Identity);
    }
    internal static T Stable<T>(Func<T> decode)
    {
        T first = decode(), second = decode();
        if (CollectorFormat.Hash(first) != CollectorFormat.Hash(second))
            throw new InvalidDataException("Observed window data is changing. No snapshot was saved.");
        return second;
    }
}
