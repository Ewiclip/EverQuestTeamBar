using System.Text;
using System.Text.Json;

internal static class CollectorTests
{
    internal static object Run()
    {
        var passed = new List<string>();
        void Test(string name, Action test) { test(); passed.Add(name); }
        void ExpectFailure(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Expected the invalid capture to be rejected.");
        }
        void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Collector assertion failed."); }

        Test("bounded inventory and item identity", () =>
        {
            var f = new Fixture(); var result = InventoryDecoder.ReadStable(f.Read, Fixture.Image);
            Assert(result.Character.Name == "Fixture" && result.Items.Length == 1
                && result.Items[0].SlotPath == "0" && result.Items[0].Item.ItemId == 173940);
        });
        Test("loading fails closed", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Game + 0x5e4, 4);
            ExpectFailure(() => InventoryDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("instance and definition disagreement", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Item + 0x11c, 99);
            ExpectFailure(() => InventoryDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("oversized array rejected before allocation", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Profile + 0x30, ulong.MaxValue);
            ExpectFailure(() => InventoryDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("partial read rejected", () =>
        {
            var f = new Fixture();
            ExpectFailure(() => InventoryDecoder.Decode((a, n) => a == Fixture.Definition ? new byte[1] : f.Read(a, n), Fixture.Image));
        });
        Test("duplicate or cyclic item pointers rejected", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Array + 16, Fixture.Item);
            ExpectFailure(() => InventoryDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("changing container discarded", () =>
        {
            var f = new Fixture(); int calls = 0;
            byte[] Read(ulong address, int size)
            {
                byte[] bytes = f.Read(address, size);
                if (address == Fixture.Array && ++calls == 2) bytes[0] ^= 8;
                return bytes;
            }
            ExpectFailure(() => InventoryDecoder.Decode(Read, Fixture.Image));
        });
        Test("changing item between snapshots discarded", () =>
        {
            var f = new Fixture(); int calls = 0;
            byte[] Read(ulong address, int size)
            {
                byte[] bytes = f.Read(address, size);
                if (address == Fixture.Definition && size == 0x640 && ++calls == 1) f.I32(Fixture.Definition + 0x104, 123);
                return bytes;
            }
            ExpectFailure(() => InventoryDecoder.ReadStable(Read, Fixture.Image));
        });
        Test("invalid strings and flags rejected", () =>
        {
            var f = new Fixture(); byte[] definition = f.Read(Fixture.Definition, 0x640);
            definition[0xea] = 3; ExpectFailure(() => InventoryDecoder.DecodeItem(definition));
            definition[0xea] = 0; definition.AsSpan(0, 64).Fill(65);
            ExpectFailure(() => InventoryDecoder.DecodeItem(definition));
        });
        Test("non-player class mask bits retained", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Definition + 0x140, 65537);
            Assert(InventoryDecoder.Decode(f.Read, Fixture.Image).Items[0].Item.CandidateDetails.ClassesMask == 65537);
        });
        Test("recipe summaries stay distinct from loaded ingredients", () =>
        {
            byte[] recipe = new byte[0x100];
            BitConverter.GetBytes(10261).CopyTo(recipe, 0); BitConverter.GetBytes(152).CopyTo(recipe, 4);
            Encoding.ASCII.GetBytes("Barbequed Minotaur Meat\0").CopyTo(recipe, 0x14);
            var summary = RecipeDecoder.DecodeRecipe(recipe);
            Assert(!summary.DetailsLoaded && summary.FilledIngredientSlots == 0);
            recipe[0x58] = 1; BitConverter.GetBytes(36396).CopyTo(recipe, 0x7c); BitConverter.GetBytes(58271).CopyTo(recipe, 0x80);
            BitConverter.GetBytes(817).CopyTo(recipe, 0xa4); BitConverter.GetBytes(830).CopyTo(recipe, 0xa8);
            var loaded = RecipeDecoder.DecodeRecipe(recipe);
            Assert(loaded.DetailsLoaded && loaded.FilledIngredientSlots == 2
                && loaded.IngredientSlots[8].ItemId == 36396 && loaded.IngredientSlots[9].IconId == 830);
            recipe[0x58] = 2; ExpectFailure(() => RecipeDecoder.DecodeRecipe(recipe));
        });
        Test("bounded ingredient names reject bad lengths", () =>
        {
            byte[] recipe = new byte[0x100]; BitConverter.GetBytes(10).CopyTo(recipe, 0);
            Encoding.ASCII.GetBytes("Recipe\0").CopyTo(recipe, 0x14); recipe[0x58] = 1;
            BitConverter.GetBytes(36396).CopyTo(recipe, 0x5c); BitConverter.GetBytes(0x30000000UL).CopyTo(recipe, 0xb0);
            ExpectFailure(() => RecipeDecoder.DecodeRecipe(recipe, (_, _) => new byte[24]));
        });
        Test("open recipe window ignores unrelated container properties", () =>
        {
            var f = new Fixture(); f.Write(Fixture.ContainerDefinition + 0xea, [3]);
            // Inventory still requires a fully understood item definition. Recipe
            // observations need only this container's independently valid identity.
            ExpectFailure(() => InventoryDecoder.DecodeItem(f.Read(Fixture.ContainerDefinition, 0x640)));
            var result = RecipeDecoder.Decode(f.Read, Fixture.Image);
            Assert(result.CharacterName == "Fixture" && result.ContainerName == "Mixing Bowl"
                && result.ContainerItemId == 17906 && result.SelectedRecipeId is null
                && result.Recipes.Select(r => r.RecipeId).SequenceEqual([10261, 11195])
                && result.Recipes.All(r => !r.DetailsLoaded && r.FilledIngredientSlots == 0));
        });
        Test("recipe ingredients refresh while the same container stays open", () =>
        {
            var f = new Fixture(); f.Write(Fixture.ContainerDefinition + 0xea, [3]);
            var before = RecipeDecoder.Decode(f.Read, Fixture.Image);
            f.LoadRecipeDetails(); f.U64(Fixture.RecipeWindow + 0x718, Fixture.FirstRecipe);
            var after = RecipeDecoder.Decode(f.Read, Fixture.Image);
            var loaded = after.Recipes.Single(r => r.RecipeId == 10261);
            Assert(before.ContainerItemId == after.ContainerItemId && after.ContainerName == "Mixing Bowl"
                && !before.Recipes[0].DetailsLoaded && after.SelectedRecipeId == 10261
                && loaded.DetailsLoaded && loaded.FilledIngredientSlots == 2
                && loaded.IngredientSlots[8] == new RecipeSlot(9, 36396, 817, "Minotaur Meat")
                && loaded.IngredientSlots[9] == new RecipeSlot(10, 58271, 830, "Barbecue Sauce")
                && !after.Recipes.Single(r => r.RecipeId == 11195).DetailsLoaded);
        });
        Test("recipe selection changes without closing the container", () =>
        {
            var f = new Fixture(); f.LoadRecipeDetails();
            f.U64(Fixture.RecipeWindow + 0x718, Fixture.FirstRecipe);
            var first = RecipeDecoder.Decode(f.Read, Fixture.Image);
            f.U64(Fixture.RecipeWindow + 0x718, Fixture.SecondRecipe);
            var second = RecipeDecoder.Decode(f.Read, Fixture.Image);
            Assert(first.SelectedRecipeId == 10261 && second.SelectedRecipeId == 11195
                && first.ContainerItemId == second.ContainerItemId && second.ContainerName == "Mixing Bowl"
                && CollectorFormat.Hash(first.Recipes) == CollectorFormat.Hash(second.Recipes));
        });
        Test("closing a recipe container preserves cached results", () =>
        {
            var f = new Fixture(); f.LoadRecipeDetails();
            f.U64(Fixture.RecipeWindow + 0x718, Fixture.FirstRecipe);
            var open = RecipeDecoder.Decode(f.Read, Fixture.Image);
            f.U64(Fixture.RecipeWindow + 0x6f8, 0); f.U64(Fixture.RecipeWindow + 0x718, 0);
            var closed = RecipeDecoder.Decode(f.Read, Fixture.Image);
            Assert(open.ContainerItemId == 17906 && closed.ContainerItemId is null && closed.ContainerName is null
                && closed.SelectedRecipeId is null && closed.Recipes.Length == 2
                && CollectorFormat.Hash(open.Recipes) == CollectorFormat.Hash(closed.Recipes));
        });
        Test("recipe container identity rejects invalid names and IDs", () =>
        {
            Action<Fixture>[] invalid = [
                f => f.Write(Fixture.ContainerDefinition, [0]),
                f => f.Write(Fixture.ContainerDefinition, Enumerable.Repeat((byte)'A', 64).ToArray()),
                f => f.Write(Fixture.ContainerDefinition, [31, 0]),
                f => f.I32(Fixture.ContainerDefinition + 0xb8, 0),
                f => f.I32(Fixture.ContainerDefinition + 0xb8, -1),
                f => f.I32(Fixture.ContainerDefinition + 0xb8, 10_000_001),
                f => f.I32(Fixture.Container + 0x11c, 0),
                f => f.I32(Fixture.Container + 0x11c, 17907)
            ];
            foreach (var mutate in invalid)
            {
                var f = new Fixture(); mutate(f);
                ExpectFailure(() => RecipeDecoder.Decode(f.Read, Fixture.Image));
            }
        });
        Test("recipe container pointers remain validated", () =>
        {
            foreach (ulong pointer in new ulong[] { 0x1234, Fixture.Container + 1, 0x0000800000000000 })
            {
                var f = new Fixture(); f.U64(Fixture.RecipeWindow + 0x6f8, pointer);
                ExpectFailure(() => RecipeDecoder.Decode(f.Read, Fixture.Image));
            }
            foreach (ulong pointer in new ulong[] { 0, Fixture.ContainerDefinition + 1, 0x0000800000000000 })
            {
                var f = new Fixture(); f.U64(Fixture.Container + 0x128, pointer);
                ExpectFailure(() => RecipeDecoder.Decode(f.Read, Fixture.Image));
            }
        });
        Test("recipe container mutations during enumeration are rejected", () =>
        {
            Action<Fixture>[] mutations = [
                f => f.I32(Fixture.Container + 0x11c, 17907),
                f => f.I32(Fixture.ContainerDefinition + 0xb8, 17907),
                f => { f.I32(Fixture.Container + 0x11c, 17907); f.I32(Fixture.ContainerDefinition + 0xb8, 17907); },
                // The replacement has identical ID/name; pointer stability itself
                // must prevent mixing two different item-definition lifetimes.
                f => f.U64(Fixture.Container + 0x128, Fixture.ReplacementContainerDefinition),
                f => f.Write(Fixture.ContainerDefinition, Encoding.ASCII.GetBytes("Another Bowl\0")),
                f => f.U64(Fixture.RecipeWindow + 0x6f8, 0)
            ];
            foreach (var mutate in mutations)
            {
                var f = new Fixture(); bool changed = false;
                byte[] Read(ulong address, int size)
                {
                    byte[] bytes = f.Read(address, size);
                    // Trigger inside recipe enumeration, independent of how the
                    // decoder divides its reads of the preceding container fields.
                    if (!changed && address < Fixture.FirstRecipe + 0x100 && address + (ulong)size > Fixture.FirstRecipe)
                    { changed = true; mutate(f); }
                    return bytes;
                }
                ExpectFailure(() => RecipeDecoder.Decode(Read, Fixture.Image));
                Assert(changed);
            }
        });

        var fixture = new Fixture(); var snapshot = InventoryDecoder.Decode(fixture.Read, Fixture.Image);
        var capture = new InventoryCapture(1, Guid.NewGuid(), new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero),
            "Live", Identity.BuildHash, CollectorFormat.Decoder, "inventory", CollectorFormat.DetailStatus,
            "PrivateCharacter", "Cleric", snapshot.Items);
        Test("duplicate captures and repeated slots do not inflate evidence", () =>
        {
            var c = capture with { Items = [snapshot.Items[0], snapshot.Items[0] with { SlotPath = "1" }] };
            var catalog = Catalog.Build([c, c]);
            Assert(catalog.Items.Length == 1 && catalog.Items[0].Versions.Length == 1
                && catalog.Items[0].Versions[0].Evidence.Length == 1);
        });
        Test("property changes preserve versions with first and last seen", () =>
        {
            var original = snapshot.Items[0];
            var changed = original with { Item = original.Item with { CandidateDetails = original.Item.CandidateDetails with { HitPoints = 42 } } };
            var later = capture with { CaptureId = Guid.NewGuid(), ObservedAtUtc = capture.ObservedAtUtc.AddMinutes(1), Items = [changed] };
            var catalog = Catalog.Build([later, capture]);
            Assert(catalog.Items[0].Versions.Length == 2 && catalog.Items[0].FirstSeenUtc == capture.ObservedAtUtc
                && catalog.Items[0].LastSeenUtc == later.ObservedAtUtc);
        });
        Test("Live and Test or unknown decoder captures cannot mix", () =>
        {
            ExpectFailure(() => Catalog.Build([capture with { ClientChannel = "Test" }]));
            ExpectFailure(() => Catalog.Build([capture with { BuildSha256 = "unknown" }]));
            ExpectFailure(() => Catalog.Build([capture with { Decoder = "unknown" }]));
        });
        Test("Beta catalog export preserves its validated source and cannot mix with Live", () =>
        {
            var beta = capture with { ClientChannel = "Beta", BuildSha256 = CollectorBuild.BetaBuildHash, Decoder = "beta-inventory-research-2" };
            ExpectFailure(() => Catalog.Build([beta with { Decoder = "beta-inventory-research-1" }]));
            var catalog = Catalog.Build([beta]);
            Assert(catalog.ClientChannel == "Beta" && catalog.Items.Length == 1
                && catalog.Items[0].Versions.All(v => v.BuildSha256 == CollectorBuild.BetaBuildHash && v.Decoder == beta.Decoder));
            ExpectFailure(() => Catalog.Build([capture, beta with { CaptureId = Guid.NewGuid() }]));
            ExpectFailure(() => Catalog.Build([beta with { BuildSha256 = Identity.BuildHash }]));
            ExpectFailure(() => Catalog.Build([beta with { Decoder = CollectorFormat.Decoder }]));
        });
        Test("inventory cannot claim a drop source", () =>
        {
            ExpectFailure(() => Catalog.Build([capture with { SourceKind = "corpse" }]));
            var catalog = Catalog.Build([capture]);
            string json = JsonSerializer.Serialize(catalog, CollectorFormat.Json);
            Assert(!json.Contains("PrivateCharacter") && !json.Contains("slotPath") && !json.Contains("dropSource")
                && catalog.Items[0].Versions[0].Evidence[0].SourceKind == "inventory");
        });
        Test("conflicting capture IDs fail instead of overwriting history", () =>
            ExpectFailure(() => Catalog.Build([capture, capture with { CharacterName = "Different" }])));
        Test("existing output preserved", () =>
        {
            string file = Path.Combine(Path.GetTempPath(), "team-bar-item-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                CollectorFormat.WriteNew(file, new { original = true });
                byte[] before = File.ReadAllBytes(file);
                bool rejected = false;
                try { CollectorFormat.WriteNew(file, new { original = false }); } catch (IOException) { rejected = true; }
                Assert(rejected && before.AsSpan().SequenceEqual(File.ReadAllBytes(file)));
            }
            finally { if (File.Exists(file)) File.Delete(file); }
        });
        return new { passed = true, tests = passed, gameInputSent = false, websiteRequestsSent = false };
    }

