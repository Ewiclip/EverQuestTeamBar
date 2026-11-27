using System.Text.Json;
namespace TeamBar;

internal static class SpellOverlayNumberedBleedTests
{
    internal static void Run(Action<bool, string> check)
    {
        // Installed IDs in numeric rank order, deliberately not ID order.
        int[] ids = [16124, 16125, 16126, 16679, 16680, 16681, 30903, 30904, 30905,
            41347, 41348, 41349, 36933, 36934, 36935, 49675, 50962, 50963, 50964,
            49587, 49797, 50607, 53764, 53765, 53766, 70950, 70951, 70952];
        var data = SpellNameCatalog.Read();
        var records = data.Records ?? throw new InvalidDataException("Installed spell metadata is required for the numbered bleed fixture.");
        var catalog = new SpellOverlayCatalog(data);
        var (sample, capture) = SpellOverlayTests.Fixture();
        TargetCapture Seen(int id, string? caster = null) => capture with
        {
            Buffs = capture.Buffs! with { Effects = [new(0, id, "Observed labels are not family proof", caster ?? capture.CharacterName, 83000, 56)] }
        };
        SpellOverlayState Evaluate(SpellWatch rule, TargetCapture actual, SpellOverlayCatalog? source = null) =>
            SpellOverlayRules.Evaluate(sample with { Spells = [rule] }, actual, catalog: source ?? catalog);
        foreach (int id in ids)
        {
            check(catalog.MatchingSpellIds(id).SequenceEqual(ids),
                "installed Languid Bite Bleed family includes I through XXVIII in numeric order from " + id);
            var saved = new SpellWatch(id, records[id].Name, ClickKey: "Alt+NumpadEnter");
            var latest = new SpellWatch(ids[^1], records[ids[^1]].Name, ClickKey: "Ctrl+PageDown");
            check(catalog.SameRankFamily(id, ids[^1]) && catalog.SameRankFamily(ids[^1], id)
                && Evaluate(saved, Seen(ids[^1])).Rows.Single() is { State: "Active", RemainingMilliseconds: 83000 }
                && Evaluate(latest, Seen(id)).Rows.Single() is { State: "Active", RemainingMilliseconds: 83000 },
                "each installed bleed rank and XXVIII satisfy one another, including changed durations: " + id);
        }
        check(catalog.SameRankFamily(49587, 49797) && catalog.SameRankFamily(49797, 49587),
            "Languid Bite Bleed XX and XXI are the same watched family in both directions");
        foreach (int id in new[] { 16121, 16122, 16123, 41346, 302, 2152, 15332 })
            check(!catalog.SameRankFamily(49587, id) && !catalog.SameRankFamily(id, 49587),
                "numbered bleed excludes its Languid Bite proc parent and nearby unrelated names: " + id);

        var watch = new SpellWatch(49587, "Saved Languid Bite Bleed XX", ClickKey: "Alt+NumpadEnter", PersonaClasses: ["Shaman"]);
        var second = new SpellWatch(302, "Languid Pace", ClickKey: "5");
        var definition = sample with { Spells = [watch, second] };
        var library = new SpellOverlayLibrary(1, [definition]);
        string before = JsonSerializer.Serialize(library, SpellOverlayStore.Json);
        var state = SpellOverlayRules.Evaluate(definition, Seen(49797), catalog: catalog);
        check(state.Rows.Select(r => r.Rule).SequenceEqual(definition.Spells)
            && state.Rows.Select(r => r.Priority).SequenceEqual(new[] { 1, 2 })
            && state.Rows[0].State == "Active" && state.Rows[1].Missing
            && ReferenceEquals(state.Rows[0].Rule, watch) && state.Rows[0].Rule.ClickKey == "Alt+NumpadEnter"
            && JsonSerializer.Serialize(library, SpellOverlayStore.Json) == before,
            "XX to XXI preserves saved spell ID, custom label, key, persona filter, order, and serialized settings");
        check(Evaluate(watch, Seen(49797, "Other")).Rows.Single().Missing,
            "numbered bleed equivalence preserves the my-casts filter");
        check(Evaluate(watch with { Caster = SpellCasterKind.Anyone }, Seen(49797, "Other")).Rows.Single().State == "Active",
            "numbered bleed can track another caster when anyone-casts is configured");
        var unknownCaster = Seen(49797);
        unknownCaster = unknownCaster with { Buffs = unknownCaster.Buffs! with { Effects = [unknownCaster.Buffs!.Effects[0] with { CasterName = null }] } };
        check(Evaluate(watch, unknownCaster).Rows.Single().State == "Caster unknown",
            "unknown caster on an upgraded numbered bleed does not become a missing alert");
        var otherPersona = Seen(49797) with { CharacterClass = "Cleric", Buffs = Seen(49797).Buffs! with { CharacterClass = "Cleric" } };
        check(Evaluate(watch, otherPersona).Rows.Length == 0, "numbered bleed matching retains persona filtering");
        var player = capture.Target! with { RawType = 0, Type = "Player" };
        var playerCapture = Seen(49797) with { Target = player, Buffs = Seen(49797).Buffs! with { Target = player } };
        check(Evaluate(watch, playerCapture).Rows.Length == 0, "numbered bleed matching retains target-type filtering");
        check(Evaluate(watch with { ShowMissing = false }, Seen(49797)).Rows.Length == 1
            && Evaluate(watch with { ShowMissing = false }, capture with { Buffs = capture.Buffs! with { Effects = [] } }).Rows.Length == 0,
            "numbered bleed upgrades respect the show-if-missing setting");

        var fixtureRecords = ids.Select(id => records[id]).ToArray();
        SpellOverlayCatalog With(params LocalSpellRecord[] extra)
        {
            var byId = fixtureRecords.Concat(extra).ToDictionary(s => s.Id);
            return new(new(byId.ToDictionary(p => p.Key, p => p.Value.Name), "fixture", null, byId));
        }
        var future = records[70952] with { Id = 990029, Name = "Languid Bite Bleed XXIX", Rank = 29 };
        var futureCatalog = With(future);
        check(futureCatalog.MatchingSpellIds(49587).SequenceEqual(ids.Append(future.Id))
            && futureCatalog.SameRankFamily(future.Id, 49587)
            && Evaluate(watch, Seen(future.Id), futureCatalog).Rows.Single().State == "Active",
            "a future metadata-verified XXIX upgrade joins the same watched bleed without an ID allowlist change");

        LocalSpellRecord[] excluded = [
            future with { Group = 990030 }, future with { Group = 0 }, future with { Target = 6 },
            future with { PlayerSpell = false }, future with { Rank = 28 }, future with { Duration = 0 },
            future with { Name = "Languid Bite Bleed XXIX Recourse" }, future with { Name = "Languid Bite Bleed XXIXI" },
            future with { Name = "Languid Bite Bleed IXX", Rank = 19 }, future with { Name = "Languid Bite Bleed IIII", Rank = 4 },
            future with { Name = "Languid Bite Bleed 29" }, future with { Name = "Greater Languid Bite Bleed XXIX" },
            future with { Name = "Languid Bite XXIX" }];
        for (int i = 0; i < excluded.Length; i++)
        {
            var invalid = excluded[i] with { Id = 991000 + i };
            var invalidCatalog = With(invalid);
            check(!invalidCatalog.SameRankFamily(49587, invalid.Id) && !invalidCatalog.SameRankFamily(invalid.Id, 49587)
                && Evaluate(watch, Seen(invalid.Id), invalidCatalog).Rows.Single().Missing,
                "numbered bleed rejects invalid numeral/name/group/rank/scope evidence: " + i);
        }
        var ordinaryOne = future with { Id = 992001, Name = "Other Effect I", Group = 992000, Rank = 1 };
        var ordinaryTwo = ordinaryOne with { Id = 992002, Name = "Other Effect II", Rank = 2 };
        check(!With(ordinaryOne, ordinaryTwo).SameRankFamily(ordinaryOne.Id, ordinaryTwo.Id),
            "the Languid Bite Bleed exception does not strip Roman numerals from unrelated spell names");
        var mixedSuffix = future with { Id = 992003, Name = "Languid Bite Bleed I Rk. III", Rank = 10 };
        var mixedCatalog = With(mixedSuffix);
        check(!mixedCatalog.SameRankFamily(mixedSuffix.Id, ids[0])
            && !mixedCatalog.SameRankFamily(ids[0], mixedSuffix.Id)
            && mixedCatalog.MatchingSpellIds(ids[0]).SequenceEqual(ids),
            "ordinary rank suffixes cannot steal a numbered bleed member or corrupt its cached family");
        var unverified = Seen(999999);
        unverified = unverified with { Buffs = unverified.Buffs! with { Effects = [unverified.Buffs!.Effects[0] with { SpellName = "Languid Bite Bleed XXI" }] } };
        check(Evaluate(watch, unverified).Rows.Single().Missing,
            "observed numbered bleed names without installed ID proof cannot manufacture a match");
    }
}
