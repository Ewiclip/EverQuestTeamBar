using System.Diagnostics;
using System.Runtime.InteropServices;
using W = System.Windows;
namespace TeamBar;

internal static class PrivacyTests
{
    internal static void Run(string output)
    {
        using var restore = GameDataAccess.FixtureScope(false);
        string folder = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(folder)) throw new IOException("Use a new privacy-test output.");
        Directory.CreateDirectory(folder); var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        var preferences = new LocalUserSettingsStore(Path.Combine(folder, "preferences.json"));
        var fresh = preferences.Load();
        Check(!fresh.EnableScreenDetection && !fresh.EnableSpellBarData && !fresh.EnableDataCollection,
            "All three memory features default off.");
        File.WriteAllText(Path.Combine(folder, "preferences.json"), """{"version":1,"theme":"dark","enableDataCollection":true,"spellOverlaysLoaded":true}""");
        GameDataAccess.Initialize(preferences);
        Check(!Enum.GetValues<MemoryFeature>().Any(GameDataAccess.IsEnabled) && preferences.Load().SpellOverlaysLoaded,
            "A legacy collection choice or loaded overlay cannot start reading when legacy memory permission was off.");
        Check(Identity.Discover().Clients.Length == 0 && CollectorIdentity.Discover().Clients.Length == 0,
            "Disabled identity and collection lookups return before enumerating clients.");

        TestIndependentReaders(Check);
        string profile = Path.Combine(folder, "team.json");
        var manual = ClientSessions.Assign(new(11, 22, 33, 0, "Fixture monitor"), "Testhero", "Shaman");
        int detectors = 0;
        var verified = ClientSessions.Verify(manual, _ => new(11, 22, 33, 10, "Fixture monitor"), _ => { detectors++; throw new Exception(); });
        Check(verified.UserAssigned && detectors == 0, "Manual identity verification uses only window/session metadata.");
        ManualAssignments.Save(profile, [manual]);
        Check(ManualAssignments.Load(profile, c => c).Single().UserAssigned && ManualAssignments.Load(profile, c => c with { Window = 999 }).Length == 0,
            "Confirmed assignments survive app restarts but cannot move to another window.");
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new W.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        var team = new TeamDefinition(1, [new("fellwort", "Fellwort")], [new("shaman", "fellwort", "Shaman")], []);
        TeamEditing.Save(profile, team, []);
        var button = new StandaloneButton(1, "assist", "Assist target", team.Characters, team.Personas,
            [new(new(CharacterId: "fellwort"), "Ctrl+Shift+H")], []);
        StandaloneButtons.Save(StandaloneButtons.PathFor(profile, "assist"), button, null);
        var store = new MemoryUserSettingsStore();
        var setup = new SetupWindow(profile, "input-free-test", () => new([], []), userSettings: store);
        try { setup.TestPrivacyShell(folder, store, Check); }
        finally { setup.ClosePrivacyFixture(); }
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameMemoryRead = false, gameInputSent = false,
            ownProcessFixtureRead = true, screenshots = folder });
    }

    private static void TestIndependentReaders(Action<bool, string> check)
    {
        var features = new[] { MemoryFeature.ScreenDetection, MemoryFeature.SpellBarData, MemoryFeature.DataCollection };
        using var self = Process.GetCurrentProcess();
        var executable = self.MainModule!.FileName;
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 12345);
            for (int mask = 0; mask < 8; mask++)
            {
                for (int i = 0; i < features.Length; i++)
                    GameDataAccess.SetEnabled(features[i], (mask & (1 << i)) != 0);

                for (int i = 0; i < features.Length; i++)
                {
                    var feature = features[i];
                    bool enabled = (mask & (1 << i)) != 0;
                    bool opened = false;
                    try
                    {
                        using var reader = new MemoryReader(self.Id, executable, feature);
                        opened = reader.I32((ulong)buffer) == 12345;
                    }
                    catch (InvalidDataException e) when (!enabled && e.Message == GameDataAccess.DisabledReason(feature)) { }
                    check(opened == enabled, $"Combination {mask}: {feature} enforces its permission on the fixture process.");
                }
                int captures = 0;
                try { DataCollectionAccess.Read(() => ++captures); }
                catch (InvalidOperationException) when ((mask & 4) == 0) { }
                check(captures == ((mask & 4) == 0 ? 0 : 1), $"Combination {mask}: only Data Collection authorizes a capture callback.");
            }

            foreach (var feature in features)
            {
                using var reader = new MemoryReader(self.Id, executable, feature);
                GameDataAccess.SetEnabled(feature, false);
                bool blocked = false;
                try { reader.Read((ulong)buffer, sizeof(int)); }
                catch (InvalidDataException e) when (e.Message == GameDataAccess.DisabledReason(feature)) { blocked = true; }
                check(blocked && features.Where(other => other != feature).All(GameDataAccess.IsEnabled),
                    $"Revoking {feature} immediately blocks existing handles and keeps the other two permissions.");
                GameDataAccess.SetEnabled(feature, true);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var read = Task.Run(() =>
        {
            using var lease = GameDataAccess.EnterRead(MemoryFeature.SpellBarData);
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        check(reading.Wait(TimeSpan.FromSeconds(2)), "A spell fixture read acquires its access lease.");
        var revoke = Task.Run(() => GameDataAccess.SetEnabled(MemoryFeature.SpellBarData, false));
        try { check(!revoke.Wait(100), "Spell revocation waits for an in-progress spell read to finish."); }
        finally { release.Set(); }
        check(Task.WaitAll([read, revoke], 5000) && !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData),
            "No spell read outlives completed revocation.");
        foreach (var feature in features) GameDataAccess.SetEnabled(feature, false);
    }
}

