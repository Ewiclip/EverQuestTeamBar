using System.Text;
using System.Text.Json;

namespace TeamBar;

// All memory in this suite is synthetic. No process, UI, network or input access.
internal static class BetaMerchantImportFixtureTests
{
    // Independent, reviewed wire-field read contract; unrelated definition bytes
    // deliberately do not exist in the fixture reader.
    private static readonly (int Offset, int Width)[] Fields =
    [
        (0, 64), (0x40, 80), (0xb8, 4), (0xc4, 4), (0xbc, 4), (0x140, 4), (0x144, 4),
        (0x110, 4), (0x114, 4), (0xea, 1), (0x5a8, 1), (0xd1, 1), (0xd2, 1), (0xd3, 1),
        (0xec, 4), (0x10c, 4), (0x104, 4), (0x108, 4), (0x56c, 4), (0x158, 4),
        (0x152, 1), (0x1d8, 4), (0x590, 4)
    ];

    internal static void Run(string output)
    {
        var tests = new List<string>();
        void Test(string name, Action action) { action(); tests.Add(name); }
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid or changing merchant definition data was accepted.");
        }
        static ItemData Definition(Fixture f, Func<ulong, int, byte[]>? read = null) =>
            BetaMerchantImportFixture.ReadDefinition(new(read ?? f.Read, Fixture.Image), Fixture.Definition);

        Test("merchant research remains pinned independently of the current Beta registry", () =>
        {
            Check(BetaMerchantImportFixture.ValidatedBuildHash == "ca05079325390770ba58390d78d52424d5fe6de15b78786c19255a453ca875a1",
                "Merchant research unexpectedly inherited a newer executable hash.");
            Check(BetaMerchantImportFixture.ValidatedIdentityLayout == new IdentityLayout(0xf341d8, 0xf36a78, 0xfb5530,
                0x5e4, 0x2890, 0x135, 0xb4, 0x2fe8, 0x2f58, 0x2f50, 0xfdc, 0x17cc, 0x17e8)
                && BetaMerchantImportFixture.ValidatedItemLayout == new ItemMemoryLayout(0xa4, 0x120, 0x18, 0x97e2e0),
                "Merchant research unexpectedly inherited a newer identity or item layout.");
        });
        Test("unverified executable fails before the first memory-reading callback", () =>
        {
            string fixtureDirectory = Path.GetFullPath(output) + ".files";
            Directory.CreateDirectory(fixtureDirectory);
            string executable = Path.Combine(fixtureDirectory, "unsupported-beta.exe");
            using (var stream = new FileStream(executable, FileMode.CreateNew))
                stream.Write(Encoding.ASCII.GetBytes("Synthetic incompatible executable; no game contents."));
            bool read = false; string? requiredHash = null;
            Reject(() => BetaMerchantImportFixture.ReadVerified(hash =>
            {
                requiredHash = hash;
                Identity.VerifyFile(executable, hash);
            }, () => { read = true; return [Fixture.Expected]; }));
            Check(!read && requiredHash == "ca05079325390770ba58390d78d52424d5fe6de15b78786c19255a453ca875a1",
                "A later executable could reach old merchant memory reads.");
        });
        Test("October 16 Beta hash cannot reach the merchant memory reader", () =>
        {
            const string actualHash = "2d97f027f290568514a79990ba4ca2e7efe47b1d9fee690badf288aa218f72ac";
            bool read = false;
            Reject(() => BetaMerchantImportFixture.ReadVerified(requiredHash =>
            {
                if (!string.Equals(actualHash, requiredHash, StringComparison.Ordinal))
                    throw new InvalidDataException("This fixture executable is not the pinned merchant build.");
            }, () => { read = true; return [Fixture.Expected]; }));
            Check(!read, "The later Beta hash inherited the older merchant layout.");
        });

        Test("all 23 declared fields use exact read widths totaling 210 bytes", () =>
        {
            var f = new Fixture(); var reads = new List<(int Offset, int Width)>();
            byte[] Read(ulong address, int count)
            {
                var field = (checked((int)(address - Fixture.Definition)), count);
                Check(Fields.Contains(field), $"Undeclared definition read: {field}.");
                reads.Add(field); return f.Read(address, count);
            }
            Check(Definition(f, Read) == Fixture.Expected, "A declared item property changed or was omitted.");
            Check(reads.Count == Fields.Length && reads.Sum(field => field.Width) == 210
                && reads.ToHashSet().SetEquals(Fields), "Definition fields were skipped or read twice.");
        });

