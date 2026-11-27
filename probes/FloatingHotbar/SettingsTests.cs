using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

internal static class SettingsTests
{
    internal static void Run(string output)
    {
        using var access = GameDataAccess.FixtureScope(true);
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (Directory.Exists(directory) || File.Exists(output)) throw new IOException("Use a new settings-test report.");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new W.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        var team = new TeamDefinition(1, [new("fellwort", "Fellwort")], [new("shaman", "fellwort", "Shaman")], []);
        string profile = Path.Combine(directory, "team.json"); TeamEditing.Save(profile, team, []);
        foreach (var (id, key) in new[] { ("assist", "E"), ("alternate", "Alt+E") })
        {
            var button = new StandaloneButton(1, id, id == "assist" ? "Assist" : "Alternate action", team.Characters, team.Personas,
                [new(new(CharacterId: "fellwort"), key)], []);
            StandaloneButtons.Save(StandaloneButtons.PathFor(profile, id), button, null);
        }
        var store = new MemoryUserSettingsStore(new() { Theme = AppearanceTheme.Dark, EnableScreenDetection = true, EnableSpellBarData = true });
        var client = new DetectedClient(123, 456, 789, 0, new("Fellwort", 10, 130));
        var setup = new SetupWindow(profile, "input-free-test", () => new([client], []), userSettings: store);
        var spells = SpellOverlayManagerWindow.CreateSettingsFixture(directory, store);
        try { setup.TestUnifiedSettings(spells, directory, Check); }
        finally { setup.CloseForExit(); spells.CloseSettingsFixture(); }
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
    }
}

