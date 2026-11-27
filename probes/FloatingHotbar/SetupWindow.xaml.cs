using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace TeamBar;

public partial class SetupWindow : W.Window
{
    private TeamDefinition team;
    private readonly string profilePath, controllerPath;
    private readonly Func<Discovery> discover;
    private readonly ObservableCollection<ButtonDraft> drafts = [];
    private readonly ObservableCollection<ClientOption> clients = [];
    private readonly Dictionary<string, bool> clientSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<RuleRow> rows = [];
    public System.Collections.IEnumerable RecipientChoices => choices;
    public string[] SupportedKeys { get; } = InputKeys.Names;
    public string[] SupportedModifiers { get; } = SpellOverlayKey.ModifierNames;
    private readonly ObservableCollection<RecipientChoice> choices = [];
    private bool loading, changed, applying, initialized, refreshing, closed;
    private readonly bool review;
    private ButtonDraft? selected;
    private byte[]? savedBytes;
    private Func<string, Task<bool>> clearHotkeys = AppInputSession.Clear;
    private bool HasButtonDrafts => groupDrafts.Count > 0 || standaloneDrafts.Count > 0;

    internal SetupWindow(string profile, string controller, Func<Discovery>? discovery = null, bool reviewOnly = false,
        IUserSettingsStore? userSettings = null, Func<GameSession[]>? windowProvider = null,
        Func<GameSession, bool>? requiresWindowedMode = null)
    {
        profilePath = Path.GetFullPath(profile); controllerPath = Path.GetFullPath(controller);
        discover = discovery ?? ScanClients; review = reviewOnly;
        inputWindows = windowProvider ?? ClientSessions.ListWindows;
        bool layoutUsesInputFolder = windowProvider is not null || ScreenInstallation.Same(ScreenInstallation.Current, ScreenInstallation.Original);
        layoutWindows = layoutUsesInputFolder ? inputWindows : ClientSessions.ListScreenWindows;
        layoutRoster = layoutUsesInputFolder ? discover : () => new Discovery([], []);
        this.requiresWindowedMode = requiresWindowedMode ?? (session =>
            GameViewWindows.SameOwner((nint)session.Window, session.ProcessId) &&
            GameViewPlacements.RequiresWindowedMode((nint)session.Window));
        if (!review) ButtonGroups.Migrate(profilePath);
        team = TeamEditing.Load(File.Exists(profilePath) ? profilePath : Path.Combine(AppContext.BaseDirectory, "team.json"));
        savedBytes = File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : null;
        foreach (var b in team.Buttons) drafts.Add(new(b));
        foreach (var group in team.Groups ?? []) groupDrafts.Add(group);
        LoadStandaloneLibrary();
        InitializeComponent();
        InitializeInlineEditor();
        Icon = AppBranding.WindowIcon;
        ApplicationLifetimeFooter.Visibility = TeamBarApplication.Current is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        InitializePreferences(userSettings);
        Title = $"{BuildEdition.Label} · {(review ? "Design review" : "Configuration")}";
        EditionTitle.Text = BuildEdition.Label;
        ButtonList.ItemsSource = standaloneDrafts; TeamCards.ItemsSource = clients; RuleList.ItemsSource = rows;
        PlacementCharacter.ItemsSource = anchorChoices; SavedScreenList.ItemsSource = screenPlacements;
        initialized = true; ButtonList.SelectedIndex = 0; LoadButton();
        SourceInitialized += (_, _) => UpdateTitleBarTheme();
        Loaded += async (_, _) =>
        {
            // Fit the current work area in device-independent units, while allowing the editor body to scroll.
            var source = W.PresentationSource.FromVisual(this);
            double scale = source?.CompositionTarget.TransformToDevice.M22 ?? 1;
            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
            double availableHeight = screen.Height / scale - 20, availableWidth = screen.Width / scale - 20;
            MinHeight = Math.Min(MinHeight, availableHeight); MinWidth = Math.Min(MinWidth, availableWidth);
            Height = Math.Min(Height, availableHeight); Width = Math.Min(Width, availableWidth);
            await RefreshClients();
        };
        SizeChanged += (_, _) => SidebarColumn.Width = new W.GridLength(ActualWidth < 1050 ? 200 : 240);
        InitializeAutoSave();
        RestoreManualAssignments();
        if (!review && (discovery is null || windowProvider is not null)) MonitorOpenWindows();
        Closing += OnClosing;
        Closed += (_, _) => closed = true;
    }
    private void Status(string message, bool error = false)
    {
        StatusText.Text = message; StatusText.SetResourceReference(C.TextBlock.ForegroundProperty, error ? "Error" : "Muted");
    }
    private void Act(Action action)
    {
        try { action(); } catch (Exception error) { Status(error.Message, true); }
    }
    private void MarkChanged()
    {
        if (IsStandalone && selected is not null) selected.Dirty = true; else changed = true;
        SaveState.Text = review ? "REVIEW COPY · Changes are not saved" : "Saving changes…";
        ScheduleAutoSave();
        SaveState.SetResourceReference(C.TextBlock.ForegroundProperty, "Unsaved"); UpdatePreview();
    }
    private TeamDefinition CurrentTeam() => team with { Buttons = [], Groups = groupDrafts.Count == 0 ? null : groupDrafts.ToArray() };
    private DetectedClient[] SelectedClients() => clients.Where(c => c.Included).Select(c => c.Client).ToArray();
    private void CaptureRules()
    {
        if (loading || selected is null) return;
        selected.Rules = rows.Select(r => new ButtonRule(r.Target.Target, r.Chord)).ToArray();
    }
    private void LoadButton()
    {
        if (!initialized) return;
        UpdateLibraryControls();
        if (!IsStandalone) { LoadGroupEditor(); UpdatePreview(); return; }
        if (selected is null) { UpdatePreview(); return; }
        loading = true;
        try
        {
            NameField.Text = selected.Label;
            var selectors = team.Characters.Select(c => new TargetSelector(CharacterId: c.Id))
                .Concat(team.Personas.Where(p => p.Class is not null).Select(p => new TargetSelector(PersonaId: p.Id))).ToList();
            selectors.AddRange(Identity.Classes.Select(c => new TargetSelector(Class: c)));
            selectors.AddRange(selected.Rules.Select(r => r.Target));
            rows.Clear(); choices.Clear();
            foreach (var target in selectors.DistinctBy(TeamEditing.SelectorKey))
            {
                string label = TeamEditing.SelectorLabel(team, target);
                string detail = target.PersonaId is not null ? "Only while this persona is active" : target.CharacterId is not null ? target.Class is null ? "Any active persona" : "Only while this class is active" : "Class default · all matching characters";
                choices.Add(new(TeamEditing.SelectorKey(target), label, detail, target));
            }
            foreach (var rule in selected.Rules) AddRow(choices.Single(c => c.Id == TeamEditing.SelectorKey(rule.Target)), rule.Key);
            FloatButton.IsChecked = selected.Floating;
        }
        finally { loading = false; }
        UpdatePreview();
    }
    private void AddRow(RecipientChoice target, string key)
    {
        var row = new RuleRow(target, key);
        row.PropertyChanged += (_, change) =>
        {
            if (loading || change.PropertyName is not (nameof(RuleRow.Target) or nameof(RuleRow.Key) or nameof(RuleRow.Modifiers))) return;
            CaptureRules(); MarkChanged();
        };
        rows.Add(row);
    }
    private async Task RefreshClients(bool background = false)
    {
        if (refreshing) return; refreshing = true;
        if (!background)
        {
            ApplyControl.IsEnabled = SaveControl.IsEnabled = false;
            Status("Finding your characters…");
        }
        bool updateControls = !background;
        try
        {
            var scan = await Task.Run(discover); if (closed) return;
            if (!review && !GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection)) scan = new(scan.Clients.Where(c => c.UserAssigned).ToArray(), []);
            if (background && (applying || (scan.Clients.Length == clients.Count &&
                scan.Clients.All(f => clients.Any(c => c.Client.UserAssigned == f.UserAssigned && Identity.Same(c.Client, f)))))) return;
            updateControls = true;
            RememberClientSelections();
            // A character keeps its selection across logout, missing scans and a new process.
            team = TeamEditing.MergeDetected(CurrentTeam(), scan.Clients); clients.Clear();
            foreach (var client in scan.Clients)
            {
                var option = new ClientOption(client, clientSelections.GetValueOrDefault(client.Character.Name, true));
                option.PropertyChanged += (_, _) => UpdatePreview(); clients.Add(option);
            }
            if (ScreensView.IsVisible) RefreshScreenWindowedWarning();
            NoClients.Visibility = clients.Count == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
            Status(scan.Errors.Length > 0 ? string.Join(" ", scan.Errors) + " You can assign windows yourself."
                : scan.Clients.Any(c => c.UserAssigned) ? "Using your assignments. Assign again before clicking after character or persona switches."
                : "Edits save automatically. Connect hot keys when you want to show your floating buttons.", scan.Errors.Length > 0);
            LoadButton();
            if (PlacementView.Visibility == W.Visibility.Visible) LoadPlacement();
        }
        catch (Exception error) { if (!closed) Status(error.Message, true); }
        finally { refreshing = false; if (!closed && updateControls) { ApplyMemoryPreference(); UpdatePreview(); } }
    }
    private void RememberClientSelections()
    {
        foreach (var group in clients.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            clientSelections[group.Key] = group.All(c => c.Included);
    }
    private void ValidateDraft(TeamDefinition current, ClientBinding[] bindings)
    {
        foreach (var button in current.Buttons)
        {
            if (button.Rules.Length == 0) throw new InvalidDataException(string.IsNullOrWhiteSpace(button.Label)
                ? "Add a recipient to the unnamed button." : $"Add a recipient to “{button.Label}”.");
        }
        Routing.Validate(current, bindings, requireClients: false);
    }
    private void UpdatePreview()
    {
        if (!initialized || loading) return;
        UpdateRosterCaption();
        RefreshMappingConflicts();
        if (IsStandalone) UpdateStandalonePreview(); else UpdateGroupPreview();
        RefreshSaveAndConnectionState();
    }
    private void CheckFileUnchanged()
    {
        byte[]? current = File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : null;
        if (savedBytes is null ? current is not null : current is null || !savedBytes.SequenceEqual(current))
            throw new IOException("The profile was changed in another editor. Reopen this window to load those changes before saving.");
    }
    private LiveSetup Save(bool includeClients = true)
    {
        if (review) throw new InvalidOperationException("This review copy cannot save or control the bar.");
        CheckFileUnchanged();
        var current = CurrentTeam(); var selectedClients = includeClients ? SelectedClients() : []; var bindings = TeamEditing.Bind(current, selectedClients);
        ValidateDraft(current, bindings); savedBytes = TeamEditing.SaveChecked(profilePath, current, bindings, savedBytes);
        team = current; changed = false;
        RememberClientSelections();
        SaveState.Text = "All changes saved"; SaveState.SetResourceReference(C.TextBlock.ForegroundProperty, "Success");
        Status("Saved. Connected buttons update automatically.");
        return new(controllerPath, current, bindings, selectedClients)
        {
            ExcludedCharacterNames = clientSelections.Where(c => !c.Value).Select(c => c.Key).ToArray()
        };
    }
    private async Task OpenBar()
    {
        if (review) return;
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        using var applyLock = new FileStream(profilePath + ".apply.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckFileUnchanged();
        // An intentional empty library must clear old hotkeys without starting a
        // controller or disconnecting spell overlays that share the current host.
        // Recheck disk so a new standalone saved by another editor is not erased.
        if (!HasButtonDrafts && StandaloneButtons.Load(profilePath).Length == 0)
        {
            Save(includeClients: false);
            await clearHotkeys(profilePath);
            Status("Applied. Hotkey buttons are cleared. Spell overlays remain available.");
            return;
        }
        if (!File.Exists(controllerPath)) throw new FileNotFoundException("The input controller is missing. Restore the app build before applying.");
        CheckFileUnchanged(); ValidateDraft(CurrentTeam(), TeamEditing.Bind(CurrentTeam(), SelectedClients()));
        Routing.Validate(CurrentTeam(), TeamEditing.Bind(CurrentTeam(), SelectedClients()));
        if (standaloneDrafts.Any(d => d.Dirty && (!IsStandalone || d != selected))) throw new InvalidOperationException("Complete or remove the unfinished button edits before connecting.");
        if (IsStandalone && selected is not null) ValidateStandaloneDraft(selected);
        foreach (var d in standaloneDrafts) StandaloneButtons.CheckUnchanged(StandaloneButtons.PathFor(profilePath, d.Id), d.SavedBytes);
        var old = SelectedClients();
        await VerifySelectedPersonas(old);
        // Keep the overlay manager from starting a replacement spell-only host
        // while this saved profile takes over the shared input connection.
        using var transition = await BeginInputTransition();
        AppInputSession.Stop();
        await BarSession.CloseExisting(profilePath);
        if (!string.Equals(profilePath, SpellOverlayInputHost.ProfilePath, StringComparison.OrdinalIgnoreCase))
            await BarSession.CloseExisting(SpellOverlayInputHost.ProfilePath);
        foreach (var process in Process.GetProcessesByName("TeamController"))
            using (process) { throw new InvalidOperationException("An older bar or another team's bar is running. Close that bar once, then apply again."); }
        await VerifySelectedPersonas(old);
        LiveSetup setup;
        if (IsStandalone)
        {
            SaveStandalone();
            setup = Save(); // Persist refreshed identity metadata; standalone mappings stay in their own files.
        }
        else setup = Save();
        string logs = Path.Combine(Path.GetDirectoryName(profilePath)!, "sessions"); Directory.CreateDirectory(logs);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), bindings = Path.Combine(logs, "clients-" + stamp + ".json"), log = Path.Combine(logs, "bar-" + stamp + ".jsonl");
        File.WriteAllText(bindings, JsonSerializer.Serialize(new ClientFile(2, setup.Clients, setup.Identities), Program.Json));
        await AppInputSession.Start(setup, StandaloneButtons.Load(profilePath), profilePath, log);
        Status("Applied. Your buttons are ready for clicks.");
    }
    private static async Task<FileStream> BeginInputTransition()
    {
        long until = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < until)
        {
            if (SpellOverlayInputHost.TryBeginTransition() is { } transition) return transition;
            await Task.Delay(150);
        }
        throw new IOException("Another input session is still starting. Wait a moment, then apply again.");
    }
    private void ButtonList_SelectionChanged(object sender, C.SelectionChangedEventArgs e)
    {
        if (!initialized || loading || loadingGroup || filteringLibrary) return;
        selectedGroup = ButtonList.SelectedItem as ButtonGroup;
        selected = ButtonList.SelectedItem as ButtonDraft;
        if (selected is not null) lastButtonId = selected.Id;
        if (selectedGroup is not null) lastGroupId = selectedGroup.Id;
        LoadButton();
        if (ButtonList.SelectedItem is { } chosen) ButtonList.ScrollIntoView(chosen);
    }
    private void NameField_TextChanged(object sender, C.TextChangedEventArgs e)
    {
        if (!initialized || loading || selected is null) return;
        selected.Label = string.IsNullOrWhiteSpace(NameField.Text) ? "" : NameField.Text; MarkChanged();
    }
    private async void Refresh_Click(object sender, W.RoutedEventArgs e) => await RefreshClients();
    private void AddButton_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (!IsStandalone) { AddGroup(); return; }
        LibrarySearch.Clear();
        var draft = new ButtonDraft(new("button-" + Guid.NewGuid().ToString("N")[..8], "", []));
        if (IsStandalone) { draft.Document = DocumentFor(draft); draft.Dirty = true; }
        ActiveDrafts.Add(draft);
        ButtonList.SelectedItem = draft; MarkChanged(); NameField.Focus(); NameField.SelectAll();
    });
    private void RemoveSelectedLibraryItem() => Act(() =>
    {
        if (!IsStandalone) { RemoveGroup(); return; }
        if (selected is null) return;
        var collection = ActiveDrafts; int index = ButtonList.SelectedIndex;
        if (IsStandalone && !review) StandaloneButtons.Remove(StandaloneButtons.PathFor(profilePath, selected.Id), selected.SavedBytes);
        collection.Remove(selected); ButtonList.SelectedIndex = Math.Min(index, ButtonList.Items.Count - 1); selected = ButtonList.SelectedItem as ButtonDraft;
        if (!IsStandalone) MarkChanged();
        libraryChanged = true; ScheduleAutoSave();
        LoadButton();
        if (!HasButtonDrafts) Status("No hotkey buttons remain. Floating hotkeys clear automatically.");
        else if (IsStandalone) Status("Button removed. Connected controls update automatically.");
    });
    private void Move(int delta)
    {
        if (selected is null) return;
        int from = drafts.IndexOf(selected), to = from + delta; if (to < 0 || to >= drafts.Count) return;
        var item = selected; drafts.Move(from, to); ButtonList.SelectedItem = item; LoadButton(); MarkChanged();
    }
    private void MoveUp_Click(object sender, W.RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, W.RoutedEventArgs e) => Move(1);
    private void AddRecipient_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (choices.Count == 0 || rows.Count >= 24) return;
        var used = rows.Select(r => r.Target.Id).ToHashSet();
        var choice = choices.FirstOrDefault(c => c.Target.CharacterId is not null && c.Target.Class is null && !used.Contains(c.Id)) ?? choices[0];
        AddRow(choice, "5"); CaptureRules(); MarkChanged();
    });
    private void RemoveRule_Click(object sender, W.RoutedEventArgs e)
    {
        if (sender is C.Button { DataContext: RuleRow row }) { rows.Remove(row); CaptureRules(); MarkChanged(); }
    }
    private void Save_Click(object sender, W.RoutedEventArgs e) => Act(() => { if (IsStandalone) SaveStandalone(); else Save(); });
    private async void Apply_Click(object sender, W.RoutedEventArgs e)
    {
        applying = true; ReviewRoot.IsEnabled = false; Status("Applying your button changes…");
        try { await OpenBar(); } catch (Exception error) { Status(error.Message, true); }
        finally { applying = false; ReviewRoot.IsEnabled = true; UpdatePreview(); }
    }
    private void SaveWindows_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    { if (review) return; WindowLayouts.SaveGames(profilePath, SelectedClients()); Status("Window positions saved for your selected characters."); });
    private void RestoreWindows_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    { if (review) return; WindowLayouts.RestoreGames(profilePath, SelectedClients()); Status("Your selected game windows are back in their saved positions."); });
    private async void ExitTeamBar_Click(object sender, W.RoutedEventArgs e)
    {
        if (TeamBarApplication.Current is not { } app) return;
        ExitTeamBarButton.IsEnabled = false;
        try { await app.Exit(); }
        finally { if (!closed) ExitTeamBarButton.IsEnabled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (applying) { e.Cancel = true; return; }
        FlushAutoSave(); spellEditor?.FlushAutoSave();
        if (TeamBarApplication.Current is not null && !closingApplication) { e.Cancel = true; Hide(); return; }
        if (closingApplication && spellEditor is not null && !spellEditor.PrepareSettingsExit()) { e.Cancel = true; return; }
        if ((changed || standaloneDrafts.Any(d => d.Dirty)) && !review && W.MessageBox.Show(this, "Some incomplete changes could not be saved. Exit and discard those changes?", "Unsaved changes", W.MessageBoxButton.YesNo, W.MessageBoxImage.Question) != W.MessageBoxResult.Yes) e.Cancel = true;
    }
#if !TEAMBAR_PUBLIC_RELEASE
    private void Settle() { UpdateLayout(); Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); }
    internal void SelfTest(Action<DetectedClient[]> replaceDiscovery)
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        Show();
        var until = Environment.TickCount64 + 10000;
        while ((refreshing || clients.Count == 0) && Environment.TickCount64 < until) { Settle(); Thread.Sleep(5); }
        Check(clients.Count == 2 && clients.All(c => c.Included), "Modern editor discovery failed.");
        AssignmentSelfTest();
        KeyPickerSelfTest(Check);
        Check(drafts.Count == 0 && standaloneDrafts.Count == 2 && groupDrafts.Single().Buttons.Length == 2,
            "Legacy bar did not migrate to shared button references.");
        string id = selected!.Id;
        NameField.Text = "Edited action";
        Check(selected.Id == id && selected.Label == "Edited action", "Unified editor lost a button edit.");
        string beforeThemeChange = JsonSerializer.Serialize(CurrentTeam());
        ShowGeneralSettings(true); LightThemeChoice.IsChecked = true; Settle();
        Check(settings.Theme == AppearanceTheme.Light && settingsStore.Load().Theme == AppearanceTheme.Light, "Light mode did not persist.");
        Check(((M.SolidColorBrush)ReviewRoot.Background).Color == (M.Color)M.ColorConverter.ConvertFromString("#F3F6F8"), "Light resources did not reach the rendered window.");
        DarkThemeChoice.IsChecked = true; Settle(); ShowGeneralSettings(false);
        Check(settings.Theme == AppearanceTheme.Dark && settingsStore.Load().Theme == AppearanceTheme.Dark, "Dark mode did not persist.");
        Check(selected!.Label == "Edited action" && !selected.Dirty
            && StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == id).Value.Label == "Edited action"
            && beforeThemeChange == JsonSerializer.Serialize(CurrentTeam()), "Settings navigation must retain and autosave the button edit.");
        var workingStore = settingsStore; settingsStore = new UserSettingsTests.FailingStore(settings);
        LightThemeChoice.IsChecked = true; Settle();
        Check(settings.Theme == AppearanceTheme.Dark && DarkThemeChoice.IsChecked == true && SettingsState.Text == "Could not save appearance", "A failed save must retain the last saved theme and report the failure.");
        settingsStore = workingStore;
        FloatingSettingsSelfTest();
        Check(choices.Any(c => c.Target.Class == "Shaman" && c.Target.CharacterId is null), "Class defaults must be available without switching editor modes.");
        AddRow(choices.Single(c => c.Target.Class == "Shaman" && c.Target.CharacterId is null), "3"); CaptureRules(); MarkChanged();
        LoadButton();
        Check(RuleList.IsEnabled && rows.Any(r => r.Target.Target.Class == "Shaman" && r.Key == "3"), "Reloading must keep class defaults editable in the unified editor.");
        SaveStandalone();
        Check(StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == id).Value.Rules.Any(r => r.Target.Class == "Shaman" && r.Key == "3"), "Modern editor lost class default.");
        AddButton_Click(this, new()); Check(!SaveControl.IsEnabled, "Empty recipient list must block save.");
        AddRecipient_Click(this, new()); Check(rows.Count == 1 && SaveControl.IsEnabled, "Add recipient did not validate.");
        RemoveSelectedLibraryItem(); Check(standaloneDrafts.Count == 2, "Remove button failed.");
        BarLibraryMode.IsChecked = true;
        Check(selectedGroup is not null && groupColumnsEditor!.EntryCount == 2, "Groups must edit references from the common library.");
        ChangeGroup(selectedGroup! with { Scale = 1.35 });
        groupTitle!.BeginEdit(); groupName!.Text = "Support"; groupTitle.Commit();
        int previousColumns = selectedGroup!.Columns; AddGroupColumn();
        var firstReference = selectedGroup!.Buttons[0];
        ChangeGroup(selectedGroup with { Buttons = ButtonColumns.Move(selectedGroup.Buttons, firstReference.ButtonId, 1) });
        Save();
        Check(TeamEditing.Load(profilePath).Groups![0] is { Name: "Support", Scale: 1.35 } savedGroup && savedGroup.Columns == previousColumns + 1
            && StandaloneButtons.Load(profilePath).Length == 2, "Group edits copied or removed button mappings.");
        StandaloneLibraryMode.IsChecked = true;
        foreach (var client in clients) client.Included = false;
        Check(!ApplyControl.IsEnabled, "No selected team must block apply.");
        var refresh = RefreshClients(); while (!refresh.IsCompleted) Settle(); refresh.GetAwaiter().GetResult();
        Check(clients.All(c => !c.Included), "Refresh must preserve an intentionally empty selection.");
        var beforeRestart = clients.Select(c => c.Client).ToArray();
        replaceDiscovery([]);
        refresh = RefreshClients(); while (!refresh.IsCompleted) Settle(); refresh.GetAwaiter().GetResult();
        Check(clients.Count == 0 && Save(includeClients: false).ExcludedCharacterNames.Length == beforeRestart.Length,
            "An offline refresh and save must retain deliberately excluded character names.");
        replaceDiscovery(beforeRestart.Select(c => c with { ProcessId = c.ProcessId + 100, StartedUtcTicks = c.StartedUtcTicks + 100 }).ToArray());
        refresh = RefreshClients(); while (!refresh.IsCompleted) Settle(); refresh.GetAwaiter().GetResult();
        Check(clients.Count == beforeRestart.Length && clients.All(c => !c.Included),
            "Restarted character sessions must retain deliberate deselection.");
        replaceDiscovery(beforeRestart);
        refresh = RefreshClients(); while (!refresh.IsCompleted) Settle(); refresh.GetAwaiter().GetResult();
        foreach (var client in clients) client.Included = true;
        Save();
        BarLibraryMode.IsChecked = true;
        while (groupDrafts.Count > 0) { ButtonList.SelectedIndex = 0; RemoveSelectedLibraryItem(); }
        Check(StandaloneButtons.Load(profilePath).Length == 2, "Removing a group deleted shared buttons.");
        StandaloneLibraryMode.IsChecked = true;
        while (standaloneDrafts.Count > 0) { ButtonList.SelectedIndex = 0; RemoveSelectedLibraryItem(); }
        EmptyApplySelfTest(Check);
        foreach (var client in clients) client.Included = true;
        Save();
        byte[] original = File.ReadAllBytes(profilePath); File.AppendAllText(profilePath, " ");
        bool refused = false; try { Save(); } catch (IOException) { refused = true; }
        Check(refused, "Concurrent profile edits must be detected."); File.WriteAllBytes(profilePath, original);
        Close();
    }
    internal static void Review(string profile, string destination)
    {
        Directory.CreateDirectory(destination);
        // Use the supplied review profile for every visible identity. Website exports
        // provide a fictional fixture, independent of the user's saved characters.
        // A separate profile in memory and disabled save/apply keep review input-free.
        var reviewTeam = TeamEditing.Load(profile);
        if (reviewTeam.Characters.Length < 2) throw new InvalidDataException("UI review needs two sample characters.");
        var scan = new Discovery([new(11, 1, 111, 0, new(reviewTeam.Characters[0].Name, 5, 130)), new(22, 2, 222, 3440, new(reviewTeam.Characters[1].Name, 10, 130))], []);
        var app = new W.Application(); Exception? reviewError = null;
        var window = new SetupWindow(profile, "review-only-controller.exe", () => scan, true, new MemoryUserSettingsStore());
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                while (window.refreshing) await Task.Delay(25);
                window.ButtonList.SelectedItem = window.drafts.FirstOrDefault(b => b.Id == "invisibility") ?? window.drafts.FirstOrDefault();
                if (window.rows.Count > 0) { window.rows[0].Key = "NumpadMultiply"; window.rows[0].Modifiers = 4; }
                window.Settle(); window.Export(Path.Combine(destination, "01-button-editor.png"));
                window.Width = 980; window.Height = 720; window.Settle(); window.Export(Path.Combine(destination, "03-compact.png"));
                window.Width = 1200; window.Height = 920;
                window.ShowGeneralSettings(true); window.Settle(); window.Export(Path.Combine(destination, "04-settings-dark.png"));
                window.LightThemeChoice.IsChecked = true; window.Settle(); window.Export(Path.Combine(destination, "05-settings-light.png"));
                window.ShowGeneralSettings(false); window.Settle(); window.Export(Path.Combine(destination, "06-editor-light.png"));
                window.StandaloneLibraryMode.IsChecked = true;
                window.AddButton_Click(window, new()); window.NameField.Text = "Invisibility";
                window.AddRecipient_Click(window, new()); window.AddRecipient_Click(window, new());
                window.rows[0].Key = "6"; window.rows[0].Modifiers = 4;
                window.rows[1].Target = window.rows[0].Target; window.rows[1].Key = "7"; window.rows[1].Modifiers = 3;
                window.Settle(); window.Export(Path.Combine(destination, "ordered-keys.png"));
                window.DarkThemeChoice.IsChecked = true; window.Settle(); window.Export(Path.Combine(destination, "07-standalone-dark.png"));
                window.LightThemeChoice.IsChecked = true; window.Settle(); window.Export(Path.Combine(destination, "08-standalone-light.png"));
                window.Width = 980; window.Height = 720; window.Settle(); window.Export(Path.Combine(destination, "10-standalone-compact.png"));
                window.Width = 1200; window.Height = 920;
                string anchorId = reviewTeam.Characters[0].Id;
                // Sample histories exist only in the review copy, never in user layout files.
                window.readPlacements = _ => new(1, [new(anchorId, new(.8, .75), [new("review-wide", "Wide monitor · 3440 × 1440", new(.86, .82)), new("review-small", "Second monitor · 1920 × 1080", new(.72, .76))])]);
                window.ShowPlacement(true); window.PlacementCharacter.SelectedItem = window.anchorChoices.Single(c => c.Id == anchorId);
                window.DarkThemeChoice.IsChecked = true; window.Settle(); window.Export(Path.Combine(destination, "11-follow-character-dark.png"));
                window.LightThemeChoice.IsChecked = true; window.Settle(); window.Export(Path.Combine(destination, "12-follow-character-light.png"));
                window.ShowPlacement(false); window.BarLibraryMode.IsChecked = true; window.ShowPlacement(true);
                window.PlacementCharacter.SelectedItem = window.anchorChoices.Single(c => c.Id == anchorId);
                window.Settle(); window.Export(Path.Combine(destination, "13-follow-bar.png"));
                window.ReviewAssignments(destination);
                window.ReviewFloatingThemes(destination);
                File.WriteAllText(Path.Combine(destination, "review.json"), JsonSerializer.Serialize(new { source = "Rendered from the working WPF configuration window", liveIdentityData = scan.Clients.All(c => c.ProcessId > 22), savedProfileChanged = false, gameplayInput = false }));
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(destination, "error.txt"), error.ToString()); reviewError = error; }
            finally { window.Close(); }
        };
        app.Run(window);
        if (reviewError is not null) throw new InvalidOperationException("UI review export failed.", reviewError);
    }
    private void Export(string path)
    {
        const double scale = 1.5;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ReviewRoot.ActualWidth * scale), (int)Math.Ceiling(ReviewRoot.ActualHeight * scale), 96 * scale, 96 * scale, M.PixelFormats.Pbgra32);
        bitmap.Render(ReviewRoot);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); encoder.Save(file);
    }
#endif
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

