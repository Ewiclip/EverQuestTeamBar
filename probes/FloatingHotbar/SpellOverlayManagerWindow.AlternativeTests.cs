using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private void TestAlternativesEditor(Action<bool, string> check)
    {
        var originalDefinitions = definitions.ToArray();
        var originalSelected = selected;
        var originalLibrary = savedLibrary;
        var originalSource = spellSource;
        var originalNames = spellNames;
        var originalCatalog = spellCatalog;
        var originalResults = SearchResults.ItemsSource;
        int originalResultIndex = SearchResults.SelectedIndex;
        bool originalDirty = dirty;
        LocalSpellRecord[] records = [
            new(930001, "Fixture slow", 100, 5, 56, 9300, 1, true, []),
            new(930002, "Fixture slow Rk. II", 100, 5, 56, 9300, 5, true, []),
            new(930003, "Fixture bite", 100, 5, 104, 9400, 1, true, []),
            new(930004, "Fixture bite Rk. II", 100, 5, 104, 9400, 5, true, []),
            new(930005, "Fixture cripple", 100, 5, 56, 9500, 1, true, [])];
        var names = new SpellNames(records.ToDictionary(r => r.Id, r => r.Name), "alternatives-editor-fixture", null,
            records.ToDictionary(r => r.Id));
        var primary = new SpellWatch(930001, "My slow", SpellTargetKind.Either, SpellCasterKind.Anyone,
            false, 56, true, ["Shaman"], "Alt+6");
        var other = new SpellWatch(930003, "Independent bite", ClickKey: "Ctrl+F1");
        var fixture = new SpellOverlayDefinition("alternatives-editor-fixture", "Alternatives fixture", "Fixture", [primary, other],
            X: .17, Y: .63, Width: 517, Opacity: .71, Locked: false, Scale: 1.37);
        SpellSearchChoice Pick(int id)
        {
            var choice = SearchChoice(id, names.Names.GetValueOrDefault(id) ?? "Additional spell " + id, 56);
            SearchResults.ItemsSource = new[] { choice }; SearchResults.SelectedIndex = 0;
            // WPF can retain the previous, value-equal record when ItemsSource changes.
            // Preserve the actual selection across Add, not a new fixture instance.
            return (SpellSearchChoice)SearchResults.SelectedItem;
        }
        void Add(int id) { Pick(id); AddAlternative_Click(this, new()); }
        void Remove(int id) => RemoveAlternative_Click(new C.Button { Tag = id }, new());
        try
        {
            definitions.Add(fixture); OverlayList.SelectedItem = fixture; SetCatalog(names); dirty = false;
            Pick(930003); WatchList.SelectedIndex = -1;
            check(!AddAlternativeButton.IsEnabled && !AlternativeEditor.IsEnabled,
                "alternatives require a selected watched rule");
            AddAlternative_Click(this, new());
            check(ReferenceEquals(selected, fixture) && !dirty, "adding without a watched rule cannot alter the draft");
            WatchList.SelectedIndex = 0;
            var picked = Pick(930003); AddAlternative_Click(this, new());
            check(selected!.Spells[0].Alternatives is [{ SpellId: 930003, Name: "Fixture bite", IconId: 56 }],
                "adding an alternative retains the explicitly selected spell name, ID and icon");
            check((selected.Spells[0] with { Alternatives = null }) == primary,
                "adding an alternative preserves primary rule settings");
            check(selected.Spells[1] == other, "adding an alternative preserves other watched rows");
            check(dirty && WatchList.SelectedIndex == 0, "adding an alternative marks its selected watched rule dirty");
            check(ReferenceEquals(SearchResults.SelectedItem, picked), "adding an alternative retains the picker selection");
            check(AlternativeTarget.Text.Contains(primary.Name), "adding an alternative retains the explicit watched-rule destination");
            check(AlternativeList.Items.OfType<SpellSearchChoice>().Single() is { Image: not null }
                && NoAlternatives.Visibility == W.Visibility.Collapsed,
                "alternative names retain their installed icons and replace the empty hint");
            var withOneAlternative = selected;
            foreach (int duplicate in new[] { 930001, 930002, 930003, 930004 })
            {
                dirty = false; Add(duplicate);
                check(ReferenceEquals(selected, withOneAlternative) && !dirty && StatusLabel.Text.Contains("already counts"),
                    "alternatives reject exact or verified-rank duplicates of the primary and alternatives: " + duplicate);
            }
            Add(930005); var approved = selected!;
            AddSpell_Click(this, new());
            check(selected!.Spells.Length == 3 && selected.Spells[2].SpellId == 930005
                && selected.Spells[0].Alternatives!.Length == 2,
                "a configured alternative may also be added as an independent watched row");
            WatchList.SelectedIndex = 0;
            Remove(930003);
            check(selected!.Spells[0].Alternatives is [{ SpellId: 930005 }] && selected.Spells[0].ClickKey == primary.ClickKey
                && selected.Spells[1] == other && dirty,
                "removing one alternative preserves the remaining choice, primary click key and independent row");
            Remove(930005);
            check(selected!.Spells[0].Alternatives is null && AlternativeList.Items.Count == 0
                && NoAlternatives.Visibility == W.Visibility.Visible,
                "removing the final alternative restores the ordinary watched rule");

            Replace(approved); LoadEditor();
            SetCatalog(names with { Sha256 = "refreshed-alternatives-fixture" });
            OverlayName.Text = "Renamed alternatives";
            CasterChoice.SelectedIndex = 0; ClickKeyChoice.SelectedIndex = Array.IndexOf(InputKeys.Names, "PageDown") + 1;
            Move(1); Move(-1);
            check(selected!.Spells[0].Alternatives!.SequenceEqual(approved.Spells[0].Alternatives!)
                && selected.Spells[0].Caster == SpellCasterKind.Mine && selected.Spells[0].ClickKey == "Alt+PageDown"
                && selected.X == fixture.X && selected.Y == fixture.Y && selected.Width == fixture.Width
                && selected.Opacity == fixture.Opacity && selected.Scale == fixture.Scale && selected.Locked == fixture.Locked,
                "catalog refresh, renaming, caster/key edits and reordering preserve alternatives and saved layout");

            string fixturePath = Path.Combine(Path.GetTempPath(), "team-bar-alternatives-editor-" + Guid.NewGuid().ToString("N"), "spell-overlays.json");
            byte[] bytes = SpellOverlayStore.Save(fixturePath, new(1, [selected!]), null);
            var loaded = SpellOverlayStore.Load(fixturePath).Library;
            check(JsonSerializer.Serialize(loaded.Overlays[0], SpellOverlayStore.Json) == JsonSerializer.Serialize(selected, SpellOverlayStore.Json),
                "the editor's alternatives persist with primary settings and layout in an isolated profile");
            savedLibrary = loaded; Remove(930003);
            check(dirty, "removing a saved alternative marks only the editor draft dirty");
            RestoreSavedDraft();
            check(!dirty && selected!.Spells[0].Alternatives!.Length == 2
                && File.ReadAllBytes(fixturePath).SequenceEqual(bytes),
                "discard restores saved alternatives without writing the profile");

            Replace(fixture); LoadEditor();
            for (int i = 0; i < 12; i++) Add(930100 + i);
            var full = selected; dirty = false; Add(930112);
            check(ReferenceEquals(selected, full) && selected!.Spells[0].Alternatives!.Length == 12 && !dirty
                && StatusLabel.Text.Contains("12 alternatives"), "the editor bounds alternative lists without losing the current draft");
            WatchList.SelectedIndex = 1;
            check(AlternativeList.Items.Count == 0 && AlternativeTarget.Text.Contains(other.Name),
                "switching watched rows updates the alternative list and explicit destination");
            RemoveSpell_Click(this, new()); RemoveSpell_Click(this, new());
            check(!AddAlternativeButton.IsEnabled && AlternativeList.Items.Count == 0,
                "deleting all watched rows clears and disables alternative controls");
        }
        finally
        {
            spellSource = originalSource; spellNames = originalNames; spellCatalog = originalCatalog; savedLibrary = originalLibrary;
            loading = true; definitions.Clear();
            foreach (var definition in originalDefinitions) definitions.Add(definition);
            selected = originalSelected; OverlayList.SelectedItem = originalSelected;
            loading = false; LoadEditor();
            SearchResults.ItemsSource = originalResults; SearchResults.SelectedIndex = originalResultIndex;
            dirty = originalDirty;
        }
    }
}
