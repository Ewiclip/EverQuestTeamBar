using System.Windows.Data;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SetupWindow
{
    private bool filteringLibrary;
    private string buttonSearch = "", groupSearch = "";
    private string? lastButtonId, lastGroupId;

    private void LibrarySearch_Changed(object sender, C.TextChangedEventArgs e)
    {
        if (!initialized || filteringLibrary) return;
        if (IsStandalone) buttonSearch = LibrarySearch.Text; else groupSearch = LibrarySearch.Text;
        FilterLibrary();
    }
    private void ClearSearch_Click(object sender, W.RoutedEventArgs e) => LibrarySearch.Clear();
    private void FilterLibrary(bool switchLibrary = false)
    {
        filteringLibrary = true;
        try
        {
            if (switchLibrary)
            {
                LibrarySearch.Text = IsStandalone ? buttonSearch : groupSearch;
                ButtonList.ItemsSource = IsStandalone ? standaloneDrafts : groupDrafts;
            }
            var view = CollectionViewSource.GetDefaultView(ButtonList.ItemsSource);
            string[] words = LibrarySearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            view.Filter = item => (loadingGroup && item is ButtonGroup g && g.Id == selectedGroup?.Id)
                || words.All(word => SearchText(item).Contains(word, StringComparison.OrdinalIgnoreCase));
            view.Refresh();
            string? wanted = IsStandalone ? lastButtonId : lastGroupId;
            ButtonList.SelectedItem = ButtonList.Items.Cast<object>().FirstOrDefault(item => LibraryId(item) == wanted)
                ?? ButtonList.Items.Cast<object>().FirstOrDefault();
            selected = ButtonList.SelectedItem as ButtonDraft;
            selectedGroup = ButtonList.SelectedItem as ButtonGroup;
        }
        finally { filteringLibrary = false; }
        LoadButton();
        if (ButtonList.SelectedItem is { } chosen) ButtonList.ScrollIntoView(chosen);
    }
    private static string LibraryId(object item) => item is ButtonDraft b ? b.Id : ((ButtonGroup)item).Id;
    private string SearchText(object item)
    {
        if (item is ButtonGroup group)
            return group.ManagementName + " " + string.Join(" ", group.Buttons.Select(p => standaloneDrafts.FirstOrDefault(b => b.Id == p.ButtonId))
                .OfType<ButtonDraft>().Select(SearchText));
        var button = (ButtonDraft)item;
        return button.ManagementName + " " + string.Join(" ", button.Rules.Select(r => r.Key + " " + TeamEditing.SelectorLabel(team, r.Target)))
            + " " + string.Join(" ", button.ShowFor.Select(t => TeamEditing.SelectorLabel(team, t)));
    }
    private void DuplicateButton_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (!IsStandalone) { DuplicateGroup(); return; }
        if (selected is null) return;
        ValidateStandaloneDraft(selected);
        var original = selected;
        string label = CopyLabel(original.Label, standaloneDrafts.Select(b => b.Label));
        var doc = DocumentFor(original) with
        {
            Id = "button-" + Guid.NewGuid().ToString("N")[..12], Label = label,
            Rules = [.. original.Rules], ShowFor = [.. original.ShowFor], Floating = false
        };
        LibrarySearch.Clear();
        var copy = new ButtonDraft(new(doc.Id, doc.Label, doc.Rules))
        {
            Document = doc, ShowFor = doc.ShowFor, PersonaSpecific = original.PersonaSpecific,
            FollowCharacterId = doc.FollowCharacterId, Floating = false, Dirty = true
        };
        standaloneDrafts.Add(copy); ButtonList.SelectedItem = copy; MarkChanged();
        NameField.Focus(); NameField.SelectAll();
        Status("Copy created. You can add it to a group or turn on its floating display.");
    });
    private void DuplicateGroup()
    {
        if (selectedGroup is not { } original) return;
        ButtonGroups.Validate([original], team.Characters);
        var copy = original with { Id = "group-" + Guid.NewGuid().ToString("N")[..12],
            Name = CopyLabel(original.Name, groupDrafts.Select(g => g.Name), 40), Buttons = [.. original.Buttons] };
        LibrarySearch.Clear(); groupDrafts.Add(copy); ButtonList.SelectedItem = copy; MarkChanged();
        groupTitle?.BeginEdit();
        Status("Group copied. Both groups use the same saved buttons; each group has its own layout.");
    }
    internal static string CopyLabel(string source, IEnumerable<string> labels, int maxLength = 24)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var used = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string suffix = n == 1 ? " copy" : $" copy {n}";
            string candidate = source[..Math.Min(source.Length, maxLength - suffix.Length)].TrimEnd() + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }
    private void ReviewDraft_Click(object sender, W.RoutedEventArgs e)
    {
        ShowHotKeys(); StandaloneLibraryMode.IsChecked = true; LibrarySearch.Clear();
        var unfinished = standaloneDrafts.FirstOrDefault(d => d.Dirty);
        if (unfinished is not null) ButtonList.SelectedItem = unfinished;
        else if (changed)
        {
            BarLibraryMode.IsChecked = true; LibrarySearch.Clear();
            var invalid = groupDrafts.FirstOrDefault(group =>
            {
                try { ButtonGroups.Validate([group], team.Characters); return false; }
                catch (InvalidDataException) { return true; }
            });
            if (invalid is not null) ButtonList.SelectedItem = invalid;
        }

    }
    private void RefreshSaveAndConnectionState()
    {
        int unfinished = standaloneDrafts.Count(d => d.Dirty);
        bool pending = changed || libraryChanged || unfinished > 0;
        SaveState.Text = review ? "REVIEW COPY · No game input" : !pending ? "All changes saved automatically"
            : autoSaveTimer.IsEnabled ? "Saving changes…" : "Incomplete changes not saved";
        SaveState.SetResourceReference(C.TextBlock.ForegroundProperty, pending ? "Unsaved" : "Success");
        ReviewDraftControl.Visibility = pending && !autoSaveTimer.IsEnabled && !review ? W.Visibility.Visible : W.Visibility.Collapsed;
        // Selection must never make another unfinished draft look saved or ready.
        bool invalid = standaloneDrafts.Any(d =>
        {
            try { ValidateStandaloneDraft(d); return false; }
            catch (InvalidDataException) { return true; }
        });
        try { Routing.Validate(CurrentTeam(), [], requireClients: false); }
        catch (InvalidDataException) { invalid = true; }
        if (invalid) ApplyControl.IsEnabled = false;
        ApplyControl.ToolTip = invalid ? "Finish or remove the incomplete button or group. Use Review unfinished to find it."
            : SelectedClients().Length == 0 && HasButtonDrafts ? "Assign your game windows and select at least one character first."
            : "Show your floating controls for the selected characters. Future edits save and update automatically.";
        DuplicateButtonControl.IsEnabled = IsStandalone ? selected is not null : selectedGroup is not null;
        if (DuplicateButtonControl.IsEnabled)
            try { if (IsStandalone) ValidateStandaloneDraft(selected!); else ButtonGroups.Validate([selectedGroup!], team.Characters); }
            catch (InvalidDataException) { DuplicateButtonControl.IsEnabled = false; }
        if (!DuplicateButtonControl.IsEnabled)
            DuplicateButtonControl.ToolTip = (IsStandalone ? selected is null : selectedGroup is null) ? "Select an item to duplicate."
                : "Finish the selected item's required settings before duplicating it.";
    }
}