public partial class SetupWindow
{
    private static IEnumerable<W.DependencyObject> SettingsDescendants(W.DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in SettingsDescendants(M.VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal void TestUnifiedSettings(SpellOverlayManagerWindow spells, string directory, Action<bool, string> check)
    {
        Show();
        void Wait(Func<bool> done)
        {
            long until = Environment.TickCount64 + 5000;
            while (!done() && Environment.TickCount64 < until) { Settle(); Thread.Sleep(10); }
            check(done(), "Debounced save/discovery completed without an input host.");
        }
        Wait(() => !refreshing && clients.Count == 1);
        ButtonList.SelectedItem = standaloneDrafts.Single(d => d.Id == "assist");
        NameField.Text = "Assist target";
        Wait(() => !selected!.Dirty);
        check(StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == "assist").Value.Label == "Assist target", "A text edit saves after the debounce without clicking Save.");
        var picker = SettingsDescendants(RuleList).OfType<C.ComboBox>().Single(c => W.Automation.AutomationProperties.GetName(c) == "Key to send");
        picker.SelectedItem = "E"; rows[0].Modifiers = 4; Settle();
        AddRow(rows[0].Target, "E"); CaptureRules(); MarkChanged();
        check(FlushAutoSave(), "A button can reuse a key with different modifiers.");
        var doc = StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == "assist").Value;
        check(doc.Rules.Select(r => r.Key).SequenceEqual(["Alt+E", "E"]), "E and Alt+E save as distinct ordered chords for the same character.");
        check(StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == "alternate").Value.Rules.Single().Key == "Alt+E", "A key already used by another button remains available.");
        Settle();
        var keys = SettingsDescendants(RuleList).OfType<C.ComboBox>().Where(c => W.Automation.AutomationProperties.GetName(c) == "Key to send").ToArray();
        check(keys.Length == 2 && keys.All(c => (string)c.SelectedItem == "E"), "Two rendered key pickers can select E simultaneously.");
        var mods = SettingsDescendants(RuleList).OfType<ModifierPicker>().First();
        mods.Value = 3; keys[0].SelectedItem = "H"; Settle(); FlushAutoSave();
        check(selected!.Rules[0].Key == "Ctrl+Shift+H" && rows[1].Chord == "E", "Two modifier toggles update only their own mapping.");
        byte[] good = File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, selected.Id));
        NameField.Text = "  ";
        check(FlushAutoSave() && StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == selected.Id).Value.Label == ""
            && selected.ManagementName == "Ctrl+Shift+H",
            "Clearing a button name autosaves an empty label while its shortcut still identifies the action in Settings.");
        NameField.Text = "Assist target"; FlushAutoSave();
        File.AppendAllText(StandaloneButtons.PathFor(profilePath, selected.Id), " ");
        NameField.Text = "Conflicting edit";
        check(!FlushAutoSave() && StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == selected.Id).Value.Label == "Assist target", "Autosave refuses to overwrite a concurrent button edit.");
        File.WriteAllBytes(StandaloneButtons.PathFor(profilePath, selected.Id), good); NameField.Text = "Assist target"; FlushAutoSave();
        BarLibraryMode.IsChecked = true; AddGroup(); groupName!.Text = "Utilities";
        check(FlushAutoSave() && TeamEditing.Load(profilePath).Groups!.Single().Name == "Utilities", "Group edits also save automatically.");
        StandaloneLibraryMode.IsChecked = true;
        ShowSpellSettings(spells); Settle();
        check(SpellOverlaysTab.IsChecked == true && SpellSettingsHost.Content is not null && !spells.IsVisible && W.Window.GetWindow((W.DependencyObject)SpellSettingsHost.Content) == this,
            "Spell settings are hosted inside the same top-level window.");
        spells.TestSettingsAutoSave(this, check);
        ShowHotKeys(); Settle();
        check(ReviewRoot.IsVisible && !SpellSettingsHost.IsVisible && selected is not null, "Switching back to Hot Keys preserves the current editor.");
        ShowGeneralSettings(true); ShowFloatingPreferences(true);
        FloatingSize.Value = 115; FloatingOpacity.Value = 85;
        Wait(() => !floatingSaveTimer.IsEnabled);
        check(settingsStore.Load().Floating is { Scale: 1.15, Opacity: .85 }, "Floating size and opacity still autosave without palette controls.");
        check(!SettingsDescendants(FloatingPreferencePanel).OfType<C.ComboBox>().Any(), "Floating settings no longer offer a separate style selector.");
        Settle(); RenderSettings(Path.Combine(directory, "floating-controls-dark.png"));
        LightThemeChoice.IsChecked = true; Settle(); RenderSettings(Path.Combine(directory, "floating-controls-light.png"));
        check(FloatingThemes.ForApplication(settings).Selected == FloatingThemes.ForAppTheme(AppearanceTheme.Light), "Floating controls use the app's Light appearance.");
        DarkThemeChoice.IsChecked = true; ShowGeneralSettings(false);
        FloatingThemeTests.Export(directory);
        // Capture the complete combined window, including its new top navigation.
        RenderSettings(Path.Combine(directory, "hot-keys-dark.png"));
        ShowSpellSettings(spells); Settle(); RenderSettings(Path.Combine(directory, "spell-overlays-dark.png"));
        spells.ExpandBarSettingsForReview(true); Settle(); RenderSettings(Path.Combine(directory, "spell-overlays-expanded-dark.png"));
        check(SettingsDescendants(SpellSettingsHost).OfType<C.Button>().Any(b => b.Content as string == "⌄  Bar settings"
            && W.Automation.AutomationProperties.GetName(b) == "Collapse bar settings"), "The embedded bar-settings icon updates to collapse in the unified window.");
        spells.ExpandBarSettingsForReview(false); Settle();
        LightThemeChoice.IsChecked = true; Settle(); RenderSettings(Path.Combine(directory, "spell-overlays-light.png"));
        check(((M.SolidColorBrush)spells.Resources["AppBackground"]).Color == ((M.SolidColorBrush)Resources["AppBackground"]).Color, "Theme changes reach both editors.");
        Width = 1020; Height = 760; Settle(); RenderSettings(Path.Combine(directory, "settings-compact.png"));
    }
    private void RenderSettings(string path)
    {
        var root = (W.FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, M.PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}

public partial class SpellOverlayManagerWindow
{
    internal static SpellOverlayManagerWindow CreateSettingsFixture(string directory, IUserSettingsStore preferences)
    {
        string path = Path.Combine(directory, "overlays.json");
        SpellOverlayStore.Save(path, new(3, [new("fixture", "Shaman Bar", "Fellwort", [new(100, "Breath of Pustim", ClickKey: "Alt+E"), new(101, "Nectar of Pain XIII", ClickKey: "E")], PersonaClass: "Shaman")]), null);
        var manager = new SpellOverlayManagerWindow(path, userSettings: preferences) { runtimeInitialized = true, nextDiscovery = DateTime.MaxValue };
        manager.DiscoverCharacters = () => new([], []); manager.ReadDockButtons = () => [];
        manager.RequestSpellInput = _ => Task.FromResult<SpellClickReply?>(new(true, "Fixture", false, "fixture"));
        manager.SetCatalog(new SpellNames(new Dictionary<int, string>(), null, null));
        return manager;
    }
    internal void TestSettingsAutoSave(SetupWindow host, Action<bool, string> check)
    {
        WatchList.SelectedIndex = 0;
        ClickKeyChoice.SelectedItem = "H"; ClickModifierChoice.Value = 3;
        SpellAlias.Text = "Priority spell";
        long deadline = Environment.TickCount64 + 5000;
        while (dirty && Environment.TickCount64 < deadline)
        { Dispatcher.Invoke(() => { }, W.Threading.DispatcherPriority.ContextIdle); Thread.Sleep(10); }
        check(!dirty, "Spell edits save after the debounce without Save & show.");
        var saved = SpellOverlayStore.Load(path).Library.Overlays.Single();
        check(saved.Spells[0].ClickKey == "Ctrl+Shift+H" && saved.Spells[0].Alias == "Priority spell", "Spell overlays support two held modifiers and autosaved aliases.");
        PlacementChoice.SelectedIndex = 1; FlushAutoSave();
        var free = SpellOverlayStore.Load(path).Library.Overlays.Single();
        check(!free.FollowCharacterWindow && free.FreePosition is not null && free.Locked == saved.Locked,
            "Placement mode autosaves its own desktop position while preserving the movement lock.");
        PlacementChoice.SelectedIndex = 0; FlushAutoSave();
        var window = floating["fixture"];
        SpellAlias.Text = "Breath"; FlushAutoSave();
        check(ReferenceEquals(window, floating["fixture"]), "Autosave preserves the floating window and input registration instead of recreating it.");
        ClickKeyChoice.SelectedItem = "E"; ClickModifierChoice.Value = 4; FlushAutoSave();
        check(SpellOverlayStore.Load(path).Library.Overlays.Single().Spells.Select(s => s.ClickKey).SequenceEqual(["Alt+E", "E"]), "Spell entries also allow E and Alt+E together.");
        OverlayName.Text = "  ";
        check(FlushAutoSave() && SpellOverlayStore.Load(path).Library.Overlays.Single() is { Name: "", Id: "fixture", Spells.Length: 2 }
            && !string.IsNullOrWhiteSpace(selected!.EditorName), "Clearing a spell bar name saves its original ID and spells and leaves it selectable.");
        byte[] baseline = File.ReadAllBytes(path);
        ClassChoice.SelectedIndex = -1;
        check(!FlushAutoSave() && baseline.SequenceEqual(File.ReadAllBytes(path)), "Incomplete overlay settings preserve the last saved library.");
        ClassChoice.SelectedItem = "Shaman";
        OverlayName.Text = "Shaman Bar"; FlushAutoSave();
        Hide_Click(this, new()); SpellAlias.Text = "Breath of Pustim"; FlushAutoSave();
        check(applied.Length == 0 && overlaysHidden && !preferences.Load().SpellOverlaysLoaded, "Editing hidden overlays saves without reopening them.");
        host.ShowHotKeys(); OpenOverlaySettings("fixture");
        check(host.SpellOverlaysTab.IsChecked == true && !IsVisible && selected?.Id == "fixture", "The gear reopens the same combined window on the correct spell overlay.");
        var original = selected!;
        var other = original with { Id = "delete-fixture", Name = "Unselected bar" };
        definitions.Add(other); Dirty(); FlushAutoSave();
        var remove = new C.Button { DataContext = other };
        byte[] beforeDelete = File.ReadAllBytes(path);
        var confirmation = ConfirmOverlayDeletion;
        try
        {
            ConfirmOverlayDeletion = (_, _) => false;
            Delete_Click(remove, new(C.Button.ClickEvent)); FlushAutoSave();
            check(definitions.Count == 2 && beforeDelete.SequenceEqual(File.ReadAllBytes(path)), "Canceling inline overlay deletion leaves the library unchanged.");
            ConfirmOverlayDeletion = (_, _) => true;
            Delete_Click(remove, new(C.Button.ClickEvent)); FlushAutoSave();
            check(selected?.Id == original.Id && definitions.Count == 1 && definitions[0].Id == original.Id
                && SpellOverlayStore.Load(path).Library.Overlays.Length == 1,
                "The inline X deletes its own unselected bar and preserves the selected bar: " + StatusLabel.Text);
            remove.DataContext = selected;
            Delete_Click(remove, new(C.Button.ClickEvent)); FlushAutoSave();
            check(selected is null && !Editor.IsEnabled && SpellOverlayStore.Load(path).Library.Overlays.Length == 0,
                "Deleting the final bar saves an empty library and disables its editor.");
            definitions.Add(original); OverlayList.SelectedItem = original; Dirty(); FlushAutoSave();
        }
        finally { ConfirmOverlayDeletion = confirmation; }
    }
    internal void CloseSettingsFixture() { dirty = false; exiting = true; Close(); }
}
