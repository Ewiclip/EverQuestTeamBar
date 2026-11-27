using System.Runtime.InteropServices;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

internal static class SpellOverlayTests
{
    internal static (SpellOverlayDefinition Definition, TargetCapture Capture) Fixture()
    {
        var target = new ObservedTarget(12, "Sample NPC · preview", 130, 1, "NPC"); var zone = new ObservedZone(1, "fixture", "Sample zone");
        var at = DateTimeOffset.UtcNow;
        var effects = new TargetBuffCapture(1, Guid.NewGuid(), at, "Live", Identity.BuildHash, "fixture", "target-buffs", 1, 1,
            "Preview character", "Shaman", zone, target, "observed-target-window",
            [new(0, 100, "Sample slow", "Preview character", 94000, 56), new(1, 103, "Sample disease", "Other", 60000, 131),
             new(2, 104, "Sample debuff", null, 40000, 128)], null, null, "Synthetic UI sample, not live spell identities or casts");
        var capture = new TargetCapture(1, Guid.NewGuid(), at, "Live", Identity.BuildHash, "fixture", "selected-target", 1, 1,
            effects.CharacterName, effects.CharacterClass, zone, target, effects.Completeness, effects);
        var definition = new SpellOverlayDefinition("sample", "Debuff priorities", effects.CharacterName,
            [new(100, "Sample slow", IconId: 56), new(101, "Sample resistance debuff", IconId: 104),
             new(102, "Sample damage over time", IconId: 138), new(103, "Sample disease", Caster: SpellCasterKind.Anyone, IconId: 131)]);
        return (definition, capture);
    }
    internal static void Run(string output)
    {
        using var access = GameDataAccess.FixtureScope(true);
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        var (definition, capture) = Fixture();
        var state = SpellOverlayRules.Evaluate(definition, capture);
        Check(state.Rows.Select(r => r.Priority).SequenceEqual(new[] { 1, 2, 3, 4 }), "active and missing spells retain configured order");
        Check(state.Rows[0].Time == "1:34" && state.Rows[3].Time == "1:00", "own and anyone casts display game remaining time");
        var changedEffects = capture with { Buffs = capture.Buffs! with { Effects = [new(0, 101, "Sample resistance debuff", capture.CharacterName, 45000)] } };
        var changedState = SpellOverlayRules.Evaluate(definition, changedEffects);
        Check(changedState.Rows.Select(r => r.Rule.SpellId).SequenceEqual(state.Rows.Select(r => r.Rule.SpellId))
            && changedState.Rows[0].Missing && !changedState.Rows[1].Missing, "spell status changes preserve row positions and missing indicators");
        var hideAbsent = definition with { Spells = definition.Spells.Select(s => s with { ShowMissing = false }).ToArray() };
        Check(SpellOverlayRules.Evaluate(hideAbsent, capture).Rows.Select(r => r.Priority).SequenceEqual(new[] { 1, 4 }),
            "hiding missing spells preserves the relative configured order of visible rows");
        var mine = definition with { Spells = [new(103, "Other's spell"), new(104, "Unknown caster")] };
        state = SpellOverlayRules.Evaluate(mine, capture);
        Check(state.Rows[0].Missing && state.Rows[1].State == "Caster unknown", "other casts cannot fulfill mine; unknown caster cannot claim missing");
        state = SpellOverlayRules.Evaluate(mine with { Spells = [new(104, "Any caster", Caster: SpellCasterKind.Anyone)] }, capture);
        Check(state.Rows.Single().Time == "0:40", "anyone setting accepts an observed spell with unknown caster");
        Check(SpellOverlayRules.Evaluate(definition with { Spells = [new(999, "Absent", ShowMissing: false)] }, capture).Rows.Length == 0,
            "show-if-missing off hides absent spell");
        foreach (var unavailable in new[] { capture with { Target = null }, capture with { Buffs = null }, capture with { BuffsError = "Read failed" },
            capture with { CharacterName = "Another" }, capture with { Buffs = capture.Buffs! with { Status = "waiting-for-target-data" } },
            capture with { Buffs = capture.Buffs! with { Target = capture.Target! with { SpawnId = 99 } } } })
            Check(SpellOverlayRules.Evaluate(definition, unavailable).Rows.Length == 0, "unavailable/mismatched observation cannot manufacture missing rows: " + checks.Count);
        Check(SpellOverlayRules.Evaluate(definition, capture, "Read failed").Rows.Length == 0, "read error removes previous state");
        var player = capture.Target! with { RawType = 0, Type = "Player" };
        var playerCapture = capture with { Target = player, Buffs = capture.Buffs! with { Target = player } };
        Check(SpellOverlayRules.Evaluate(definition, playerCapture).Rows.Length == 0, "NPC rules do not leak into player target");
        Check(SpellOverlayRules.Evaluate(definition with { Spells = [new(101, "Buff", SpellTargetKind.Player)] }, playerCapture).Rows.Single().Missing,
            "player-target rules can report missing buffs");
        var corpse = capture.Target! with { RawType = 2, Type = "Corpse" };
        Check(SpellOverlayRules.Evaluate(definition with { Spells = [new(101, "Either", SpellTargetKind.Either)] },
            capture with { Target = corpse, Buffs = capture.Buffs! with { Target = corpse } }).Rows.Length == 0, "corpses never request recasting");
        Check(SpellOverlayRules.Evaluate(definition, capture with { Buffs = capture.Buffs! with { Effects = [new(0, 100, "Sample", "Preview character", -1)] } }).Rows.Single(r => r.Rule.SpellId == 100).Time == "No timer", "negative duration is not expired");
        string store = Path.GetFullPath(output) + ".library.json";
        var library = new SpellOverlayLibrary(1, [definition with { Screens = [new("display-one", "Monitor", new(.25, .5))] }]);
        var saved = SpellOverlayStore.Save(store, library, null);
        Check(SpellOverlayStore.Load(store).Library.Overlays[0].Spells.SequenceEqual(definition.Spells), "saved spell order and per-rule settings round trip");
        Check(SpellOverlayStore.Load(store).Library.Overlays[0].Screens![0].Position.X == .25, "per-monitor placement round trips");
        bool conflict = false; try { SpellOverlayStore.Save(store, library, []); } catch (IOException) { conflict = true; }
        Check(conflict && File.ReadAllBytes(store).SequenceEqual(saved), "stale editor cannot overwrite saved configuration");
        bool invalid = false; try { SpellOverlayStore.Save(store, library with { Overlays = [definition with { X = double.NaN }] }, saved); } catch (InvalidDataException) { invalid = true; }
        Check(invalid && File.ReadAllBytes(store).SequenceEqual(saved), "invalid configuration cannot replace saved settings");
        TestTriggeredAndPersonas(Check);
        SpellOverlayRankTests.Run(Check);
        SpellOverlayNumberedBleedTests.Run(Check);
        SpellOverlayAlternativeTests.Run(Check, output);
        SpellOverlayLayoutTests.Run(Check);
        SpellOverlayMigrationTests.Run(Check, output);
        SpellDockTests.Run(Check, output);
        SpellOverlayButtonEditingTests.Run(Check, output);
        OverlayButtonEditorTests.Run(Check, output);
        SpellOverlayInputPipeTests.Run(Check);
        SpellOverlayClickTests.Run(Check, output);
        SpellOverlayManagerWindow.TestEditor(checks, output + ".button-editing.png");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false, processMemoryWritten = false });
    }
    internal static (SpellOverlayDefinition Definition, TargetCapture Capture, SpellOverlayCatalog Catalog) GroupFixture()
    {
        var (_, capture) = Fixture();
        var definition = new SpellOverlayDefinition("groups", "Debuff priorities", capture.CharacterName,
            [new(70420, "Chaotic Bloodcurse Rk. III", IconId: 42, PersonaClasses: ["Shaman"]),
             new(72115, "Breath of Pustim", IconId: 41)], Width: 440);
        capture = capture with { Buffs = capture.Buffs! with { Effects =
            [new(0, 67174, "Nectar of Obscurity Rk. II", capture.CharacterName, 94000, 42),
             new(1, 72115, "Breath of Pustim", capture.CharacterName, 132000, 41)] } };
        return (definition, capture, new(SpellNameCatalog.Read()));
    }
    private static void TestTriggeredAndPersonas(Action<bool, string> check)
    {
        var (definition, capture, catalog) = GroupFixture();
        var plan = catalog.Resolve(70420);
        check(plan.Error is null && plan.Children.Select(c => c.Name).SequenceEqual(new[] { "Caustic Blood", "Nectar of Obscurity" }),
            "installed spell data resolves both required Bloodcurse effects without bonus triggers");
        check(plan.Children[0].SpellIds.SequenceEqual(new[] { 67165, 67166, 67167 })
            && plan.Children[1].SpellIds.SequenceEqual(new[] { 67173, 67174, 67175 }), "rank families exclude same-group cast-on-fade spells");
        SpellOverlayState Evaluate(SpellOverlayDefinition d, TargetCapture c) => SpellOverlayRules.Evaluate(d, c, catalog: catalog);
        var state = Evaluate(definition, capture); var parent = state.Rows[0];
        check(parent.Name == "Chaotic Bloodcurse Rk. III" && parent.Children is { Length: 2 } && parent.Time == "1 missing",
            "wrapper retains its name with a partial-missing summary");
        check(parent.Children![0].Name == "Caustic Blood" && parent.Children[0].Missing
            && parent.Children[1].Time == "1:34", "independent missing and rank-II countdown beneath one parent");
        var firstChildOnly = capture with { Buffs = capture.Buffs! with { Effects = [new(0, 67167, "Caustic Blood Rk. III", capture.CharacterName, 55000, 42)] } };
        var changedChildren = Evaluate(definition, firstChildOnly).Rows[0].Children!;
        check(changedChildren.Select(c => c.Name).SequenceEqual(plan.Children.Select(c => c.Name))
            && changedChildren[0].Time == "0:55" && changedChildren[1].Missing,
            "grouped effects retain defined trigger order when their active and missing states switch");
        foreach (int id in new[] { 67173, 67174, 67175 })
            check(!Evaluate(definition, capture with { Buffs = capture.Buffs! with { Effects = [new(0, id, null, capture.CharacterName, 90000)] } })
                .Rows[0].Children!.Single(r => r.Name == "Nectar of Obscurity").Missing, "observed child rank is accepted: " + id);
        state = Evaluate(definition, capture with { Buffs = capture.Buffs! with { Effects = [new(0, 67177, null, capture.CharacterName, 20000)] } });
        check(state.Rows[0].Children!.All(c => c.Missing), "follow-up Vengeance cannot satisfy Nectar");
        var both = capture with { Buffs = capture.Buffs! with { Effects = [.. capture.Buffs!.Effects, new(2, 67167, null, capture.CharacterName, 55000, 42)] } };
        check(Evaluate(definition, both).Rows[0].Time == "2 active", "all required child effects present");
        var other = capture with { Buffs = capture.Buffs! with { Effects = [capture.Buffs!.Effects[0] with { CasterName = "Other" }] } };
        check(Evaluate(definition, other).Rows[0].Children!.All(c => c.Missing), "child inherits my-casts filter");
        var anyone = definition with { Spells = [definition.Spells[0] with { Caster = SpellCasterKind.Anyone }] };
        check(Evaluate(anyone, other).Rows.Single().Children!.Any(c => c.State == "Active"), "child inherits anyone-casts filter");
        var unknown = capture with { Buffs = capture.Buffs! with { Effects = [capture.Buffs!.Effects[0] with { CasterName = null }] } };
        check(Evaluate(definition, unknown).Rows[0].Children!.Any(c => c.State == "Caster unknown"), "unknown child caster is not called missing");
        var hidden = definition with { Spells = [definition.Spells[0] with { ShowMissing = false }] };
        check(Evaluate(hidden, capture).Rows.Single().Children is { Length: 1 }, "show-if-missing off hides only absent children");
        check(Evaluate(hidden, capture with { Buffs = capture.Buffs! with { Effects = [] } }).Rows.Length == 0, "empty child group disappears when missing is hidden");
        var exact = definition with { Spells = [definition.Spells[0] with { ExpandTriggered = false }] };
        check(Evaluate(exact, capture).Rows.Single() is { Missing: true, Children: null }, "automatic expansion can be disabled per rule");
        var ordered = definition with { Spells = [definition.Spells[1], definition.Spells[0]] };
        var orderedState = Evaluate(ordered, capture);
        check(orderedState.Rows.Select(r => r.Rule.SpellId).SequenceEqual(new[] { 72115, 70420 })
            && orderedState.Rows[1].Missing && orderedState.Rows[1].Children is { Length: 2 },
            "partially missing group stays at its configured position with its children together");
        check(catalog.Resolve(72149).Children.Length == 0, "ordinary Nectar does not expand cast-on-fade effects");
        var missingCatalog = new SpellOverlayCatalog(new(new Dictionary<int, string>(), null, "Unavailable"));
        check(SpellOverlayRules.Evaluate(definition, capture, catalog: missingCatalog).Rows.All(r => !r.Missing), "unavailable spell metadata cannot manufacture missing alerts");
        var data = SpellNameCatalog.Read(); var records = data.Records!.ToDictionary(p => p.Key, p => p.Value);
        records[999001] = records[67165] with { Id = 999001, Name = "Ambiguous sibling" };
        check(new SpellOverlayCatalog(data with { Records = records }).Resolve(70420).Error is not null, "ambiguous rank group fails closed");
        records = data.Records!.ToDictionary(p => p.Key, p => p.Value);
        records[67165] = records[67165] with { Effects = [new(1, 470, 100, 125100520, 100, 0)] };
        check(new SpellOverlayCatalog(data with { Records = records }).Resolve(70420).Error is not null, "nested or cyclic triggers are not recursively guessed");
        check(LocalSpellRecord.Parse("70420^Name") is null, "unsupported spell layout is rejected");
        var multiClass = definition with { Spells = [definition.Spells[0] with { PersonaClasses = ["Shaman", "Necromancer"] }] };
        TargetCapture Persona(string name) => capture with { CharacterClass = name, Buffs = capture.Buffs! with { CharacterClass = name } };
        check(Evaluate(multiClass, Persona("Necromancer")).Rows.Single().Children is { Length: 2 }, "multiple persona classes enable the whole group");
        check(Evaluate(multiClass, Persona("Cleric")).Rows.Length == 0, "nonmatching persona hides parent and children");
        check(Evaluate(multiClass, Persona("")).Rows.Length == 0, "unknown class cannot activate a persona-specific rule");
        check(Evaluate(definition, Persona("Cleric")).Rows.Single().Rule.SpellId == 72115, "all-classes rules remain visible after persona switch");
        check(Evaluate(multiClass, capture with { CharacterClass = "Necromancer" }).Rows.Length == 0, "stale class observation cannot leak across persona switch");
        var json = System.Text.Json.JsonSerializer.Serialize(new SpellOverlayLibrary(1, [multiClass]), SpellOverlayStore.Json);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SpellOverlayLibrary>(json, SpellOverlayStore.Json)!;
        check(restored.Overlays[0].Spells[0].PersonaClasses!.SequenceEqual(new[] { "Shaman", "Necromancer" }), "persona selections persist in configuration");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SpellWatch>("{\"spellId\":70420,\"name\":\"Chaotic Bloodcurse\"}", SpellOverlayStore.Json)!;
        check(legacy.ExpandTriggered && legacy.PersonaClasses is null, "existing configurations enable auto-expansion and retain all-classes behavior");
        foreach (var classes in new[] { Array.Empty<string>(), new[] { "Shaman", "shaman" }, new[] { "Unknown" } })
        {
            bool rejected = false;
            try { SpellOverlayStore.Validate(new(1, [definition with { Spells = [definition.Spells[0] with { PersonaClasses = classes }] }])); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid persona selection rejected: " + string.Join(",", classes));
        }
    }
}

