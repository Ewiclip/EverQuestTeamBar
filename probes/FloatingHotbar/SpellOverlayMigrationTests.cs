namespace TeamBar;

internal static class SpellOverlayMigrationTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        var (sample, capture) = SpellOverlayTests.Fixture();
        var old = sample with { Name = "Target spells", Scale = .7, X = .2, Y = .8,
            Screens = [new("screen", "Monitor", new(.3, .4))], Spells = [
                new(100, "Shaman spell", PersonaClasses: ["Shaman"], ClickKey: "Alt+6"),
                new(101, "Shared spell", SpellTargetKind.Player, PersonaClasses: ["Shaman", "Berserker"],
                    Alternatives: [new(102, "Alternative")]),
                new(103, "Berserker spell", PersonaClasses: ["Berserker"], ClickKey: "Alt+P")] };
        var legacy = new SpellOverlayLibrary(1, [old]);
        var upgraded = SpellOverlayMigration.Upgrade(legacy);
        var shaman = upgraded.Overlays.Single(d => d.PersonaClass == "Shaman");
        var berserker = upgraded.Overlays.Single(d => d.PersonaClass == "Berserker");
        check(upgraded.Version == 2 && upgraded.Overlays.Length == 2 && shaman.Name == "Shaman Bar" && berserker.Name == "Berserker Bar",
            "mixed persona bar splits into named character/class bars");
        var unnamed = SpellOverlayMigration.Upgrade(new(1, [old with { Name = "" }]));
        check(unnamed.Overlays.Length == 2 && unnamed.Overlays.All(d => d.Name == ""),
            "a legacy bar with a blank title keeps that choice for both class bars");
        check(shaman.Spells.Select(s => s.SpellId).SequenceEqual(new[] {100, 101})
            && berserker.Spells.Select(s => s.SpellId).SequenceEqual(new[] {101, 103}), "migration preserves each persona's relative spell order");
        foreach (var bar in upgraded.Overlays)
        {
            check(bar.Character == old.Character && bar.Scale == old.Scale && bar.X == old.X && bar.Y == old.Y
                && bar.Screens!.SequenceEqual(old.Screens!) && bar.Locked == old.Locked && bar.Opacity == old.Opacity,
                "migration preserves character and layout for " + bar.PersonaClass);
            check(bar.Spells.All(s => s.PersonaClasses is null)
                && bar.Spells.All(s => s == old.Spells.Single(o => o.SpellId == s.SpellId) with { PersonaClasses = null }),
                "migration preserves keys, target, caster and alternatives for " + bar.PersonaClass);
        }
        check(ReferenceEquals(SpellOverlayMigration.Upgrade(upgraded), upgraded), "migration is idempotent");
        var unrestricted = SpellOverlayMigration.Upgrade(new(1, [sample]));
        check(unrestricted.Overlays.Length == Identity.Classes.Length
            && unrestricted.Overlays.All(d => d.Spells.SequenceEqual(sample.Spells)), "legacy unrestricted spells remain available in every class bar");
        string path = output + ".migration.json";
        var bytes = SpellOverlayStore.Save(path, legacy, null);
        SpellOverlayMigration.Save(path, upgraded, bytes);
        check(File.ReadAllBytes(path + ".before-character-class-bars.json").SequenceEqual(bytes), "migration keeps an exact original backup");
        check(SpellOverlayStore.Load(path).Library.Overlays.Select(d => d.PersonaClass).SequenceEqual(upgraded.Overlays.Select(d => d.PersonaClass)),
            "class bars survive save and reload");
        bool conflict = false; try { SpellOverlayMigration.Save(path, upgraded, bytes); } catch (IOException) { conflict = true; }
        check(conflict, "migration refuses to overwrite a newer saved profile");
        foreach (var invalid in new[] { shaman with { PersonaClass = null }, shaman with { PersonaClass = "Invalid" },
            shaman with { Spells = old.Spells } })
        {
            bool rejected = false; try { SpellOverlayStore.Validate(new(2, [invalid])); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "new profiles require valid bar classes and no per-spell class restrictions");
        }
        check(SpellOverlayRules.Evaluate(shaman, capture).Rows.Length > 0
            && SpellOverlayRules.Evaluate(berserker, capture).Rows.Length == 0, "whole-bar class gates displayed spell rows");
        var client = new DetectedClient(capture.ProcessId, 1, capture.ProcessStartedUtcTicks, 0, new(capture.CharacterName, 10, 130));
        var request = new SpellClickRequest("tap", OverlayId: berserker.Id, SpellId: 103, Target: SpellTargetKind.Npc,
            ProcessId: client.ProcessId, Started: client.StartedUtcTicks, Class: "Shaman", Key: "Alt+P");
        bool blocked = false, read = false;
        try { SpellOverlayClickRules.Resolve(upgraded, request, [client], (_, _) => { read = true; return capture; }); }
        catch (InvalidDataException) { blocked = true; }
        check(blocked && !read, "wrong-persona bar click is rejected before target reading or input");
    }
}
