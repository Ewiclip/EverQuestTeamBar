using System.Text.Json;
namespace TeamBar;

internal static class SpellOverlayRankTests
{
    private static SpellOverlayCatalog Catalog(IEnumerable<LocalSpellRecord> records)
    {
        var byId = records.ToDictionary(s => s.Id);
        return new(new(byId.ToDictionary(p => p.Key, p => p.Value.Name), "fixture", null, byId));
    }
    private static LocalSpellRecord Spell(int id, string name, int group, int rank = 1) =>
        new(id, name, 7, 5, 56, group, rank, true, []);

    internal static void Run(Action<bool, string> check)
    {
        var (sample, capture) = SpellOverlayTests.Fixture();
        LocalSpellRecord[] ranks = [Spell(100, "Lasting Ward", 10), Spell(101, "Lasting Ward Rk. I", 10),
            Spell(102, "Lasting Ward Rk. II", 10, 5), Spell(103, "Lasting Ward Rk. III", 10, 10)];
        LocalSpellRecord[] unrelated = [Spell(110, "Lasting Ward Extended", 10), Spell(111, "Greater Lasting Ward", 10),
            Spell(112, "Lasting Ward Rk. II", 11, 5), Spell(113, "Lasting Ward Rk. III Recourse", 10),
            Spell(114, "Lasting Ward Rk. IV", 10), Spell(115, "Lasting Ward Rk. II", 0, 5),
            Spell(116, "Lasting Ward", 10) with { PlayerSpell = false },
            Spell(117, "Lasting Ward Rk. II", 10, 5) with { Target = 6 },
            Spell(118, "Lasting Ward Rk. II", 10, 5) with { Duration = 0 }];
        var catalog = Catalog([.. ranks, .. unrelated]);
        TargetCapture Seen(params TargetEffect[] effects) => capture with { Buffs = capture.Buffs! with { Effects = effects } };
        TargetEffect Effect(int id, string? caster = null, int milliseconds = 94000) =>
            new(0, id, "Untrusted observed display name", caster ?? capture.CharacterName, milliseconds, 104);
        SpellOverlayDefinition Watching(SpellWatch rule) => sample with { Spells = [rule] };
        WatchedSpellRow Row(SpellWatch rule, TargetCapture actual, SpellOverlayCatalog? source = null) =>
            SpellOverlayRules.Evaluate(Watching(rule), actual, catalog: source ?? catalog).Rows.Single();

        foreach (var selected in ranks)
        {
            var saved = new SpellWatch(selected.Id, "My saved label", ClickKey: "Ctrl+Shift+PageDown");
            foreach (var observed in ranks)
            {
                var row = Row(saved, Seen(Effect(observed.Id)));
                check(row is { State: "Active", RemainingMilliseconds: 94000, IconId: 104 }
                    && ReferenceEquals(row.Rule, saved) && row.Rule.ClickKey == "Ctrl+Shift+PageDown",
                    $"saved rank {selected.Name} tracks observed {observed.Name} without rewriting the rule or key");
                check(catalog.SameRankFamily(selected.Id, observed.Id) && catalog.SameRankFamily(observed.Id, selected.Id),
                    $"rank family equality is symmetric for {selected.Id}/{observed.Id}");
            }
            foreach (var other in unrelated)
                check(!catalog.SameRankFamily(selected.Id, other.Id) && Row(saved, Seen(Effect(other.Id))).Missing,
                    $"rank matching rejects a different name, group, or effect scope: {selected.Id}/{other.Id}");
        }
        var watch = new SpellWatch(100, "Lasting Ward", ClickKey: "Ctrl+Shift+PageDown");
        check(Row(watch, Seen(Effect(999999) with { SpellName = "Lasting Ward Rk. III" })).Missing,
            "an observed name without installed ID metadata cannot satisfy a rank family");
        check(Row(watch, Seen(Effect(103, "Other"))).Missing, "rank equivalence retains the my-casts filter");
        check(Row(watch with { Caster = SpellCasterKind.Anyone }, Seen(Effect(103, "Other"))).State == "Active",
            "anyone-casts can match another caster's upgraded rank");
        check(Row(watch, Seen(Effect(103) with { CasterName = null })).State == "Caster unknown",
            "an upgraded rank with an unknown caster cannot manufacture a missing alert");
        check(Row(watch, Seen(Effect(100, milliseconds: 12000), Effect(103, milliseconds: 55000))).RemainingMilliseconds == 55000,
            "multiple observed ranks choose the longest matching remaining duration");
        check(Row(watch, Seen(Effect(100, milliseconds: 12000), Effect(103, milliseconds: -1))).Time == "No timer",
            "an indefinite upgraded rank retains the no-timer state");
        check(!Row(watch with { ExpandTriggered = false }, Seen(Effect(103))).Missing,
            "disabling triggered expansion does not disable rank equivalence");
        check(SpellOverlayRules.Evaluate(Watching(watch with { ShowMissing = false }), Seen(Effect(103)), catalog: catalog).Rows.Length == 1,
            "hide-missing rules remain visible when another rank is active");

        LocalSpellRecord[] groupless = ranks.Select(s => s with { Id = s.Id + 100, Name = s.Name.Replace("Lasting Ward", "Hidden Ward"),
            Group = 0, Rank = 0, PlayerSpell = false }).ToArray();
        var effectsCatalog = Catalog(groupless);
        foreach (var selected in groupless)
            foreach (var observed in groupless)
                check(!Row(new(selected.Id, selected.Name), Seen(Effect(observed.Id)), effectsCatalog).Missing,
                    $"unambiguous installed groupless rank names match {selected.Id}/{observed.Id}");
        var ambiguous = Catalog([.. groupless, groupless[2] with { Id = 299 }]);
        check(ambiguous.MatchingSpellIds(200).SequenceEqual(new[] { 200 }) && !ambiguous.SameRankFamily(200, 203),
            "duplicate groupless rank names fall back to exact IDs");
        var inconsistent = Catalog(ranks.Select(s => s.Id == 102 ? s with { Rank = 10 } : s));
        check(inconsistent.MatchingSpellIds(100).SequenceEqual(new[] { 100 }) && !inconsistent.SameRankFamily(100, 103),
            "contradictory installed rank metadata falls back to exact IDs");
        var absent = new SpellOverlayCatalog(new(new Dictionary<int, string> { [100] = "Lasting Ward", [103] = "Lasting Ward Rk. III" }, null, null));
        check(absent.MatchingSpellIds(100).SequenceEqual(new[] { 100 }) && !absent.SameRankFamily(100, 103),
            "names from an unsupported metadata layout cannot establish rank equivalence");
        var unavailable = new SpellOverlayCatalog(new(new Dictionary<int, string>(), null, "Unavailable", ranks.ToDictionary(s => s.Id)));
        check(unavailable.MatchingSpellIds(100).SequenceEqual(new[] { 100 }), "unavailable spell metadata never broadens matching");

        var installed = new SpellOverlayCatalog(SpellNameCatalog.Read());
        int[] installedRanks = [72115, 72116, 72117]; // Breath of Pustim: base, II, III in installed Live data.
        foreach (int selected in installedRanks)
        {
            check(installed.MatchingSpellIds(selected).SequenceEqual(installedRanks),
                "installed ordinary spell metadata proves the Breath of Pustim rank family: " + selected);
            foreach (int observed in installedRanks)
                check(Row(new(selected, "Breath of Pustim", ClickKey: "Alt+6"), Seen(Effect(observed)), installed)
                    is { State: "Active", RemainingMilliseconds: 94000, Rule.ClickKey: "Alt+6" },
                    $"installed ordinary rank {selected} tracks {observed} with its saved key");
        }

        var otherWatch = new SpellWatch(110, "Second saved row", ClickKey: "5");
        var ordered = sample with { Spells = [watch, otherWatch] };
        var library = new SpellOverlayLibrary(1, [ordered]);
        string before = JsonSerializer.Serialize(library, SpellOverlayStore.Json);
        var state = SpellOverlayRules.Evaluate(ordered, Seen(Effect(103)), catalog: catalog);
        check(state.Rows.Select(r => r.Rule).SequenceEqual(ordered.Spells) && state.Rows.Select(r => r.Priority).SequenceEqual(new[] { 1, 2 })
            && state.Rows[0].State == "Active" && state.Rows[1].Missing
            && JsonSerializer.Serialize(library, SpellOverlayStore.Json) == before,
            "rank upgrades preserve saved IDs, labels, row order, click mappings, and serialized configuration");
        var upgraded = Seen(Effect(103));
        var client = new DetectedClient(capture.ProcessId, capture.ProcessStartedUtcTicks, 123, 0, new(capture.CharacterName, 10, 130));
        var request = new SpellClickRequest("tap", OverlayId: ordered.Id, SpellId: watch.SpellId, ProcessId: client.ProcessId,
            Started: client.StartedUtcTicks, TargetId: capture.Target!.SpawnId, TargetName: capture.Target.Name,
            Class: client.Character.Class, ZoneId: capture.Zone.Id, Key: watch.ClickKey);
        var tap = SpellOverlayClickRules.Resolve(library, request, [client], (_, _) => upgraded);
        check(tap.Scan == InputKeys.Scan("PageDown") && tap.Modifiers == 3 && tap.ProcessId == client.ProcessId,
            "an observed rank upgrade retains the saved overlay's one-tap key and recipient");

        LocalSpellRecord[] echoes = [Spell(500, "Echo Ward", 50), Spell(501, "Echo Ward Rk. II", 50, 5), Spell(502, "Echo Ward Rk. III", 50, 10)];
        echoes = echoes.Select(s => s with { Effects = [new(1, 289, 503, 0, 100, 0)] }).ToArray();
        LocalSpellRecord[] wrappers = [Spell(400, "Joined Ward", 40), Spell(401, "Joined Ward Rk. II", 40, 5), Spell(402, "Joined Ward Rk. III", 40, 10)];
        wrappers = wrappers.Select(s => s with { Duration = 1, Effects = [new(1, 470, 100, 10, 100, 0), new(2, 470, 100, 50, 100, 0)] }).ToArray();
        var triggered = Catalog([.. ranks, .. echoes, .. wrappers, Spell(503, "Echo Ward Recourse", 50)]);
        foreach (var wrapper in wrappers)
        {
            var parent = new SpellWatch(wrapper.Id, wrapper.Name, ClickKey: "Alt+NumpadEnter");
            var row = Row(parent, Seen(Effect(103), Effect(502)), triggered);
            check(row.Time == "2 active" && ReferenceEquals(row.Rule, parent) && row.Children is { } children
                && children.Select(c => c.Name).SequenceEqual(new[] { "Lasting Ward", "Echo Ward" })
                && children.All(c => c.Rule.ClickKey == parent.ClickKey),
                "triggered rank families preserve parent identity/key and child order: " + wrapper.Id);
            check(triggered.Resolve(wrapper.Id).Children[0].SpellIds.SequenceEqual(new[] { 100, 101, 102, 103 }),
                "triggered children accept both unranked and explicit rank-I aliases: " + wrapper.Id);
            check(Row(parent, Seen(Effect(103), Effect(503)), triggered).Children![1].Missing,
                "same-group cast-on-fade recourse cannot satisfy a triggered rank family: " + wrapper.Id);
        }
    }
}