        foreach (var field in Fields)
            Test($"short read at definition +{field.Offset:x}/{field.Width} rejects zero-fill substitution", () =>
            {
                var f = new Fixture(); bool shortened = false;
                byte[] Read(ulong address, int count)
                {
                    var bytes = f.Read(address, count);
                    if (address == Fixture.Definition + (ulong)field.Offset && count == field.Width)
                    { shortened = true; return bytes[..^1]; }
                    return bytes;
                }
                Reject(() => Definition(f, Read)); Check(shortened, "Expected field was never read.");
            });

        Test("bounded names require terminators; empty lore is valid", () =>
        {
            var f = new Fixture(); f.Text(Fixture.Definition, new string('N', 63));
            f.Text(Fixture.Definition + 0x40, new string('L', 79));
            var item = Definition(f);
            Check(item.Name.Length == 63 && item.CandidateDetails.LoreName.Length == 79, "Maximum bounded names failed.");
            f.Byte(Fixture.Definition + 0x40, 0);
            Check(Definition(f).CandidateDetails.LoreName.Length == 0, "Empty lore name was rejected.");
            f.Bytes(Fixture.Definition, Enumerable.Repeat((byte)'N', 64).ToArray());
            Reject(() => Definition(f));
            f = new Fixture(); f.Bytes(Fixture.Definition + 0x40, Enumerable.Repeat((byte)'L', 80).ToArray());
            Reject(() => Definition(f));
        });
        foreach (int offset in new[] { 0xea, 0x5a8, 0xd1, 0xd2, 0xd3 })
            Test($"flag +{offset:x} rejects values other than zero or one", () =>
            {
                var f = new Fixture(); f.Byte(Fixture.Definition + (ulong)offset, 2);
                Reject(() => Definition(f));
            });

