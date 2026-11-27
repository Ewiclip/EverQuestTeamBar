using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

internal static class UsabilityTests
{
    internal static void Run(string output)
    {
        string folder = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(folder)) throw new IOException("Use a new usability-test output.");
        Directory.CreateDirectory(folder);
        using var memory = GameDataAccess.FixtureScope(false);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); checks.Add(why); }
        string profile = Path.Combine(folder, "team.json");
        var team = new TeamDefinition(1, [new("runa", "Runa"), new("fellwort", "Fellwort")],
            [new("runa-sk", "runa", "Shadowknight"), new("fellwort-shm", "fellwort", "Shaman")], [])
            { Groups = [new("support", "Support", [new("healing", 0), new("travel", 1)], 2)] };
        TeamEditing.Save(profile, team, []);
        foreach (var button in new[]
        {
            new StandaloneButton(1, "healing", "Healing", team.Characters, team.Personas,
                [new(new(CharacterId: "runa"), "Alt+1"), new(new(PersonaId: "fellwort-shm"), "Ctrl+Shift+H")],
                [new(Class: "Shaman")], "fellwort", true) { Extra = new() { ["fixtureExtension"] = JsonSerializer.SerializeToElement("preserved") } },
            new StandaloneButton(1, "invisibility", "Invisibility", team.Characters, team.Personas, [new(new(CharacterId: "runa"), "5")], [], Floating: false),
            new StandaloneButton(1, "travel", "Travel", team.Characters, team.Personas, [new(new(CharacterId: "fellwort"), "2")], [])
        }) StandaloneButtons.Save(StandaloneButtons.PathFor(profile, button.Id), button, null);
        var clients = new DetectedClient[] { new(11, 1, 111, 0, new("Runa", 5, 130), true), new(22, 2, 222, 1920, new("Fellwort", 10, 130), true) };
        var store = new MemoryUserSettingsStore(new() { Theme = AppearanceTheme.Dark });
        var setup = new SetupWindow(profile, "no-input-fixture", () => new(clients, []), userSettings: store);
        try { setup.TestUsability(folder, Check); Check(!Enum.GetValues<MemoryFeature>().Any(GameDataAccess.IsEnabled), "All usability workflows leave game-data memory reading off."); }
        finally { setup.CloseUsabilityFixture(); }
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, screenshots = folder, gameInputSent = false, gameMemoryRead = false });
    }
}

