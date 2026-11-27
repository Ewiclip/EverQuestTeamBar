using System.Text.Json;
namespace TeamBar;

internal static class SpellOverlayAlternativeTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        var (sample, capture) = SpellOverlayTests.Fixture();
        LocalSpellRecord Spell(int id, string name, int group, int rank = 1) => new(id, name, 7, 5, 56, group, rank, true, []);
        LocalSpellRecord[] records = [Spell(100, "Priority Slow", 10), Spell(101, "Priority Slow Rk. II", 10, 5),
            Spell(200, "Replacement Slow", 20), Spell(201, "Replacement Slow Rk. III", 20, 10),
            Spell(300, "Independent Slow", 30), Spell(301, "Replacement Slow Extended", 31),
            Spell(400, "Combined Slow", 40) with { Duration = 1,
                Effects = [new(1, 470, 100, 10, 100, 0), new(2, 470, 100, 20, 100, 0)] },
            Spell(500, "Unresolved Slow", 50) with { Duration = 1, Effects = [new(1, 470, 100, 999, 100, 0)] }];
        var byId = records.ToDictionary(s => s.Id);
        var catalog = new SpellOverlayCatalog(new(byId.ToDictionary(p => p.Key, p => p.Value.Name), "fixture", null, byId));
        TargetEffect Effect(int id, string? caster = null, int milliseconds = 94000) =>
            new(0, id, byId.GetValueOrDefault(id)?.Name, caster ?? capture.CharacterName, milliseconds, 104);
        TargetCapture Seen(params TargetEffect[] effects) => capture with { Buffs = capture.Buffs! with { Effects = effects } };
        SpellOverlayState Evaluate(SpellWatch rule, TargetCapture actual, SpellOverlayCatalog? source = null) =>
            SpellOverlayRules.Evaluate(sample with { Spells = [rule] }, actual, catalog: source ?? catalog);
        WatchedSpellRow Row(SpellWatch rule, params TargetEffect[] effects) => Evaluate(rule, Seen(effects)).Rows.Single();
        var watch = new SpellWatch(100, "Saved slow label", IconId: 56, PersonaClasses: ["Shaman"], ClickKey: "Alt+H",
            Alternatives: [new(200, "Saved replacement label", 57)]);

        foreach (int id in new[] { 100, 101, 200, 201 })
        {
            var row = Row(watch, Effect(id));
            check(row is { State: "Active", Children: null, Priority: 1, RemainingMilliseconds: 94000, IconId: 104 }
                && ReferenceEquals(row.Rule, watch) && row.Rule.ClickKey == "Alt+H" && row.Name == "Saved slow label"
                && row.CasterName == capture.CharacterName && row.MatchedSpellName == byId[id].Name,
                $"OR rule accepts primary/alternative rank {id} in one row while retaining saved identity, label, and key");
        }
        var longest = Row(watch, Effect(100, milliseconds: 15000), Effect(201, "Another", 85000));
        check(longest.RemainingMilliseconds == 15000 && longest.CasterName == capture.CharacterName,
            "a longer alternative from another caster cannot override the mine filter");
        var anyone = watch with { Caster = SpellCasterKind.Anyone };
        longest = Row(anyone, Effect(100, milliseconds: 15000), Effect(201, "Another", 85000));
        check(longest.RemainingMilliseconds == 85000 && longest.CasterName == "Another" && longest.MatchedSpellName == "Replacement Slow Rk. III",
            "anyone OR rule exposes the actual longest-lived approved effect and caster");
        check(Row(anyone, Effect(100, "First", 30000), Effect(200, "Second", 30000)).CasterName == "First",
            "equal option durations use the saved primary-first order");
        check(Row(anyone, Effect(100, milliseconds: 15000), Effect(200, "Another", -1)) is { Time: "No timer", CasterName: "Another" },
            "an indefinite approved option retains the no-timer state");
        check(Row(watch, Effect(301)) is { State: "Missing", CasterName: null, MatchedSpellName: null, Children: null },
            "an unapproved similarly named spell cannot satisfy an alternatives rule");
        check(Row(watch, Effect(201, "Another")).Missing, "another caster's alternative does not satisfy mine");
        check(Row(watch) is { State: "Missing", IconId: 56, Children: null }, "all absent alternatives produce one primary missing indicator");
        check(Evaluate(watch with { ShowMissing = false }, Seen()).Rows.Length == 0,
            "hide-missing removes an absent alternatives rule as a whole");
        check(Evaluate(watch with { ShowMissing = false }, Seen(Effect(201))).Rows.Single().State == "Active",
            "hide-missing keeps a present alternative visible");

        var unknown = Effect(201) with { CasterName = null };
        check(Row(watch, unknown) is { State: "Caster unknown", Missing: false, CasterName: null, MatchedSpellName: "Replacement Slow Rk. III" },
            "an alternative with an unknown caster suppresses a missing alert");
        check(Row(watch with { ShowMissing = false }, unknown).State == "Caster unknown",
            "hiding missing rows does not hide unresolved alternative ownership");
        check(Row(watch, unknown, Effect(100)).State == "Active", "one verified active option satisfies OR despite another unknown caster");
        check(Row(anyone, unknown) is { State: "Active", CasterName: null }, "anyone accepts an unknown observed caster without inventing a name");
        check(Row(watch, Effect(201) with { CasterName = "  " }).State == "Caster unknown", "blank observed caster names remain unknown");
        check(Row(watch, Effect(201) with { SpellName = null }).MatchedSpellName == "Saved replacement label",
            "missing observed spell names use the configured option label");
        check(Row(watch, Effect(201) with { SpellName = "Observed current rank" }).MatchedSpellName == "Observed current rank",
            "observed names provide display details without changing the saved rule");
        var ordinary = watch with { Alternatives = null };
        check(Row(ordinary, Effect(101)) is { State: "Active", CasterName: not null, MatchedSpellName: "Priority Slow Rk. II" }
            && Row(ordinary, Effect(200)).Missing && Row(watch with { Alternatives = [] }, Effect(200)).Missing,
            "legacy and empty-alternatives rules retain their original matching with added observed details");

        var player = capture.Target! with { RawType = 0, Type = "Player" };
        var playerCapture = Seen(Effect(200)) with { Target = player, Buffs = Seen(Effect(200)).Buffs! with { Target = player } };
        check(Evaluate(watch, playerCapture).Rows.Length == 0 && Evaluate(watch with { Target = SpellTargetKind.Player }, playerCapture).Rows.Single().State == "Active",
            "alternatives inherit the watched rule's target filter");
        var cleric = Seen(Effect(200)) with { CharacterClass = "Cleric", Buffs = Seen(Effect(200)).Buffs! with { CharacterClass = "Cleric" } };
        check(Evaluate(watch, cleric).Rows.Length == 0, "alternatives inherit the watched rule's persona filter");
        foreach (var unavailable in new[] { Seen() with { Buffs = null }, Seen() with { BuffsError = "Unavailable" },
            Seen() with { Buffs = capture.Buffs! with { Effects = [], Status = "waiting-for-target-data" } } })
            check(Evaluate(watch, unavailable).Rows.Length == 0, "unavailable target data cannot manufacture missing alternatives");
        var badCatalog = new SpellOverlayCatalog(new(new Dictionary<int, string>(), null, "Unavailable"));
        check(Evaluate(watch, Seen(), badCatalog).Rows.Single() is { Missing: false, State: "Spell data unavailable" },
            "unavailable installed metadata cannot manufacture an alternatives missing alert");
        check(SpellOverlayRules.Evaluate(sample with { Spells = [watch] }, Seen()).Rows.Single().State == "Spell data unavailable",
            "unresolved triggered expansion without a catalog fails closed for alternatives");
        check(SpellOverlayRules.Evaluate(sample with { Spells = [watch with { ExpandTriggered = false }] }, Seen(Effect(200)))
            .Rows.Single().State == "Active", "explicitly disabled expansion can match exact alternative IDs without installed metadata");
        var uncertainOption = watch with { Alternatives = [new(500, "Unresolved Slow")] };
        check(Row(uncertainOption).State == "Triggered spell group unavailable" && !Row(uncertainOption).Missing,
            "an unresolved approved option suppresses a missing alert");
        check(Row(uncertainOption, Effect(101)).State == "Active", "a complete known option wins over unresolved spell metadata elsewhere");

        var compound = watch with { SpellId = 300, Name = "Saved compound choice", Alternatives = [new(400, "Combined Slow", 80)] };
        foreach (int child in new[] { 101, 201 })
        {
            check(Row(compound, Effect(child)) is { State: "Missing", Children: null },
                "one triggered child alone cannot satisfy a compound alternative: " + child);
            check(Evaluate(compound with { ShowMissing = false }, Seen(Effect(child))).Rows.Length == 0,
                "hiding missing rows cannot turn a partial triggered alternative into active: " + child);
        }
        var combined = Row(compound, Effect(101, milliseconds: 25000), Effect(201, milliseconds: 60000));
        check(combined is { State: "Active", RemainingMilliseconds: 25000, Children: null, IconId: 80 }
            && combined.CasterName == capture.CharacterName && combined.MatchedSpellName == "Priority Slow Rk. II + Replacement Slow Rk. III"
            && ReferenceEquals(combined.Rule, compound), "all triggered children satisfy one flat alternative row until the first child expires");
        check(Row(compound, Effect(101, milliseconds: -1), Effect(201, milliseconds: 60000)).RemainingMilliseconds == 60000,
            "an indefinite triggered child does not hide another child's finite expiry");
        check(Row(compound, Effect(101, milliseconds: -1), Effect(201, milliseconds: -1)).Time == "No timer",
            "a completely indefinite compound option has no timer");
        check(Row(compound, Effect(101), unknown).State == "Caster unknown" && !Row(compound, unknown).Missing,
            "unresolved compound ownership suppresses missing even when another required child is absent");
        var compoundAnyone = compound with { Caster = SpellCasterKind.Anyone };
        check(Row(compoundAnyone, Effect(101, "First"), Effect(201, "Second")).CasterName == "First, Second",
            "a compound anyone option names its distinct observed casters without attributing every child to one person");
        check(Row(compoundAnyone, Effect(101), unknown) is { State: "Active", CasterName: null },
            "a complete compound option containing an unknown caster cannot imply complete known ownership");
        check(Row(compound, Effect(101), unknown, Effect(300)).State == "Active",
            "a complete ordinary option wins over incomplete compound ownership");
        check(Row(compound, Effect(101, milliseconds: 80000), Effect(201, milliseconds: 15000), Effect(300, milliseconds: 20000))
            .MatchedSpellName == "Independent Slow", "OR selection compares compound completeness duration with ordinary alternatives");
        check(Row(compound with { ExpandTriggered = false }, Effect(101), Effect(201)).Missing
            && Row(compound with { ExpandTriggered = false }, Effect(400)).State == "Active",
            "disabled expansion requires the configured compound spell itself rather than its children");
        var primaryCompound = compound with { SpellId = 400, Name = "Primary compound", Alternatives = [new(300, "Independent Slow")] };
        check(Row(primaryCompound, Effect(101)).Missing && Row(primaryCompound, Effect(101), Effect(201)).State == "Active",
            "a triggered primary follows the same AND requirements as a triggered alternative");
        var legacyGroup = primaryCompound with { Alternatives = null };
        var group = Row(legacyGroup, Effect(101), Effect(201));
        check(group.State == "Group" && group.Children is { Length: 2 } children && children.All(c => c.CasterName == capture.CharacterName)
            && Row(legacyGroup, Effect(101)).Missing, "legacy triggered groups retain children and missing semantics with observed caster details");

        var second = new SpellWatch(300, "Second row", ClickKey: "5");
        var definition = sample with { Spells = [watch, second] };
        var library = new SpellOverlayLibrary(1, [definition]);
        string before = JsonSerializer.Serialize(library, SpellOverlayStore.Json);
        var state = SpellOverlayRules.Evaluate(definition, Seen(Effect(201)), catalog: catalog);
        check(state.Rows.Select(r => r.Priority).SequenceEqual(new[] { 1, 2 }) && ReferenceEquals(state.Rows[0].Rule, watch)
            && state.Rows[1].Missing && JsonSerializer.Serialize(library, SpellOverlayStore.Json) == before,
            "alternative changes preserve row order, rule identity, click mappings, and serialized configuration");
        string path = Path.GetFullPath(output) + ".alternatives.library.json";
        var saved = SpellOverlayStore.Save(path, library, null);
        var restored = SpellOverlayStore.Load(path).Library;
        check(JsonSerializer.Serialize(restored, SpellOverlayStore.Json) == before
            && restored.Overlays[0].Spells[0].Alternatives!.SequenceEqual(watch.Alternatives!)
            && restored.Overlays[0].Spells[0].ClickKey == "Alt+H", "alternatives, order, filters, icons, and keys round trip through the settings store");
        var legacy = JsonSerializer.Deserialize<SpellOverlayLibrary>("""
            {"version":1,"overlays":[{"id":"legacy","name":"Legacy","character":"Preview character","spells":[{"spellId":100,"name":"Legacy slow"}]}]}
            """, SpellOverlayStore.Json)!;
        SpellOverlayStore.Validate(legacy);
        check(legacy.Overlays[0].Spells[0].Alternatives is null, "old saved JSON without alternatives remains valid");
        var maximum = watch with { Alternatives = Enumerable.Range(600, SpellOverlayStore.MaxAlternatives).Select(id => new SpellAlternative(id, "Option " + id)).ToArray() };
        SpellOverlayStore.Validate(new(1, [sample with { Spells = [maximum, new(600, "Separate watched row")] }]));
        check(true, "twelve alternatives are allowed and independent watched rows may overlap an option");
        SpellAlternative[][] invalidOptions = [
            Enumerable.Range(600, SpellOverlayStore.MaxAlternatives + 1).Select(id => new SpellAlternative(id, "Option")).ToArray(),
            [new(100, "Duplicate primary")], [new(200, "First"), new(200, "Duplicate alternative")], [null!],
            [new(0, "Invalid ID")], [new(1000001, "Invalid ID")], [new(200, " ")], [new(200, new string('x', 256))],
            [new(200, "Invalid icon", -1)], [new(200, "Invalid icon", 9216)]];
        foreach (var invalidOptionsSet in invalidOptions)
        {
            bool rejected = false;
            try { SpellOverlayStore.Save(path, new(1, [sample with { Spells = [watch with { Alternatives = invalidOptionsSet }] }]), saved); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllBytes(path).SequenceEqual(saved), "invalid alternatives cannot replace saved settings");
        }
    }
}
