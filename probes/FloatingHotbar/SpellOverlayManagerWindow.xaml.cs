using System.Collections.ObjectModel;
using W = System.Windows;
using C = System.Windows.Controls;
using System.Windows.Media.Imaging;
namespace TeamBar;

internal sealed record SpellSearchChoice(int Id, string Name, int? Icon = null, BitmapSource? Image = null) { public string Label => $"{Name}  ·  {Id}"; }
internal sealed record SpellWatchChoice(int Priority, SpellWatch Rule, BitmapSource? Image = null, int Index = 0, string Section = "NPC")
{
    public string Name => Rule.DisplayName;
    public string Summary => $"{Priority}. {Rule.ClickKey ?? "Display only"} · {(Rule.Caster == SpellCasterKind.Mine ? "My casts" : "Any caster")}" + (Rule.Target == SpellTargetKind.Either ? " · Shared NPC + Player" : "");
    public string Detail => $"{Rule.SpellId} · {(Rule.Target == SpellTargetKind.Npc ? "NPC target" : Rule.Target == SpellTargetKind.Player ? "Player target" : "NPC or player")} · {(Rule.Caster == SpellCasterKind.Mine ? "My casts" : "Anyone’s casts")} · {(Rule.ShowMissing ? "Show if missing" : "Only while present")} · {Rule.ClickKey ?? "Display only"}"
        + (Rule.Alternatives is { Length: > 0 } alternatives ? $" · {alternatives.Length} alternative{(alternatives.Length == 1 ? "" : "s")}" : "");
}
public partial class SpellOverlayManagerWindow : W.Window
{
    private static SpellOverlayManagerWindow? current;
    internal static SpellOverlayManagerWindow? CurrentManager => current;
    private readonly string path;
    private readonly bool review;
    private readonly IUserSettingsStore preferences;
    private readonly ObservableCollection<SpellOverlayDefinition> definitions = [];
    private byte[]? savedBytes;
    private SpellOverlayLibrary savedLibrary = new(1, []);
    private FileStream? runtimeLock;
    private SpellOverlayDefinition? selected;
    private bool loading, initialized, dirty, closed, runtimeInitialized;
    private DetectedClient[] detected = [];
    private IReadOnlyDictionary<int, string> spellNames = new Dictionary<int, string>();
    private SpellNames? spellSource;
    private SpellOverlayCatalog? spellCatalog;
    private readonly SpellIcons spellIcons = new();
    private bool resultsFromTarget;
    private readonly System.Windows.Threading.DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, SpellOverlayWindow> floating = [];
    private SpellOverlayDefinition[] applied = [];
    private bool polling;
    private int generation;
    private DateTime nextDiscovery;
    private int lookupGeneration;
    internal Func<Discovery> DiscoverCharacters = () => Identity.Discover(feature: MemoryFeature.SpellBarData);
    internal Func<int, long?, TargetCapture> CaptureTarget = (pid, started) => TargetBuffDecoder.CaptureTarget(pid, started, MemoryFeature.SpellBarData);
    internal Func<DetectedClient, GameFrame?> ReadClientFrame = AnchorPositions.ReadFrame;

