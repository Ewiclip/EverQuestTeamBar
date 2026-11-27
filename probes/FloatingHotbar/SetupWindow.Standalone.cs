using System.Collections.ObjectModel;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SetupWindow
{
    internal void ShowStandaloneLibrary(string? buttonId = null)
    {
        ShowHotKeys(); StandaloneLibraryMode.IsChecked = true;
        if (buttonId is null) return;
        LibrarySearch.Clear();
        var button = standaloneDrafts.FirstOrDefault(b => b.Id == buttonId);
        if (button is null) { Status("This button is no longer in the library. Remove its reference from the group.", true); return; }
        ButtonList.SelectedItem = button; ButtonList.ScrollIntoView(button);
    }
    private readonly ObservableCollection<ButtonDraft> standaloneDrafts = [];
    private bool IsStandalone => StandaloneLibraryMode.IsChecked == true;
    private ObservableCollection<ButtonDraft> ActiveDrafts => IsStandalone ? standaloneDrafts : drafts;
    private void LoadStandaloneLibrary()
    {
        foreach (var saved in StandaloneButtons.Load(profilePath))
        {
            team = TeamEditing.IncludeButtonRecipients(team, saved.Value);
            standaloneDrafts.Add(DraftFromSavedButton(saved));
        }
    }
    private static ButtonDraft DraftFromSavedButton(SavedButton saved)
    {
        var doc = saved.Value;
        return new(new(doc.Id, doc.Label, doc.Rules))
        {
            Document = doc, ShowFor = doc.ShowFor, PersonaSpecific = doc.ShowFor.Length > 0, SavedBytes = saved.Bytes,
            FollowCharacterId = doc.FollowCharacterId, Floating = doc.Floating
        };
    }
    internal bool HasPendingButtonEdit(string id) => !closed && standaloneDrafts.Any(d => d.Id == id && d.Dirty);
    internal void ObserveSavedOverlayButton(SavedButton saved)
    {
        if (closed || HasPendingButtonEdit(saved.Value.Id)) return;
        team = TeamEditing.IncludeButtonRecipients(team, saved.Value);
        var old = standaloneDrafts.FirstOrDefault(d => d.Id == saved.Value.Id);
        var updated = DraftFromSavedButton(saved);
        filteringLibrary = true;
        try
        {
            if (old is null) standaloneDrafts.Add(updated);
            else standaloneDrafts[standaloneDrafts.IndexOf(old)] = updated;
        }
        finally { filteringLibrary = false; }
        FilterLibrary();
    }
    private StandaloneButton DocumentFor(ButtonDraft draft) => new(1, draft.Id, draft.Label, team.Characters, team.Personas, draft.Build().Rules, draft.ShowFor, draft.FollowCharacterId, draft.Floating) { Extra = draft.Document?.Extra, Icon = draft.Document?.Icon };
    private void Library_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized) return;
        FilterLibrary(switchLibrary: true);
    }
    private void UpdateLibraryControls()
    {
        ButtonList.ItemTemplate = (W.DataTemplate)ButtonList.FindResource(IsStandalone ? "ButtonLibraryItem" : "GroupLibraryItem");
        int count = IsStandalone ? standaloneDrafts.Count : groupDrafts.Count;
        AddButtonControl.Content = IsStandalone ? "+ Add button" : "+ Add group";
        AddButtonControl.ToolTip = IsStandalone ? "Create a new button" : "Create a new group";
        bool searching = !string.IsNullOrWhiteSpace(LibrarySearch.Text);
        LibrarySearchPlaceholder.Text = IsStandalone ? "Search buttons…" : "Search groups…";
        W.Automation.AutomationProperties.SetName(LibrarySearch, IsStandalone ? "Search buttons" : "Search groups");
        ClearLibrarySearch.Visibility = searching ? W.Visibility.Visible : W.Visibility.Collapsed;
        DuplicateButtonControl.ToolTip = IsStandalone ? "Copy this button's mappings and visibility. The copy starts with floating display off."
            : "Copy this group's layout using the same saved buttons.";
        LibraryEmptyHint.Text = searching ? IsStandalone ? "No matching buttons" : "No matching groups" : IsStandalone ? "No buttons yet" : "No groups yet";
        LibraryEmptyHint.Visibility = ButtonList.Items.Count == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        ButtonHeading.Text = "Button";
        ButtonEditor.Visibility = IsStandalone && selected is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
        GroupEditor.Visibility = !IsStandalone && selectedGroup is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
        EmptyButtonState.Visibility = (IsStandalone ? selected is null : selectedGroup is null) ? W.Visibility.Visible : W.Visibility.Collapsed;
        EmptyHeading.Text = searching ? "No matches" : IsStandalone ? "Create your first button" : "Create a button group";
        EmptyHint.Text = searching ? "Try a button name, character, class, or key combination. Your library is still here."
            : !HasButtonDrafts ? "Create a button, add a keypress, then choose whether it floats on its own or belongs to a group."
            : IsStandalone ? "Configure a button, then float it on its own or place it in a group." : "Choose saved buttons, columns and an independent size.";
        EmptyAddControl.Content = IsStandalone ? "＋  Add button" : "＋  Add group";
        EmptyAddControl.Visibility = searching ? W.Visibility.Collapsed : W.Visibility.Visible;
        EmptyClearSearch.Visibility = searching ? W.Visibility.Visible : W.Visibility.Collapsed;
        SaveControl.Content = IsStandalone ? "Save button" : "Save groups";
        W.Automation.AutomationProperties.SetName(ButtonList, IsStandalone ? "Button library" : "Button groups");
    }
    private void SaveStandalone()
    {
        if (review) throw new InvalidOperationException("This review copy cannot save buttons.");
        if (selected is null) return;
        ValidateStandaloneDraft(selected);
        var doc = DocumentFor(selected);
        selected.SavedBytes = StandaloneButtons.Save(StandaloneButtons.PathFor(profilePath, selected.Id), doc, selected.SavedBytes);
        selected.Document = doc; selected.Dirty = false;
        SaveState.Text = "All changes saved automatically"; SaveState.SetResourceReference(C.TextBlock.ForegroundProperty, "Success");
        Status("Saved. Connected buttons update automatically.");
    }
    private void ValidateStandaloneDraft(ButtonDraft draft)
    {
        if (draft.Rules.Length == 0) throw new InvalidDataException("Add a keypress to choose a character and the key this button sends.");
        if (draft.PersonaSpecific && draft.ShowFor.Length == 0)
            throw new InvalidDataException("This button has incomplete saved visibility rules. Reopen its saved configuration.");
        StandaloneButtons.Validate(DocumentFor(draft));
    }
    private void UpdateStandalonePreview()
    {
        ButtonValidationMessage.Text = "";
        ButtonValidationMessage.Visibility = W.Visibility.Collapsed;
        SaveControl.IsEnabled = ApplyControl.IsEnabled = false;
        if (selected is null) { ApplyControl.IsEnabled = !refreshing && !applying && (!HasButtonDrafts || SelectedClients().Length is > 0 and <= 6); return; }
        try
        {
            ValidateStandaloneDraft(selected); var doc = DocumentFor(selected); var active = SelectedClients();
            // Keep route validation even though the redundant send-preview UI is gone.
            _ = StandaloneButtons.Resolve(doc, active);
            SaveControl.IsEnabled = !refreshing && !applying;
            ApplyControl.IsEnabled = SaveControl.IsEnabled && active.Length is > 0 and <= 6;
        }
        catch (Exception error) { ButtonValidationMessage.Text = error.Message; ButtonValidationMessage.Visibility = W.Visibility.Visible; }
        SaveState.Text = review ? "REVIEW COPY · No game input" : selected.Dirty ? "Saving button changes…" : "All changes saved automatically";
        if (review) Status("Review copy. Your saved buttons are unchanged.");
    }
