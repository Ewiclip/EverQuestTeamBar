using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private async Task TestSimplifiedEditor(Action<bool, string> check)
    {
        var original = selected!; bool originalDirty = dirty;
        var oldCapture = CaptureTarget; var oldDetected = detected;
        var npc1 = new SpellWatch(100, "NPC first", ClickKey: "Alt+6", Alternatives: [new(900, "Alternative")]);
        var player = new SpellWatch(200, "Player first", SpellTargetKind.Player, ClickKey: "Alt+7");
        var npc2 = new SpellWatch(300, "NPC second", ClickKey: "Ctrl+H");
        var player2 = new SpellWatch(400, "Player second", SpellTargetKind.Player);
        var fixture = original with { Spells = [npc1, player, npc2, player2] };
        SpellWatchChoice Choice(int id, string section = "NPC") => WatchList.Items.OfType<SpellWatchChoice>().Single(c => c.Rule.SpellId == id && c.Section == section);
        int[] Results() => SearchResults.Items.OfType<SpellSearchChoice>().Select(c => c.Id).Order().ToArray();
        try
        {
            Replace(fixture); LoadEditor();
            SelectRule(0); UpdateLayout();
            check(ClickModifierChoice.TranslatePoint(new(), SelectedSpellSettings).X < ClickKeyChoice.TranslatePoint(new(), SelectedSpellSettings).X,
                "spell editor matches the button editor with Hold Key(s) on the left and Click Key on the right");
            check(WatchList.Items.OfType<SpellWatchChoice>().Select(c => (c.Section, c.Rule.SpellId)).SequenceEqual(
                new[] { ("NPC", 100), ("NPC", 300), ("Player", 200), ("Player", 400) }), "watch list groups interleaved saved rules into NPC and Player sections");
            SpellTabs.SelectedItem = AddTab; WatchList.SelectedItem = Choice(200, "Player");
            check(SpellTabs.SelectedItem == ConfigureTab && SelectedSpellName.Text == player.Name && CurrentRuleIndex == 1,
                "selecting a grouped player rule immediately shows its settings using the saved rule index");
            ClickKeyChoice.SelectedIndex = Array.IndexOf(InputKeys.Names, "8") + 1;
            check(selected!.Spells[1].ClickKey == "Alt+8" && selected.Spells[2] == npc2, "editing a sorted row cannot change its neighbor's key");
            var before = selected.Spells.ToArray();
            check(MoveRule(Choice(300), Choice(100), false), "drag reorder can move an NPC spell above another NPC spell");
            check(selected.Spells.Select(s => s.SpellId).SequenceEqual(new[] { 300, 200, 100, 400 })
                && selected.Spells[1] == before[1] && selected.Spells[3] == before[3]
                && selected.Spells[2] == npc1, "section reorder preserves player positions, keys and alternatives");
            check(!MoveRule(Choice(100), Choice(200, "Player"), false), "cross-section drops cannot silently change target type");
            var stale = Choice(100); Replace(selected with { Spells = selected.Spells.Select(s => s.SpellId == 100 ? s with { ClickKey = "9" } : s).ToArray() }); RefreshRules();
            check(!MoveRule(stale, Choice(300), false), "a stale drag cannot move a rule edited after dragging began");
            SelectRule(0);
            RemoveSpell_Click(new C.Button { Tag = Choice(200, "Player") }, new(C.Button.ClickEvent));
            check(selected.Spells.Select(s => s.SpellId).SequenceEqual(new[] { 300, 100, 400 }) && CurrentRuleIndex == 0,
                "inline X removes its own unselected row and keeps the selected rule");
            Replace(fixture); LoadEditor();
            var shared = npc1 with { Target = SpellTargetKind.Either };
            Replace(selected! with { Spells = [shared, player] }); LoadEditor();
            check(WatchList.Items.OfType<SpellWatchChoice>().Count(c => c.Rule == shared) == 2, "legacy NPC-or-player rules appear in both sections without duplicated storage");
            WatchList.SelectedItem = Choice(100, "Player"); CasterChoice.SelectedIndex = 1;
            check(selected!.Spells[0].Caster == SpellCasterKind.Anyone && selected.Spells[1] == player,
                "editing either view of a shared rule preserves its single saved configuration");
            Replace(fixture); LoadEditor();
            SearchResults.ItemsSource = new[] { SearchChoice(100, "NPC first", 56) }; SearchResults.SelectedIndex = 0;
            AddTargetChoice.SelectedIndex = 1; AddSpell_Click(this, new());
            check(selected!.Spells.Count(s => s.SpellId == 100) == 2 && selected.Spells.Last().Target == SpellTargetKind.Player,
                "the same spell can be independently configured in both target sections");

            var (_, capture) = SpellOverlayTests.Fixture();
            capture = capture with { CharacterName = selected.Character, Buffs = capture.Buffs! with { CharacterName = selected.Character, Effects = [
                new(0, 100, "Shared slow", "Someoneelse", 50000, 56), new(1, 100, "Shared slow", selected.Character, 40000, 56),
                new(2, 101, "Other spell", "Someoneelse", 30000, 56), new(3, 102, "Unknown spell", null, 30000, 56),
                new(4, 103, "Unnamed caster", " ", 30000, 56), new(5, 104, "My spell", selected.Character.ToUpperInvariant(), 20000, 56)] } };
            SpellSearch.Text = ""; FilterMine.IsChecked = true; AcceptPickerCapture(capture);
            check(Results().SequenceEqual(new[] { 100, 104 }), "Mine includes only this character and filters before spell deduplication");
            FilterOthers.IsChecked = true;
            check(Results().SequenceEqual(new[] { 100, 101 }), "Others excludes my casts and unidentified casters");
            FilterAll.IsChecked = true;
            check(Results().SequenceEqual(new[] { 100, 101, 102, 103, 104 }), "All includes unknown casters without duplicate spell IDs");
            SpellSearch.Text = "slow"; check(Results().SequenceEqual(new[] { 100 }) && resultsFromTarget, "typing filters the captured target list without changing source");
            SpellSearch.Text = "102"; check(Results().SequenceEqual(new[] { 102 }), "target results also filter by exact spell ID");
            SpellSearch.Text = ""; FilterMine.IsChecked = true;
            var ownerBefore = selected;
            CharacterChoice.ItemsSource = new[] { selected.Character, "Different" }; CharacterChoice.SelectedItem = "Different";
            check(pickerCapture is null && Results().Length == 0, "changing the configured character clears another character's target results");
            Replace(ownerBefore); LoadEditor();
            detected = [new(123, 1, 1, 0, new(selected!.Character, 10, 130))];
            var release = new TaskCompletionSource<TargetCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
            CaptureTarget = (_, _) => release.Task.GetAwaiter().GetResult();
            var pending = ReadTargetForPicker();
            SpellSourceChoice.SelectedIndex = 0;
            release.SetResult(capture); await pending;
            check(!resultsFromTarget && pickerCapture is null, "late target reads cannot replace catalog results after changing source");
            resultsFromTarget = true; CaptureTarget = (_, _) => throw new IOException("Fixture read failure");
            await ReadTargetForPicker();
            check(pickerCapture is null && Results().Length == 0 && SearchHint.Text.Contains("Fixture read failure"),
                "failed target reads clear stale choices and show the failure");
            var playerTarget = capture.Target! with { RawType = 0, Type = "Player" };
            var playerCapture = capture with { Target = playerTarget, Buffs = capture.Buffs! with { Target = playerTarget } };
            AcceptPickerCapture(playerCapture);
            check(AddTargetChoice.SelectedIndex == 1, "reading a player target defaults new additions to the Player section");
            var state = SpellOverlayRules.Evaluate(fixture, playerCapture);
            check(state.Rows.Select(r => r.DisplayPriority).SequenceEqual(new int?[] {1, 2})
                && state.Rows.Select(r => r.Priority).SequenceEqual(new[] {2, 4}), "overlay display numbers are section-specific while click lookup retains saved indices");
            SpellTabs.SelectedItem = AddTab; double oldHeight = Height, oldWidth = Width;
            Height = MinHeight; Width = MinWidth;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout();
            check(SearchResults.ActualHeight >= 100 && AddScroll.ScrollableHeight > 0, "small-window picker keeps a usable list and scroll access to every action");
            SelectRule(1, "Player"); ShowConfigure();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout();
            check(ClickKeyChoice.IsVisible && SelectedSpellName.IsVisible && ConfigureScroll.VerticalOffset == 0,
                "selected spell name and key settings stay immediately visible at minimum window size");
            Height = oldHeight; Width = oldWidth;
        }
        finally
        {
            CaptureTarget = oldCapture; detected = oldDetected; resultsFromTarget = false; pickerCapture = null;
            changingSource = true; SpellSourceChoice.SelectedIndex = 0; changingSource = false;
            FilterMine.IsChecked = true; AddTargetChoice.SelectedIndex = 0;
            Replace(original); LoadEditor(); dirty = originalDirty;
        }
    }
}