    internal static void Open(bool restore = false)
    {
        if (!GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) { if (!restore) TeamBarApplication.Current?.ShowSpells(); return; }
        if (!restore && TeamBarApplication.Current is { } app) { app.ShowSpells(); return; }
        if (current is not null) { if (!restore) current.ReopenSettings(); else _ = current.InitializeRuntime(); return; }
        if (!restore && TryActivateExisting()) return;
        var manager = new SpellOverlayManagerWindow();
        if (restore) _ = manager.InitializeRuntime();
        else manager.Show();
    }
    internal SpellOverlayManagerWindow(string? storePath = null, bool reviewOnly = false, IUserSettingsStore? userSettings = null)
    {
        path = storePath ?? SpellOverlayStore.DefaultPath; review = reviewOnly;
        preferences = userSettings ?? (review ? new MemoryUserSettingsStore() : new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath));
        var saved = SpellOverlayStore.Load(path); savedBytes = saved.Bytes; savedLibrary = saved.Library;
        foreach (var definition in saved.Library.Overlays) definitions.Add(definition);
        if (!review)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try { runtimeLock = new(path + ".runtime.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new IOException("Spell overlays are already open in another Team Bar window. Use that window or close it first."); }
        }
        try { InitializeComponent(); Icon = AppBranding.WindowIcon; } catch { runtimeLock?.Dispose(); throw; }
        AppearanceTheme theme = AppearanceTheme.Light;
        try { theme = preferences.Load().Theme; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        ThemePalette.Apply(Resources, theme);
        TargetChoice.ItemsSource = new[] { "NPC target", "Player target", "NPC or player" };
        CasterChoice.ItemsSource = new[] { "My casts", "Anyone’s casts" };
        ClickKeyChoice.ItemsSource = new[] { "Display only" }.Concat(InputKeys.Names).ToArray();
        ClassChoice.ItemsSource = Identity.Classes;
        PlacementChoice.ItemsSource = new[] { "Follow character’s game window", "Free placement · stay where I put it" };
        SpellSourceChoice.ItemsSource = new[] { "Spell catalog", "Current target" }; SpellSourceChoice.SelectedIndex = 0;
        AddTargetChoice.ItemsSource = new[] { "NPC", "Player" }; AddTargetChoice.SelectedIndex = 0;
        OverlayList.ItemsSource = definitions; initialized = true;
        if (definitions.Count > 0) OverlayList.SelectedIndex = 0; else LoadEditor();
        InitializeAutoSave();
        pollTimer.Tick += async (_, _) => await Poll();
        Loaded += async (_, _) => await InitializeRuntime();
        if (!review) InitializeLifetime();
        Closing += (_, e) => HandleSettingsClosing(e, !review || activateSettings is not null, ConfirmDiscardChanges);
        Closed += (_, _) =>
        {
            closed = true; autoSaveTimer.Stop(); pollTimer.Stop(); StopOverlays(); ReleaseLifetime(); runtimeLock?.Dispose();
            if (ReferenceEquals(current, this)) current = null;
            TeamBarApplication.Current?.CheckIdle();
        };
    }
    private async Task InitializeRuntime()
    {
            if (review || runtimeInitialized || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) return;
            runtimeInitialized = true;
            try
            {
                if (savedLibrary.Version == 1)
                {
                    var upgraded = SpellOverlayMigration.Upgrade(savedLibrary);
                    if (savedBytes is not null) savedBytes = SpellOverlayMigration.Save(path, upgraded, savedBytes);
                    savedLibrary = upgraded; RestoreSavedDraft();
                    Status("Spell bars now belong to a character and class · existing spell settings preserved");
                }
                var source = W.PresentationSource.FromVisual(settingsContent ?? (System.Windows.Media.Visual)this);
                double scale = source?.CompositionTarget.TransformToDevice.M22 ?? 1;
                var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
                MinHeight = Math.Min(MinHeight, area.Height / scale - 20); MinWidth = Math.Min(MinWidth, area.Width / scale - 20);
                Height = Math.Min(Height, area.Height / scale - 20); Width = Math.Min(Width, area.Width / scale - 20);
                await RefreshCharacters();
                var names = await Task.Run(SpellNameCatalog.Read);
                if (closed || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) return;
                SetCatalog(names);
                SearchHint.Text = names.Error is null ? "Search spell names or an ID. Choose any rank; all ranks are tracked automatically."
                    : "Spell-name file unavailable. Use From current target.";
                if (definitions.Count == 0) AddOverlay(); else ApplySaved();
            }
            catch (Exception error) { if (!closed) Status("Could not open overlay session: " + error.Message, true); }
            finally { if (!closed && GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) pollTimer.Start(); }
    }
    private void Status(string message, bool error = false)
    { StatusLabel.Text = message; StatusLabel.SetResourceReference(C.TextBlock.ForegroundProperty, error ? "Error" : "Accent"); }
    private void Act(Action action) { try { action(); } catch (Exception e) { Status(e.Message, true); } }
    private void Dirty() { dirty = true; Status("Saving changes…"); ScheduleAutoSave(); }
    private void Replace(SpellOverlayDefinition value)
    {
        if (selected is null) return;
        int index = definitions.IndexOf(selected); selected = value;
        loading = true; definitions[index] = value; OverlayList.SelectedItem = value; loading = false; ApplyBarSettingsExpansion(); Dirty();
    }
    private void Overlay_Changed(object sender, C.SelectionChangedEventArgs e)
    { if (!initialized || loading) return; selected = OverlayList.SelectedItem as SpellOverlayDefinition; LoadEditor(); }
    private void LoadEditor()
    {
        ResetPickerForBar();
        loading = true; Editor.IsEnabled = selected is not null;
        OverlayName.Text = selected?.Name ?? ""; UpdateCharacterChoices();
        ClassChoice.SelectedItem = selected?.PersonaClass;
        OverlayEnabled.IsChecked = selected?.Enabled ?? true;
        PlacementChoice.SelectedIndex = selected?.FollowCharacterWindow == false ? 1 : 0;
        UpdatePlacementHint(); ApplyBarSettingsExpansion();
        loading = false; RefreshRules(); LoadDockEditor();
    }
    private void UpdateCharacterChoices()
    {
        CharacterChoice.ItemsSource = detected.Select(c => c.Character.Name).Concat(selected is null ? [] : new[] { selected.Character })
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        CharacterChoice.SelectedItem = selected?.Character;
    }
    private void Fields_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized || loading || selected is null) return;
        string? persona = ClassChoice.SelectedItem as string;
        string name = string.IsNullOrWhiteSpace(OverlayName.Text) ? "" : OverlayName.Text;
        if (ReferenceEquals(sender, ClassChoice) && name == selected.PersonaClass + " Bar" && persona is not null)
        { name = persona + " Bar"; loading = true; OverlayName.Text = name; loading = false; }
        Replace(selected with { Name = name, Character = CharacterChoice.SelectedItem as string ?? selected.Character,
            PersonaClass = persona, Enabled = OverlayEnabled.IsChecked == true });
        ResetPickerForBar();
    }
    private void RefreshRules(int index = 0)
    {
        string section = (WatchList.SelectedItem as SpellWatchChoice)?.Section ?? "NPC";
        refreshingRules = true;
        var choices = new List<SpellWatchChoice>();
        foreach (var kind in new[] { SpellTargetKind.Npc, SpellTargetKind.Player })
        {
            int priority = 0;
            foreach (var entry in (selected?.Spells ?? []).Select((rule, i) => (rule, i)).Where(e => InSection(e.rule, kind)))
                choices.Add(new(++priority, entry.rule, spellIcons.Get(entry.rule.IconId ?? spellSource?.Records?.GetValueOrDefault(entry.rule.SpellId)?.Icon),
                    entry.i, kind == SpellTargetKind.Npc ? "NPC" : "Player"));
        }
        var view = new System.Windows.Data.ListCollectionView(choices);
        var groups = new System.Windows.Data.PropertyGroupDescription(nameof(SpellWatchChoice.Section));
        groups.GroupNames.Add("NPC"); groups.GroupNames.Add("Player"); view.GroupDescriptions.Add(groups);
        WatchList.ItemsSource = view;
        SelectRule(selected?.Spells.Length > 0 ? Math.Clamp(index, 0, selected.Spells.Length - 1) : -1, section);
        refreshingRules = false;
        NoSpells.Visibility = selected?.Spells.Length > 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        LoadRule();
    }
    private void Watch_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        LoadRule();
        if (!refreshingRules && !loading && CurrentRuleIndex >= 0) ShowConfigure();
    }
    private void LoadRule()
    {
        bool previous = loading; loading = true;
        var rule = (WatchList.SelectedItem as SpellWatchChoice)?.Rule;
        SelectedSpellName.Text = rule?.Name ?? "Select a spell";
        SelectedSpellHint.Text = rule is null ? "Choose a spell from the list, or open Add spells."
            : $"{selected?.CharacterPersona} · {((WatchList.SelectedItem as SpellWatchChoice)?.Section)} section"
                + (rule.Target == SpellTargetKind.Either ? " · Settings shared with both sections" : "");
        SelectedSpellSettings.Visibility = rule is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        RuleEditor.IsEnabled = rule is not null;
        TargetChoice.SelectedIndex = (int)(rule?.Target ?? SpellTargetKind.Npc);
        CasterChoice.SelectedIndex = (int)(rule?.Caster ?? SpellCasterKind.Mine);
        ShowMissing.IsChecked = rule?.ShowMissing ?? true;
        ExpandTriggered.IsChecked = rule?.ExpandTriggered ?? true;
        SpellAlias.Text = rule?.Alias ?? "";
        ExpandTriggered.IsEnabled = rule is not null;
        ClickKeyChoice.IsEnabled = rule is not null;
        var clickKey = rule?.ClickKey is { } key ? SpellOverlayKey.Parse(key) : null;
        ClickKeyChoice.SelectedIndex = clickKey is null ? 0 : Array.IndexOf(InputKeys.Names, clickKey.Key) + 1;
        ClickModifierChoice.IsEnabled = clickKey is not null;
        ClickModifierChoice.Value = clickKey?.Modifiers ?? 0;
        ClickKeyHint.Text = $"One tap to {selected?.Character ?? "this character"}. Match the key to your in-game hotbar. Grouped effects use the parent spell’s key. Unlocking allows dragging without casting.";
        var plan = rule is { ExpandTriggered: true } ? spellCatalog?.Resolve(rule.SpellId) : null;
        TriggeredHint.Text = rule?.Alternatives is { Length: > 0 } ? rule.ExpandTriggered
            ? "For cast-together spells, all required effects must be present for that option to count."
            : "Any one listed spell counts as present. All ranks count automatically."
            : plan?.Error is { } problem ? problem + ". Automatic missing alerts are suspended for this spell."
            : plan?.Children.Length > 0 ? "Tracks separately: " + string.Join(" · ", plan.Children.Select(c => c.Name))
                + ". Any rank counts; each effect uses the caster setting above. Chance-based bonuses are excluded."
            : "Any rank counts automatically. Supported cast-together spells show their effects underneath.";
        LoadAlternatives(rule);
        loading = previous;
    }
    private void SetCatalog(SpellNames names)
    {
        if (ReferenceEquals(spellSource, names)) return;
        spellSource = names; spellNames = names.Names; spellCatalog = new(names);
        RefreshRules(CurrentRuleIndex);
        int? selectedId = (SearchResults.SelectedItem as SpellSearchChoice)?.Id;
        if (resultsFromTarget) RefreshSearchResults();
        else if (!string.IsNullOrWhiteSpace(SpellSearch.Text)) Search_Changed(SpellSearch, null!);
        if (selectedId is not null)
            SearchResults.SelectedItem = SearchResults.Items.OfType<SpellSearchChoice>().FirstOrDefault(choice => choice.Id == selectedId);
    }
    private SpellSearchChoice SearchChoice(int id, string name, int? icon)
    {
        int? resolvedIcon = icon ?? spellSource?.Records?.GetValueOrDefault(id)?.Icon;
        return new(id, name, resolvedIcon, spellIcons.Get(resolvedIcon));
    }
    private void Rule_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized || loading || selected is null || CurrentRuleIndex < 0 || TargetChoice.SelectedIndex < 0 || CasterChoice.SelectedIndex < 0) return;
        int index = CurrentRuleIndex; var rules = selected.Spells.ToArray();
        rules[index] = rules[index] with { Target = (SpellTargetKind)TargetChoice.SelectedIndex, Caster = (SpellCasterKind)CasterChoice.SelectedIndex,
            ShowMissing = ShowMissing.IsChecked == true, ExpandTriggered = ExpandTriggered.IsChecked == true };
        Replace(selected with { Spells = rules }); RefreshRules(index);
    }
    private void AddOverlay()
    {
        if (definitions.Count >= SpellOverlayStore.MaxOverlays) throw new InvalidOperationException($"Use up to {SpellOverlayStore.MaxOverlays} spell bars.");
        var client = detected.FirstOrDefault(c => c.Character.Name == selected?.Character) ?? detected.FirstOrDefault();
        string? persona = client?.Character.Class ?? selected?.PersonaClass;
        var definition = new SpellOverlayDefinition(Guid.NewGuid().ToString("N"), persona is null ? "Target spells" : persona + " Bar",
            client?.Character.Name ?? selected?.Character ?? "", [], PersonaClass: persona);
        expandedBarSettings.Add(definition.Id);
        definitions.Add(definition); OverlayList.SelectedItem = definition; Dirty();
    }
    private void ClickKey_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized || loading || selected is null || CurrentRuleIndex < 0 || ClickKeyChoice.SelectedIndex < 0) return;
        int index = CurrentRuleIndex; var rules = selected.Spells.ToArray();
        rules[index] = rules[index] with { ClickKey = ClickKeyChoice.SelectedIndex == 0 ? null
            : SpellOverlayKey.Format(InputKeys.Names[ClickKeyChoice.SelectedIndex - 1], Math.Max(0, ClickModifierChoice.Value)) };
        Replace(selected with { Spells = rules }); RefreshRules(index);
    }
    private void New_Click(object sender, W.RoutedEventArgs e) => Act(AddOverlay);
    internal Func<W.Window, DeleteRequest, bool> ConfirmOverlayDeletion = DeleteConfirmation.Ask;
    private void Delete_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        e.Handled = true;
        if ((sender as W.FrameworkElement)?.DataContext is not SpellOverlayDefinition clicked) return;
        var owner = W.Window.GetWindow((W.DependencyObject)sender) ?? this;
        if (!ConfirmOverlayDeletion(owner, new("Delete spell overlay?", clicked.EditorName,
            "This removes the spell overlay and its docked groups. Saved buttons remain in your library."))) return;
        var current = definitions.FirstOrDefault(d => d.Id == clicked.Id);
        if (current is null) return;
        int index = definitions.IndexOf(current); bool selectedRemoved = selected?.Id == current.Id;
        definitions.Remove(current); expandedBarSettings.Remove(current.Id);
        if (selectedRemoved) OverlayList.SelectedIndex = Math.Min(index, definitions.Count - 1);
        selected = OverlayList.SelectedItem as SpellOverlayDefinition;
        LoadEditor(); Dirty();
    });
    private void AddSpell_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (selected is null || SearchResults.SelectedItem is not SpellSearchChoice choice) return;
        var targetKind = AddTargetChoice.SelectedIndex == 1 ? SpellTargetKind.Player : SpellTargetKind.Npc;
        int existing = Array.FindIndex(selected.Spells, spell => InSection(spell, targetKind) && (spell.SpellId == choice.Id
            || spellCatalog?.SameRankFamily(spell.SpellId, choice.Id) == true));
        if (existing >= 0)
        {
            SelectRule(existing, targetKind == SpellTargetKind.Npc ? "NPC" : "Player"); ShowConfigure();
            Status("That spell or another rank is already listed in this section.");
            return;
        }
        if (selected.Spells.Length >= 24) throw new InvalidOperationException("Use up to 24 spells per overlay.");
        Replace(selected with { Spells = [.. selected.Spells, new(choice.Id, choice.Name, targetKind, IconId: choice.Icon)] });
        RefreshRules(selected!.Spells.Length - 1);
    });
    private void Move(int offset)
    {
        if (selected is null || WatchList.SelectedItem is not SpellWatchChoice source) return;
        var section = WatchList.Items.OfType<SpellWatchChoice>().Where(c => c.Section == source.Section).ToArray();
        int position = Array.IndexOf(section, source), next = position + offset;
        if (next >= 0 && next < section.Length) MoveRule(source, section[next], offset > 0);
    }
    private void RemoveSpell_Click(object sender, W.RoutedEventArgs e)
    {
        if (e.RoutedEvent is not null) e.Handled = true;
        var choice = (sender as W.FrameworkElement)?.Tag as SpellWatchChoice ?? WatchList.SelectedItem as SpellWatchChoice;
        if (!CurrentChoice(choice)) return;
        int currentIndex = CurrentRuleIndex, removed = choice!.Index;
        Replace(selected! with { Spells = selected.Spells.Where((_, i) => i != removed).ToArray() });
        RefreshRules(currentIndex > removed ? currentIndex - 1 : currentIndex == removed ? removed : currentIndex);
    }
    private async Task RefreshCharacters()
    {
        if (review || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) return;
        try
        {
            var found = await Task.Run(DiscoverCharacters); if (closed || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) return;
            bool changedRoster = !SameDiscoveredRoster(detected, found.Clients);
            detected = found.Clients; nextDiscovery = DateTime.UtcNow.AddSeconds(5);
            await RefreshDockLibrary();
            if (changedRoster) RequestInputRosterRefresh();
            loading = true; UpdateCharacterChoices(); loading = false;
            if (detected.Length == 0) Status("Waiting for a character to finish loading in EverQuest.");
        }
        catch (Exception error) { if (!closed) Status(error.Message, true); }
    }
    private async void Refresh_Click(object sender, W.RoutedEventArgs e) => await RefreshCharacters();
    private void Save_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (review) { Status("Preview only · no settings saved"); return; }
        int version = savedLibrary.Version >= 3 || definitions.Any(d => d.DockGroups?.Length > 0 || d.Spells.Any(s => s.Alias is not null)) ? 3 : 2;
        var library = new SpellOverlayLibrary(version, definitions.ToArray());
        savedBytes = SpellOverlayStore.Save(path, library, savedBytes); savedLibrary = library; dirty = false;
        ApplySaved(); Status("Saved · overlays stay open when you close these settings");
    });
    private void StopOverlays()
    { generation++; applied = []; clickRegistrations.Clear(); clickSession = null; nextClickCheck = DateTime.MinValue; foreach (var window in floating.Values) window.Close(); floating.Clear(); UpdateOverlayControls(); }
    private void UpdateOverlayControls()
    {
        bool loaded = applied.Length > 0;
        HideOverlaysControl.Visibility = loaded ? W.Visibility.Visible : W.Visibility.Collapsed;
        SaveButton.Visibility = loaded ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
    private void ApplySaved()
    {
        if (review || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) return;
        overlaysHidden = false;
        var appearance = FloatingThemes.ForApplication(preferences.Load());
        StopOverlays();
        ResetHostRetry(); hostConnectionMessage = null;
        ownedHostNeedsRestart = ownedClickHost is not null;
        applied = savedLibrary.Overlays.Where(d => d.Enabled).ToArray();
        UpdateOverlayControls();
        foreach (var definition in applied) floating.Add(definition.Id, CreateFloatingOverlay(appearance.Selected));
        RememberLoaded(applied.Length > 0);
        _ = RefreshClickSession();
        _ = Poll();
    }
    private SpellOverlayDefinition SaveFloatingLayout(SpellOverlayDefinition original, SpellOverlayLayoutChange change)
    {
        try
        {
            var baseline = savedLibrary.Overlays.SingleOrDefault(d => d.Id == original.Id);
            if (baseline is null || baseline.Character != original.Character) throw new InvalidDataException("This overlay changed. Show overlays to reopen it.");
            if (baseline.FollowCharacterWindow != original.FollowCharacterWindow) throw new InvalidDataException("The placement mode changed. Try moving the overlay again.");
            if (baseline.Locked && (change.Scale is not null || change.Position is not null || change.GroupScale is not null || change.FreePosition is not null))
                throw new InvalidDataException("Unlock the overlay before moving or resizing it.");
            var updated = change.Apply(baseline);
            if (change.ButtonMove is not null && ReferenceEquals(updated, baseline)) return baseline;
            int index = definitions.ToList().FindIndex(d => d.Id == updated.Id);
            if (index < 0 && (change.GroupName is not null || change.ButtonMove is not null))
                throw new InvalidDataException("This overlay was removed in Settings.");
            // Check the draft before writing so a pending edit cannot be overwritten.
            var draft = index < 0 ? null : change.Apply(definitions[index], validate: false);
            var library = savedLibrary with { Version = change.ButtonMove is null ? savedLibrary.Version : 3,
                Overlays = savedLibrary.Overlays.Select(d => d.Id == updated.Id ? updated : d).ToArray() };
            savedBytes = SpellOverlayStore.Save(path, library, savedBytes); savedLibrary = library;
            generation++; // Discard a reading that captured the previous placement before the gesture completed.
            applied = applied.Select(d => d.Id == updated.Id ? updated : d).ToArray();
            if (draft is not null)
            {
                loading = true; definitions[index] = draft;
                if (selected?.Id == draft.Id) { selected = draft; OverlayList.SelectedItem = draft; }
                loading = false;
                if (selected?.Id == draft.Id && (change.GroupName is not null || change.ButtonMove is not null))
                {
                    RefreshDockChoices(change.GroupId);
                    if (change.ButtonMove is { } move && (dockSelection == move.SourceGroup.Id || dockSelection == move.TargetGroup.Id))
                        FillDockOrder();
                }
            }
            string saved = change.ButtonMove is not null ? "Button moved" :
                change.GroupName is not null ? "Button group renamed" : "Layout saved · position, size and lock are remembered";
            Status(dirty ? "Layout saved · other editor changes are still unsaved" : saved);
            return updated;
        }
        catch (Exception error) { Status("Layout not saved: " + error.Message, true); throw; }
    }
    private void RememberLoaded(bool loaded)
    {
        if (preferences.Load().SpellOverlaysLoaded != loaded) preferences.SaveSpellOverlaysLoaded(loaded);
    }
    private void Hide_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        FlushAutoSave(); overlaysHidden = true; RememberLoaded(false); StopOverlays(); Status("Overlays hidden · Show overlays to reopen them");
    });
    private async Task Poll()
    {
        if (polling || closed || review || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData) || applied.Length == 0) return;
        polling = true; int version = generation;
        try
        {
            if (DateTime.UtcNow >= nextDiscovery) await RefreshCharacters();
            var names = await Task.Run(SpellNameCatalog.Read);
            if (closed || version != generation) return;
            SetCatalog(names);
            var clients = detected; var definitionsToRead = applied;
            var readings = await Task.Run(() => definitionsToRead.Select(d => d.Character).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(name => name, name =>
            {
                var matches = clients.Where(c => c.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) return (Client: (DetectedClient?)null, Capture: (TargetCapture?)null, Error: matches.Length == 0 ? "Character not loaded" : "More than one matching character");
                try { return (Client: (DetectedClient?)matches[0], Capture: (TargetCapture?)CaptureTarget(matches[0].ProcessId, matches[0].StartedUtcTicks), Error: (string?)null); }
                catch (Exception e) { return (Client: (DetectedClient?)matches[0], Capture: (TargetCapture?)null, Error: (string?)e.Message); }
            }, StringComparer.OrdinalIgnoreCase));
            if (closed || version != generation) return;
            var overlayTheme = FloatingThemes.ForApplication(preferences.Load()).Selected;
            foreach (var definition in definitionsToRead)
            {
                var reading = readings[definition.Character]; var window = floating[definition.Id];
                var dockClients = detected.Select(c => reading.Capture is { } current && c.ProcessId == current.ProcessId
                    ? c with { Character = c.Character with { ClassId = Array.FindIndex(Identity.Classes, name => name == current.CharacterClass) + 1 } } : c).ToArray();
                window.SetDockButtons(savedDockButtons, DockInputClients(dockClients), dockLibraryError);
                var state = SpellOverlayRules.Evaluate(definition, reading.Capture, reading.Error, spellCatalog);
                window.SetClickContext(reading.Error is null ? reading.Capture : null);
                window.SetTheme(overlayTheme);
                window.Render(definition, state);
                // Never follow a different character if this process changed characters between discovery and reading.
                bool sameCharacter = reading.Capture is null || reading.Capture.CharacterName.Equals(definition.Character, StringComparison.OrdinalIgnoreCase);
                bool samePersona = definition.MatchesPersona(reading.Capture?.CharacterClass ?? reading.Client?.Character.Class);
                window.Place(definition, sameCharacter && samePersona && reading.Client is not null ? ReadClientFrame(reading.Client) : null);
            }
            // Input startup can wait for a game to resume polling. Target and
            // timer updates must continue during that wait.
            _ = RefreshClickSession();
        }
        catch (Exception error)
        {
            if (!closed && version == generation)
            { foreach (var window in floating.Values) window.Hide(); Status("Overlay reading unavailable: " + error.Message, true); }
        }
        finally { polling = false; }
    }
}