        Test("stable open Beta fixture returns actual complete definition", () =>
        {
            var f = new Fixture();
            Check(BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image).SequenceEqual([Fixture.Expected]),
                "Stable merchant definition did not round-trip.");
        });
        foreach (ulong offset in new ulong[] { 0xa9, 0x234 })
            foreach (byte state in new byte[] { 0, 2 })
                Test($"visibility +{offset:x}={state} yields no observation despite retained rows", () =>
                {
                    var f = new Fixture(); f.Byte(Fixture.Window + offset, state);
                    byte[] Read(ulong address, int count)
                    {
                        Check(!Fixture.IsDefinitionAddress(address), "Closed visibility allowed a definition read.");
                        return f.Read(address, count);
                    }
                    try
                    {
                        Check(BetaMerchantImportFixture.ReadStable(Read, Fixture.Image).Length == 0,
                            "Closed/invalid visibility exposed cached definitions.");
                    }
                    catch (InvalidDataException) { }
                });
        Test("loading identity fails before any item definition read", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Game + BetaMerchantImportFixture.ValidatedIdentityLayout.GameState, 0);
            byte[] Read(ulong address, int count)
            {
                Check(!Fixture.IsDefinitionAddress(address), "Definition read preceded valid identity.");
                return f.Read(address, count);
            }
            Reject(() => BetaMerchantImportFixture.ReadStable(Read, Fixture.Image));
        });
        Test("invalid object types and mismatched instance IDs are rejected", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Window, Fixture.Image + 0xad6ce0);
            Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
            f = new Fixture(); f.U64(Fixture.Handler, Fixture.Image + 0xad6b88);
            Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
            f = new Fixture(); f.U64(Fixture.Item, Fixture.Image + 0x97e2e8);
            Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
            f = new Fixture(); f.I32(Fixture.Item + 0xa4, Fixture.Expected.ItemId + 1);
            Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
        });
        Test("recipe world-container exception is never accepted for item observations", () =>
        {
            var f = new Fixture(); f.I32(Fixture.Item + 0xa4, 0);
            f.U64(Fixture.Item + 0x120, 0); f.U64(Fixture.Item + 0x18, Fixture.Definition);
            Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
        });
        Test("preferred definition has precedence and invalid preferred pointers never fall back", () =>
        {
            var f = new Fixture(); f.U64(Fixture.Item + 0x18, Fixture.AlternateDefinition);
            f.I32(Fixture.AlternateDefinition + 0x104, 4321);
            Check(BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image).Single().CandidateDetails.HitPoints == 4321,
                "Preferred item definition was ignored.");
            f.U64(Fixture.Item + 0x18, 7);
            Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
        });
        Test("malformed page and item bounds reject before enumeration", () =>
        {
            foreach (Action<Fixture> mutate in new Action<Fixture>[]
            {
                f => f.U64(Fixture.Window + 0x2e0, 4),
                f => f.U64(Fixture.Window + 0x2e8, ulong.MaxValue),
                f => f.I32(Fixture.Handler + 0x40, -1),
                f => f.I32(Fixture.Handler + 0x40, 201),
                f => f.I32(Fixture.Handler + 0x44, 0),
                f => f.I32(Fixture.Handler + 0x44, int.MaxValue)
            })
            {
                var f = new Fixture(); mutate(f);
                Reject(() => BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image));
            }
        });

        void Mutation(string name, Action<Fixture> mutate)
        {
            Test(name, () =>
            {
                var f = new Fixture(); bool changed = false;
                byte[] Read(ulong address, int count)
                {
                    var bytes = f.Read(address, count);
                    // Return the original property, then change state. Replacing an
                    // object with equal item data must still fail structural checks.
                    if (!changed && address == Fixture.Definition + 0x104 && count == 4)
                    { changed = true; mutate(f); }
                    return bytes;
                }
                Reject(() => BetaMerchantImportFixture.ReadStable(Read, Fixture.Image));
                Check(changed, "Mutation trigger was not reached.");
            });
        }
        Mutation("property mutation between complete reads discards capture", f => f.I32(Fixture.Definition + 0x104, 6543));
        Mutation("preferred selector replacement with equal-ID definition discards capture", f => f.U64(Fixture.Item + 0x18, Fixture.AlternateDefinition));
        Mutation("fallback selector replacement with equal-ID definition discards capture", f => f.U64(Fixture.Item + 0x120, Fixture.AlternateDefinition));
        Mutation("row pointer replacement with identical item data discards capture", f => f.U64(Fixture.Rows, Fixture.AlternateItem));
        Mutation("window root replacement with identical item data discards capture", f => f.U64(Fixture.Image + 0xe83698, Fixture.AlternateWindow));
        Mutation("closing during item read discards capture", f => f.Byte(Fixture.Window + 0x234, 0));
        Mutation("item count changing during read discards capture", f => f.I32(Fixture.Handler + 0x40, 0));
        Mutation("character level changing during read discards capture", f => f.I32(Fixture.Profile + BetaMerchantImportFixture.ValidatedIdentityLayout.ProfileLevel, 131));
        Mutation("item type changing during read discards capture", f => f.U64(Fixture.Item, Fixture.Image + 0x97e2e8));

        Test("contribution projection contains only definition fields and build provenance", () =>
        {
            var f = new Fixture(); var rejections = new List<ContributionRejection>();
            var source = new ContributionSource("Team Bar", "Beta", "1", BetaMerchantImportFixture.ValidatedBuildHash, "beta-merchant-item-definitions-fixture-1");
            var records = ContributionPayload.Items(BetaMerchantImportFixture.ReadStable(f.Read, Fixture.Image), source, rejections.Add);
            Check(records.Length == 1 && rejections.Count == 0, "A validated definition was lost during export.");
            var batch = ContributionPayload.Batches(records).Single();
            using var json = JsonDocument.Parse(batch.Body);
            static HashSet<string> Names(JsonElement value) => value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var root = json.RootElement; var item = root.GetProperty("items")[0];
            Check(Names(root).SetEquals(["schemaVersion", "source", "items", "spells"]), "Extra capture context reached the envelope.");
            Check(Names(root.GetProperty("source")).SetEquals(["client", "clientChannel", "version", "buildSha256", "decoder"]), "Unexpected provenance field.");
            Check(root.GetProperty("source").GetProperty("buildSha256").GetString() == BetaMerchantImportFixture.ValidatedBuildHash
                && root.GetProperty("source").GetProperty("decoder").GetString() == source.Decoder, "Exact decoder provenance changed.");
            Check(Names(item).SetEquals(["itemId", "name", "candidateDetails"]), "Nondefinition item context was exported.");
            Check(Names(item.GetProperty("candidateDetails")).SetEquals([
                "loreName", "iconId", "equipSlotsMask", "classesMask", "racesMask", "requiredLevel", "recommendedLevel",
                "tradeskill", "questItem", "droppable", "attuneable", "heirloom", "loreGroup", "armorClass", "hitPoints",
                "mana", "endurance", "damage", "delay", "augmentationType", "stackSize"]), "Definition property allowlist changed.");
        });

        CollectorFormat.WriteNew(output, new { passed = true, tests, gameInputSent = false, processMemoryRead = false });
    }

    private sealed class Fixture
    {
        internal const ulong Image = 0x140000000, Game = 0x100000, Pc = 0x200000, Player = 0x300000,
            List = 0x400000, Profile = 0x500000, Window = 0x600000, Pages = 0x700000,
            Handler = 0x800000, Rows = 0x900000, Item = 0xa00000, Definition = 0xb00000,
            AlternateDefinition = 0xc00000, AlternateItem = 0xd00000, AlternateWindow = 0xe00000;
        internal static readonly ItemData Expected = new(987654, "Fixture Blade", new("Fixture Blade Lore", 54321,
            0x80000005u, 0x80010000u, 0x40000003u, 125, 130, true, false, true, false, true,
            12345, -321, 123456, -234, 34567, 5678, 42, 77, 1234));
        private readonly Dictionary<ulong, byte> data = [];

        internal Fixture()
        {
            var l = BetaMerchantImportFixture.ValidatedIdentityLayout;
            U64(Image + l.GameRoot, Game); I32(Game + l.GameState, l.ReadyState);
            U64(Image + l.PlayerRoot, Player); U64(Image + l.PcRoot, Pc); U64(Pc + l.PcPlayer, Player);
            Text(Player + l.PlayerName, "Fixture"); Text(Pc + l.PcName, "Fixture");
            U64(Pc + l.PcProfileList, List); U64(List + l.ListProfile, Profile);
            I32(Player + l.PlayerClass, 16); I32(Profile + l.ProfileClass, 16); I32(Profile + l.ProfileLevel, 130);
            U64(Image + 0xe83698, Window);
            foreach (ulong window in new[] { Window, AlternateWindow })
            {
                U64(window, Image + 0xad6ce8); Byte(window + 0xa9, 1); Byte(window + 0x234, 1);
                U64(window + 0x2d8, Pages); U64(window + 0x2e0, 3); U64(window + 0x2e8, 3);
            }
            for (int i = 0; i < 3; i++)
            {
                ulong handler = Handler + (ulong)i * 0x1000;
                U64(Pages + (ulong)i * 0x10, handler); U64(handler, Image + 0xad6ae8 + (ulong)i * 0xa0);
                U64(handler + 0x30, Image + 0xad6ac8);
            }
            U64(Handler + 0x38, Rows); I32(Handler + 0x40, 1); I32(Handler + 0x44, 1);
            U64(Rows, Item);
            foreach (ulong item in new[] { Item, AlternateItem })
            {
                U64(item, Image + 0x97e2e0); I32(item + 0xa4, Expected.ItemId); U64(item + 0x120, Definition);
            }
            foreach (ulong definition in new[] { Definition, AlternateDefinition })
            {
                Text(definition, Expected.Name); Text(definition + 0x40, Expected.CandidateDetails.LoreName);
                I32(definition + 0xb8, 987654); I32(definition + 0xc4, 54321);
                U32(definition + 0xbc, 0x80000005u); U32(definition + 0x140, 0x80010000u); U32(definition + 0x144, 0x40000003u);
                I32(definition + 0x110, 125); I32(definition + 0x114, 130);
                Byte(definition + 0xea, 1); Byte(definition + 0xd1, 1); Byte(definition + 0xd3, 1);
                I32(definition + 0xec, 12345); I32(definition + 0x10c, -321); I32(definition + 0x104, 123456);
                I32(definition + 0x108, -234); I32(definition + 0x56c, 34567); I32(definition + 0x158, 5678);
                Byte(definition + 0x152, 42); I32(definition + 0x1d8, 77); I32(definition + 0x590, 1234);
            }
        }
        internal static bool IsDefinitionAddress(ulong address) =>
            address >= Definition && address < Definition + 0x640 || address >= AlternateDefinition && address < AlternateDefinition + 0x640;
        internal byte[] Read(ulong address, int count)
        {
            if (IsDefinitionAddress(address))
            {
                ulong start = address >= AlternateDefinition ? AlternateDefinition : Definition;
                if (!Fields.Contains((checked((int)(address - start)), count)))
                    throw new InvalidOperationException("Reader attempted an undeclared definition range.");
            }
            if (address < Rows + 0x18 && address + (ulong)count > Rows + 8)
                throw new InvalidOperationException("Reader attempted excluded row ownership or alternate-object fields.");
            return Enumerable.Range(0, count).Select(i => data.GetValueOrDefault(address + (ulong)i)).ToArray();
        }
        internal void Bytes(ulong address, byte[] bytes)
        { for (int i = 0; i < bytes.Length; i++) data[address + (ulong)i] = bytes[i]; }
        internal void Byte(ulong address, byte value) => data[address] = value;
        internal void I32(ulong address, int value) => Bytes(address, BitConverter.GetBytes(value));
        internal void U32(ulong address, uint value) => Bytes(address, BitConverter.GetBytes(value));
        internal void U64(ulong address, ulong value) => Bytes(address, BitConverter.GetBytes(value));
        internal void Text(ulong address, string value) => Bytes(address, Encoding.ASCII.GetBytes(value + "\0"));
    }
}
