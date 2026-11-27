using System.Text;

internal static class TargetBuffDecoderTests
{
    internal static object Run()
    {
        var passed = new List<string>();
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Target-effect assertion failed."); }
        void Test(string name, Action action) { action(); passed.Add(name); }
        void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid target-effect capture accepted.");
        }
        Test("spell ID, separate caster and remaining milliseconds survive capture", () =>
        {
            var f = new Fixture(); var buff = f.Decode().Effects.Single();
            Check(buff.SpellId == 68116 && buff.CasterName == "Caster" && buff.Remaining == "2h 47m 17s");
        });
        Test("unreported caster stays unknown instead of using target name", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Data + 0x28, 0); f.U64(Fixture.Data + 0x20, 0);
            Check(f.Decode().Effects.Single().CasterName is null);
        });
        Test("validated animation supplies the matching spell icon cell", () =>
        {
            var f = new Fixture(); f.AddIcon();
            Check(f.Decode().Effects.Single().IconId == 104);
        });
        Test("bad or unreadable artwork cannot remove the spell and timer", () =>
        {
            var f = new Fixture(); f.AddIcon(); f.I32(Fixture.Icon + 0x50, 0);
            var effect = f.Decode().Effects.Single();
            Check(effect.IconId is null && effect.SpellId == 68116 && effect.RemainingMilliseconds == 10037000);
            f.U64(Fixture.Data + 8, Fixture.Icon + 0x1000);
            Check(f.Decode().Effects.Single().IconId is null);
        });
        Test("negative duration stays no-countdown rather than an expiry time", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Data + 0x14, -1);
            Check(f.Decode().Effects.Single().Remaining == "No countdown reported");
        });
        Test("normal timer changes between reads are accepted", () =>
        {
            var f = new Fixture(); int reads = 0;
            byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Data && ++reads == 2) BitConverter.GetBytes(10036000).CopyTo(b, 0x14); return b; }
            Check(TargetBuffDecoder.Decode(Read, Fixture.Image, f.Expected).Effects.Single().RemainingMilliseconds == 10036000);
        });
        Test("changing spell or caster rejects the observation", () =>
        {
            foreach (int offset in new[] { 0x10, 0x34 })
            {
                var f = new Fixture(); int reads = 0;
                byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == Fixture.Data && ++reads == 2) b[offset] ^= 1; return b; }
                Reject(() => TargetBuffDecoder.Decode(Read, Fixture.Image, f.Expected));
            }
        });
        Test("old target-window data cannot be attributed to a new target", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Window + 0x3c0, Fixture.Target + 8); Reject(() => f.Decode());
            f.U64(Fixture.Window + 0x3c0, Fixture.Target); f.Write(Fixture.Window + 0x350, [1]); Reject(() => f.Decode());
        });
        Test("target selection or identity changing mid-read rejects", () =>
        {
            foreach (ulong address in new[] { Fixture.Image + TargetDecoder.TargetRoot, Fixture.Target + 0x168 })
            {
                var f = new Fixture(); int reads = 0;
                byte[] Read(ulong a, int n) { var b = f.Read(a, n); if (a == address && ++reads > (a == Fixture.Target + 0x168 ? 1 : 2)) b[0] ^= 8; return b; }
                Reject(() => TargetBuffDecoder.Decode(Read, Fixture.Image, f.Expected));
            }
        });
        Test("null selection and pending target data have distinct states", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Image + TargetDecoder.TargetRoot, 0);
            Check(TargetBuffDecoder.Decode(f.Read, Fixture.Image, f.Expected with { Target = null }).Status == "no-target");
            f.U64(Fixture.Image + TargetDecoder.TargetRoot, Fixture.Target); f.I32(Fixture.Window + 0x330, 0);
            var pending = f.Decode(); Check(pending.Status == "waiting-for-target-data" && pending.Effects.Length == 0);
        });
        Test("oversized arrays, bad string pointers and partial reads reject", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Window + 0x310, 401); Reject(() => f.Decode());
            f.I32(Fixture.Window + 0x310, 2); f.U64(Fixture.Data + 0x20, Fixture.Data + 8); Reject(() => f.Decode());
            f.U64(Fixture.Data + 0x20, Fixture.Data + 0x34);
            Reject(() => TargetBuffDecoder.Decode((a, n) => a == Fixture.Data ? [0] : f.Read(a, n), Fixture.Image, f.Expected));
        });
        Test("wrong window or string vtable rejects", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Window, Fixture.Image + 1); Reject(() => f.Decode());
            f.U64(Fixture.Window, Fixture.Image + TargetBuffDecoder.WindowVtable);
            f.U64(Fixture.Data + 0x18, Fixture.Image + 1); Reject(() => f.Decode());
        });
        Test("relocated target/window/array members preserve spells and reject stale window data", () =>
        {
            var f = new Fixture();
            var target = TargetMemoryLayout.Legacy with { TargetRoot = 0x1000, SpawnLevel = 0x600, SpawnVtable = 0x2000 };
            var layout = TargetEffectLayout.Legacy with { WindowRoot = 0x3000, WindowVtable = 0x4000, ArrayVtable = 0x5000,
                CasterVtable = 0x6000, EffectArray = 0x700, PreviousTarget = 0x800, FirstUpdate = 0x810, PendingData = 0x820 };
            f.Block(Fixture.Image + target.TargetRoot, 8); f.Block(Fixture.Image + layout.WindowRoot, 8);
            f.Grow(Fixture.Target, 0x900); f.Grow(Fixture.Window, 0x900);
            f.U64(Fixture.Image + target.TargetRoot, Fixture.Target); f.U64(Fixture.Target, Fixture.Image + target.SpawnVtable);
            f.Write(Fixture.Target + target.SpawnLevel, [130]); f.Write(Fixture.Target + 0x270, [1]);
            f.U64(Fixture.Image + layout.WindowRoot, Fixture.Window); f.U64(Fixture.Window, Fixture.Image + layout.WindowVtable);
            f.U64(Fixture.Window + layout.PreviousTarget, Fixture.Target); f.I32(Fixture.Window + layout.PendingData, 1234);
            f.U64(Fixture.Window + layout.EffectArray, Fixture.Image + layout.ArrayVtable);
            f.U64(Fixture.Window + layout.EffectArray + 8, Fixture.Data);
            f.I32(Fixture.Window + layout.EffectArray + 16, 2); f.I32(Fixture.Window + layout.EffectArray + 20, 2);
            f.U64(Fixture.Data + 0x18, Fixture.Image + layout.CasterVtable);
            var effect = TargetBuffDecoder.Decode(f.Read, Fixture.Image, f.Expected, target, layout).Effects.Single();
            Check(effect.CasterName == "Caster" && effect.SpellId == 68116 && effect.RemainingMilliseconds == 10037000);
            f.Write(Fixture.Window + layout.FirstUpdate, [1]);
            Reject(() => TargetBuffDecoder.Decode(f.Read, Fixture.Image, f.Expected, target, layout));
        });
        return new { passed = true, tests = passed, gameInputSent = false, processMemoryRead = false, processMemoryWritten = false };
    }
    private sealed class Fixture
    {
        internal const ulong Image = 0x10000000, Target = 0x20000000, Window = 0x21000000, Data = 0x22000000, Icon = 0x23000000;
        private readonly Dictionary<ulong, byte[]> blocks = [];
        internal void Block(ulong address, int size) => blocks[address] = new byte[size];
        internal void Grow(ulong address, int size) { var old = blocks[address]; var data = new byte[size]; old.CopyTo(data, 0); blocks[address] = data; }
        internal TargetState Expected => new(new("Observer", 2, 130), new(151, "bazaar", "The Bazaar"), new(123, "Target", 130, 0, "Player"));
        internal Fixture()
        {
            blocks[Image + TargetDecoder.TargetRoot] = new byte[8]; blocks[Image + TargetBuffDecoder.WindowRoot] = new byte[8];
            blocks[Target] = new byte[0x278]; blocks[Window] = new byte[0x3d0]; blocks[Data] = new byte[2 * TargetBuffDecoder.Stride];
            U64(Image + TargetDecoder.TargetRoot, Target); U64(Target, Image + TargetDecoder.SpawnVtable);
            Write(Target + 0xf4, Encoding.ASCII.GetBytes("Target\0")); I32(Target + 0x168, 123); Write(Target + 0x270, [130]);
            U64(Image + TargetBuffDecoder.WindowRoot, Window); U64(Window, Image + TargetBuffDecoder.WindowVtable);
            U64(Window + 0x3c0, Target); I32(Window + 0x330, 1234);
            U64(Window + 0x300, Image + TargetBuffDecoder.ArrayVtable); U64(Window + 0x308, Data);
            I32(Window + 0x310, 2); I32(Window + 0x314, 2);
            I32(Data + 0x10, 68116); I32(Data + 0x14, 10037000);
            U64(Data + 0x18, Image + TargetBuffDecoder.CasterVtable); U64(Data + 0x20, Data + 0x34);
            I32(Data + 0x28, 6); I32(Data + 0x2c, 64); Write(Data + 0x34, Encoding.ASCII.GetBytes("Caster\0"));
        }
        private (byte[] Block, int Offset) Find(ulong address, int size)
        {
            foreach (var (start, block) in blocks)
                if (address >= start && address - start <= (ulong)block.Length && (ulong)size <= (ulong)block.Length - (address - start))
                    return (block, (int)(address - start));
            throw new InvalidDataException("Out-of-bounds fixture access.");
        }
        internal void AddIcon()
        {
            blocks[Icon] = new byte[0x60]; U64(Data + 8, Icon); U64(Icon, Image + 0xaf2ab8);
            I32(Icon + 0x10, 63); I32(Icon + 0x2c, 2); Write(Icon + 0x3e, [1, 0]);
            I32(Icon + 0x40, 40); I32(Icon + 0x44, 40); I32(Icon + 0x48, 104);
            I32(Icon + 0x50, 80); I32(Icon + 0x54, 200); I32(Icon + 0x58, 120); I32(Icon + 0x5c, 240);
        }
        internal byte[] Read(ulong a, int n) { var (b, o) = Find(a, n); return b.AsSpan(o, n).ToArray(); }
        internal void Write(ulong a, byte[] data) { var (b, o) = Find(a, data.Length); data.CopyTo(b, o); }
        internal void U64(ulong a, ulong n) => Write(a, BitConverter.GetBytes(n));
        internal void I32(ulong a, int n) => Write(a, BitConverter.GetBytes(n));
        internal TargetBuffState Decode() => TargetBuffDecoder.Decode(Read, Image, Expected);
    }
}
