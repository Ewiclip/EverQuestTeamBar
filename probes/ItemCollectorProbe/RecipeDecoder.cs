using System.Text;

internal sealed record RecipeSlot(int Slot, int ItemId, int IconId, string? Name);
internal sealed record LoadedRecipe(int RecipeId, string Name, int Trivial, int ContainerType,
    int ContainerItemId, int RequiredSize, bool DetailsLoaded, RecipeSlot[] IngredientSlots)
{
    public int FilledIngredientSlots => IngredientSlots.Count(i => i.ItemId != 0);
    public string Summary => $"Recipe {RecipeId} · Trivial {Trivial} · " + (!DetailsLoaded ? "Summary only" : $"{FilledIngredientSlots} ingredient slots");
}
internal sealed record RecipeSnapshot(int SchemaVersion, DateTimeOffset ObservedAtUtc, string CharacterName,
    string BuildSha256, string Scope, string? ContainerName, int? ContainerItemId, int? SelectedRecipeId,
    LoadedRecipe[] Recipes, string Completeness, string ClientChannel = "Live", string Decoder = "live-recipe-window-1",
    bool? IsOpen = null, string? CharacterClass = null);

internal static class RecipeDecoder
{
    private sealed record Layout(ulong Root, ulong Vtable, ulong Results, ulong Selected, ulong Container, ulong? ContainerLocation = null);
    private static readonly Layout Live = new(0xf4d408, 0xac1798, 0x3c8, 0x718, 0x6f8);
    // Exact 2d97... constructor/result/selection handlers: the window grew by 0x18;
    // recipe records and their detail-loaded/ingredient fields remain unchanged.
    private static readonly Layout Beta = new(0xfce870, 0xb37778, 0x3c8, 0x718, 0x6f8, 0x708);

