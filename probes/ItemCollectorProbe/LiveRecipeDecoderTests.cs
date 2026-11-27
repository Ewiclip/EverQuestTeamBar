using System.Text;

internal static class LiveRecipeDecoderTests
{
    internal static object Run()
    {
        var passed = new List<string>();
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Live recipe assertion failed."); }
        void Test(string name, Action run) { run(); passed.Add(name); }
        void Reject(Action run)
        { try { run(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid recipe observation accepted."); }
        Test("current window decodes summaries and selected ingredients", () =>
        {
            var state = new Fixture().Capture();
            Check(state.IsOpen && state.ContainerItemId == 17906 && state.ContainerName == "Mixing Bowl"
                && state.SelectedRecipeId == 10261 && state.Recipes.Length == 2
                && state.Recipes[0].IngredientSlots.Single(s => s.ItemId == 36396).Name == "Minotaur Meat"
                && state.Recipes[0].IngredientSlots.Single(s => s.ItemId == 58271).Name == "Barbecue Sauce"
                && !state.Recipes[1].DetailsLoaded && state.Recipes[1].IngredientSlots.All(s => s.ItemId == 0));
        });
        Test("unloaded ingredients never follow stale string pointers", () =>
        {
            var f = new Fixture(); f.M.Write(Fixture.First + 0x58, [0]); f.M.U64(Fixture.First + 0xf0, ulong.MaxValue);
            Check(f.Capture().Recipes.All(r => !r.DetailsLoaded && r.IngredientSlots.All(s => s.ItemId == 0 && s.Name is null)));
        });
        Test("absent and closed windows never follow cached rows or containers", () =>
        {
            var f = new Fixture(); f.M.Write(Fixture.Window + LiveRecipeDecoder.Visible, [0]);
            f.M.U64(Fixture.Window + LiveRecipeDecoder.Container, ulong.MaxValue);
            f.M.U64(Fixture.Window + LiveRecipeDecoder.Results, ulong.MaxValue);
            Check(!f.Capture().IsOpen && f.Capture().Recipes.Length == 0);
            f.M.U64(Fixture.Image + LiveRecipeDecoder.Root, 0); Check(!f.Capture().IsOpen);
        });
        Test("zero results ignore stale tail slots", () =>
        { var f = new Fixture(); f.M.I32(Fixture.Window + LiveRecipeDecoder.Count, 0); Check(f.Capture().Recipes.Length == 0); });
        Test("unlisted selection is not traversed or reported as a loaded recipe", () =>
        { var f = new Fixture(); f.M.U64(Fixture.Window + LiveRecipeDecoder.Selected, 0x12345678); Check(f.Capture().SelectedRecipeId is null); });
        Test("legacy and Beta images cannot enable the new Live decoder", () =>
        { Reject(() => LiveRecipeDecoder.RequireBuild(Identity.BuildHash)); Reject(() => LiveRecipeDecoder.RequireBuild(CollectorBuild.BetaBuildHash)); });
        Test("standing forge identity is allowed only in the verified world location", () =>
        {
            var f = new Fixture(); f.WorldContainer();
            Check(f.Capture().ContainerName == "Forge - Standing" && f.Capture().ContainerItemId == 54258);
            f.M.I32(Fixture.Window + LiveRecipeDecoder.ContainerLocation, 0);
            Reject(() => f.Capture());
            // The shared item reader stays strict without recipe-only opt-in.
            Reject(() => new ObservedSurfaceReader(f.M.Read, Fixture.Image).ItemIdentity(Fixture.Container, ItemMemoryLayout.November));
        });
        foreach (var mutation in new (string Name, Action<Fixture> Change)[]
        {
            ("nonzero mismatched instance", f => f.M.I32(Fixture.Container + 0xc0, 1)),
            ("non-null fallback", f => f.M.U64(Fixture.Container + 0x118, Fixture.Definition)),
            ("null preferred definition", f => f.M.U64(Fixture.Container + 0x58, 0)),
            ("wrong world slot", f => f.M.Write(Fixture.Window + LiveRecipeDecoder.ContainerLocation + 4, [1])),
            ("wrong world subslot", f => f.M.Write(Fixture.Window + LiveRecipeDecoder.ContainerLocation + 6, [0]))
        }) Test("standing container rejects " + mutation.Name, () =>
        { var f = new Fixture(); f.WorldContainer(); mutation.Change(f); Reject(() => f.Capture()); });
        foreach (var mutation in new (string Name, Action<Fixture> Change)[]
        {
            ("wrong window class", f => f.M.U64(Fixture.Window, Fixture.Image + 0xac1798)),
            ("invalid visibility", f => f.M.Write(Fixture.Window + LiveRecipeDecoder.Visible, [2])),
            ("negative count", f => f.M.I32(Fixture.Window + LiveRecipeDecoder.Count, -1)),
            ("oversized count", f => f.M.I32(Fixture.Window + LiveRecipeDecoder.Count, 101)),
            ("null populated row", f => f.M.U64(Fixture.Window + LiveRecipeDecoder.Results, 0)),
            ("unaligned row", f => f.M.U64(Fixture.Window + LiveRecipeDecoder.Results, Fixture.First + 1)),
            ("duplicate pointer", f => f.M.U64(Fixture.Window + LiveRecipeDecoder.Results + 8, Fixture.First)),
            ("duplicate recipe ID", f => f.M.I32(Fixture.Second, 10261)),
            ("invalid loaded flag", f => f.M.Write(Fixture.First + 0x58, [2])),
            ("invalid ingredient ID", f => f.M.I32(Fixture.First + 0x7c, -1)),
            ("oversized ingredient string", f => f.M.I32(Fixture.Name1 + 8, 256)),
            ("missing container", f => f.M.U64(Fixture.Window + LiveRecipeDecoder.Container, 0)),
            ("wrong item class", f => f.M.U64(Fixture.Container, Fixture.Image)),
            ("mismatched container ID", f => f.M.I32(Fixture.Container + 0xc0, 1)),
            ("unvalidated zero-instance container", f => f.M.I32(Fixture.Container + 0xc0, 0)),
            ("loading character", f => f.M.I32(SurfaceDecoderTests.Fixture.Game + 0x5e4, 4))
        }) Test(mutation.Name + " rejects", () => { var f = new Fixture(); mutation.Change(f); Reject(() => f.Capture()); });
        foreach (var mutation in new (string Name, ulong Address, byte[] Changed)[]
        {
            ("selection", Fixture.Window + LiveRecipeDecoder.Selected, BitConverter.GetBytes(Fixture.Second)),
            ("count", Fixture.Window + LiveRecipeDecoder.Count, BitConverter.GetBytes(1)),
            ("window visibility", Fixture.Window + LiveRecipeDecoder.Visible, [0]),
            ("container location", Fixture.Window + LiveRecipeDecoder.ContainerLocation, new byte[10] { 4, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
        }) Test(mutation.Name + " changing during collection rejects", () =>
        {
            var f = new Fixture(); int reads = 0;
            Reject(() => f.Capture((a, n) => a == mutation.Address && ++reads == 2 ? mutation.Changed : f.M.Read(a, n)));
        });
        Test("partial recipe and string reads reject", () =>
        {
            foreach (ulong address in new[] { Fixture.First, Fixture.Name1 })
            { var f = new Fixture(); Reject(() => f.Capture((a, n) => a == address ? [0] : f.M.Read(a, n))); }
        });
        Test("details arriving between full snapshots reject the mixed observation", () =>
        {
            var f = new Fixture(); int windows = 0;
            Reject(() => f.Capture((a, n) =>
            { if (a == Fixture.Image + LiveRecipeDecoder.Root && ++windows == 3) f.M.Write(Fixture.First + 0x58, [0]); return f.M.Read(a, n); }));
        });
        return new { passed = true, tests = passed, gameInputSent = false, processMemoryRead = false, processMemoryWritten = false };
    }

    internal static RecipeSnapshot FixtureCapture(bool loaded)
    {
        var f = new Fixture(); if (!loaded) f.M.Write(Fixture.First + 0x58, [0]);
        var s = f.Capture();
        return new(1, new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero), s.Character.Name,
            AdvancedLootMemoryLayout.NovemberBuildHash, "loaded-tradeskill-window-results", s.ContainerName, s.ContainerItemId,
            s.SelectedRecipeId, s.Recipes, LiveRecipeDecoder.Completeness, "Live", LiveRecipeDecoder.Decoder, s.IsOpen, s.Character.Class);
    }
    private sealed class Fixture
    {
        internal const ulong Image = SurfaceDecoderTests.Fixture.Image, Window = 0x40000000, First = 0x41000000,
            Second = 0x42000000, Container = 0x43000000, Definition = 0x44000000, Name1 = 0x45000000, Name2 = 0x46000000;
        internal readonly SurfaceDecoderTests.Fixture M = new();
        internal Fixture()
        {
            M.Add(Image + LiveRecipeDecoder.Root, new byte[8]); M.U64(Image + LiveRecipeDecoder.Root, Window);
            M.Add(Window, new byte[0x740]); M.U64(Window, Image + LiveRecipeDecoder.Vtable); M.Write(Window + LiveRecipeDecoder.Visible, [1]);
            M.I32(Window + LiveRecipeDecoder.Count, 2); M.U64(Window + LiveRecipeDecoder.Results, First); M.U64(Window + LiveRecipeDecoder.Results + 8, Second);
            M.U64(Window + LiveRecipeDecoder.Selected, First); M.U64(Window + LiveRecipeDecoder.Container, Container);
            M.Add(Container, new byte[0x138]); M.Add(Definition, new byte[0x640]);
            M.U64(Container, Image + ItemMemoryLayout.November.Vtable!.Value); M.U64(Container + 0x118, Definition);
            M.I32(Container + 0xc0, 17906); M.I32(Definition + 0xb8, 17906); M.Write(Definition, Encoding.ASCII.GetBytes("Mixing Bowl\0"));
            foreach (var (pointer, id, name) in new[] { (First, 10261, "Barbequed Minotaur Meat"), (Second, 11195, "Barbequed Shark Fin") })
            {
                M.Add(pointer, new byte[0x100]); M.I32(pointer, id); M.I32(pointer + 4, 152); M.I32(pointer + 8, 14);
                M.I32(pointer + 12, 17906); M.I32(pointer + 16, 2); M.Write(pointer + 0x14, Encoding.ASCII.GetBytes(name + "\0"));
            }
            foreach (var (pointer, name) in new[] { (Name1, "Minotaur Meat"), (Name2, "Barbecue Sauce") })
            {
                M.Add(pointer, new byte[0x100]); M.I32(pointer + 4, 128); M.I32(pointer + 8, name.Length);
                M.Write(pointer + 24, Encoding.ASCII.GetBytes(name + "\0"));
            }
            M.Write(First + 0x58, [1]); M.I32(First + 0x7c, 36396); M.I32(First + 0x80, 58271);
            M.I32(First + 0xa4, 817); M.I32(First + 0xa8, 830); M.U64(First + 0xf0, Name1); M.U64(First + 0xf8, Name2);
        }
        internal LiveRecipeState Capture(Func<ulong, int, byte[]>? read = null) =>
            LiveRecipeDecoder.ReadStable(read ?? M.Read, Image, Identity.LiveLayout);
        internal void WorldContainer()
        {
            M.I32(Container + 0xc0, 0); M.U64(Container + 0x118, 0); M.U64(Container + 0x58, Definition);
            M.I32(Definition + 0xb8, 54258); M.Write(Definition, Encoding.ASCII.GetBytes("Forge - Standing\0"));
            M.Write(Window + LiveRecipeDecoder.ContainerLocation, [4, 0, 0, 0, 0, 0, 255, 255, 255, 255]);
        }
    }
}
