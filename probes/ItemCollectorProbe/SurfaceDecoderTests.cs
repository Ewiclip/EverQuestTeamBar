using System.Text;

internal static class SurfaceDecoderTests
{
    internal static object Run()
    {
        var passed = new List<string>();
        void Test(string name, Action action) { action(); passed.Add(name); }
        void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Surface decoder assertion failed."); }
        void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid observation was accepted.");
        }
        Test("inspection decodes an item without any possessions", () =>
        {
            var f = new Fixture(); var result = InspectionDecoder.Decode(f.Read, Fixture.Image);
            Assert(result.Items.Single().Item.ItemId == 120780 && result.Items[0].Item.Name == "Ornament");
        });
        Test("empty item window retains no false item", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Display + 0x3f8, 0);
            Assert(InspectionDecoder.Decode(f.Read, Fixture.Image).Items.Length == 0);
        });
        Test("item display rejects excessive window count", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Manager + 8, 7);
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("item display rejects wrong object type", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Display, Fixture.Image + 1);
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("item display rejects changed item pointer", () =>
        {
            var f = new Fixture(); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Display + 0x3f8 && ++reads == 2) b[0] ^= 8; return b; }
            Reject(() => InspectionDecoder.Decode(Read, Fixture.Image));
        });
        Test("partial definition rejects whole inspection", () =>
        {
            var f = new Fixture(); Reject(() => InspectionDecoder.Decode((a, n) => a == Fixture.Definition ? [0] : f.Read(a, n), Fixture.Image));
        });
        Test("item instance and definition IDs must agree", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Item + 0x11c, 2);
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("direct loot source preserves item, corpse, name and quantity", () =>
        {
            var f = new Fixture(); var result = AdvancedLootDecoder.Decode(f.Read, Fixture.Image);
            var item = result.Items.Single(); var source = item.Sources.Single();
            Assert(item.List == "shared" && item.ItemId == 172583 && item.LootId == 59
                && source.CorpseId == 26922 && source.Name == "an overcharged octodrone" && source.StackCount == 2
                && source.Evidence == "direct-loot-entry-source-record");
        });
        Test("loot without source stays unassociated", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Entries + 0x80, 0);
            Assert(AdvancedLootDecoder.Decode(f.Read, Fixture.Image).Items.Single().Sources.Length == 0);
        });
        Test("personal loot retains its separate list identity", () =>
        {
            var f = new Fixture(); f.U64(Fixture.LootWindow + 0x3f0, 0); f.U64(Fixture.LootWindow + 0x3f8, Fixture.LootList);
            f.Write(Fixture.LootList + 24, [1]);
            var item = AdvancedLootDecoder.Decode(f.Read, Fixture.Image).Items.Single();
            Assert(item.List == "personal" && item.Sources.Single().CorpseId == 26922);
        });
        Test("empty loot array produces zero observations", () =>
        {
            var f = new Fixture(); f.I32(Fixture.LootList + 16, 0);
            Assert(AdvancedLootDecoder.Decode(f.Read, Fixture.Image).Items.Length == 0);
        });
        Test("loot count bounds prevent excessive reads", () =>
        {
            var f = new Fixture(); f.I32(Fixture.LootList + 16, 257); f.I32(Fixture.LootList + 20, 257);
            Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("source count bounds prevent excessive reads", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Entries + 0x80, 129); f.I32(Fixture.Entries + 0x84, 129);
            Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("source identity and positive quantity required", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Sources, 0); Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image));
            f.I32(Fixture.Sources, 26922); f.I32(Fixture.Sources + 4, 0); Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("unterminated source name fails closed", () =>
        {
            var f = new Fixture(); f.Write(Fixture.Sources + 13, Enumerable.Repeat((byte)65, 64).ToArray());
            Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("changed loot source rejects capture", () =>
        {
            var f = new Fixture(); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Sources && ++reads == 2) b[0] ^= 1; return b; }
            Reject(() => AdvancedLootDecoder.Decode(Read, Fixture.Image));
        });
        Test("same item from two corpses remains two associations", () =>
        {
            var f = new Fixture(); var second = f.Read(Fixture.Sources, 0x50);
            BitConverter.GetBytes(26923).CopyTo(second, 0); f.Write(Fixture.Sources + 0x50, second);
            f.I32(Fixture.Entries + 0x80, 2); f.I32(Fixture.Entries + 0x84, 2);
            Assert(AdvancedLootDecoder.Decode(f.Read, Fixture.Image).Items.Single().Sources.Length == 2);
        });
        Test("November loot layout keeps repeated item rows and distinct corpse identities", () =>
        {
            var layout = AdvancedLootMemoryLayout.November; var f = new Fixture(layout);
            f.Write(Fixture.Entries + 0xb0, f.Read(Fixture.Entries, 0xb0));
            f.I32(Fixture.Entries + 0xb0 + 0x5c, 60);
            f.U64(Fixture.Entries + 0xb0 + 0x78, Fixture.Sources + 0x50);
            f.Write(Fixture.Sources + 0x50, f.Read(Fixture.Sources, 0x50)); f.I32(Fixture.Sources + 0x50, 26923);
            f.I32(Fixture.LootList + 16, 2);
            var items = AdvancedLootDecoder.Decode(f.Read, Fixture.Image, layout).Items;
            Assert(items.Length == 2 && items[0].ItemId == items[1].ItemId && items[0].LootId != items[1].LootId
                && items.SelectMany(i => i.Sources).Select(s => s.CorpseId).Distinct().Count() == 2);
            f.I32(Fixture.Entries + 0xb0 + 0x5c, 59);
            Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image, layout));
        });
        Test("November personal list, empty list and metadata validation remain bounded", () =>
        {
            var layout = AdvancedLootMemoryLayout.November; var f = new Fixture(layout);
            f.U64(Fixture.LootWindow + layout.Lists, 0); f.U64(Fixture.LootWindow + layout.Lists + 8, Fixture.LootList);
            f.Write(Fixture.LootList + 24, [1]);
            Assert(AdvancedLootDecoder.Decode(f.Read, Fixture.Image, layout).Items.Single().List == "personal");
            f.I32(Fixture.LootList + 16, 0);
            Assert(AdvancedLootDecoder.Decode(f.Read, Fixture.Image, layout).Items.Length == 0);
            f.I32(Fixture.LootList + 16, 1); f.U64(Fixture.Entries + 0x70, Fixture.Image + AdvancedLootMemoryLayout.Legacy.SourceVtable);
            Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image, layout));
            f.U64(Fixture.Entries + 0x70, Fixture.Image + layout.SourceVtable); f.I32(Fixture.LootList + 20, 257);
            Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image, layout));
        });
        Test("November loot and context honor independently relocated identity and spawn fields", () =>
        {
            var lootLayout = AdvancedLootMemoryLayout.November; var f = new Fixture(lootLayout);
            var identityLayout = Identity.LiveLayout with { ProfileLevel = 0x17ec };
            f.I32(Fixture.Profile + 0x17e8, 0); f.I32(Fixture.Profile + 0x17ec, 130);
            var targetLayout = TargetMemoryLayout.Legacy with { Identity = identityLayout, SpawnLevel = 0x274,
                SpawnId = 0x16c, DisplayName = 0xc8, ZoneHeader = LootContextDecoder.ZoneHeader + 0x200 };
            f.Add(Fixture.Image + targetLayout.ZoneHeader, f.Read(Fixture.Image + LootContextDecoder.ZoneHeader, 0x188));
            f.Write(Fixture.Player + 0x270, [0]); f.Write(Fixture.Player + 0x274, [130]);
            f.Write(Fixture.Target + 0x274, [7]); f.I32(Fixture.Target + 0x16c, 26922); f.Write(Fixture.Target + 0x135, [2]);
            f.Write(Fixture.Target + 0xc8, Encoding.ASCII.GetBytes("an overcharged octodrone's corpse\0"));
            f.U64(Fixture.Player + 8, Fixture.Target); f.U64(Fixture.Target + 16, Fixture.Player);
            var loot = AdvancedLootDecoder.Decode(f.Read, Fixture.Image, lootLayout, identityLayout);
            var sources = loot.Items.SelectMany(i => i.Sources).ToArray();
            var context = LootContextDecoder.Decode(f.Read, Fixture.Image, sources, targetLayout);
            Assert(context.Npcs.Single() is { CorpseId: 26922, Level: 7 } && context.Npcs[0].Zone.Id == 151);
            f.I32(Fixture.Target + 0x16c, 26923);
            Assert(LootContextDecoder.Decode(f.Read, Fixture.Image, sources, targetLayout).Npcs.Length == 0);
            f.I32(Fixture.Target + 0x16c, 26922); f.Write(Fixture.Target + 0x135, [1]);
            Assert(LootContextDecoder.Decode(f.Read, Fixture.Image, sources, targetLayout).Npcs.Length == 0);
            f.Write(Fixture.Target + 0x135, [2]); f.Write(Fixture.Target + 0xc8, Encoding.ASCII.GetBytes("wrong corpse\0"));
            Assert(LootContextDecoder.Decode(f.Read, Fixture.Image, sources, targetLayout).Npcs.Length == 0);
        });
        Test("November capture rejects a changing list pointer and unknown build", () =>
        {
            var layout = AdvancedLootMemoryLayout.November; var f = new Fixture(layout); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.LootWindow + layout.Lists && ++reads == 2) b[0] ^= 8; return b; }
            Reject(() => AdvancedLootDecoder.Decode(Read, Fixture.Image, layout));
            Reject(() => AdvancedLootMemoryLayout.ForHash(new string('a', 64)));
        });
        Test("snapshot differences are rejected", () =>
        {
            int reads = 0; Reject(() => ObservedSurfaceReader.Stable(() => ++reads));
        });
        Test("loading state blocks both readers", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Game + 0x5e4, 4);
            Reject(() => InspectionDecoder.Decode(f.Read, Fixture.Image)); Reject(() => AdvancedLootDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("selected target preserves NPC identity and zone", () =>
        {
            var f = new Fixture(); var result = TargetDecoder.Decode(f.Read, Fixture.Image);
            Assert(result.Target == new ObservedTarget(11613, "Torm the Golden", 1, 1, "NPC") && result.Zone.Id == 151);
        });
        Test("players and corpses remain distinct from NPCs", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Image + TargetDecoder.TargetRoot, Fixture.Player);
            Assert(TargetDecoder.Decode(f.Read, Fixture.Image).Target?.Type == "Player");
            f.U64(Fixture.Image + TargetDecoder.TargetRoot, Fixture.Target); f.Write(Fixture.Target + 0x135, [2]);
            Assert(TargetDecoder.Decode(f.Read, Fixture.Image).Target?.Type == "Corpse");
            f.Write(Fixture.Target + 0x135, [99]);
            Assert(TargetDecoder.Decode(f.Read, Fixture.Image).Target?.Type == "Unknown");
        });
        Test("cleared target is an explicit empty observation", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Image + TargetDecoder.TargetRoot, 0);
            Assert(TargetDecoder.Decode(f.Read, Fixture.Image).Target is null);
        });
        Test("changed target pointer rejects observation", () =>
        {
            var f = new Fixture(); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Image + TargetDecoder.TargetRoot && ++reads == 2) Array.Clear(b); return b; }
            Reject(() => TargetDecoder.Decode(Read, Fixture.Image));
        });
        Test("recycled target identity rejects observation", () =>
        {
            var f = new Fixture(); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Target + 0x168 && ++reads == 2) b[0] ^= 1; return b; }
            Reject(() => TargetDecoder.Decode(Read, Fixture.Image));
        });
        Test("invalid target pointers and object types reject", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Image + TargetDecoder.TargetRoot, Fixture.Target + 1);
            Reject(() => TargetDecoder.Decode(f.Read, Fixture.Image));
            f.U64(Fixture.Image + TargetDecoder.TargetRoot, Fixture.Target); f.U64(Fixture.Target, Fixture.Image + 1);
            Reject(() => TargetDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("target capture rejects loading and inconsistent local level", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Game + 0x5e4, 4);
            Reject(() => TargetDecoder.Decode(f.Read, Fixture.Image));
            f.I32(Fixture.Game + 0x5e4, 5); f.Write(Fixture.Player + 0x270, [129]);
            Reject(() => TargetDecoder.Decode(f.Read, Fixture.Image));
        });
        Test("target capture rejects changed zone and partial target reads", () =>
        {
            var f = new Fixture(); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Image + LootContextDecoder.ZoneHeader && ++reads == 2) b[0] ^= 1; return b; }
            Reject(() => TargetDecoder.Decode(Read, Fixture.Image));
            Reject(() => TargetDecoder.Decode((a, n) => a == Fixture.Target + 0xf4 ? [0] : f.Read(a, n), Fixture.Image));
        });
        return new { passed = true, tests = passed, gameInputSent = false, processMemoryWritten = false };
    }
    internal sealed class Fixture
    {
        internal const ulong Image = 0x10000000, Game = 0x20000000, Player = 0x21000000, Pc = 0x22000000,
            Profile = 0x23000000, ProfileList = 0x24000000, Item = 0x26000000, Definition = 0x27000000,
            Manager = 0x30000000, Bins = 0x31000000, Windows = 0x32000000, Display = 0x33000000,
            LootWindow = 0x34000000, LootList = 0x35000000, Entries = 0x36000000, Sources = 0x37000000, Target = 0x38000000;
        private readonly Dictionary<ulong, byte[]> blocks = [];
        internal Fixture(AdvancedLootMemoryLayout? lootLayout = null)
        {
            lootLayout ??= AdvancedLootMemoryLayout.Legacy;
            foreach (ulong rva in new[] { 0xf35250UL, 0xeb6440UL, 0xeb5fb0UL, InspectionDecoder.ManagerRoot, lootLayout.WindowRoot }) blocks[Image + rva] = new byte[8];
            blocks[Game] = new byte[0x600]; blocks[Player] = new byte[0x1100]; blocks[Pc] = new byte[0x3100];
            blocks[Profile] = new byte[0x1900]; blocks[ProfileList] = new byte[32]; blocks[Item] = new byte[0x138]; blocks[Definition] = new byte[0x640];
            blocks[Manager] = new byte[0x50]; blocks[Bins] = new byte[8]; blocks[Windows] = new byte[48]; blocks[Display] = new byte[0xab8];
            blocks[LootWindow] = new byte[0x450]; blocks[LootList] = new byte[0x30]; blocks[Entries] = new byte[0x160]; blocks[Sources] = new byte[0xa0];
            U64(Image + 0xf35250, Game); I32(Game + 0x5e4, 5); U64(Image + 0xeb6440, Player); U64(Image + 0xeb5fb0, Pc);
            U64(Pc + 0x2848, Player); Write(Player + 0xb4, Encoding.ASCII.GetBytes("Fixture\0")); Write(Pc + 0x2fa0, Encoding.ASCII.GetBytes("Fixture\0"));
            U64(Pc + 0x2f08, ProfileList); U64(ProfileList + 8, Profile); I32(Player + 0xfe4, 2); I32(Profile + 0x17cc, 2); I32(Profile + 0x17e8, 130);
            I32(Item + 0x11c, 120780); U64(Item + 0x128, Definition); I32(Definition + 0xb8, 120780); Write(Definition, Encoding.ASCII.GetBytes("Ornament\0"));
            U64(Image + InspectionDecoder.ManagerRoot, Manager); U64(Manager, Image + InspectionDecoder.ManagerVtable);
            I32(Manager + 8, 1); I32(Manager + 12, 32); I32(Manager + 16, 31); I32(Manager + 20, 5); U64(Manager + 24, Bins); I32(Manager + 32, 1); I32(Manager + 0x48, 6);
            U64(Bins, Windows); U64(Windows, Display); U64(Display, Image + InspectionDecoder.WindowVtable); U64(Display + 0x3f8, Item);
            U64(Image + lootLayout.WindowRoot, LootWindow); U64(LootWindow, Image + lootLayout.WindowVtable); U64(LootWindow + lootLayout.Lists, LootList);
            U64(LootList, Image + lootLayout.ListVtable); U64(LootList + 8, Entries); I32(LootList + 16, 1); I32(LootList + 20, 2);
            U64(Entries, 172583); Write(Entries + 8, Encoding.ASCII.GetBytes("Mechanical Plating\0")); I32(Entries + 0x48, 1238); I32(Entries + 0x5c, 59);
            U64(Entries + 0x70, Image + lootLayout.SourceVtable); U64(Entries + 0x78, Sources); I32(Entries + 0x80, 1); I32(Entries + 0x84, 1);
            I32(Sources, 26922); I32(Sources + 4, 2); Write(Sources + 13, Encoding.ASCII.GetBytes("an overcharged octodrone\0"));
            blocks[Image + TargetDecoder.TargetRoot] = new byte[8]; blocks[Target] = new byte[0x278];
            blocks[Image + LootContextDecoder.ZoneHeader] = new byte[0x188];
            U64(Player, Image + TargetDecoder.SpawnVtable); Write(Player + 0x270, [130]);
            Write(Player + 0xf4, Encoding.ASCII.GetBytes("Fixture\0")); I32(Player + 0x168, 1234);
            U64(Image + TargetDecoder.TargetRoot, Target); U64(Target, Image + TargetDecoder.SpawnVtable);
            Write(Target + 0xf4, Encoding.ASCII.GetBytes("Torm the Golden\0")); Write(Target + 0x135, [1]);
            I32(Target + 0x168, 11613); Write(Target + 0x270, [1]);
            Write(Image + LootContextDecoder.ZoneHeader, Encoding.ASCII.GetBytes("bazaar\0"));
            Write(Image + LootContextDecoder.ZoneHeader + 128, Encoding.ASCII.GetBytes("The Bazaar\0"));
            I32(Image + LootContextDecoder.ZoneHeader + 0x184, 151);
        }
        private (byte[] Block, int Offset) Find(ulong address, int size)
        {
            foreach (var (start, block) in blocks)
                if (address >= start && address - start <= (ulong)block.Length && (ulong)size <= (ulong)block.Length - (address - start))
                    return (block, (int)(address - start));
            throw new InvalidDataException("Fixture read is outside supported blocks.");
        }
        internal byte[] Read(ulong address, int size) { var (b, o) = Find(address, size); return b.AsSpan(o, size).ToArray(); }
        internal void Add(ulong address, byte[] data) => blocks.Add(address, data);
        internal void Write(ulong address, byte[] data) { var (b, o) = Find(address, data.Length); data.CopyTo(b, o); }
        internal void U64(ulong address, ulong value) => Write(address, BitConverter.GetBytes(value));
        internal void I32(ulong address, int value) => Write(address, BitConverter.GetBytes(value));
    }
}