    private sealed class Fixture
    {
        internal const ulong Image = 0x10000000, Game = 0x20000000, Player = 0x21000000,
            Pc = 0x22000000, Profile = 0x23000000, List = 0x24000000, Array = 0x25000000,
            Item = 0x26000000, Definition = 0x27000000, RecipeWindow = 0x28000000,
            FirstRecipe = 0x29000000, SecondRecipe = 0x2a000000, Container = 0x2b000000,
            ContainerDefinition = 0x2c000000, ReplacementContainerDefinition = 0x2d000000,
            FirstIngredientName = 0x2e000000, SecondIngredientName = 0x2f000000;
        private readonly Dictionary<ulong, byte[]> blocks = [];
        internal Fixture()
        {
            blocks[Image + 0xf35250] = new byte[8]; blocks[Image + 0xeb6440] = new byte[8];
            blocks[Image + 0xeb5fb0] = new byte[8]; blocks[Image + 0xf4d408] = new byte[8];
            blocks[Game] = new byte[0x600]; blocks[Player] = new byte[0x1100]; blocks[Pc] = new byte[0x3100];
            blocks[Profile] = new byte[0x1900]; blocks[List] = new byte[0x20]; blocks[Array] = new byte[36 * 16];
            blocks[Item] = new byte[0x138]; blocks[Definition] = new byte[0x640];
            blocks[RecipeWindow] = new byte[0x740]; blocks[FirstRecipe] = new byte[0x100]; blocks[SecondRecipe] = new byte[0x100];
            blocks[Container] = new byte[0x138]; blocks[ContainerDefinition] = new byte[0x640];
            blocks[ReplacementContainerDefinition] = new byte[0x640];
            blocks[FirstIngredientName] = new byte[0x100]; blocks[SecondIngredientName] = new byte[0x100];
            U64(Image + 0xf35250, Game); I32(Game + 0x5e4, 5); U64(Image + 0xeb6440, Player); U64(Image + 0xeb5fb0, Pc);
            U64(Pc + 0x2848, Player); Write(Player + 0xb4, Encoding.ASCII.GetBytes("Fixture\0"));
            Write(Pc + 0x2fa0, Encoding.ASCII.GetBytes("Fixture\0")); U64(Pc + 0x2f08, List); U64(List + 8, Profile);
            I32(Player + 0xfe4, 2); I32(Profile + 0x17cc, 2); I32(Profile + 0x17e8, 130);
            I32(Profile + 0x20, 36); U64(Profile + 0x28, Array); U64(Profile + 0x30, 36); I32(Profile + 0x38, 36);
            U64(Array, Item); I32(Item + 0x11c, 173940); U64(Item + 0x128, Definition); Write(Item + 0x90, [1]);
            Write(Definition, Encoding.ASCII.GetBytes("Defender's Charm of Rebellion\0"));
            I32(Definition + 0xb8, 173940); I32(Definition + 0x140, 65535); I32(Definition + 0x590, 1);
            U64(Image + 0xf4d408, RecipeWindow); U64(RecipeWindow, Image + 0xac1798);
            U64(RecipeWindow + 0x3c8, FirstRecipe); U64(RecipeWindow + 0x3d0, SecondRecipe);
            U64(RecipeWindow + 0x6f8, Container); I32(Container + 0x11c, 17906); U64(Container + 0x128, ContainerDefinition);
            foreach (ulong definition in new[] { ContainerDefinition, ReplacementContainerDefinition })
            {
                Write(definition, Encoding.ASCII.GetBytes("Mixing Bowl\0")); I32(definition + 0xb8, 17906);
            }
            foreach (var (record, id, name) in new[] {
                (FirstRecipe, 10261, "Barbequed Minotaur Meat"), (SecondRecipe, 11195, "Barbequed Shark Fin") })
            {
                I32(record, id); I32(record + 4, 152); I32(record + 8, 14); I32(record + 12, 17906); I32(record + 16, 2);
                Write(record + 0x14, Encoding.ASCII.GetBytes(name + "\0"));
            }
            foreach (var (address, text) in new[] { (FirstIngredientName, "Minotaur Meat"), (SecondIngredientName, "Barbecue Sauce") })
            {
                I32(address + 4, 128); I32(address + 8, text.Length); I32(address + 12, 0);
                Write(address + 24, Encoding.ASCII.GetBytes(text + "\0"));
            }
        }
        internal void LoadRecipeDetails()
        {
            Write(FirstRecipe + 0x58, [1]); I32(FirstRecipe + 0x7c, 36396); I32(FirstRecipe + 0x80, 58271);
            I32(FirstRecipe + 0xa4, 817); I32(FirstRecipe + 0xa8, 830);
            U64(FirstRecipe + 0xf0, FirstIngredientName); U64(FirstRecipe + 0xf8, SecondIngredientName);
        }
        private (byte[] Block, int Offset) Find(ulong address, int size)
        {
            foreach (var (start, block) in blocks)
                if (address >= start && address - start <= (ulong)block.Length && (ulong)size <= (ulong)block.Length - (address - start))
                    return (block, (int)(address - start));
            throw new InvalidDataException("Fixture invalid read.");
        }
        internal byte[] Read(ulong address, int size) { var (b, o) = Find(address, size); return b.AsSpan(o, size).ToArray(); }
        internal void Write(ulong address, byte[] data) { var (b, o) = Find(address, data.Length); data.CopyTo(b, o); }
        internal void U64(ulong address, ulong value) => Write(address, BitConverter.GetBytes(value));
        internal void I32(ulong address, int value) => Write(address, BitConverter.GetBytes(value));
    }
}
