using System.Text;

internal static class BetaRecipeTests
{
    internal static void Run(string output)
    {
        const ulong image = 0x140000000, game = 0x100000, pc = 0x200000, player = 0x300000,
            list = 0x400000, profile = 0x500000, window = 0x600000, recipe = 0x700000,
            container = 0x800000, definition = 0x900000, ingredientName = 0xa00000;
        var build = CollectorBuild.Beta; var layout = build.IdentityLayout;
        var data = new Dictionary<ulong, byte>(); var tests = new List<string>();
        void Bytes(ulong at, byte[] value) { for (int i = 0; i < value.Length; i++) data[at + (ulong)i] = value[i]; }
        void I32(ulong at, int value) => Bytes(at, BitConverter.GetBytes(value));
        void U64(ulong at, ulong value) => Bytes(at, BitConverter.GetBytes(value));
        void Name(ulong at, string value) => Bytes(at, Encoding.ASCII.GetBytes(value + "\0"));
        byte[] Read(ulong at, int count) => Enumerable.Range(0, count).Select(i => data.GetValueOrDefault(at + (ulong)i)).ToArray();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); tests.Add(message); }
        void Reject(Action action, string message)
        { bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } Check(failed, message); }
        U64(image + layout.GameRoot, game); I32(game + layout.GameState, 5);
        U64(image + layout.PlayerRoot, player); U64(image + layout.PcRoot, pc); U64(pc + layout.PcPlayer, player);
        Name(player + layout.PlayerName, "Fellwort"); Name(pc + layout.PcName, "Fellwort");
        U64(pc + layout.PcProfileList, list); U64(list + 8, profile);
        I32(player + layout.PlayerClass, 16); I32(profile + layout.ProfileClass, 16); I32(profile + layout.ProfileLevel, 130);
        U64(image + 0xfce870, window); U64(window, image + 0xb37778); U64(window + 0x3c8, recipe);
        U64(window + 0x718, recipe); U64(window + 0x6f8, container);
        U64(container, image + 0x97f2e0); U64(container + 0x118, definition); I32(container + 0x0c, 51977); I32(definition + 0xb8, 51977); Name(definition, "Fixture sewing kit");
        I32(recipe, 30); I32(recipe + 4, 26); I32(recipe + 8, 16); I32(recipe + 12, 51977); I32(recipe + 16, 2);
        Name(recipe + 0x14, "Patchwork Boots"); Bytes(recipe + 0x58, [1]); I32(recipe + 0x5c, 13783); I32(recipe + 0x84, 555);
        U64(recipe + 0xb0, ingredientName); I32(ingredientName + 4, 64); I32(ingredientName + 8, 15); Name(ingredientName + 24, "Ruined Cat Pelt");
        var captured = RecipeDecoder.Decode(Read, image, build);
        Check(captured.ClientChannel == "Beta" && captured.BuildSha256 == CollectorBuild.BetaBuildHash && captured.Decoder == "beta-recipe-window-2",
            "Beta recipe capture retains its exact channel, executable hash and decoder");
        Check(captured.SelectedRecipeId == 30 && captured.ContainerItemId == 51977 && captured.Recipes.Single().IngredientSlots[0].ItemId == 13783,
            "Beta-specific window/container offsets decode selected recipe and loaded ingredients");
        Reject(() => RecipeDecoder.Decode(Read, image), "default Live recipe decoder rejects Beta memory layout");
        Bytes(recipe + 0x58, [0]);
        Check(RecipeDecoder.Decode(Read, image, build).Recipes.Single().IngredientSlots.All(slot => slot.ItemId == 0 && slot.Name is null),
            "Beta summary never exposes stale unloaded ingredient slots");
        Bytes(recipe + 0x58, [1]); U64(window, image + 0xac1798);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "Live tradeskill vtable is invalid for a Beta snapshot");
        U64(window, image + 0xb36738);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "the previous Beta window class is rejected after the patch");
        U64(window, image + 0xb37778); I32(container + 0x0c, 1);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "Beta container instance and definition IDs must match");
        I32(container + 0x0c, 51977);
        U64(container + 0x118, 0); U64(container + 0xd8, definition); I32(container + 0x0c, 0);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "Beta zero-instance exception requires a world-container window location");
        I32(window + 0x708, 4);
        Check(RecipeDecoder.Decode(Read, image, build).ContainerItemId == 51977,
            "Beta world station uses its validated preferred definition when its instance ID is zero");
        I32(window + 0x708, 0); I32(window + 0x6f0, 4);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "the previous Beta location field cannot authorize a world-container exception");
        I32(window + 0x708, 4); I32(window + 0x6f0, 0);
        I32(container + 0x0c, 1);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "Beta world station still rejects a nonzero mismatched instance ID");
        I32(container + 0x0c, 0);
        int selections = 0;
        byte[] Changing(ulong at, int count)
        { var result = Read(at, count); if (at == window + 0x718 && ++selections == 2) result[0] ^= 8; return result; }
        Reject(() => RecipeDecoder.Decode(Changing, image, build), "Beta recipe selection change discards the snapshot");
        I32(ingredientName + 8, 256);
        Reject(() => RecipeDecoder.Decode(Read, image, build), "Beta ingredient strings stay bounded");
        CollectorFormat.WriteNew(output, new { passed = true, tests, gameInputSent = false, processMemoryRead = false });
    }
}
