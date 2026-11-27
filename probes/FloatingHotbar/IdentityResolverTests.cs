using System.Text;

internal static class IdentityResolverTests
{
    internal static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        void Reject(Action action, string message)
        { bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } Check(failed, message); }
        var f = new Fixture(); var resolved = IdentityResolver.Resolve(f.File);
        Check(resolved.Fields.OrderBy(p => p.Key).SequenceEqual(f.Fields.OrderBy(p => p.Key)), "All roots and field displacements are recovered from coherent code references");
        var memory = f.Memory(resolved);
        resolved.VerifyLoaded(memory.Read, Fixture.Image);
        var layout = resolved.Bind(memory.Read, Fixture.Image);
        Check(layout.PcName == 0x2fe8 && layout.PcProfileList == 0x2f50 && layout.PcProfileType == 0x2f58,
            "PC fields derive from validated virtual-base metadata rather than the old layout offsets");
        Check(Identity.Decode(memory.Read, Fixture.Image, layout).Character == new CharacterIdentity("Fixture", 10, 130),
            "Resolved layout passes the actual two-name/two-class/player/profile/loading decoder");

        var shifted = new Fixture(0x7000, 0x1500, 0x2a00);
        var shiftedResult = IdentityResolver.Resolve(shifted.File);
        var shiftedMemory = shifted.Memory(shiftedResult);
        Check(shiftedResult.Fields["PlayerRoot"] == 0xb020 && shiftedResult.Fields["PlayerClass"] == 0x1500
            && shiftedResult.Fields["PcPlayer"] == 0x2a00
            && Identity.Decode(shiftedMemory.Read, Fixture.Image, shiftedResult.Bind(shiftedMemory.Read, Fixture.Image)).Character.Name == "Fixture",
            "Relocated code/roots and changed member displacements are derived without hash edits");

        byte[] copy = (byte[])f.File.Clone(); copy[f.At("game-state")] = 0;
        Reject(() => IdentityResolver.Resolve(copy), "Missing code evidence is rejected");
        copy = (byte[])f.File.Clone(); var sample = resolved.Evidence[0]; sample.Bytes.CopyTo(copy, 0x1000);
        Reject(() => IdentityResolver.Resolve(copy), "Duplicate signature evidence is rejected instead of selecting a first match");
        copy = (byte[])f.File.Clone(); f.Field(copy, "player-class", "PlayerRoot", f.Fields["PlayerRoot"] + 8);
        Reject(() => IdentityResolver.Resolve(copy), "Independent root disagreements are rejected");
        copy = (byte[])f.File.Clone(); f.Field(copy, "profile-level", "ProfileHelper", f.Fields["ProfileHelper"] + 16);
        Reject(() => IdentityResolver.Resolve(copy), "Class/level access must call the same verified profile-list helper");
        copy = (byte[])f.File.Clone();
        foreach (var p in IdentityResolver.Patterns.Where(p => p.Fields.ContainsKey("PlayerRoot"))) f.Field(copy, p.Id, "PlayerRoot", 0x1100);
        Reject(() => IdentityResolver.Resolve(copy), "Code-section addresses cannot be accepted as writable roots");
        copy = (byte[])f.File.Clone(); f.Field(copy, "player-class", "PlayerClass", 0x10001);
        Reject(() => IdentityResolver.Resolve(copy), "Unbounded member displacements are rejected");
        copy = (byte[])f.File.Clone(); Fixture.U32(copy, Fixture.SectionTable + 40 + 12, 0x1000);
        Reject(() => IdentityResolver.Resolve(copy), "Overlapping image sections are rejected");
        Reject(() => IdentityResolver.Resolve(f.File[..200]), "Truncated PE images are rejected");
        copy = (byte[])f.File.Clone(); Fixture.U16(copy, 0x84, 0x14c);
        Reject(() => IdentityResolver.Resolve(copy), "Wrong executable architecture is rejected");

        memory.Bytes(Fixture.Image + sample.Rva, [0]);
        Reject(() => resolved.VerifyLoaded(memory.Read, Fixture.Image), "Running-image instruction mismatch rejects a stale or modified executable");
        memory = f.Memory(resolved);
        memory.U64(Fixture.Pc + 8, Fixture.Image + f.Fields["PcRoot"]);
        Reject(() => resolved.Bind(memory.Read, Fixture.Image), "Writable image data cannot stand in for virtual-base metadata");
        memory = f.Memory(resolved); memory.I32(Fixture.Image + f.ReadOnlyRva + 0x204, 0x2f48);
        Reject(() => resolved.Bind(memory.Read, Fixture.Image), "Runtime virtual-base metadata must agree with the file");
        memory = f.Memory(resolved);
        int rootReads = 0;
        byte[] Changing(ulong at, int count) => at == Fixture.Image + f.Fields["PcRoot"] && ++rootReads > 1
            ? BitConverter.GetBytes(Fixture.Pc + 8) : memory.Read(at, count);
        Reject(() => resolved.Bind(Changing, Fixture.Image), "A changing character root is rejected while binding fields");
        memory = f.Memory(resolved); memory.Text(Fixture.Pc + layout.PcName, "Different");
        Reject(() => Identity.Decode(memory.Read, Fixture.Image, layout), "Resolved offsets cannot authorize mismatched names");
        memory = f.Memory(resolved); memory.I32(Fixture.Profile + layout.ProfileClass, 1);
        Reject(() => Identity.Decode(memory.Read, Fixture.Image, layout), "Resolved offsets cannot authorize mismatched persona classes");
        memory = f.Memory(resolved); memory.U64(Fixture.Pc + layout.PcPlayer, Fixture.Profile);
        Reject(() => Identity.Decode(memory.Read, Fixture.Image, layout), "Resolved offsets retain the local-player back-reference check");
        memory = f.Memory(resolved); memory.I32(Fixture.Game + layout.GameState, 0);
        Reject(() => Identity.Decode(memory.Read, Fixture.Image, layout), "Loading state cannot authorize input");
        memory = f.Memory(resolved); memory.I32(Fixture.Profile + layout.ProfileType, 1);
        Reject(() => Identity.Decode(memory.Read, Fixture.Image, layout), "Unsupported active profile types are rejected");
        var live = new DetectedClient(1, 2, 3, 0, new("Fixture", 10, 130), BuildSha256: resolved.Hash);
        var discovery = Identity.DiscoverInput([1], _ => live);
        Check(discovery.Clients.Single() == live && discovery.Errors.Length == 0, "Validated Live identity with a new fingerprint reaches input discovery");
        Reject(() => CollectorBuild.ForClient(live), "Input identity resolution does not authorize obsolete passive collector layouts");
        discovery = Identity.DiscoverInput([1], _ => throw new InvalidDataException("inconclusive"));
        Check(discovery.Clients.Length == 0 && discovery.Errors.Single().Contains("PID 1"), "An unresolved client stays unavailable with its process identified");
        string file = Path.GetFullPath(output) + ".fixture.exe";
        File.WriteAllBytes(file, f.File); var cached = IdentityResolver.FileLayout(file);
        Check(ReferenceEquals(cached, IdentityResolver.FileLayout(file)), "An unchanged file reuses static resolution");
        copy = (byte[])f.File.Clone(); copy[f.At("game-state")] = 0;
        File.WriteAllBytes(file, copy); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Reject(() => IdentityResolver.FileLayout(file), "A changed file invalidates the cached resolution and cannot reuse its approval");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, processMemoryRead = false, gameInputSent = false });
    }

    private sealed class Fixture
    {
        internal const ulong Image = 0x140000000, Pc = 0x100000, Player = 0x200000, Game = 0x300000, List = 0x400000, Profile = 0x500000;
        internal const int SectionTable = 0x188;
        internal byte[] File = new byte[0x3400];
        internal uint ReadOnlyRva;
        internal Dictionary<string, uint> Fields;
        private readonly Dictionary<string, int> locations = new();
        private readonly uint codeRva;
        internal Fixture(uint code = 0x1000, uint playerClass = 0xfe4, uint pcPlayer = 0x2848)
        {
            codeRva = code; ReadOnlyRva = code + 0x2000; uint data = code + 0x4000;
            Fields = new() { ["GameRoot"] = data + 0x10, ["PlayerRoot"] = data + 0x20, ["PcRoot"] = data + 0x30,
                ["GameState"] = 0x5e4, ["PlayerClass"] = playerClass, ["PcPlayer"] = pcPlayer,
                ["ProfileHelper"] = code + 0x400, ["ProfileClass"] = 0x17cc, ["ProfileLevel"] = 0x17e8,
                ["PlayerName"] = 0xb4, ["PcNameRelative"] = 0xa8, ["PlayerType"] = 0x135 };
            U16(File, 0, 0x5a4d); U32(File, 0x3c, 0x80); U32(File, 0x80, 0x4550); U16(File, 0x84, 0x8664);
            U16(File, 0x86, 3); U16(File, 0x94, 0xf0); U16(File, 0x96, 0x22); U16(File, 0x98, 0x20b);
            BitConverter.GetBytes(Image).CopyTo(File, 0x98 + 24); U32(File, 0x98 + 32, 0x1000); U32(File, 0x98 + 36, 0x200);
            U32(File, 0x98 + 56, data + 0x1000); U32(File, 0x98 + 60, 0x400); U16(File, 0x98 + 68, 2); U32(File, 0x98 + 108, 16);
            for (int i = 0; i < 3; i++)
            {
                int s = SectionTable + 40 * i;
                U32(File, s + 8, 0x1000); U32(File, s + 12, code + (uint)i * 0x2000);
                U32(File, s + 16, 0x1000); U32(File, s + 20, (uint)(0x400 + i * 0x1000));
                U32(File, s + 36, i == 0 ? 0x60000020u : i == 1 ? 0x40000040u : 0xc0000040u);
            }
            for (int i = 0; i < IdentityResolver.Patterns.Length; i++)
            {
                var pattern = IdentityResolver.Patterns[i]; int at = 0x500 + i * 0x100; locations[pattern.Id] = at;
                byte[] bytes = pattern.PatternText.Split(' ').Select(s => s == "??" ? (byte)0 : Convert.ToByte(s, 16)).ToArray();
                bytes.CopyTo(File, at);
                foreach (var key in pattern.Fields.Keys) Field(File, pattern.Id, key, Fields[key]);
            }
            BitConverter.GetBytes(-8).CopyTo(File, 0x1600); U32(File, 0x1604, 0x2f40);
        }
        internal int At(string id) => locations[id];
        internal void Field(byte[] bytes, string id, string name, uint value)
        {
            var operand = IdentityResolver.Patterns.Single(p => p.Id == id).Fields[name];
            if (operand[0] < 0) return;
            int at = At(id); uint rva = codeRva + (uint)(at - 0x400);
            U32(bytes, at + operand[0], operand[1] == 0 ? value : unchecked(value - rva - (uint)operand[1]));
        }
        internal static void U16(byte[] b, int at, ushort value) => BitConverter.GetBytes(value).CopyTo(b, at);
        internal static void U32(byte[] b, int at, uint value) => BitConverter.GetBytes(value).CopyTo(b, at);
        internal Memory Memory(IdentityResolver.Resolved resolved)
        {
            var m = new Memory();
            foreach (var s in resolved.Sections) m.Bytes(Image + s.Rva, File.AsSpan(s.Raw, s.RawSize).ToArray());
            foreach (var (address, size) in new[] { (Pc, 0x5000), (Player, 0x2000), (Game, 0x1000), (List, 0x100), (Profile, 0x2000) }) m.Bytes(address, new byte[size]);
            m.U64(Image + Fields["PcRoot"], Pc); m.U64(Image + Fields["PlayerRoot"], Player); m.U64(Image + Fields["GameRoot"], Game);
            m.U64(Pc + 8, Image + ReadOnlyRva + 0x200); var l = resolved.Bind(m.Read, Image);
            m.U64(Pc + l.PcPlayer, Player); m.I32(Game + l.GameState, 5); m.Text(Player + l.PlayerName, "Fixture"); m.Text(Pc + l.PcName, "Fixture");
            m.U64(Pc + l.PcProfileList, List); m.U64(List + l.ListProfile, Profile);
            m.I32(Player + l.PlayerClass, 10); m.I32(Profile + l.ProfileClass, 10); m.I32(Profile + l.ProfileLevel, 130);
            return m;
        }
    }
    private sealed class Memory
    {
        private readonly Dictionary<ulong, byte> data = new();
        internal void Bytes(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) data[address + (ulong)i] = bytes[i]; }
        internal void U64(ulong address, ulong value) => Bytes(address, BitConverter.GetBytes(value));
        internal void I32(ulong address, int value) => Bytes(address, BitConverter.GetBytes(value));
        internal void Text(ulong address, string text) => Bytes(address, Encoding.ASCII.GetBytes(text + '\0'));
        internal byte[] Read(ulong address, int count) => Enumerable.Range(0, count).Select(i => data.TryGetValue(address + (ulong)i, out byte b)
            ? b : throw new InvalidDataException("Unmapped fixture read.")).ToArray();
    }
}
