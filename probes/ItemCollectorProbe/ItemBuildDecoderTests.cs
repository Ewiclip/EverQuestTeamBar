using System.Text;

// Sparse synthetic memory only: no process access, game input or UI.
internal static class ItemBuildDecoderTests
{
    internal static object Run()
    {
        var passed = new List<string>();
        void Test(string name, Action test) { test(); passed.Add(name); }
        static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Item build decoder assertion failed."); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Expected item build decoder rejection.");
        }

        Test("November inspection reads intrinsic effects and never traverses augment instances", () =>
        {
            var f = new Fixture(CollectorBuild.Live, ItemMemoryLayout.November, InspectionMemoryLayout.November);
            f.I32(Fixture.Definition + 0x220, 20525);
            f.I32(Fixture.Child + 0xc0, 999); f.I32(Fixture.AlternateDefinition + 0x220, 999);
            f.AddChild(Fixture.Child);
            byte[] Read(ulong a, int n)
            {
                if (a >= Fixture.Child && a < Fixture.Child + 0x140) throw new InvalidOperationException("Inspection traversed an augment.");
                return f.Read(a, n);
            }
            InspectionState Decode() => InspectionDecoder.Decode(Read, Fixture.Image, Identity.LiveLayout,
                InspectionMemoryLayout.November, ItemMemoryLayout.November, ItemDefinitionLayout.Live, true);
            var result = ObservedSurfaceReader.Stable(Decode);
            Assert(result.Items.Single().Effects!.Single() == new ItemSpellReference("Click", 20525));
            f.U64(Fixture.Item, Fixture.Image + 0x97f2e0); Reject(() => Decode());
        });
        Test("November inspection rejects changed effect slots between stable reads", () =>
        {
            var f = new Fixture(CollectorBuild.Live, ItemMemoryLayout.November, InspectionMemoryLayout.November);
            int spell = 1;
            InspectionState Decode()
            {
                f.I32(Fixture.Definition + 0x220, spell++);
                return InspectionDecoder.Decode(f.Read, Fixture.Image, Identity.LiveLayout,
                    InspectionMemoryLayout.November, ItemMemoryLayout.November, ItemDefinitionLayout.Live, true);
            }
            Reject(() => ObservedSurfaceReader.Stable(Decode));
            f.I32(Fixture.Display + 0xaa8, 1); Reject(() => Decode());
        });
        foreach (var build in new[] { CollectorBuild.Live, CollectorBuild.Beta })
        {
            Test($"{build.Channel} inventory reads exact identity and all 21 fields", () =>
            {
                var f = new Fixture(build); var result = InventoryDecoder.ReadStable(f.Read, Fixture.Image, build);
                Assert(result.Character == new CharacterIdentity("Fixture", 16, 130) && result.Items.Length == 1
                    && result.Items[0].SlotPath == "0" && result.Items[0].Item == Fixture.Expected);
            });
            Test($"{build.Channel} inspection retains exact item properties", () =>
            {
                var f = new Fixture(build); var result = InspectionDecoder.Decode(f.Read, Fixture.Image, build);
                Assert(result.Items.Length == 1 && result.Items[0] == new InspectedItem(0, Fixture.Expected));
            });
            Test($"{build.Channel} preserves each boolean property independently", () =>
            {
                int[] offsets = [0xea, 0x5a8, 0xd1, 0xd2, 0xd3];
                for (int selected = 0; selected < offsets.Length; selected++)
                {
                    var f = new Fixture(build);
                    for (int i = 0; i < offsets.Length; i++) f.Write(Fixture.Definition + (ulong)offsets[i], [(byte)(i == selected ? 1 : 0)]);
                    var d = InventoryDecoder.Decode(f.Read, Fixture.Image, build).Items[0].Item.CandidateDetails;
                    bool[] flags = [d.Tradeskill, d.QuestItem, d.Droppable, d.Attuneable, d.Heirloom];
                    Assert(flags.Where((flag, i) => flag != (i == selected)).Count() == 0);
                }
            });
            Test($"{build.Channel} rejects instance-definition disagreement", () =>
            {
                var f = new Fixture(build); f.I32(Fixture.Item + f.Memory.InstanceId, 17);
                Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, build));
                Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, build));
            });
            Test($"{build.Channel} rejects invalid properties but preserves identity-only reads", () =>
            {
                var f = new Fixture(build); f.Write(Fixture.Definition + 0xea, [2]);
                var identity = new ObservedSurfaceReader(f.Read, Fixture.Image).ItemIdentity(Fixture.Item, f.Memory);
                Assert(identity.ItemId == 168091 && identity.Name == Fixture.Expected.Name);
                Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, build));
                Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, build));
            });
            Test($"{build.Channel} rejects inspection index changes during item read", () =>
            {
                var f = new Fixture(build);
                byte[] Read(ulong address, int count)
                {
                    var bytes = f.Read(address, count);
                    if (address == Fixture.Definition && count == 0x640) f.I32(Fixture.Display + f.DisplayLayout.WindowIndex, 1);
                    return bytes;
                }
                Reject(() => InspectionDecoder.Decode(Read, Fixture.Image, build));
            });
            Test($"{build.Channel} rejects partial definition reads", () =>
            {
                var f = new Fixture(build);
                byte[] Read(ulong a, int n) => a == Fixture.Definition && n == 0x640 ? [0] : f.Read(a, n);
                Reject(() => InventoryDecoder.Decode(Read, Fixture.Image, build));
                Reject(() => InspectionDecoder.Decode(Read, Fixture.Image, build));
            });
            Test($"{build.Channel} rejects nested item cycles", () =>
            {
                var f = new Fixture(build); f.AddChild(Fixture.Item);
                Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, build));
            });
            Test($"{build.Channel} rejects changing item properties between complete snapshots", () =>
            {
                var f = new Fixture(build); bool changed = false;
                byte[] Read(ulong address, int count)
                {
                    var bytes = f.Read(address, count);
                    if (!changed && address == Fixture.Definition && count == 0x640)
                    { changed = true; f.I32(Fixture.Definition + 0x104, 77); }
                    return bytes;
                }
                Reject(() => InventoryDecoder.ReadStable(Read, Fixture.Image, build));
            });
        }
        Test("Beta follows preferred definition and retains instance-specific properties", () =>
        {
            var f = new Fixture(CollectorBuild.Beta);
            f.U64(Fixture.Item + 0xd8, Fixture.AlternateDefinition);
            f.I32(Fixture.AlternateDefinition + 0x104, 321);
            var result = InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta);
            Assert(result.Items[0].Item.CandidateDetails.HitPoints == 321);
            Assert(new ObservedSurfaceReader(f.Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Beta).Definition == Fixture.AlternateDefinition);
        });
        Test("Beta falls back only when preferred definition is null", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.U64(Fixture.Item + 0xd8, 7);
            Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("Beta rejects preferred definition changes even when IDs match", () =>
        {
            var f = new Fixture(CollectorBuild.Beta);
            byte[] Read(ulong a, int n)
            {
                var bytes = f.Read(a, n);
                if (a == Fixture.Definition && n == 0x640) f.U64(Fixture.Item + 0xd8, Fixture.AlternateDefinition);
                return bytes;
            }
            Reject(() => InventoryDecoder.Decode(Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("Beta rejects fallback changes while preferred definition is active", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.U64(Fixture.Item + 0xd8, Fixture.AlternateDefinition);
            byte[] Read(ulong a, int n)
            {
                var bytes = f.Read(a, n);
                if (a == Fixture.AlternateDefinition && n == 0x640) f.U64(Fixture.Item + 0x118, Fixture.AlternateDefinition);
                return bytes;
            }
            Reject(() => InspectionDecoder.Decode(Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("Beta rejects wrong item vtable before reading definition", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.U64(Fixture.Item, Fixture.Image + 0x909ce0);
            Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("patched Beta rejects the retired item vtable", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.U64(Fixture.Item, Fixture.Image + 0x97e2e0);
            Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("patched Beta ignores retired instance members and child container", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.AddChild(Fixture.Child);
            f.I32(Fixture.Item + 0xa4, -1); f.U64(Fixture.Item + 0x18, 7);
            f.U64(Fixture.Item + 0x120, 7); f.I32(Fixture.Item + 0x20, 65);
            var inventory = InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta);
            Assert(inventory.Items.Length == 2 && inventory.Items[1].SlotPath == "0/0");
            Assert(InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta).Items[0].Item == Fixture.Expected);
        });
        Test("Beta reads new inspection index and ignores plausible old locations", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.I32(Fixture.Display + 0xc50, 5); f.I32(Fixture.Display + 0xab0, 5);
            f.I32(Fixture.Display + 0xd680, 5); f.U64(Fixture.Display + 0x598, 7);
            Assert(InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta).Items.Length == 1);
            f.I32(Fixture.Display + 0xd698, 5);
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("Beta item identity works with only bounded name and ID blocks", () =>
        {
            var f = new Fixture(CollectorBuild.Beta);
            byte[] Read(ulong a, int n)
            {
                if (a >= Fixture.Definition && a < Fixture.Definition + 0x640
                    && !((a == Fixture.Definition && n == 64) || (a == Fixture.Definition + 0xb8 && n == 4)))
                    throw new InvalidOperationException("Identity helper read an equipment property.");
                return f.Read(a, n);
            }
            Assert(new ObservedSurfaceReader(Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Beta).ItemId == 168091);
        });
        Test("Beta inventory preserves child slot path and rejects malformed child bounds", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.AddChild(Fixture.Child);
            var result = InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta);
            Assert(result.Items.Length == 2 && result.Items[1].SlotPath == "0/0" && result.Items[1].Item == Fixture.Expected);
            f.I32(Fixture.Item + 0x80, 65);
            Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("wrong build roots fail without fallback scanning", () =>
        {
            var beta = new Fixture(CollectorBuild.Beta); var live = new Fixture(CollectorBuild.Live);
            Reject(() => InventoryDecoder.Decode(beta.Read, Fixture.Image));
            Reject(() => InventoryDecoder.Decode(live.Read, Fixture.Image, CollectorBuild.Beta));
            Reject(() => InspectionDecoder.Decode(beta.Read, Fixture.Image));
        });
        Test("Beta inspection manager limit remains bounded to six", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.I32(Fixture.Manager + 8, 7);
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("Beta world-container identity requires explicit recipe-only opt-in", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.I32(Fixture.Item + 0x0c, 0);
            f.U64(Fixture.Item + 0xd8, Fixture.Definition); f.U64(Fixture.Item + 0x118, 0);
            var identity = new ObservedSurfaceReader(f.Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Beta, allowWorldContainer: true);
            Assert(identity.ItemId == 168091 && identity.InstanceId == 0 && identity.Definition == Fixture.Definition);
            Reject(() => new ObservedSurfaceReader(f.Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Beta));
            Reject(() => InventoryDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image, CollectorBuild.Beta));
        });
        Test("world-container opt-in never excuses mismatches or other zero-ID shapes", () =>
        {
            Action<Fixture>[] changes = [
                f => f.I32(Fixture.Item + 0x0c, 17),
                f => f.I32(Fixture.Item + 0x0c, -1),
                f => f.U64(Fixture.Item + 0x118, Fixture.Definition),
                f => { f.U64(Fixture.Item + 0xd8, 0); f.U64(Fixture.Item + 0x118, Fixture.Definition); },
                f => f.U64(Fixture.Item, Fixture.Image + 0x909ce0)
            ];
            foreach (var change in changes)
            {
                var f = new Fixture(CollectorBuild.Beta); f.I32(Fixture.Item + 0x0c, 0);
                f.U64(Fixture.Item + 0xd8, Fixture.Definition); f.U64(Fixture.Item + 0x118, 0); change(f);
                Reject(() => new ObservedSurfaceReader(f.Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Beta, allowWorldContainer: true));
            }
            var live = new Fixture(CollectorBuild.Live); live.I32(Fixture.Item + 0x11c, 0);
            Reject(() => new ObservedSurfaceReader(live.Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Live, allowWorldContainer: true));
        });
        Test("Beta world-container identity rechecks the raw instance ID", () =>
        {
            var f = new Fixture(CollectorBuild.Beta); f.I32(Fixture.Item + 0x0c, 0);
            f.U64(Fixture.Item + 0xd8, Fixture.Definition); f.U64(Fixture.Item + 0x118, 0);
            byte[] Read(ulong a, int n)
            {
                var bytes = f.Read(a, n);
                if (a == Fixture.Definition && n == 64) f.I32(Fixture.Item + 0x0c, 168091);
                return bytes;
            }
            Reject(() => new ObservedSurfaceReader(Read, Fixture.Image).ItemIdentity(Fixture.Item, ItemMemoryLayout.Beta, allowWorldContainer: true));
        });
        return new { passed = true, tests = passed, processMemoryRead = false, gameInputSent = false };
    }

    private sealed class Fixture
    {
        internal const ulong Image = 0x10000000, Game = 0x20000000, Player = 0x21000000,
            Pc = 0x22000000, Profile = 0x23000000, ProfileList = 0x24000000, Array = 0x25000000,
            Item = 0x26000000, Definition = 0x27000000, AlternateDefinition = 0x28000000,
            Child = 0x29000000, Children = 0x2a000000, Manager = 0x30000000, Bins = 0x31000000,
            Windows = 0x32000000, Display = 0x33000000;
        internal static readonly ItemData Expected = new(168091, "Ravenous Cracked Mask of the Vortex",
            new("Lore fixture", 13937, 0x80000004, 0x18000, 0x10000001, 125, 130,
                true, false, true, false, true, -1, 678, 12345, 23456, 34567, 89, 21, 0x80000, 1000));
        internal ItemMemoryLayout Memory { get; }
        internal InspectionMemoryLayout DisplayLayout { get; }
        private readonly CollectorBuild build;
        private readonly Dictionary<ulong, byte[]> blocks = [];
        internal Fixture(CollectorBuild build, ItemMemoryLayout? memory = null, InspectionMemoryLayout? display = null)
        {
            this.build = build; Memory = memory ?? ItemMemoryLayout.For(build); DisplayLayout = display ?? InspectionMemoryLayout.For(build);
            var id = build.IdentityLayout;
            foreach (ulong root in new[] { id.GameRoot, id.PlayerRoot, id.PcRoot, DisplayLayout.ManagerRoot }) blocks[Image + root] = new byte[8];
            blocks[Game] = new byte[0x600]; blocks[Player] = new byte[0x1100]; blocks[Pc] = new byte[0x3100];
            blocks[Profile] = new byte[0x1900]; blocks[ProfileList] = new byte[32]; blocks[Array] = new byte[36 * 16];
            blocks[Item] = new byte[0x140]; blocks[Child] = new byte[0x140]; blocks[Children] = new byte[16];
            blocks[Definition] = new byte[0x640]; blocks[AlternateDefinition] = new byte[0x640];
            blocks[Manager] = new byte[0x50]; blocks[Bins] = new byte[8]; blocks[Windows] = new byte[48];
            blocks[Fixture.Display] = new byte[build == CollectorBuild.Beta ? 0xd6a0 : 0xab8];
            U64(Image + id.GameRoot, Game); I32(Game + id.GameState, 5); U64(Image + id.PlayerRoot, Player); U64(Image + id.PcRoot, Pc);
            U64(Pc + id.PcPlayer, Player); Text(Player + id.PlayerName, "Fixture"); Text(Pc + id.PcName, "Fixture");
            U64(Pc + id.PcProfileList, ProfileList); U64(ProfileList + id.ListProfile, Profile);
            I32(Player + id.PlayerClass, 16); I32(Profile + id.ProfileClass, 16); I32(Profile + id.ProfileLevel, 130);
            Header(Profile + 0x20, 36, Array, 36, 0); U64(Array, Item);
            InitItem(Item, 1); InitItem(Child, 2);
            foreach (var definition in new[] { Definition, AlternateDefinition }) InitDefinition(definition);
            U64(Image + DisplayLayout.ManagerRoot, Manager); U64(Manager, Image + DisplayLayout.ManagerVtable);
            I32(Manager + 8, 1); I32(Manager + 12, 32); I32(Manager + 16, 31); I32(Manager + 20, 5);
            U64(Manager + 24, Bins); I32(Manager + 32, 1); I32(Manager + 0x48, 6);
            U64(Bins, Windows); U64(Windows, Fixture.Display); U64(Fixture.Display, Image + DisplayLayout.WindowVtable);
            U64(Fixture.Display + DisplayLayout.ItemReference, Item);
        }
        private void InitItem(ulong pointer, byte depth)
        {
            if (Memory.Vtable is ulong vtable) U64(pointer, Image + vtable);
            I32(pointer + Memory.InstanceId, 168091); U64(pointer + Memory.Definition, Definition);
            Header(pointer + (build == CollectorBuild.Beta ? 0x80UL : 0x70UL), 0, 0, 0, depth);
        }
        private void InitDefinition(ulong p)
        {
            Text(p, Expected.Name); Text(p + 0x40, "Lore fixture"); I32(p + 0xb8, 168091); I32(p + 0xc4, 13937);
            I32(p + 0xbc, unchecked((int)0x80000004)); I32(p + 0x140, 0x18000); I32(p + 0x144, 0x10000001);
            I32(p + 0x110, 125); I32(p + 0x114, 130); Write(p + 0xea, [1]); Write(p + 0x5a8, [0]);
            Write(p + 0xd1, [1, 0, 1]); I32(p + 0xec, -1); I32(p + 0x10c, 678); I32(p + 0x104, 12345);
            I32(p + 0x108, 23456); I32(p + 0x56c, 34567); I32(p + 0x158, 89); Write(p + 0x152, [21]);
            I32(p + 0x1d8, 0x80000); I32(p + 0x590, 1000);
        }
        private void Header(ulong p, int slots, ulong array, int length, byte depth)
        { I32(p, slots); U64(p + 8, array); U64(p + 16, (ulong)length); I32(p + 24, length); Write(p + 32, [depth]); }
        internal void AddChild(ulong child) { Header(Item + (build == CollectorBuild.Beta ? 0x80UL : 0x70UL), 1, Children, 1, 1); U64(Children, child); }
        private (byte[] Bytes, int Offset) Find(ulong address, int count)
        {
            foreach (var (start, bytes) in blocks)
                if (address >= start && address - start <= (ulong)bytes.Length && (ulong)count <= (ulong)bytes.Length - (address - start))
                    return (bytes, (int)(address - start));
            throw new InvalidDataException("Fixture read outside known memory blocks.");
        }
        internal byte[] Read(ulong address, int count) { var (bytes, offset) = Find(address, count); return bytes.AsSpan(offset, count).ToArray(); }
        internal void Write(ulong address, byte[] data) { var (bytes, offset) = Find(address, data.Length); data.CopyTo(bytes, offset); }
        internal void I32(ulong address, int value) => Write(address, BitConverter.GetBytes(value));
        internal void U64(ulong address, ulong value) => Write(address, BitConverter.GetBytes(value));
        private void Text(ulong address, string value) => Write(address, Encoding.ASCII.GetBytes(value + "\0"));
    }
}
