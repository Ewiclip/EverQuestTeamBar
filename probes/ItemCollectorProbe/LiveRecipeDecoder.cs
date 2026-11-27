internal sealed record LiveRecipeState(CharacterIdentity Character, bool IsOpen, string? ContainerName,
    int? ContainerItemId, int? SelectedRecipeId, LoadedRecipe[] Recipes);

// Current Live window layout. Recipe records keep their validated field offsets;
// the UI members and item instances changed independently in this patch.
internal static class LiveRecipeDecoder
{
    internal const string Decoder = "live-recipe-window-2";
    internal const ulong Root = 0xf5c478, Vtable = 0xacd8c8, Results = 0x3c0, Count = 0x6e0,
        Container = 0x6f0, ContainerLocation = 0x700, Selected = 0x710, Visible = 0x1c1;
    internal const string Completeness = "Partial observations from the open tradeskill window. Search or favorites load summaries; selecting a recipe may load its ingredient slots. Result item ID, yield, restrictions and full recipe coverage are not decoded. No automatic searches, selections or combines.";
    // Registration, named constructor, fixed array and count, summary response,
    // ingredient response/loaded flag, selection, and ordinary window visibility.
    internal static readonly (uint Rva, int Length)[] Evidence =
    [(0x1a2fcd, 0x44), (0x54cdaa, 0x61), (0x54d090, 0x2bc), (0x550b99, 0x46),
     (0x54e5a6, 0x45), (0x54e64d, 0x40), (0x551681, 0x23),
     (0x5509e1, 0x101), (0x550899, 0x2a), (0x5dc8f4, 13),
     (0x3d5dce, 0x20), (0x3d5b66, 0x12)];

    internal static void RequireBuild(string hash)
    {
        if (hash != AdvancedLootMemoryLayout.NovemberBuildHash)
            throw new InvalidDataException("Live recipe reading is not validated for this game build.");
    }
    private static void VerifyCode(IdentityResolver.Resolved image, MemoryReader memory)
    {
        RequireBuild(image.Hash);
        foreach (var (rva, size) in Evidence)
            if (!memory.Read(memory.Base + rva, size).AsSpan().SequenceEqual(image.FileBytes(rva, size)))
                throw new InvalidDataException("Loaded recipe code differs from the validated executable.");
    }
    internal static RecipeSnapshot Capture(int pid, long? startedUtcTicks, CollectorBuild build)
    {
        build.Require(CollectorCapability.Recipes); RequireBuild(build.BuildSha256);
        using var session = new TargetLayoutResolver.Session(pid, startedUtcTicks);
        if (session.Resolved.Image.Hash != build.BuildSha256) throw new InvalidDataException("Recipe build changed.");
        VerifyCode(session.Resolved.Image, session.Memory);
        var state = ReadStable(session.Memory.Read, session.Memory.Base, session.Target.Identity);
        VerifyCode(session.Resolved.Image, session.Memory); session.Verify();
        return new(1, DateTimeOffset.UtcNow, state.Character.Name, build.BuildSha256, "loaded-tradeskill-window-results",
            state.ContainerName, state.ContainerItemId, state.SelectedRecipeId, state.Recipes, Completeness,
            build.Channel, Decoder, state.IsOpen, state.Character.Class);
    }

    private sealed record StableState(LiveRecipeState State, ulong Window, ObservedItemIdentity? Container,
        int? Location, ulong Selected, string RecordsHash);
    private sealed record RecipeReference(ulong Pointer, byte[] Bytes);
    internal static LiveRecipeState ReadStable(Func<ulong, int, byte[]> read, ulong image, IdentityLayout identityLayout) =>
        ObservedSurfaceReader.Stable(() => Decode(read, image, identityLayout)).State;

    private static StableState Decode(Func<ulong, int, byte[]> read, ulong image, IdentityLayout identityLayout)
    {
        var r = new ObservedSurfaceReader(read, image);
        var identity = Identity.Decode(r.Read, image, identityLayout);
        ulong window = r.U64(image + Root);
        if (window != 0) ObservedSurfaceReader.Pointer(window);
        byte visibility = window == 0 ? (byte)0 : r.Read(window + Visible, 1)[0];
        if (window != 0 && r.U64(window) != image + Vtable || visibility > 1)
            throw new InvalidDataException("Tradeskill-window class or visibility validation failed.");
        void VerifyWindow()
        {
            if (r.U64(image + Root) != window || window != 0 && (r.U64(window) != image + Vtable || r.Read(window + Visible, 1)[0] != visibility)
                || Identity.Decode(r.Read, image, identityLayout) != identity)
                throw new InvalidDataException("Character or tradeskill window changed during collection.");
        }
        if (visibility == 0)
        {
            VerifyWindow();
            return new(new(identity.Character, false, null, null, null, []), window, null, null, 0, "");
        }
        int count = r.I32(window + Count);
        if (count is < 0 or > 100) throw new InvalidDataException("Tradeskill result count exceeded its validated bound.");
        byte[] pointers = count == 0 ? [] : r.Read(window + Results, count * 8);
        byte[] containerReference = r.Read(window + Container, 16);
        ulong container = BitConverter.ToUInt64(containerReference), selected = r.U64(window + Selected);
        byte[] locationBytes = r.Read(window + ContainerLocation, 10);
        int location = BitConverter.ToInt32(locationBytes);
        // The standing-container path constructs location 4, slot 0, no subslots.
        // The final two bytes of the copied 12-byte struct are padding, not identity.
        bool worldContainer = locationBytes.AsSpan().SequenceEqual(new byte[] { 4, 0, 0, 0, 0, 0, 255, 255, 255, 255 });
        ObservedItemIdentity? ReadContainer() => container == 0 ? null
            : r.ItemIdentity(container, ItemMemoryLayout.November, allowWorldContainer: worldContainer);
        var containerData = ReadContainer();
        if (count > 0 && containerData is null) throw new InvalidDataException("Recipe results have no verified container.");
        var recipes = new List<LoadedRecipe>(); var references = new List<RecipeReference>();
        var visited = new HashSet<ulong>(); var ids = new HashSet<int>(); int? selectedId = null;
        for (int index = 0; index < count; index++)
        {
            ulong pointer = ObservedSurfaceReader.Pointer(BitConverter.ToUInt64(pointers, index * 8));
            if (!visited.Add(pointer)) throw new InvalidDataException("Duplicate recipe pointers.");
            byte[] bytes = r.Read(pointer, 0x100);
            var recipe = RecipeDecoder.DecodeRecipe(bytes, r.Read);
            if (!ids.Add(recipe.RecipeId)) throw new InvalidDataException("Duplicate recipe identities.");
            references.Add(new(pointer, bytes)); recipes.Add(recipe);
            if (selected == pointer) selectedId = recipe.RecipeId;
        }
        foreach (var record in references) r.Unchanged(record.Pointer, record.Bytes);
        if (pointers.Length > 0) r.Unchanged(window + Results, pointers);
        r.Unchanged(window + Container, containerReference); r.Unchanged(window + ContainerLocation, locationBytes);
        if (r.I32(window + Count) != count || r.U64(window + Selected) != selected || ReadContainer() != containerData)
            throw new InvalidDataException("Tradeskill results, selection or container changed during collection.");
        VerifyWindow();
        return new(new(identity.Character, true, containerData?.Name, containerData?.ItemId, selectedId, recipes.ToArray()),
            window, containerData, location, selected, CollectorFormat.Hash(references));
    }
}
