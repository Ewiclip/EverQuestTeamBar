using System.Collections.ObjectModel;
using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

public partial class SetupWindow
{
    private readonly ObservableCollection<ButtonGroup> groupDrafts = [];
    private ButtonGroup? selectedGroup;
    private bool loadingGroup;
    private ButtonColumnsEditor? groupColumnsEditor;
    private EditableTitle? groupTitle;
    private C.TextBox? groupName;
    internal Func<W.Window, string, SavedButton[], IEnumerable<string>, string?> ChooseGroupButton = SavedButtonPicker.Choose;
    private void FloatButton_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized || loading || selected is null) return;
        selected.Floating = FloatButton.IsChecked == true; selected.VisibilityChanged(); MarkChanged();
    }
    private void AddGroup()
    {
        LibrarySearch.Clear();
        var group = new ButtonGroup("group-" + Guid.NewGuid().ToString("N")[..12], "Button group", []);
        groupDrafts.Add(group); ButtonList.SelectedItem = group; MarkChanged();
    }
    private void RemoveGroup()
    {
        if (selectedGroup is null) return;
        int index = ButtonList.SelectedIndex; groupDrafts.Remove(selectedGroup);
        ButtonList.SelectedIndex = Math.Min(index, ButtonList.Items.Count - 1); selectedGroup = ButtonList.SelectedItem as ButtonGroup;
        MarkChanged(); LoadButton(); Status("Group removed. Its buttons remain in your library.");
    }
    private void ChangeGroup(ButtonGroup group)
    {
        if (selectedGroup is null) return;
        loadingGroup = true;
        try
        {
            int index = groupDrafts.IndexOf(selectedGroup);
            groupDrafts[index] = group; selectedGroup = group; ButtonList.SelectedItem = group;
        }
        finally { loadingGroup = false; }
        MarkChanged();
    }
    private void UpdateGroupPreview()
    {
        try
        {
            Routing.Validate(CurrentTeam(), [], requireClients: false);
            SaveControl.IsEnabled = !refreshing && !applying;
            ApplyControl.IsEnabled = SaveControl.IsEnabled && (!HasButtonDrafts || SelectedClients().Length is > 0 and <= 6);
            SaveState.Text = review ? "REVIEW COPY · No game input" : changed ? "Saving group changes…" : "All changes saved automatically";
        }
        catch (Exception error) { SaveControl.IsEnabled = ApplyControl.IsEnabled = false; Status(error.Message, true); }
    }
    private void LoadGroupEditor()
    {
        if (GroupEditorHost is null) return;
        loadingGroup = true; GroupEditorHost.Children.Clear();
        try
        {
            if (selectedGroup is not { } current) return;
            groupTitle = new(current.Name, "group", 24, allowEmpty: true) { HorizontalAlignment = W.HorizontalAlignment.Left, Margin = new(0, 0, 0, 22), MaxWidth = 650 };
            groupName = groupTitle.Editor;
            groupTitle.Changed += value => { if (!loadingGroup && selectedGroup is { } group) ChangeGroup(group with { Name = value }); };
            GroupEditorHost.Children.Add(groupTitle);
            groupColumnsEditor = new ButtonColumnsEditor();
            groupColumnsEditor.Changed += entries => { if (selectedGroup is { } group) ChangeGroup(group with { Buttons = entries }); };
            groupColumnsEditor.EditRequested += id => ShowStandaloneLibrary(id);
            groupColumnsEditor.ColumnAdded += AddGroupColumn;
            groupColumnsEditor.ColumnRenamed += (column, name) =>
            {
                if (selectedGroup is not { } group) return;
                var names = ButtonColumns.Names(group.ColumnNames, group.Columns); names[column] = name;
                ChangeGroup(group with { ColumnNames = names });
            };
            groupColumnsEditor.EmptyColumnRemoved += column =>
            {
                if (selectedGroup is not { } group || group.Columns <= 1) return;
                ChangeGroup(group with { Buttons = ButtonColumns.RemoveEmpty(group.Buttons, column), Columns = group.Columns - 1,
                    ColumnNames = ButtonColumns.Names(group.ColumnNames, group.Columns).Where((_, i) => i != column).ToArray() }); FillGroupColumns();
            };
            groupColumnsEditor.AddRequested += AddSavedGroupButton;
            GroupEditorHost.Children.Add(groupColumnsEditor); FillGroupColumns();
        }
        finally { loadingGroup = false; }
    }
    private void AddGroupColumn()
    {
        if (selectedGroup is not { } group || group.Columns >= ButtonColumns.MaxColumns) return;
        ChangeGroup(group with { Columns = group.Columns + 1, ColumnNames = ButtonColumns.Names(group.ColumnNames, group.Columns + 1) }); FillGroupColumns();
    }
    private void AddSavedGroupButton(int column) => Act(() =>
    {
        if (selectedGroup is not { } original || column < 0 || column >= original.Columns) return;
        FlushAutoSave();
        var saved = StandaloneButtons.Load(profilePath);
        string? id = ChooseGroupButton(this, ButtonColumns.Names(original.ColumnNames, original.Columns)[column], saved, original.Buttons.Select(p => p.ButtonId));
        if (id is null || selectedGroup is not { } group || group.Id != original.Id || column >= group.Columns
            || group.Buttons.Any(p => p.ButtonId == id) || !saved.Any(b => b.Value.Id == id)) return;
        ChangeGroup(group with { Buttons = [.. group.Buttons, new(id, column)] }); FillGroupColumns();
    });
    private void FillGroupColumns()
    {
        if (groupColumnsEditor is not null && selectedGroup is { } group)
            groupColumnsEditor.SetLayout(group.Buttons, group.Columns, id => standaloneDrafts.FirstOrDefault(b => b.Id == id)?.ManagementName ?? "Missing button", group.ColumnNames);
    }
}