#if !TEAMBAR_PUBLIC_RELEASE
    internal void StandaloneSelfTest()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        Show(); var until = Environment.TickCount64 + 10000;
        while ((refreshing || clients.Count == 0) && Environment.TickCount64 < until) { Settle(); Thread.Sleep(5); }
        StandaloneTests.Check(clients.Count == 2, "Standalone editor discovery failed.");
        StandaloneLibraryMode.IsChecked = true;
        StandaloneTests.Check(EmptyButtonState.Visibility == W.Visibility.Visible, "Empty standalone library needs an add state.");
        AddButton_Click(this, new()); NameField.Text = "Persona action"; AddRecipient_Click(this, new());
        StandaloneTests.Check(SaveControl.IsEnabled && selected?.Document is not null, "Standalone creation failed.");
        KeyPickerSelfTest(StandaloneTests.Check);
        StandaloneTests.Check(selected!.ShowFor.Length == 0 && !selected.PersonaSpecific, "New buttons must not require a second persona selection.");
        // Simulate a saved button from an older editor: unrelated edits must preserve its existing rules.
        selected.ShowFor = [new(Class: "Cleric"), new(PersonaId: "runa-sk")]; selected.PersonaSpecific = true;
        MarkChanged(); LoadButton();
        string id = selected!.Id; SaveStandalone();
        var savedVisibility = StandaloneButtons.Load(profilePath).Single().Value.ShowFor;
        StandaloneTests.Check(savedVisibility.Length == 2, "The simplified editor lost saved visibility rules.");
        NameField.Text = "Renamed persona action";
        ShowGeneralSettings(true); LightThemeChoice.IsChecked = true; ShowGeneralSettings(false);
        BarLibraryMode.IsChecked = true; StandaloneLibraryMode.IsChecked = true;
        var renamed = StandaloneButtons.Load(profilePath).Single().Value;
        StandaloneTests.Check(selected!.Id == id && selected.Label == "Renamed persona action" && !selected.Dirty
            && selected.ShowFor.SequenceEqual(savedVisibility) && renamed.Label == selected.Label && renamed.ShowFor.SequenceEqual(savedVisibility),
            "Navigating through settings must autosave the rename while preserving existing visibility rules.");
        selected.ShowFor = []; MarkChanged();
        StandaloneTests.Check(!SaveControl.IsEnabled, "Empty persona-specific visibility must not silently become Always.");
        BarLibraryMode.IsChecked = true; StandaloneLibraryMode.IsChecked = true;
        StandaloneTests.Check(!SaveControl.IsEnabled && selected!.PersonaSpecific && ButtonValidationMessage.Visibility == W.Visibility.Visible, "Reload lost invalid conditional draft state or its validation message.");
        selected.ShowFor = savedVisibility; MarkChanged(); SaveStandalone();

        foreach (var client in clients) client.Included = false;
        StandaloneTests.Check(SaveControl.IsEnabled && !ApplyControl.IsEnabled, "Offline standalone save should work without enabling Apply.");
        SaveStandalone(); RemoveSelectedLibraryItem();
        StandaloneTests.Check(StandaloneButtons.Load(profilePath).Length == 0 && !File.Exists(StandaloneButtons.PathFor(profilePath, id)),
            "Deleting the last standalone must leave an empty library without restoring archived buttons.");
        EmptyApplySelfTest(StandaloneTests.Check); Close();
    }
#endif
}