public partial class SpellOverlayManagerWindow
{
    internal static void TestEditor(List<string> checks, string? buttonPreviewPath = null)
    {
        var app = new W.Application(); var window = new SpellOverlayManagerWindow(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), true);
        Exception? failure = null; bool exercised = false;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        window.ContentRendered += async (_, _) =>
        {
            if (exercised) return; exercised = true;
            try
            {
                window.detected = [new(1, 1, 0, 0, new("Fixture", 10, 130))]; window.AddOverlay();
                var choices = new[] { window.SearchChoice(100, "First", 56), window.SearchChoice(200, "Second", 104) };
                window.SearchResults.ItemsSource = choices;
                window.SearchResults.SelectedIndex = 0; window.AddSpell_Click(window, new());
                window.SearchResults.SelectedIndex = 1; window.AddSpell_Click(window, new());
                window.CasterChoice.SelectedIndex = 1; window.TargetChoice.SelectedIndex = 1; window.ShowMissing.IsChecked = false;
                Check(window.selected!.Spells[1] is { Caster: SpellCasterKind.Anyone, Target: SpellTargetKind.Player, ShowMissing: false }, "editor changes are per spell");
                window.TargetChoice.SelectedIndex = 0;
                window.Move(-1);
                Check(window.selected.Spells[0].SpellId == 200 && window.WatchList.SelectedIndex == 0, "higher priority moves the whole configured rule");
                var layout = window.selected with { X = .17, Y = .63, Width = 517, Opacity = .71, Locked = false, Scale = 1.37,
                    Screens = [new("first-monitor", "First monitor", new(.21, .32)), new("second-monitor", "Second monitor", new(.76, .54))] };
                window.Replace(layout); window.LoadEditor();
                window.OverlayName.Text = "Buffs and debuffs";
                Check(window.selected.Name == "Buffs and debuffs" && window.selected.Spells[0].Caster == SpellCasterKind.Anyone, "renaming preserves spell rules");
                window.OverlayEnabled.IsChecked = false; window.OverlayEnabled.IsChecked = true;
                Check(window.selected.X == layout.X && window.selected.Y == layout.Y && window.selected.Width == layout.Width
                    && window.selected.Opacity == layout.Opacity && window.selected.Locked == layout.Locked && window.selected.Scale == layout.Scale
                    && window.selected.Screens!.SequenceEqual(layout.Screens!),
                    "renaming and enabling preserve saved size, opacity, lock, fallback position and monitor placements");
                Check(window.selected.PersonaClass == "Shaman", "new bar defaults to its character's detected class");
                window.ClassChoice.SelectedItem = "Berserker";
                window.ExpandTriggered.IsChecked = false;
                Check(window.selected.PersonaClass == "Berserker" && window.selected.Name == "Buffs and debuffs"
                    && window.selected.Spells.All(s => s.PersonaClasses is null) && !window.selected.Spells[0].ExpandTriggered,
                    "class belongs to the whole bar while spell settings and custom names are preserved");
                window.ClassChoice.SelectedItem = "Shaman";
                window.ClickKeyChoice.SelectedIndex = 5;
                Check(window.selected.Spells[0].ClickKey == "5" && window.selected.Spells[1].ClickKey is null,
                    "editor click mapping changes only the selected spell");
                window.ClickKeyChoice.SelectedIndex = 6; window.ClickModifierChoice.Value = 4;
                Check(window.selected.Spells[0].ClickKey == "Alt+6", "editor combines Alt and the chosen number-row key");
                window.WatchList.SelectedIndex = 1;
                Check(window.ClickKeyChoice.SelectedIndex == 0 && window.ClickModifierChoice.Value == 0 && !window.ClickModifierChoice.IsEnabled,
                    "selecting a display-only spell resets and disables the modifier picker");
                window.WatchList.SelectedIndex = 0;
                Check(window.ClickModifierChoice.Value == 4 && window.ClickKeyChoice.SelectedIndex == 6,
                    "returning to a mapped spell restores its full chord");
                foreach (string key in new[] { "[", "]", "A", "F12", "NumpadAdd", "Up", "Down", "Left", "Right", "Home", "End",
                    "PageUp", "PageDown", "Insert", "Delete", "NumpadEnter", "NumpadDivide" })
                {
                    window.ClickKeyChoice.SelectedIndex = Array.IndexOf(InputKeys.Names, key) + 1;
                    Check(window.selected.Spells[0].ClickKey == "Alt+" + key && window.selected.Spells[1].ClickKey is null,
                        "spell selector preserves the modifier and changes only the selected spell: " + key);
                    window.WatchList.SelectedIndex = 1; window.WatchList.SelectedIndex = 0;
                    Check(window.ClickKeyChoice.SelectedIndex == Array.IndexOf(InputKeys.Names, key) + 1
                        && window.ClickModifierChoice.Value == 4,
                        "spell selector restores the expanded key and modifier after changing selection: " + key);
                }
                window.ClickKeyChoice.SelectedIndex = 0;
                Check(window.selected.Spells[0].ClickKey is null, "editor can restore display-only mode");
                Check(window.FindName("SpellPauseButton") is null && window.FindName("ConnectClicksButton") is null,
                    "spell mappings need no pause, resume, or manual connection controls");
                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    window.SpellTabs.SelectedItem = window.AddTab;
                    ThemePalette.Apply(window.Resources, theme);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); window.UpdateLayout();
                    Check(window.WatchList.Items.Count == 2 && window.CharacterChoice.SelectedItem as string == "Fixture",
                        theme + ": compiled editor retains character and priority list");
                    Check(window.SearchResults.ActualHeight >= 150, theme + ": spell picker retains a usable results viewport: " + window.SearchResults.ActualHeight);
                    Check(choices[0].Image is not null && OverlayImages(window.SearchResults)
                        .Any(image => ReferenceEquals(image.Source, choices[0].Image) && image.ActualWidth > 0),
                        theme + ": spell picker renders installed artwork beside its selectable name");
                    Check(OverlayImages(window.WatchList).Any(image => image.Source is not null && image.ActualWidth > 0),
                        theme + ": watched spells retain their installed artwork after rule edits");
                }
                window.TestRankSelection(Check);
                window.TestAlternativesEditor(Check);
                await window.TestSimplifiedEditor(Check);
                window.TestBarSettings(Check);
                var (definition, capture) = SpellOverlayTests.Fixture();
                var overlay = new SpellOverlayWindow(FloatingThemes.Presets[1]);
                try
                {
                    overlay.Render(definition, SpellOverlayRules.Evaluate(definition, capture));
                    nint foreground = Native.GetForegroundWindow(); overlay.Show();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                    nint handle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                    Check((OverlayStyle(handle, -20).ToInt64() & 0x08000020) == 0x08000020 && !overlay.IsHitTestVisible,
                        "overlay window is nonactivating and mouse-transparent");
                    Check(Native.GetForegroundWindow() == foreground, "showing the display overlay preserves foreground");
                    overlay.TestSharedAppearance(Check);
                    string[] texts = OverlayTexts(overlay).ToArray();
                    Check(texts.Contains("Missing") && texts.Contains("1:34") && texts.Contains("2. Sample resistance debuff"), "real overlay renders priority, missing label and countdown");
                    string[] rowLabels = definition.Spells.Select((spell, index) => $"{index + 1}. {spell.Name}").ToArray();
                    Check(texts.Where(rowLabels.Contains).SequenceEqual(rowLabels), "rendered overlay rows follow configured spell order");
                    overlay.Render(definition, SpellOverlayRules.Evaluate(definition, capture with { Buffs = capture.Buffs! with { Effects = [capture.Buffs!.Effects[0] with { RemainingMilliseconds = 93000 }] } }));
                    Check(OverlayTexts(overlay).Contains("1:33"), "overlay receives updated countdown");
                    overlay.Render(definition, SpellOverlayRules.Evaluate(definition, null, "Reading unavailable"));
                    Check(!OverlayTexts(overlay).Contains("Missing"), "failed reading clears red missing alerts");
                    var (grouped, partial, catalog) = SpellOverlayTests.GroupFixture();
                    window.SetCatalog(SpellNameCatalog.Read());
                    window.definitions.Add(grouped); window.OverlayList.SelectedItem = grouped;
                    Check(window.TriggeredHint.Text.Contains("Caustic Blood") && window.TriggeredHint.Text.Contains("Nectar of Obscurity"),
                        "editor explains discovered effects");
                    overlay.Render(grouped, SpellOverlayRules.Evaluate(grouped, partial, catalog: catalog));
                    texts = OverlayTexts(overlay).ToArray();
                    Check(texts.Contains("1. Chaotic Bloodcurse Rk. III") && texts.Contains("Caustic Blood") && texts.Contains("Nectar of Obscurity")
                        && texts.Contains("1 missing") && texts.Contains("1:34"), "grouped overlay renders parent and independent child states");
                    partial = partial with { Buffs = partial.Buffs! with { Effects = [partial.Buffs!.Effects[0] with { RemainingMilliseconds = 93000 }] } };
                    overlay.Render(grouped, SpellOverlayRules.Evaluate(grouped, partial, catalog: catalog));
                    Check(OverlayTexts(overlay).Contains("1:33"), "child countdown updates without key collisions");
                    partial = partial with { Buffs = partial.Buffs! with { Effects = [.. partial.Buffs!.Effects, new(2, 67165, null, partial.CharacterName, 54000, 42)] } };
                    overlay.Render(grouped, SpellOverlayRules.Evaluate(grouped, partial, catalog: catalog));
                    Check(OverlayTexts(overlay).Contains("0:54") && OverlayTexts(overlay).Contains("2 active"), "two active children retain separate timers after status changes");
                    await TestSteadyOverlay(overlay, grouped, partial, catalog, Check);
                    await TestEditableOverlay(grouped, partial, catalog, Check);
                    TestAutoRelock(grouped, partial, catalog, Check);
                    await SpellOverlayWindow.TestSpellRows(Check);
                    await SpellOverlayWindow.TestCasterAndAlternativeRows(Check);
                    await SpellOverlayWindow.TestDockedControls(Check, buttonPreviewPath);
                    await SpellOverlayWindow.TestFreePlacement(Check);
                    window.TestAliasAndDockEditor(Check);
                    window.TestLayoutSave(grouped, Check);
                    await TestManagerLifetime(grouped, partial, catalog, Check);
                }
                finally { overlay.Close(); }
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        };
        app.Run(window); if (failure is not null) throw new InvalidOperationException("Spell overlay UI check failed", failure);
    }
    private static IEnumerable<string> OverlayTexts(W.DependencyObject root)
        => OverlayLabels(root).Select(text => text.Text);
    private static IEnumerable<C.TextBlock> OverlayLabels(W.DependencyObject root)
    {
        if (root is C.TextBlock text) yield return text;
        for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in OverlayLabels(M.VisualTreeHelper.GetChild(root, i))) yield return value;
    }
    private static IEnumerable<C.Image> OverlayImages(W.DependencyObject root)
    {
        if (root is C.Image image) yield return image;
        for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in OverlayImages(M.VisualTreeHelper.GetChild(root, i))) yield return value;
    }
    private static async Task TestSteadyOverlay(SpellOverlayWindow overlay, SpellOverlayDefinition definition,
        TargetCapture capture, SpellOverlayCatalog catalog, Action<bool, string> check)
    {
        var source = (System.Windows.Interop.HwndSource)W.PresentationSource.FromVisual(overlay);
        int positionMessages = 0;
        source.AddHook((nint hwnd, int message, nint w, nint l, ref bool handled) =>
        { if (message == 0x46) positionMessages++; return 0; });
        var area = System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea;
        var frame = new GameFrame("steady-fixture", "Fixture", new(area.Left + 20, area.Top + 20, 300, 500));
        async Task Settle() => await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        overlay.Render(definition, SpellOverlayRules.Evaluate(definition, capture, catalog: catalog));
        overlay.Place(definition, frame); await Settle();
        OverlayRect(source.Handle, out var original);
        var nectarLabel = OverlayLabels(overlay).Single(t => t.Text == "Nectar of Obscurity");
        double nameWidth = nectarLabel.ActualWidth;
        positionMessages = 0;
        // Exercise ordinary seconds and duration-format boundaries while the same effects stay active.
        foreach (int milliseconds in new[] { 94000, 93500, 93000, 605000, 599000, 3600000, 3599000, 54000 })
        {
            var update = capture with { Buffs = capture.Buffs! with { Effects = [capture.Buffs!.Effects[0] with { RemainingMilliseconds = milliseconds }, .. capture.Buffs!.Effects.Skip(1)] } };
            overlay.Render(definition, SpellOverlayRules.Evaluate(definition, update, catalog: catalog));
            overlay.Place(definition, frame); await Settle();
            OverlayRect(source.Handle, out var current);
            check(current.Equals(original), "timer refresh retains fitted window bounds: " + milliseconds);
            check(ReferenceEquals(nectarLabel, OverlayLabels(overlay).Single(t => t.Text == "Nectar of Obscurity"))
                && nectarLabel.ActualWidth == nameWidth, "timer refresh reuses row and retains spell-name width: " + milliseconds);
        }
        check(positionMessages == 0, "timer-only polls do not reposition or resize the native overlay (messages: " + positionMessages + ")");
        var moved = frame with { Area = new(frame.Area.X + 25, frame.Area.Y + 15, frame.Area.Width, frame.Area.Height) };
        overlay.Place(definition, moved); await Settle(); OverlayRect(source.Handle, out var afterMove);
        check(afterMove.Left == original.Left + 25 && afterMove.Top == original.Top + 15, "steady overlay still follows game-window moves");
        overlay.Place(definition, null); check(!overlay.IsVisible, "absent game hides the steady overlay");
        overlay.Place(definition, moved); await Settle(); check(overlay.IsVisible, "steady overlay reappears when game returns");
        var spacious = moved with { Area = new(moved.Area.X, moved.Area.Y, 800, 700) };
        overlay.Place(definition, spacious); await Settle(); OverlayRect(source.Handle, out var expanded);
        check(expanded.Right - expanded.Left > afterMove.Right - afterMove.Left, "overlay returns to configured width when game area grows");
        positionMessages = 0;
        for (int i = 0; i < 4; i++)
        {
            overlay.Render(definition, SpellOverlayRules.Evaluate(definition, capture, catalog: catalog));
            overlay.Place(definition, spacious); await Settle();
        }
        check(positionMessages == 0, "unscaled overlay also avoids native movement on unchanged polls");
        overlay.Render(definition, SpellOverlayRules.Evaluate(definition, null, "Reading unavailable"));
        overlay.Place(definition, spacious); await Settle(); OverlayRect(source.Handle, out var empty);
        check(empty.Bottom - empty.Top < expanded.Bottom - expanded.Top && !OverlayTexts(overlay).Contains("Caustic Blood"),
            $"real content changes still resize the overlay and remove stale effects ({expanded.Bottom - expanded.Top} -> {empty.Bottom - empty.Top})");
    }
    [StructLayout(LayoutKind.Sequential)] private struct OverlayBounds { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool OverlayRect(nint window, out OverlayBounds bounds);
    private static async Task TestEditableOverlay(SpellOverlayDefinition definition, TargetCapture capture, SpellOverlayCatalog catalog,
        Action<bool, string> check)
    {
        int saves = 0; bool fail = false;
        var saved = definition;
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1], (original, change) =>
        { if (fail) throw new IOException("Fixture save failure"); saves++; saved = change.Apply(saved); return saved; });
        try
        {
            async Task Settle() => await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var frame = new GameFrame("layout-fixture", "Fixture", new(area.Left + 20, area.Top + 20, 850, 600));
            var state = SpellOverlayRules.Evaluate(definition, capture, catalog: catalog);
            nint foreground = Native.GetForegroundWindow();
            display.Render(saved, state); display.Place(saved, frame); await Settle();
            var corner = display.LayoutLock!;
            nint bodyHandle = new System.Windows.Interop.WindowInteropHelper(display).Handle;
            nint lockHandle = new System.Windows.Interop.WindowInteropHelper(corner).Handle;
            OverlayRect(bodyHandle, out var body); OverlayRect(lockHandle, out var button);
            check(corner.IsVisible && button.Top >= body.Top && button.Bottom < body.Top + 50 && button.Right <= body.Right && button.Left > body.Right - 50,
                "lock icon sits inside the upper-right corner with no top bar");
            check(corner.Owner == display, "corner lock stays above its body when the body takes focus");
            check(Native.GetForegroundWindow() == foreground && (OverlayStyle(lockHandle, -20).ToInt64() & 0x08000020) == 0
                && (OverlayStyle(bodyHandle, -20).ToInt64() & 0x08000020) == 0x08000020,
                "showing overlay preserves focus; only the corner lock accepts clicks while locked");
            int layoutMessages = 0;
            foreach (var nativeWindow in new W.Window[] { display, corner })
                ((System.Windows.Interop.HwndSource)W.PresentationSource.FromVisual(nativeWindow)).AddHook(
                    (nint hwnd, int message, nint w, nint l, ref bool handled) => { if (message == 0x46) layoutMessages++; return 0; });
            for (int i = 0; i < 4; i++)
            {
                display.Render(saved, state); display.Place(saved, frame); await Settle();
            }
            check(layoutMessages == 0, "corner lock and spell display both stay still on unchanged polls");
            check(!display.BeginLayoutEdit(false, new(100, 100)) && !display.BeginLayoutEdit(true, new(100, 100)), "lock blocks both moving and resizing");
            display.ToggleLayoutLock();
            check(!saved.Locked && saves == 1, "lock toggle saves independently");
            check(display.IsHitTestVisible && (OverlayStyle(bodyHandle, -20).ToInt64() & 0x08000020) == 0,
                "unlocking makes the whole overlay interactive and normally activating");
            check(OverlayTexts(display).Contains("⤢"), "resize grip appears inside the unlocked overlay");
            var nameLabel = OverlayLabels(display).Single(t => t.Text == "Nectar of Obscurity");
            double textHeight = nameLabel.TransformToAncestor(display).TransformBounds(new(0, 0, nameLabel.ActualWidth, nameLabel.ActualHeight)).Height;
            check(display.BeginLayoutEdit(false, new(100, 100)), "unlocked overlay starts a move");
            display.UpdateLayoutEdit(new(145, 125));
            // Polls still contain the old saved position while a gesture is in progress.
            display.Render(saved, state); display.Place(saved, frame); await Settle(); OverlayRect(bodyHandle, out var moved);
            check(moved.Left == body.Left + 45 && moved.Top == body.Top + 25 && saves == 1, "live drag follows cursor and polling cannot snap it back or repeatedly save");
            display.EndLayoutEdit(false); await Settle();
            check(saves == 2 && saved.Screens!.Single().ScreenId == frame.ScreenId, "release saves move and current monitor exactly once");
            double oldScale = saved.Scale;
            display.BeginLayoutEdit(true, new(100, 100)); display.UpdateLayoutEdit(new(175, 145)); display.EndLayoutEdit(false); await Settle();
            OverlayRect(bodyHandle, out var resized);
            check(saved.Scale > oldScale && resized.Right - resized.Left > moved.Right - moved.Left && saves == 3, "resize grip scales overlay and persists on release");
            check(nameLabel.TransformToAncestor(display).TransformBounds(new(0, 0, nameLabel.ActualWidth, nameLabel.ActualHeight)).Height > textHeight,
                "enlarging the overlay scales its text along with its window");
            var beforeCancel = saved;
            display.BeginLayoutEdit(false, new(100, 100)); display.UpdateLayoutEdit(new(120, 120)); display.EndLayoutEdit(true); await Settle();
            check(saved == beforeCancel && saves == 3, "cancelled gesture leaves saved layout unchanged");
            display.ToggleLayoutLock(); check(saved.Locked && !display.BeginLayoutEdit(false, new(100, 100)), "relocking disables movement immediately");
            check(!display.IsHitTestVisible && (OverlayStyle(bodyHandle, -20).ToInt64() & 0x08000020) == 0x08000020,
                "relocking restores native click-through and nonactivation");
            fail = true; display.ToggleLayoutLock();
            check(saved.Locked && ((C.Button)corner.Content).ToolTip.ToString()!.Contains("Layout not saved"), "save failure retains lock and reports the error on the lock icon");
            display.Place(saved, null); check(!display.IsVisible && !corner.IsVisible, "missing or minimized game hides display and corner lock");
            fail = false; display.Place(saved, frame); await Settle(); check(corner.IsVisible, "corner lock returns with the game window");
            display.Close(); check(!corner.IsVisible, "closing display also closes its corner lock");
        }
        finally { display.Close(); }
    }
    private static void TestAutoRelock(SpellOverlayDefinition definition, TargetCapture capture, SpellOverlayCatalog catalog, Action<bool, string> check)
    {
        long now = 0; int saves = 0, attempts = 0; bool fail = false;
        var saved = definition;
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1], (original, change) =>
        { attempts++; if (fail) throw new IOException("Fixture save failure"); saves++; saved = change.Apply(saved); return saved; }) { LayoutClock = () => now };
        try
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var frame = new GameFrame("idle-fixture", "Fixture", new(area.Left + 10, area.Top + 10, 850, 650));
            var state = SpellOverlayRules.Evaluate(definition, capture, catalog: catalog);
            display.Render(saved, state); display.Place(saved, frame); display.ToggleLayoutLock();
            now = 14999; display.Render(saved, state); display.Place(saved, frame); display.CheckAutoRelock();
            check(!saved.Locked, "automatic relock does not fire before 15 seconds");
            now = 15000; display.CheckAutoRelock();
            check(saved.Locked && !display.IsHitTestVisible && saves == 2, "15 seconds without movement locks and persists even though readings refresh");
            display.CheckAutoRelock(); check(saves == 2, "automatic relock saves only once");
            now = 20000; display.ToggleLayoutLock();
            now = 30000; display.BeginLayoutEdit(false, new(100, 100)); display.UpdateLayoutEdit(new(125, 110)); display.EndLayoutEdit(false);
            now = 44999; display.CheckAutoRelock(); check(!saved.Locked, "moving restarts the inactivity deadline");
            now = 45000; display.CheckAutoRelock(); check(saved.Locked, "moved overlay locks 15 seconds after its last movement");
            now = 50000; display.ToggleLayoutLock();
            now = 60000; display.BeginLayoutEdit(true, new(100, 100)); display.UpdateLayoutEdit(new(135, 125)); display.EndLayoutEdit(false);
            now = 74999; display.CheckAutoRelock(); check(!saved.Locked, "resizing also restarts the inactivity deadline");
            now = 75000; display.CheckAutoRelock(); check(saved.Locked, "resized overlay locks at the new deadline");
            now = 80000; display.ToggleLayoutLock(); display.BeginLayoutEdit(false, new(100, 100));
            now = 100000; display.CheckAutoRelock(); check(!saved.Locked, "automatic lock waits for an active drag to finish");
            display.EndLayoutEdit(false); check(saved.Locked, "overdue automatic lock takes effect when the stationary drag ends");
            now = 110000; display.ToggleLayoutLock(); fail = true;
            now = 125000; display.CheckAutoRelock();
            check(!saved.Locked && !display.IsHitTestVisible && !display.BeginLayoutEdit(false, new(100, 100)),
                "automatic lock still prevents movement if persistence fails");
            int priorAttempts = attempts;
            display.Render(saved, state); now += 50000; display.CheckAutoRelock();
            check(attempts == priorAttempts && !display.IsHitTestVisible, "old saved state cannot undo a local timeout lock or cause repeated failed writes");
            fail = false; display.ToggleLayoutLock(false); check(display.IsHitTestVisible, "explicit unlock recovers after a timeout save failure");
            display.Place(saved, null); now += 15000; display.CheckAutoRelock();
            check(saved.Locked && !display.IsVisible, "hidden overlay can auto-lock without reopening itself");
        }
        finally { display.Close(); }
    }
    private void TestLayoutSave(SpellOverlayDefinition definition, Action<bool, string> check)
    {
        var original = definition with { Locked = false };
        savedLibrary = new(1, [original]); savedBytes = SpellOverlayStore.Save(path, savedLibrary, null);
        applied = [original]; definitions.Clear(); definitions.Add(original); OverlayList.SelectedItem = original;
        OverlayName.Text = "Pending name edit"; bool wasDirty = dirty;
        var next = SaveFloatingLayout(original, new(Locked: true));
        var onDisk = SpellOverlayStore.Load(path).Library.Overlays.Single();
        check(onDisk.Locked && onDisk.Name == original.Name && selected!.Name == "Pending name edit" && dirty && wasDirty,
            "layout autosave preserves unrelated unsaved editor changes without committing them");
        bool blocked = false;
        try { SaveFloatingLayout(next, new(Scale: 1.4)); } catch (InvalidDataException) { blocked = true; }
        check(blocked && SpellOverlayStore.Load(path).Library.Overlays.Single().Scale == original.Scale, "manager rejects stale move or resize after locking");
        File.AppendAllText(path, " ");
        blocked = false; try { SaveFloatingLayout(next, new(Locked: false)); } catch (IOException) { blocked = true; }
        check(blocked && SpellOverlayStore.Load(path).Library.Overlays.Single().Locked, "layout autosave rejects external file conflicts");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint OverlayStyle(nint window, int index);
    internal static void Review(string directory, AppearanceTheme theme, bool interactive = false)
    {
        Directory.CreateDirectory(directory);
        var app = new W.Application(); var manager = new SpellOverlayManagerWindow(Path.Combine(directory, "unused-preview-settings.json"), true);
        var (definition, capture, catalog) = SpellOverlayTests.GroupFixture();
        definition = definition with { Name = "Shaman Bar", Character = "Fellwort", PersonaClass = "Shaman", Locked = false,
            Spells = definition.Spells.Select((s, i) => s with { PersonaClasses = null, ClickKey = i == 0 ? "Alt+NumpadMultiply" : "Alt+]" }).ToArray() };
        capture = capture with { CharacterName = definition.Character, Buffs = capture.Buffs! with { CharacterName = definition.Character,
            Effects = capture.Buffs!.Effects.Select(e => e.CasterName == capture.CharacterName ? e with { CasterName = definition.Character } : e).ToArray() } };
        definition = definition with { Spells = [.. definition.Spells, new(900001, "Sample shared buff", Target: SpellTargetKind.Player, Caster: SpellCasterKind.Anyone,
            IconId: 56, ExpandTriggered: false, ClickKey: "Alt+H", Alternatives: [new(900002, "Sample cleric buff", 104), new(900003, "Sample druid buff", 131)])] };
        definition = definition with { DockGroups = SpellDockTests.Groups(), Spells = definition.Spells.Select((s, i) => s with { Alias = i == 0 ? "Bloodcurse" : null }).ToArray() };
        manager.savedDockButtons = SpellDockTests.Buttons();
        capture = capture with { Buffs = capture.Buffs! with { Effects = [.. capture.Buffs!.Effects,
            new(3, 900003, "Sample druid buff", "Sampledruid", 74000, 131)] } };
        var names = SpellNameCatalog.Read(); manager.SetCatalog(names);
        manager.definitions.Add(definition);
        manager.definitions.Add(definition with { Id = "berserker-preview", Name = "Berserker Bar", PersonaClass = "Berserker", Spells = [] });
        manager.OverlayList.SelectedIndex = 0;
        int[] sampleIds = [72115, 72116, 72117, 70418, 70419, 70420, 67165, 67166, 67167, 67173, 67174, 67175, 67629, 68500, 72148];
        var choices = sampleIds.Select(id => names.Records?.GetValueOrDefault(id)
            ?? throw new InvalidDataException($"Review spell {id} is missing from the installed catalog."))
            .Select(spell => manager.SearchChoice(spell.Id, spell.Name, spell.Icon)).OrderBy(choice => choice.Name).ToArray();
        manager.SearchResults.ItemsSource = choices; manager.SearchResults.SelectedIndex = 0;
        ThemePalette.Apply(manager.Resources, theme);
        manager.SearchHint.Text = "Sample current target · 15 installed spells · all casters · choose a spell to watch";
        manager.Status("SAMPLE CONFIGURATION · no game input or settings changes");
        if (interactive) { manager.AcceptPickerCapture(capture); app.Run(manager); return; }
        Exception? failure = null;
        manager.ContentRendered += async (_, _) =>
        {
            var overlay = new SpellOverlayWindow(FloatingThemes.ForAppTheme(theme), (original, change) => change.Apply(original), manager.OpenOverlaySettings);
            overlay.SetDockButtons(SpellDockTests.Buttons(), SpellDockTests.Clients());
            try
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor");
                manager.SpellTabs.SelectedItem = manager.DockTab;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-docks");
                manager.SpellTabs.SelectedItem = manager.AddTab;
                manager.SearchResults.SelectedIndex = choices.Length - 1;
                manager.SearchResults.ScrollIntoView(manager.SearchResults.SelectedItem);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-picker-bottom");
                manager.AcceptPickerCapture(capture);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-target-mine");
                manager.FilterOthers.IsChecked = true;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-target-others");
                manager.FilterAll.IsChecked = true;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-target-all");
                manager.SearchResults.SelectedIndex = 0;
                manager.SearchResults.ScrollIntoView(manager.SearchResults.SelectedItem);
                manager.ShowConfigure();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-click-action");
                manager.WatchList.SelectedIndex = 2;
                manager.RuleEditor.BringIntoView();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-alternatives");
                manager.Width = manager.MinWidth; manager.Height = manager.MinHeight;
                manager.ShowConfigure();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-minimum");
                manager.SpellTabs.SelectedItem = manager.AddTab;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-add-minimum");
                manager.SpellTabs.SelectedItem = manager.DockTab;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                manager.UpdateLayout(); Render(manager.OverlayReviewRoot, "editor-docks-minimum");
                overlay.Render(definition, SpellOverlayRules.Evaluate(definition, capture, catalog: catalog));
                overlay.Place(definition, new("preview", "Preview monitor", System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                overlay.UpdateLayout(); RenderOverlay(overlay, "overlay");
                overlay.ToggleLayoutLock();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                RenderOverlay(overlay, "overlay-locked");
                foreach (double scale in new[] { .5, 2.0 })
                {
                    var scaled = definition with { Locked = true, Scale = scale };
                    overlay.Render(scaled, SpellOverlayRules.Evaluate(scaled, capture, catalog: catalog));
                    overlay.Place(scaled, new("preview", "Preview monitor", System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea));
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                    overlay.UpdateLayout(); RenderOverlay(overlay, scale < 1 ? "overlay-small" : "overlay-large");
                }
            }
            catch (Exception error) { failure = error; }
            finally { overlay.Close(); manager.Close(); }
        };
        void Render(W.FrameworkElement element, string name) => Save(Bitmap(element), name);
        void RenderOverlay(SpellOverlayWindow overlay, string name) => Save(OverlayBitmap(overlay), name);
        void Save(System.Windows.Media.Imaging.BitmapSource bitmap, string name)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = new FileStream(Path.Combine(directory, $"{name}-{theme.ToString().ToLower()}.png"), FileMode.CreateNew); encoder.Save(file);
        }
        app.Run(manager); if (failure is not null) throw failure;
    }
    private static System.Windows.Media.Imaging.RenderTargetBitmap Bitmap(W.FrameworkElement element)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, M.PixelFormats.Pbgra32);
        bitmap.Render(element); return bitmap;
    }
    internal static System.Windows.Media.Imaging.RenderTargetBitmap OverlayBitmap(SpellOverlayWindow overlay)
    {
        var corner = Bitmap((W.FrameworkElement)overlay.LayoutLock!.Content); var body = Bitmap((W.FrameworkElement)overlay.Content);
        OverlayRect(new System.Windows.Interop.WindowInteropHelper(overlay).Handle, out var outer);
        OverlayRect(new System.Windows.Interop.WindowInteropHelper(overlay.LayoutLock).Handle, out var button);
        double factor = body.Width / (outer.Right - outer.Left);
        var drawing = new M.DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.PushOpacity(overlay.Opacity);
            context.DrawImage(body, new(0, 0, body.Width, body.Height));
            context.Pop();
            context.DrawImage(corner, new((button.Left - outer.Left) * factor, (button.Top - outer.Top) * factor,
                (button.Right - button.Left) * factor, (button.Bottom - button.Top) * factor));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(body.PixelWidth, body.PixelHeight, 96, 96, M.PixelFormats.Pbgra32);
        bitmap.Render(drawing); return bitmap;
    }
}