public partial class SetupWindow
{
    internal void CloseUsabilityFixture()
    {
        autoSaveTimer.Stop(); changed = libraryChanged = false;
        foreach (var draft in standaloneDrafts) draft.Dirty = false;
        ClosePrivacyFixture();
    }
    internal void TestUsability(string folder, Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new W.Threading.DispatcherSynchronizationContext(Dispatcher));
        Show(); Settle();
        long until = Environment.TickCount64 + 5000;
        while (refreshing && Environment.TickCount64 < until) { Settle(); Thread.Sleep(5); }
        check(!refreshing && clients.Count == 2, "Manual fixture characters load for the offline editor.");
        LibrarySearch.Text = "fellwort ctrl+shift+h";
        check(ButtonList.Items.Count == 1 && selected?.Id == "healing", "Search combines recipient and complete key-chord terms.");
        LibrarySearch.Text = "unfindable";
        check(ButtonList.Items.Count == 0 && EmptyHeading.Text == "No matches" && EmptyClearSearch.IsVisible,
            "An empty search result explains how to recover without losing the library.");
        LibrarySearch.Clear(); ButtonList.SelectedItem = standaloneDrafts.Single(b => b.Id == "travel");
        BarLibraryMode.IsChecked = true; LibrarySearch.Text = "healing";
        check(ButtonList.Items.Count == 1 && selectedGroup?.Id == "support", "Group search finds the buttons a group contains.");
        StandaloneLibraryMode.IsChecked = true;
        check(selected?.Id == "travel" && LibrarySearch.Text == "", "Returning to Buttons restores its selection and search independently of Groups.");
        LibrarySearch.Text = "healing";
        NameField.Text = "Recovery";
        check(selected?.Id == "healing" && NameField.Text == "Recovery", "Renaming a search result keeps the current editor open.");
        FlushAutoSave();
        byte[] original = File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, "healing"));
        DuplicateButton_Click(this, new());
        check(selected is { Floating: false, Dirty: true } && selected.Id != "healing" && selected.Label == "Recovery copy"
            && selected.Rules.Length == 2 && selected.ShowFor.Single().Class == "Shaman" && selected.FollowCharacterId == "fellwort",
            "Duplicate creates an independent named button with mappings, visibility, and anchor retained, and floating display off.");
        string copyId = selected!.Id;
        rows[0].Key = "2"; check(FlushAutoSave(), "The duplicate is valid and autosaves without a game connection.");
        var copy = StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == copyId).Value;
        check(copy.Rules[0].Key == "Alt+2" && copy.Extra!["fixtureExtension"].GetString() == "preserved"
            && original.SequenceEqual(File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, "healing")))
            && groupDrafts.Single().Buttons.All(b => b.ButtonId != copyId),
            "Editing a duplicate preserves the original, extension fields, and existing group membership.");
        check(CopyLabel(new string('A', 24), [new string('A', 19) + " copy"]).Length <= 24
            && CopyLabel("Recovery", ["Recovery copy", "Recovery copy 2"]) == "Recovery copy 3",
            "Copy names remain unique and fit the button-name limit.");
        LibrarySearch.Text = "Recovery"; Settle(); RenderSettings(Path.Combine(folder, "library-search-dark.png"));
        LibrarySearch.Clear(); AddButton_Click(this, new()); string unfinishedId = selected!.Id;
        check(!FlushAutoSave(), "An incomplete new button stays in the editor without replacing saved configuration.");
        ButtonList.SelectedItem = standaloneDrafts.Single(b => b.Id == "invisibility");
        check(SaveState.Text == "Incomplete changes not saved" && ReviewDraftControl.IsVisible && !ApplyControl.IsEnabled,
            "Selecting another valid button cannot hide an unfinished draft or enable Connect.");
        ReviewDraft_Click(this, new());
        check(selected?.Id == unfinishedId, "Review unfinished returns directly to the unsaved button.");
        Settle(); RenderSettings(Path.Combine(folder, "unfinished-button-dark.png"));
        AddRecipient_Click(this, new()); check(FlushAutoSave(), "Finishing the draft clears the unsaved warning.");
        check(SaveState.Text == "All changes saved automatically" && ReviewDraftControl.Visibility == W.Visibility.Collapsed && ApplyControl.IsEnabled,
            "Connection readiness recovers after the draft becomes valid.");
        BarLibraryMode.IsChecked = true; LibrarySearch.Text = "Support";
        groupName!.Text = "Actions";
        check(selectedGroup?.Name == "Actions" && GroupEditor.IsVisible, "Renaming a filtered group keeps it selected during editing.");
        FlushAutoSave(); LibrarySearch.Clear(); Settle();
        IEnumerable<W.DependencyObject> Descendants(W.DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        var editLink = Descendants(groupColumnsEditor!).OfType<C.Button>()
            .Single(b => W.Automation.AutomationProperties.GetName(b) == "Edit Recovery");
        editLink.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        check(IsStandalone && selected?.Id == "healing" && LibrarySearch.Text == "",
            "A group button's edit link opens the exact shared library button, clearing an old search.");
        BarLibraryMode.IsChecked = true;
        string renamedGroupId = selectedGroup!.Id;
        groupTitle!.BeginEdit(); groupName!.Text = "  ";
        check(groupTitle.Commit() && FlushAutoSave() && TeamEditing.Load(profilePath).Groups!.Single(g => g.Id == renamedGroupId).Name == "",
            "Clearing a group title autosaves the same group with a blank name.");
        StandaloneLibraryMode.IsChecked = true;
        check(ApplyControl.IsEnabled && SaveState.Text == "All changes saved automatically",
            "An unnamed group stays ready to connect when another section is selected.");
        BarLibraryMode.IsChecked = true; LibrarySearch.Text = "Unnamed";
        check(selectedGroup?.Id == renamedGroupId && groupName!.Text == "", "The group is searchable by its editor label and reopens with an empty title.");
        LibrarySearch.Clear();
        double originalScale = selectedGroup!.Scale;
        ChangeGroup(selectedGroup with { Scale = 3 }); FlushAutoSave();
        StandaloneLibraryMode.IsChecked = true;
        check(!ApplyControl.IsEnabled && SaveState.Text == "Incomplete changes not saved",
            "An invalid group size blocks Connect when another library section is selected.");
        ReviewDraft_Click(this, new());
        check(!IsStandalone && selectedGroup?.Id == renamedGroupId, "Review unfinished also finds an invalid unnamed group.");
        ChangeGroup(selectedGroup! with { Scale = originalScale });
        groupName!.Text = "Actions"; check(FlushAutoSave(), "An unnamed group can be renamed again.");
        StandaloneLibraryMode.IsChecked = true; LibrarySearch.Text = "Recovery";
        LightThemeChoice.IsChecked = true; Settle(); RenderSettings(Path.Combine(folder, "library-search-light.png"));
        var windows = new[] { new GameSession(11, 1, 111, 0, "DISPLAY1 · 1920 × 1080"), new GameSession(22, 2, 222, 1920, "DISPLAY2 · 2560 × 1440") };
        var assignments = new WindowAssignments(this, CurrentTeam(), windows, [], c => c, () => windows);
        try { assignments.TestUsability(folder, check, value => windows = value); }
        finally { assignments.Close(); }
        LibrarySearch.Clear();
        TestDeletionUI(folder, check);
        check(FlushAutoSave(), "All fixture edits are saved before the editor exits.");
    }
}

