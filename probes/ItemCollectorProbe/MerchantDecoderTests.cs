using System.Text;

internal static class MerchantDecoderTests
{
    internal static object Run()
    {
        var passed = new List<string>();
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Merchant assertion failed."); }
        void Test(string name, Action run) { run(); passed.Add(name); }
        void Reject(Action run)
        { try { run(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid merchant observation accepted."); }
        Test("only explicit unlimited stock is collected", () =>
        {
            var f = new Fixture(); var result = f.Capture();
            Check(result.IsOpen && result.Items.Single() == new MerchantItem(13006, "Water Flask")
                && result.ExcludedLimitedStock == 2 && result.Merchant == new ObservedMerchant("Torm the Golden", 1));
            foreach (int finite in new[] { 0, 1, 7595, int.MaxValue })
            { f.M.I32(Fixture.Item + 0x40, finite); Check(f.Capture().Items.Length == 0); }
            f.M.I32(Fixture.Item + 0x40, -2); Reject(() => f.Capture());
        });
        Test("closed window does not inspect retained merchant rows", () =>
        {
            var f = new Fixture(); f.M.Write(Fixture.Window + MerchantDecoder.Visible, [0]);
            var result = f.Capture((a, n) => a >= Fixture.Rows && a < Fixture.Rows + 0x100
                ? throw new InvalidOperationException("Read cached rows while closed") : f.M.Read(a, n));
            Check(!result.IsOpen && result.Merchant is null && result.Items.Length == 0);
        });
        Test("empty list does not require or follow an item pointer", () =>
        { var f = new Fixture(); f.M.I32(Fixture.Page + 0x40, 0); f.M.U64(Fixture.Page + 0x38, 0); Check(f.Capture().Items.Length == 0); });
        Test("closed merchant never follows a stale interaction or selected target", () =>
        {
            var f = new Fixture(); f.M.Write(Fixture.Window + MerchantDecoder.Visible, [0]);
            f.M.U64(Fixture.Image + MerchantDecoder.ActiveNpcRoot, 0x12345678);
            f.M.U64(Fixture.Image + TargetDecoder.TargetRoot, 0x12345678);
            Check(!f.Capture().IsOpen);
            f.M.U64(Fixture.Image + MerchantDecoder.WindowRoot, 0);
            Check(!f.Capture().IsOpen);
        });
        Test("current target cannot relabel the merchant", () =>
        {
            var f = new Fixture(); f.M.U64(Fixture.Image + TargetDecoder.TargetRoot, SurfaceDecoderTests.Fixture.Player);
            Check(f.Capture().Merchant?.Name == "Torm the Golden");
        });
        Test("unknown and Beta builds stay disabled", () =>
        {
            Reject(() => MerchantDecoder.RequireBuild(CollectorBuild.BetaBuildHash));
            Reject(() => MerchantDecoder.RequireBuild(Identity.BuildHash));
            Check(!CollectorBuild.Beta.Supports(CollectorCapability.Merchants) && !CollectorBuild.Live.Supports(CollectorCapability.Merchants));
        });
        foreach (byte type in new byte[] { 0, 2, 99 })
            Test($"interaction type {type} cannot become a merchant", () =>
            { var f = new Fixture(); f.M.Write(SurfaceDecoderTests.Fixture.Target + 0x135, [type]); Reject(() => f.Capture()); });
        Test("missing active interaction rejects open merchant", () =>
        { var f = new Fixture(); f.M.U64(Fixture.Image + MerchantDecoder.ActiveNpcRoot, 0); Reject(() => f.Capture()); });
        foreach (var mutation in new (string Name, Action<Fixture> Change)[]
        {
            ("wrong window type", f => f.M.U64(Fixture.Window, Fixture.Image)),
            ("wrong page type", f => f.M.U64(Fixture.Page, Fixture.Image)),
            ("wrong page parent", f => f.M.U64(Fixture.Page + 8, Fixture.Page)),
            ("wrong array type", f => f.M.U64(Fixture.Page + 0x30, Fixture.Image)),
            ("too many pages", f => f.M.U64(Fixture.Window + MerchantDecoder.Pages + 8, 4)),
            ("too many entries", f => f.M.I32(Fixture.Page + 0x40, 201)),
            ("capacity smaller than count", f => f.M.I32(Fixture.Page + 0x44, 1)),
            ("oversized capacity", f => f.M.I32(Fixture.Page + 0x44, 2048)),
            ("null list row", f => f.M.U64(Fixture.Rows, 0)),
            ("unaligned list", f => f.M.U64(Fixture.Page + 0x38, Fixture.Rows + 1)),
            ("wrong item type", f => f.M.U64(Fixture.Item, Fixture.Image)),
            ("instance definition mismatch", f => f.M.I32(Fixture.Item + 0xc0, 999)),
            ("invalid visibility", f => f.M.Write(Fixture.Window + MerchantDecoder.Visible, [2]))
        }) Test(mutation.Name + " rejects", () => { var f = new Fixture(); mutation.Change(f); Reject(() => f.Capture()); });
        Test("stock changing during reading rejects", () =>
        {
            var f = new Fixture(); int reads = 0;
            Reject(() => f.Capture((a, n) => a == Fixture.Item + 0x40 && ++reads == 2 ? BitConverter.GetBytes(1) : f.M.Read(a, n)));
        });
        Test("closing merchant during collection rejects", () =>
        {
            var f = new Fixture(); int reads = 0;
            Reject(() => f.Capture((a, n) => a == Fixture.Window + MerchantDecoder.Visible && ++reads == 2 ? [0] : f.M.Read(a, n)));
        });
        Test("finite stock changing between otherwise stable snapshots rejects", () =>
        {
            var f = new Fixture(); int windows = 0;
            Reject(() => f.Capture((a, n) =>
            {
                if (a == Fixture.Image + MerchantDecoder.WindowRoot && ++windows == 4)
                    f.M.I32(Fixture.Item + 0x1000 + 0x40, 2);
                return f.M.Read(a, n);
            }));
        });
        Test("merchant identity changing between snapshots rejects", () =>
        {
            var f = new Fixture(); int windows = 0;
            Reject(() => f.Capture((a, n) =>
            {
                if (a == Fixture.Image + MerchantDecoder.WindowRoot && ++windows == 4)
                    f.M.Write(SurfaceDecoderTests.Fixture.Target + 0xf4, Encoding.ASCII.GetBytes("Different Merchant\0"));
                return f.M.Read(a, n);
            }));
        });
        Test("partial rows reject", () =>
        { var f = new Fixture(); Reject(() => f.Capture((a, n) => a == Fixture.Rows ? [0] : f.M.Read(a, n))); });
        Test("limited stock definitions never appear in capture", () =>
        {
            var result = new Fixture().Capture();
            string text = System.Text.Json.JsonSerializer.Serialize(result);
            Check(!text.Contains("Rusty Dagger") && !text.Contains("Fish Scales") && !text.Contains("7595"));
        });
        return new { passed = true, tests = passed, gameInputSent = false, processMemoryWritten = false };
    }

    private sealed class Fixture
    {
        internal const ulong Image = SurfaceDecoderTests.Fixture.Image, Window = 0x40000000,
            PageArray = 0x41000000, Page = 0x42000000, Rows = 0x43000000, Item = 0x44000000, Definition = 0x45000000;
        internal readonly SurfaceDecoderTests.Fixture M = new();
        internal Fixture()
        {
            M.Add(Image + MerchantDecoder.WindowRoot, new byte[8]); M.U64(Image + MerchantDecoder.WindowRoot, Window);
            M.Add(Image + MerchantDecoder.ActiveNpcRoot, new byte[8]); M.U64(Image + MerchantDecoder.ActiveNpcRoot, SurfaceDecoderTests.Fixture.Target);
            M.Add(Window, new byte[0xa08]); M.U64(Window, Image + MerchantDecoder.WindowVtable); M.Write(Window + MerchantDecoder.Visible, [1]);
            M.U64(Window + MerchantDecoder.Pages, PageArray); M.U64(Window + MerchantDecoder.Pages + 8, 3); M.U64(Window + MerchantDecoder.Pages + 16, 3);
            M.Add(PageArray, new byte[48]); M.U64(PageArray, Page);
            M.Add(Page, new byte[0x160]); M.U64(Page, Image + MerchantDecoder.PageVtable); M.U64(Page + 8, Window);
            M.I32(Page + 0x10, 200); M.U64(Page + 0x30, Image + MerchantDecoder.ArrayVtable);
            M.U64(Page + 0x38, Rows); M.I32(Page + 0x40, 3); M.I32(Page + 0x44, 3); M.Add(Rows, new byte[0x48]);
            for (uint i = 0; i < 3; i++)
            {
                ulong item = Item + i * 0x1000, definition = Definition + i * 0x1000;
                M.Add(item, new byte[0x130]); M.Add(definition, new byte[0x640]); M.U64(Rows + i * 0x18, item);
                M.U64(item, Image + ItemMemoryLayout.November.Vtable!.Value); M.U64(item + 0x118, definition);
                M.I32(item + 0xc0, 13006 + (int)i); M.I32(definition + 0xb8, 13006 + (int)i);
                M.Write(definition, Encoding.ASCII.GetBytes(new[] { "Water Flask\0", "Rusty Dagger\0", "Fish Scales\0" }[i]));
                M.I32(item + 0x40, new[] { -1, 1, 7595 }[i]);
            }
        }
        internal MerchantState Capture(Func<ulong, int, byte[]>? read = null) => MerchantDecoder.ReadStable(read ?? M.Read, Image, TargetMemoryLayout.Legacy);
    }
}
