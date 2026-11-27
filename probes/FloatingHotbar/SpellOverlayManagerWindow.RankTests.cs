using System.Text.Json;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private void TestRankSelection(Action<bool, string> check)
    {
        var previous = selected;
        var previousSource = spellSource;
        var previousNames = spellNames;
        var previousCatalog = spellCatalog;
        var previousResults = SearchResults.ItemsSource;
        int previousResultIndex = SearchResults.SelectedIndex;
        bool previousDirty = dirty;
        const string fixtureId = "rank-editor-fixture";
        var records = new[]
        {
            new LocalSpellRecord(910001, "Fixture ward", 100, 5, 56, 9100, 1, true, []),
            new LocalSpellRecord(910002, "Fixture ward Rk. II", 100, 5, 56, 9100, 5, true, []),
            new LocalSpellRecord(910003, "Fixture ward Rk. III", 100, 5, 56, 9100, 10, true, []),
            new LocalSpellRecord(910004, "Fixture ward Rk. III", 100, 5, 56, 9200, 10, true, [])
        };
        var names = new SpellNames(records.ToDictionary(r => r.Id, r => r.Name), "rank-editor-fixture", null,
            records.ToDictionary(r => r.Id));
        var savedRule = new SpellWatch(910002, "My named ward", SpellTargetKind.Either, SpellCasterKind.Anyone,
            false, 56, false, ["Cleric"], "Ctrl+[");
        void Add(int id)
        {
            SearchResults.ItemsSource = new[] { SearchChoice(id, names.Names[id], 56) };
            SearchResults.SelectedIndex = 0;
            AddSpell_Click(this, new());
        }
        try
        {
            var fixture = new SpellOverlayDefinition(fixtureId, "Rank fixture", "Fixture", [savedRule]);
            definitions.Add(fixture); OverlayList.SelectedItem = fixture; SetCatalog(names); dirty = false;
            foreach (int id in new[] { 910001, 910003, 910002 })
            {
                WatchList.SelectedIndex = -1; Add(id);
                check(ReferenceEquals(selected, fixture) && selected!.Spells.Length == 1 && !dirty
                    && WatchList.SelectedIndex == 0 && StatusLabel.Text.Contains("already listed"),
                    "adding a watched rank selects its existing rule without changing saved settings: " + id);
            }
            check(TriggeredHint.Text.Contains("Any rank counts"), "ordinary spell help explains automatic rank matching");
            Add(910004);
            check(selected!.Spells.Select(r => r.SpellId).SequenceEqual(new[] { 910002, 910004 }),
                "same-name spells in different installed groups remain separate picker choices");

            var legacy = fixture with { Spells = [savedRule, new(910001, "Existing first-rank entry", ClickKey: "Alt+]")] };
            legacy = JsonSerializer.Deserialize<SpellOverlayDefinition>(JsonSerializer.Serialize(legacy, SpellOverlayStore.Json), SpellOverlayStore.Json)!;
            SpellOverlayStore.Validate(new(1, [legacy]));
            Replace(legacy); LoadEditor();
            SetCatalog(names with { Sha256 = "reloaded-rank-fixture" });
            OverlayName.Text = "Renamed legacy overlay";
            check(selected!.Spells.SequenceEqual(legacy.Spells) && WatchList.Items.Count == 3,
                "loading, refreshing the catalog and renaming preserve existing saved rank duplicates and their settings");

            Replace(fixture); LoadEditor();
            SetCatalog(names with { Records = null }); Add(910001);
            check(selected!.Spells.Select(r => r.SpellId).SequenceEqual(new[] { 910002, 910001 }),
                "picker does not infer rank equivalence from display names when installed metadata is unavailable");
        }
        finally
        {
            spellSource = previousSource; spellNames = previousNames; spellCatalog = previousCatalog;
            loading = true;
            foreach (var fixture in definitions.Where(d => d.Id == fixtureId).ToArray()) definitions.Remove(fixture);
            loading = false; OverlayList.SelectedItem = previous; selected = previous; LoadEditor();
            SearchResults.ItemsSource = previousResults; SearchResults.SelectedIndex = previousResultIndex;
            dirty = previousDirty;
        }
    }
}