public partial class WindowAssignments
{
    internal void TestUsability(string folder, Action<bool, string> check, Action<GameSession[]> replaceWindows)
    {
        Show(); UpdateLayout();
        void Settle() { UpdateLayout(); Dispatcher.Invoke(() => { }, W.Threading.DispatcherPriority.ContextIdle); }
        var container = (C.ContentPresenter)WindowRows.ItemContainerGenerator.ContainerFromIndex(0);
        IEnumerable<W.DependencyObject> Children(W.DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var descendant in Children(child)) yield return descendant;
            }
        }
        var combo = Children(container).OfType<C.ComboBox>().Single(c => c.IsEditable);
        combo.ApplyTemplate();
        var field = combo.Template.FindName("PART_EditableTextBox", combo) as C.TextBox;
        check(field is { IsVisible: true }, "Manual character selection contains a visible editable field in the custom theme.");
        field!.Text = "Newhero"; rows[0].Class = "Berserker"; Settle();
        check(rows[0].Name == "Newhero" && field.Text == "Newhero" && combo.Text == "Newhero",
            "Typing an unknown character name through the actual editable control updates the assignment and stays visible.");
        Confirm(); check(UseAssignmentsButton.IsEnabled && BuildAssignments().Single().Character.Name == "Newhero",
            "A new typed character can be confirmed and assigned without automatic detection.");
        rows[1].Name = "NEWHERO"; rows[1].Class = "Shaman"; Confirm();
        check(rows.All(r => r.Problem.Contains("more than one")) && !UseAssignmentsButton.IsEnabled,
            "Duplicate character assignments are highlighted inline regardless of letter case.");
        rows[1].Name = "Fellwort";
        check(Confirmed.IsChecked == false && !UseAssignmentsButton.IsEnabled,
            "Changing an assignment invalidates its previous confirmation.");
        Confirm(); var first = rows[0]; var second = rows[1];
        replaceWindows([second.Window with { Left = -100, Label = "DISPLAY2 · moved" }, first.Window, new(33, 3, 333, 3840, "DISPLAY3 · 1920 × 1080")]);
        field.Focus(); field.CaretIndex = 3;
        windowTimer.Interval = TimeSpan.FromMilliseconds(20);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (rows.Count < 3 && wait.Elapsed < TimeSpan.FromSeconds(2)) { Settle(); Thread.Sleep(10); }
        windowTimer.Interval = TimeSpan.FromSeconds(2);
        check(rows.Count == 3 && rows[0] == first && rows[1] == second && rows[2].Name == "" && rows[2].Class is null
            && ReferenceEquals(container, WindowRows.ItemContainerGenerator.ContainerFromIndex(0)) && field.CaretIndex == 3
            && rows[0].Name == "Newhero" && rows[0].Class == "Berserker" && Confirmed.IsChecked == false,
            "A newly launched EQ window appears automatically with no assumed identity while the edited character, class and caret are retained.");
        RefreshWindows(); Settle();
        check(rows[0] == second && rows[0].Number == 2 && rows[0].Name == "Fellwort" && rows[1] == first
            && rows[1].Number == 1 && rows[2].Name == "" && rows[2].Number == 3 && !Confirmed.IsChecked!.Value,
            "Refresh preserves drafts and numbers for exact sessions, adds blank new windows, and requires renewed confirmation.");
        replaceWindows([rows[0].Window with { StartedUtcTicks = 99 }, rows[1].Window, rows[2].Window]);
        RefreshWindows(); Settle();
        check(rows[0].Name == "" && rows[0].Number == 4 && rows[1].Name == "Newhero" && IdentifyStatus.Text.Contains("closed or changed"),
            "A replaced game process never inherits a previous character assignment or number.");
        rows[0].Name = "Fellwort"; Settle();
        check(rows[0].Problem.Contains("current class") && !UseAssignmentsButton.IsEnabled,
            "A missing class has a specific inline explanation.");
        rows[0].Class = "Shaman"; Confirm(); Settle();
        check(BuildAssignments().Length == 2 && UseAssignmentsButton.IsEnabled,
            "A corrected refreshed assignment is ready without closing the dialog.");
        Export(Path.Combine(folder, "assignments-light.png"));
        ThemePalette.Apply(Resources, AppearanceTheme.Dark); Settle(); Export(Path.Combine(folder, "assignments-dark.png"));
    }
}

