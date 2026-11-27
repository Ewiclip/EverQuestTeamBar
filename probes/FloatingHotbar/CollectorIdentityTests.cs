using System.Security.Cryptography;
using System.Text;

internal static class CollectorIdentityTests
{
    internal static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        const ulong image = 0x140000000, game = 0x100000, pc = 0x200000, player = 0x300000, list = 0x400000, profile = 0x500000;
        foreach (var build in new[] { CollectorBuild.Live, CollectorBuild.Beta })
        {
            var layout = build.IdentityLayout;
            var data = new Dictionary<ulong, byte>();
            void Bytes(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) data[address + (ulong)i] = bytes[i]; }
            void I32(ulong address, int value) => Bytes(address, BitConverter.GetBytes(value));
            void U64(ulong address, ulong value) => Bytes(address, BitConverter.GetBytes(value));
            byte[] Read(ulong address, int count) => Enumerable.Range(0, count).Select(i => data.GetValueOrDefault(address + (ulong)i)).ToArray();
            U64(image + layout.GameRoot, game); I32(game + layout.GameState, layout.ReadyState);
            U64(image + layout.PlayerRoot, player); U64(image + layout.PcRoot, pc); U64(pc + layout.PcPlayer, player);
            Bytes(player + layout.PlayerName, Encoding.ASCII.GetBytes("Fellwort\0")); Bytes(pc + layout.PcName, Encoding.ASCII.GetBytes("Fellwort\0"));
            U64(pc + layout.PcProfileList, list); U64(list + layout.ListProfile, profile);
            I32(player + layout.PlayerClass, 16); I32(profile + layout.ProfileClass, 16); I32(profile + layout.ProfileLevel, 130);
            var snapshot = Identity.Decode(Read, image, layout);
            Check(snapshot.Character == new CharacterIdentity("Fellwort", 16, 130) && snapshot.Player == player && snapshot.Profile == profile,
                $"{build.Channel}: explicit identity layout verifies name, class, level and pointer cross-checks");
            if (build == CollectorBuild.Beta)
            {
                Check(build.BuildSha256 == "2d97f027f290568514a79990ba4ca2e7efe47b1d9fee690badf288aa218f72ac"
                    && layout.PlayerClass == 0xfbc,
                    "October 16 Beta identity is pinned to its verified build and player-class member +fbc");
                I32(player + 0xfbc, 0); I32(player + 0xfdc, 16);
                Reject(() => Identity.Decode(Read, image, layout),
                    "a plausible class at the old Beta +fdc offset cannot authorize the new build");
                I32(player + 0xfbc, 16); I32(player + 0xfdc, 1);
                Check(Identity.Decode(Read, image, layout).Character == new CharacterIdentity("Fellwort", 16, 130),
                    "new Beta identity reads +fbc and ignores a contradictory value at obsolete +fdc");
            }
            if (build == CollectorBuild.Live) Check(snapshot == Identity.Decode(Read, image), "default decoder preserves the existing Live identity layout");
            else Reject(() => Identity.Decode(Read, image), "default Live decoder never guesses Beta offsets");
            I32(profile + layout.ProfileClass, 1);
            Reject(() => Identity.Decode(Read, image, layout), $"{build.Channel}: mismatched class sources fail closed");
            I32(profile + layout.ProfileClass, 16); U64(pc + layout.PcPlayer, profile);
            Reject(() => Identity.Decode(Read, image, layout), $"{build.Channel}: changed player back-reference fails closed");
            U64(pc + layout.PcPlayer, player); I32(game + layout.GameState, 0);
            Reject(() => Identity.Decode(Read, image, layout), $"{build.Channel}: loading state fails closed");
        }
        var live = new DetectedClient(1, 100, 11, 0, new("Fellwort", 16, 130));
        var beta = live with { ProcessId = 2, Window = 22, Left = 100, ClientChannel = "Beta", BuildSha256 = CollectorBuild.BetaBuildHash };
        DetectedClient ReadClient(int pid) => pid == 1 ? live : beta;
        var collected = CollectorIdentity.Discover([1, 2], ReadClient);
        var input = Identity.DiscoverInput([1, 2], ReadClient);
        Check(collected.Clients.SequenceEqual(new[] { live, beta }) && collected.Errors.Length == 0,
            "collector discovery distinguishes same-name Live and Beta characters");
        var partial = CollectorIdentity.Discover([1, 80420], pid => pid == 1 ? live
            : throw new InvalidDataException("Beta client: This EQ build needs a detector update."));
        Check(partial.Clients.SequenceEqual(new[] { live }) && partial.Errors.Length == 1
            && partial.Errors[0].Contains("PID 80420") && partial.Errors[0].Contains("Beta")
            && partial.Errors[0].Contains("detector update"),
            "a rejected Beta client retains process and failure context alongside valid Live clients");
        Check(input.Clients.SequenceEqual(new[] { live }) && input.Errors.Length == 1,
            "input discovery excludes Beta even when a supplied detector returns its validated passive identity");
        Check(!Identity.Same(live, live with { ClientChannel = "Beta" }) && !Identity.Same(live, live with { BuildSha256 = CollectorBuild.BetaBuildHash }),
            "session identity includes channel and executable build");
        Check(CollectorBuild.ForPath(@"c:\everquest-beta\EQGAME.EXE") == CollectorBuild.Beta
            && !MemoryReader.MatchesExecutablePath(CollectorBuild.Beta.ExecutablePath, MemoryReader.LiveExecutablePath),
            "registered installation paths are case-insensitive but never interchange Live and Beta");
        Reject(() => CollectorBuild.ForPath(@"C:\OtherEQ\eqgame.exe"), "unregistered installation is rejected");
        Reject(() => CollectorBuild.ForClient(beta with { BuildSha256 = Identity.BuildHash }), "Beta identity with a Live build hash is rejected");
        var staleBeta = beta with { BuildSha256 = "ca05079325390770ba58390d78d52424d5fe6de15b78786c19255a453ca875a1" };
        Reject(() => CollectorBuild.ForClient(staleBeta), "previous ca050 Beta identity cannot authorize the current 2d97 collector layout");
        var staleScan = CollectorIdentity.Discover([staleBeta.ProcessId], _ => staleBeta);
        Check(staleScan.Clients.Length == 0 && staleScan.Errors.Length == 1
            && staleScan.Errors[0].Contains($"PID {staleBeta.ProcessId}"),
            "discovery rejects a stale Beta identity instead of displaying it as a currently supported client");
        Reject(() => CollectorBuild.ForClient(beta with { UserAssigned = true }), "manual input assignments cannot stand in for passive identity validation");
        foreach (var capability in new[] { CollectorCapability.Inventory, CollectorCapability.Recipes, CollectorCapability.Inspection, CollectorCapability.Target,
            CollectorCapability.TargetBuffs, CollectorCapability.AdvancedLoot }.Where(capability => !CollectorBuild.Beta.Supports(capability)))
            Reject(() => CollectorBuild.Beta.Open(-1, null, capability), $"Beta {capability} is rejected before any process is opened");

        string area = Path.GetFullPath(output) + ".files"; Directory.CreateDirectory(area);
        string first = Path.Combine(area, "first.exe"), second = Path.Combine(area, "second.exe");
        File.WriteAllBytes(first, [1, 2, 3, 4]); File.WriteAllBytes(second, [4, 3, 2, 1]);
        DateTime timestamp = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, timestamp); File.SetLastWriteTimeUtc(second, timestamp);
        string firstHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(first)));
        string secondHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(second)));
        Identity.VerifyFile(first, firstHash); Identity.VerifyFile(first, firstHash);
        Reject(() => Identity.VerifyFile(second, firstHash), "equal length/timestamp cannot reuse another executable path's approval");
        Reject(() => Identity.VerifyFile(first, secondHash), "a different expected build hash cannot reuse cached approval");
        Identity.VerifyFile(second, secondHash);
        File.WriteAllBytes(first, [4, 3, 2, 1]); File.SetLastWriteTimeUtc(first, timestamp.AddSeconds(1));
        Reject(() => Identity.VerifyFile(first, firstHash), "a changed executable stamp invalidates its cached approval");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
    }
}
