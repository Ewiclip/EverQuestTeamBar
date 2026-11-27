// Offline mutation tests use the installed PE only as a realistic fixture. No
// process is opened and no modified file is written or executed.
internal static class TargetResolverTests
{
    internal static void Run(string executable, string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        void Reject(Action action, string name)
        { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, name); }
        var original = IdentityResolver.FileLayout(executable);
        var resolved = TargetLayoutResolver.Resolve(original);
        int Raw(uint rva) { var s = original.Sections.Single(s => rva >= s.Rva && rva - s.Rva < s.RawSize); return s.Raw + (int)(rva - s.Rva); }
        uint Rva(int raw) { var s = original.Sections.Single(s => raw >= s.Raw && raw - s.Raw < s.RawSize); return s.Rva + (uint)(raw - s.Raw); }
        void Field(byte[] file, string id, string name, uint value, int? location = null)
        {
            var operand = TargetLayoutResolver.Definitions.Single(d => d.Id == id).Fields[name];
            int at = location ?? Raw(resolved.Evidence.Single(e => e.Id == id).Rva);
            if (operand[0] < 0) throw new InvalidOperationException("Cannot move a function start through an operand.");
            BitConverter.GetBytes(operand[1] == 0 ? value : unchecked(value - Rva(at) - (uint)operand[1])).CopyTo(file, at + operand[0]);
        }
        TargetLayoutResolver.Resolved Resolve(byte[] file) => TargetLayoutResolver.Resolve(original with { Image = file, Hash = "offline-fixture" });
        byte[] Copy() => (byte[])original.Image.Clone();
        Check(resolved.Fields["PlayerRoot"] == original.Fields["PlayerRoot"] && resolved.Fields["SpawnLevel"] != 0x270,
            "Current target adapter shares validated identity and derives the relocated level field");
        var sample = resolved.Evidence.Single(e => e.Id == "target-selection");
        byte[] changed = Copy(); changed[Raw(sample.Rva)] = 0;
        Reject(() => Resolve(changed), "Missing target-selection evidence fails closed");
        changed = Copy(); Field(changed, "target-id", "TargetRoot", resolved.Fields["TargetRoot"] + 8);
        Reject(() => Resolve(changed), "Inconsistent target roots cannot authorize a reader");
        changed = Copy(); Field(changed, "target-registration", "WindowConstructor", resolved.Fields["WindowConstructor"] + 16);
        Reject(() => Resolve(changed), "Window registration must call the verified target constructor");
        changed = Copy(); Field(changed, "target-array-capacity", "EffectResize", resolved.Fields["EffectResize"] + 16);
        Reject(() => Resolve(changed), "Effect schema must belong to the target array's resize function");
        changed = Copy(); changed[Raw(resolved.Fields["WindowName"])] = (byte)'X';
        Reject(() => Resolve(changed), "A different named UI cannot be mistaken for the target window");
        changed = Copy(); Field(changed, "zone-long", "ZoneLongName", resolved.Fields["ZoneLongName"] + 4);
        Reject(() => Resolve(changed), "Independent zone short/long/ID references must agree");
        changed = Copy();
        foreach (var (id, field) in new[] { ("spawn-level-reader", "SpawnLevel"), ("spawn-level-reader", "SpawnLevelAgain"), ("spawn-level-write", "SpawnLevel") })
            Field(changed, id, field, 0x10001);
        Reject(() => Resolve(changed), "Consistent but unbounded level members are still rejected");
        changed = Copy();
        foreach (var (id, field) in new[] { ("spawn-level-reader", "SpawnLevel"), ("spawn-level-reader", "SpawnLevelAgain"), ("spawn-level-write", "SpawnLevel") })
            Field(changed, id, field, 0x700);
        Check(Resolve(changed).Fields["SpawnLevel"] == 0x700, "A coherently moved level field resolves without changing an executable hash or constant");
        changed = Copy();
        foreach (var (name, value) in new[] { ("EffectArray", 0x800u), ("EffectData", 0x808u), ("PendingData", 0x850u),
            ("PendingDataAgain", 0x850u), ("PreviousTarget", 0x880u), ("FirstUpdate", 0x898u), ("WindowSize", 0x900u) })
            foreach (var d in TargetLayoutResolver.Definitions.Where(d => d.Fields.ContainsKey(name))) Field(changed, d.Id, name, value);
        var shifted = Resolve(changed).Effects;
        Check(shifted.EffectArray == 0x800 && shifted.PendingData == 0x850 && shifted.PreviousTarget == 0x880 && shifted.FirstUpdate == 0x898,
            "Window fields can move independently while all code references and allocation bounds remain checked");
        changed = Copy(); Field(changed, "target-registration", "WindowSize", 0x200);
        Reject(() => Resolve(changed), "Window fields outside the constructor allocation are rejected");
        changed = Copy();
        foreach (var d in TargetLayoutResolver.Definitions.Where(d => d.Fields.ContainsKey("FirstUpdate")))
            Field(changed, d.Id, "FirstUpdate", resolved.Fields["PendingData"]);
        Reject(() => Resolve(changed), "Overlapping status and timer fields are rejected even when references agree");
        changed = Copy();
        int destination = original.Sections.First(s => s.Code).Raw + original.Sections.First(s => s.Code).RawSize - 0x800;
        sample.Bytes.CopyTo(changed, destination);
        foreach (var key in TargetLayoutResolver.Definitions.Single(d => d.Id == sample.Id).Fields.Keys)
            Field(changed, sample.Id, key, resolved.Fields[key], destination);
        Reject(() => Resolve(changed), "Two coherent target-selection matches are rejected rather than choosing the first");
        const ulong imageBase = 0x180000000;
        byte[] Read(ulong at, int count) => original.FileBytes(checked((uint)(at - imageBase)), count);
        resolved.VerifyLoaded(Read, imageBase);
        Reject(() => resolved.VerifyLoaded((at, count) => { var b = Read(at, count); if (at == imageBase + sample.Rva) b[0] ^= 1; return b; }, imageBase),
            "Loaded target instructions must match the resolved executable");
        Check(true, "Evidence verification supports a relocated image base");
        var client = new DetectedClient(1, 2, 3, 0, new("Fixture", 10, 130), BuildSha256: original.Hash);
        var build = CollectorBuild.ForClient(client);
        Check(build.UsesResolvedIdentity && build.Supports(CollectorCapability.Target | CollectorCapability.TargetBuffs),
            "Current Live collector exposes resolved targets and effects");
        foreach (var capability in new[] { CollectorCapability.Inventory, CollectorCapability.Inspection, CollectorCapability.Recipes })
            Reject(() => build.Open(-1, null, capability), $"Unvalidated {capability} reader stays blocked before opening a process");
        Check(build.Supports(CollectorCapability.AdvancedLoot) == AdvancedLootMemoryLayout.SupportsHash(original.Hash),
            "Advanced Loot is exposed only for explicitly validated executable layouts");
        Reject(() => AdvancedLootMemoryLayout.ForHash(new string('a', 64)), "Unknown builds cannot inherit the Advanced Loot layout");
        AdvancedLootMemoryLayout.VerifyCode(original, Read, imageBase);
        if (original.Hash == AdvancedLootMemoryLayout.NovemberBuildHash)
            foreach (var (rva, _) in AdvancedLootMemoryLayout.NovemberEvidence)
                Reject(() => AdvancedLootMemoryLayout.VerifyCode(original, (at, count) =>
                { var bytes = Read(at, count); if (at == imageBase + rva) bytes[0] ^= 1; return bytes; }, imageBase),
                    $"Changed loaded loot instructions at {rva:x} reject capture");
        Reject(() => { _ = build.IdentityLayout; }, "Resolved collector cannot hand out the obsolete fixed identity layout");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, sourceHash = original.Hash, processMemoryRead = false, gameInputSent = false });
    }
}