public partial class SetupWindow
{
    internal void TestPrivacyShell(string folder, IUserSettingsStore store, Action<bool, string> check)
    {
        Show(); Settle();
        void Wait(Func<bool> done)
        {
            long until = Environment.TickCount64 + 5000;
            while (!done() && Environment.TickCount64 < until) { Settle(); Thread.Sleep(10); }
            check(done(), "Privacy change and UI refresh settle.");
        }
        Wait(() => !refreshing);
        bool Enabled(MemoryFeature feature) => GameDataAccess.IsEnabled(feature);
        void SetChoice(System.Windows.Controls.CheckBox choice, bool enabled)
        {
            choice.IsChecked = enabled;
            Wait(() => ScreenDetectionChoice.IsEnabled && SpellBarDataChoice.IsEnabled && DataCollectionChoice.IsEnabled && !refreshing);
        }

        check(SettingsButton.IsVisible && !SpellOverlaysTab.IsEnabled && DataCollectionTab.Visibility == W.Visibility.Collapsed
            && HotKeysTab.IsEnabled && ScreensTab.IsEnabled && ScreensTab.IsVisible,
            "Hot Keys and Screens remain available with all memory features off.");
        RenderSettings(Path.Combine(folder, "hotkeys-memory-off-dark.png"));
        WindowLayoutActions.IsExpanded = true; Settle();
        check(ReviewRoot.RowDefinitions[0].ActualHeight >= WindowLayoutActions.ActualHeight,
            "Expanded game-window actions fit above the editor.");
        WindowLayoutActions.IsExpanded = false;
        OpenAppSettings(); Settle();
        check(GeneralSettingsView.IsVisible && GeneralPreferencePanel.IsVisible && !MemoryPreferencePanel.IsVisible,
            "The Settings button opens General.");
        ShowFloatingPreferences(true); Settle();
        check(FloatingPreferencePanel.IsVisible && !MemoryPreferencePanel.IsVisible,
            "Floating controls keep their own settings page.");
        OpenMemorySettings(); Settle();
        check(MemoryPreferencePanel.IsVisible && ScreenDetectionChoice.IsVisible && SpellBarDataChoice.IsVisible && DataCollectionChoice.IsVisible,
            "Memory Reading presents three independent checkboxes.");
        RenderSettings(Path.Combine(folder, "settings-memory-off-dark.png"));

        SetChoice(ScreenDetectionChoice, true);
        check(store.Load().EnableScreenDetection && Enabled(MemoryFeature.ScreenDetection)
            && !SpellOverlaysTab.IsEnabled && !DataCollectionAccess.CanCollect, "Screen Detection alone enables automatic window identification.");
        SetChoice(ScreenDetectionChoice, false);
        SetChoice(SpellBarDataChoice, true);
        check(store.Load().EnableSpellBarData && Enabled(MemoryFeature.SpellBarData) && SpellOverlaysTab.IsEnabled
            && !Enabled(MemoryFeature.ScreenDetection) && DataCollectionTab.Visibility == W.Visibility.Collapsed,
            "Spell bars can run with both automatic screen detection and collection off.");
        var spells = SpellOverlayManagerWindow.CreateSettingsFixture(folder, store);
        ShowSpellSettings(spells); Settle();
        check(SettingsButton.IsVisible && SpellSettingsHost.IsVisible, "Spell settings open with only spell data enabled.");
        var deniedCollector = new ItemCollectorWindow(reviewOnly: true);
        bool deniedClosed = false; deniedCollector.Closed += (_, _) => deniedClosed = true;
        ShowDataSettings(deniedCollector); Settle();
        check(deniedClosed && dataEditor is null && MemoryPreferencePanel.IsVisible,
            "Attempting to open a disabled collector leads to Memory Reading.");
        SetChoice(DataCollectionChoice, true);
        check(store.Load().EnableDataCollection && DataCollectionAccess.CanCollect && DataCollectionTab.IsVisible && DataCollectionTab.IsEnabled,
            "Enabling collection shows an available collection tab.");
        var collector = new ItemCollectorWindow(reviewOnly: true);
        bool collectorClosed = false; collector.Closed += (_, _) => collectorClosed = true;
        ShowDataSettings(collector); Settle();
        check(DataSettingsHost.IsVisible && W.Window.GetWindow((W.DependencyObject)DataSettingsHost.Content) == this,
            "Collection opens in the main window.");
        RenderSettings(Path.Combine(folder, "data-collection-dark.png"));

        OpenMemorySettings(); SetChoice(ScreenDetectionChoice, true);
        SetChoice(ScreenDetectionChoice, false);
        check(!collectorClosed && ReferenceEquals(spellEditor, spells) && SpellOverlaysTab.IsEnabled && DataCollectionAccess.CanCollect,
            "Disabling screen detection keeps the collector and spell manager available.");
        SetChoice(DataCollectionChoice, false);
        check(collectorClosed && DataSettingsHost.Content is null && DataCollectionTab.Visibility == W.Visibility.Collapsed
            && !store.Load().EnableDataCollection && ReferenceEquals(spellEditor, spells) && SpellOverlaysTab.IsEnabled,
            "Disabling collection closes the collector and leaves spell bars available.");
        SetChoice(DataCollectionChoice, true);
        var remainingCollector = new ItemCollectorWindow(reviewOnly: true);
        bool closed = false; remainingCollector.Closed += (_, _) => closed = true;
        ShowDataSettings(remainingCollector); OpenMemorySettings(); SetChoice(SpellBarDataChoice, false);
        check(!closed && DataSettingsHost.Content is not null && DataCollectionTab.IsEnabled && !SpellOverlaysTab.IsEnabled
            && !Enabled(MemoryFeature.ScreenDetection) && DataCollectionAccess.CanCollect,
            "Data Collection stays usable as the only enabled feature.");
        check(SpellOverlayStore.Load(Path.Combine(folder, "overlays.json")).Library.Overlays.Length == 1,
            "Disabling spell data preserves saved spell bars.");
        SetChoice(DataCollectionChoice, false);
        check(closed, "Turning off the last active feature closes its viewer.");

        var choices = new[]
        {
            (ScreenDetectionChoice, MemoryFeature.ScreenDetection),
            (SpellBarDataChoice, MemoryFeature.SpellBarData),
            (DataCollectionChoice, MemoryFeature.DataCollection)
        };
        foreach (var (choice, feature) in choices)
        {
            settingsStore = new UserSettingsTests.FailingStore(new());
            SetChoice(choice, true);
            check(!Enabled(feature) && choice.IsChecked == false, $"Failed opt-in keeps {feature} off.");
            settingsStore = new UserSettingsTests.FailingStore(new()
            {
                EnableScreenDetection = feature == MemoryFeature.ScreenDetection,
                EnableSpellBarData = feature == MemoryFeature.SpellBarData,
                EnableDataCollection = feature == MemoryFeature.DataCollection
            });
            GameDataAccess.SetEnabled(feature, true); ApplyMemoryPreference();
            SetChoice(choice, false);
            check(!Enabled(feature) && choice.IsChecked == false && SettingsFeedback.Text.Contains("off for this session"),
                $"Failed opt-out still stops {feature} for this session.");
        }
        settingsStore = store; settings = store.Load(); ApplyMemoryPreference();
        LightThemeChoice.IsChecked = true; Settle(); RenderSettings(Path.Combine(folder, "settings-memory-off-light.png"));
        Width = 1020; Height = 760; Settle(); RenderSettings(Path.Combine(folder, "settings-compact-light.png"));
        DataCollectionChoice.BringIntoView(); Settle();
        DataCollectionChoice.Focus(); Settle();
        var optionBottom = DataCollectionChoice.TranslatePoint(new W.Point(0, DataCollectionChoice.ActualHeight), MemoryPreferencePanel);
        check(DataCollectionChoice.IsKeyboardFocused && optionBottom.Y > 0 && optionBottom.Y < MemoryPreferencePanel.ActualHeight,
            "The collection option can be scrolled into view and focused in a compact settings window.");
        RenderSettings(Path.Combine(folder, "settings-compact-collection-light.png"));
        var windows = new[] { new GameSession(1, 2, 3, 0, "DISPLAY1 · 1920 × 1080"), new GameSession(4, 5, 6, 1920, "DISPLAY2 · 2560 × 1440") };
        var assignments = new WindowAssignments(this, new(1, [], [], []), windows, [], c => c, () => windows);
        try
        {
            assignments.Show(); assignments.Select(0, "Newhero", "Berserker"); assignments.Confirm();
            check(assignments.BuildAssignments().Single().Character.Name == "Newhero", "A first-time user can type a character name without prior memory detection.");
            assignments.UpdateLayout(); assignments.Export(Path.Combine(folder, "manual-assignments-light.png"));
        }
        finally { assignments.Close(); }
        spells.CloseSettingsFixture();
    }
    internal void ClosePrivacyFixture() { dataEditor?.Close(); CloseForExit(); }
}