    internal static RecipeSnapshot Capture(int pid, long? expectedStartedUtcTicks = null, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.ForClient(CollectorIdentity.Read(pid));
        if (build.UsesResolvedIdentity) return LiveRecipeDecoder.Capture(pid, expectedStartedUtcTicks, build);
        using var memory = build.Open(pid, expectedStartedUtcTicks, CollectorCapability.Recipes);
        var first = Decode(memory.Read, memory.Base, build);
        var second = Decode(memory.Read, memory.Base, build);
        if (CollectorFormat.Hash(first with { ObservedAtUtc = default }) != CollectorFormat.Hash(second with { ObservedAtUtc = default }))
            throw new InvalidDataException("Recipe data is changing. Try the next reading.");
        return second;
    }
    internal static RecipeSnapshot Decode(Func<ulong, int, byte[]> read, ulong image, CollectorBuild? build = null)
    {
        build ??= CollectorBuild.Live;
        var layout = ReferenceEquals(build, CollectorBuild.Beta) ? Beta : Live;
        var identity = Identity.Decode(read, image, build.IdentityLayout);
        ulong Ptr(ulong p) => MemoryReader.Pointer(p) && (p & 7) == 0 ? p : throw new InvalidDataException("Invalid recipe pointer.");
        byte[] Read(ulong a, int n) { var b = read(a, n); return b.Length == n ? b : throw new InvalidDataException("Incomplete recipe read."); }
        ulong U64(ulong a) => BitConverter.ToUInt64(Read(a, 8));
        ulong window = Ptr(U64(image + layout.Root));
        if (U64(window) != image + layout.Vtable) throw new InvalidDataException("Tradeskill-window class validation failed.");
        // Constructor in this executable initializes this fixed 100-entry result array.
        byte[] resultPointers = Read(window + layout.Results, 100 * 8);
        ulong selected = U64(window + layout.Selected), container = U64(window + layout.Container);
        int? containerLocation = layout.ContainerLocation is { } location ? BitConverter.ToInt32(Read(window + location, 4)) : null;
        var itemReader = new ObservedSurfaceReader(read, image);
        ObservedItemIdentity? ReadContainer()
        {
            if (container == 0) return null;
            // Recipes need the container identity, not its equipment stats or flags.
            // Unsupported item properties must not block observations while it is open.
            return itemReader.ItemIdentity(container, ItemMemoryLayout.For(build), allowWorldContainer: containerLocation == 4);
        }
        var containerData = ReadContainer();
        var recipes = new List<LoadedRecipe>(); var visited = new HashSet<ulong>(); int? selectedId = null;
        for (int i = 0; i < 100; i++)
        {
            ulong pointer = BitConverter.ToUInt64(resultPointers, i * 8);
            if (pointer == 0) continue;
            if (!visited.Add(Ptr(pointer))) throw new InvalidDataException("Duplicate recipe pointers.");
            var recipe = DecodeRecipe(Read(pointer, 0x100), Read);
            recipes.Add(recipe); if (pointer == selected) selectedId = recipe.RecipeId;
        }
        if (U64(image + layout.Root) != window || U64(window + layout.Selected) != selected || U64(window + layout.Container) != container
            || (layout.ContainerLocation is { } currentLocation && BitConverter.ToInt32(Read(window + currentLocation, 4)) != containerLocation)
            || !resultPointers.AsSpan().SequenceEqual(Read(window + layout.Results, 800)) || ReadContainer() != containerData
            || Identity.Decode(read, image, build.IdentityLayout) != identity)
            throw new InvalidDataException("Tradeskill window changed during collection.");
        return new(1, DateTimeOffset.UtcNow, identity.Character.Name, build.BuildSha256, "loaded-tradeskill-window-results",
            containerData?.Name, containerData?.ItemId, selectedId, recipes.ToArray(),
            "Partial observations: ingredient slots may load only after selection. Result item ID, yield, restrictions and full recipe coverage are not decoded.",
            build.Channel, build.Channel == "Beta" ? "beta-recipe-window-2" : "live-recipe-window-1");
    }
    internal static LoadedRecipe DecodeRecipe(byte[] bytes, Func<ulong, int, byte[]>? read = null)
    {
        if (bytes.Length != 0x100) throw new InvalidDataException("Incomplete recipe record.");
        int I32(int offset) => BitConverter.ToInt32(bytes, offset);
        int length = Array.IndexOf(bytes, (byte)0, 0x14, 64) - 0x14;
        if (length is < 1 or > 63 || bytes.AsSpan(0x14, length).ContainsAnyExceptInRange((byte)32, (byte)126)
            || I32(0) is < 1 or > 10_000_000 || I32(4) is < 0 or > 2000 || I32(8) is < 0 or > 1000
            || I32(12) is < 0 or > 10_000_000 || I32(16) is < 0 or > 100 || bytes[0x58] > 1)
            throw new InvalidDataException("Unrecognized recipe record layout.");
        string? Name(int index)
        {
            ulong pointer = BitConverter.ToUInt64(bytes, 0xb0 + index * 8);
            if (pointer == 0 || read is null) return null;
            if (!MemoryReader.Pointer(pointer) || (pointer & 7) != 0) throw new InvalidDataException("Invalid ingredient string pointer.");
            byte[] header = read(pointer, 24);
            int capacity = BitConverter.ToInt32(header, 4), count = BitConverter.ToInt32(header, 8), encoding = BitConverter.ToInt32(header, 12);
            if (count is < 0 or > 255 || capacity < count + 1 || capacity > 4096 || encoding != 0)
                throw new InvalidDataException("Unsupported ingredient string.");
            byte[] text = read(pointer + 24, count + 1);
            if (text.Length != count + 1 || text[count] != 0 || text.AsSpan(0, count).ContainsAnyExceptInRange((byte)32, (byte)126))
                throw new InvalidDataException("Invalid ingredient name.");
            return Encoding.ASCII.GetString(text, 0, count);
        }
        // Local executable reads IDs at +0x5c, icons at +0x84 and CXStr names at +0xb0.
        // Its detail-response handler sets the loaded flag at +0x58 only after filling all ten slots.
        var slots = Enumerable.Range(0, 10).Select(i => new RecipeSlot(i + 1, bytes[0x58] == 1 ? I32(0x5c + i * 4) : 0, bytes[0x58] == 1 ? I32(0x84 + i * 4) : 0,
            I32(0x5c + i * 4) > 0 && bytes[0x58] == 1 ? Name(i) : null)).ToArray();
        if (slots.Any(s => s.ItemId is < 0 or > 10_000_000 || s.IconId is < 0 or > 10_000_000))
            throw new InvalidDataException("Invalid recipe ingredient fields.");
        return new(I32(0), Encoding.ASCII.GetString(bytes, 0x14, length), I32(4), I32(8), I32(12), I32(16), bytes[0x58] == 1, slots);
    }
}
