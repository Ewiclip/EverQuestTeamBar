using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    internal Func<SavedButton[]> ReadDockButtons = SpellDockRules.LoadButtons;
    private SavedButton[] savedDockButtons = [];
    private string? dockLibraryError, dockSelection;
    private bool loadingDock;
    private int dockReadGeneration;
    private C.ComboBox? dockGroupsChoice, dockSideChoice;
    private EditableTitle? dockTitle;
    private ButtonColumnsEditor? dockOrder;
    internal Func<W.Window, DeleteRequest, bool> ConfirmGroupDeletion = DeleteConfirmation.Ask;
    internal Func<W.Window, string, SavedButton[], IEnumerable<string>, string?> ChooseDockButton = SavedButtonPicker.Choose;
    private SpellDockGroup? SelectedDock => selected?.DockGroups?.FirstOrDefault(g => g.Id == dockSelection);
    private async Task RefreshDockLibrary()
    {
        if (review) return;
        int revision = ++dockReadGeneration;
        try
        {
            var buttons = await Task.Run(ReadDockButtons);
            if (revision == dockReadGeneration) { savedDockButtons = buttons; dockLibraryError = null; }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { if (revision == dockReadGeneration) { savedDockButtons = []; dockLibraryError = e.Message; } }
    }
    private void LoadDockEditor()
    {
        if (DockEditorHost is null) return;
        loadingDock = true; DockEditorHost.Children.Clear();
        var groups = selected?.DockGroups ?? [];
        dockSelection = groups.FirstOrDefault(g => g.Id == dockSelection)?.Id ?? groups.FirstOrDefault()?.Id;
        C.TextBlock Hint(string text) { var t = new C.TextBlock { Text = text, TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 4, 0, 9) }; t.SetResourceReference(W.FrameworkElement.StyleProperty, "Hint"); return t; }
        C.Button Button(string text, Action action) { var b = new C.Button { Content = text, Padding = new(9, 6, 9, 6), Margin = new(0, 0, 6, 0) }; b.Click += (_, _) => action(); return b; }
        var tools = new C.StackPanel { Orientation = C.Orientation.Horizontal, Margin = new(0, 0, 0, 10) };
        tools.Children.Add(Button("+ Group", () => Act(() =>
        {
            if (selected is null) return;
            var group = new SpellDockGroup(Guid.NewGuid().ToString("N"), "Button group", SpellDockSide.Bottom, []);
            dockSelection = group.Id; Replace(selected with { DockGroups = [.. selected.DockGroups ?? [], group] }); LoadDockEditor();
        })));
        tools.Children.Add(Button("Manage saved buttons", () => { if (review) Status("Preview only"); else TeamBarApplication.Current?.ShowStandaloneLibrary(); }));
        DockEditorHost.Children.Add(tools);
        dockGroupsChoice = new C.ComboBox { ItemsSource = groups, DisplayMemberPath = nameof(SpellDockGroup.EditorName), SelectedItem = SelectedDock, MinHeight = 34 };
        dockGroupsChoice.SelectionChanged += (_, _) => { if (loadingDock) return; dockSelection = (dockGroupsChoice.SelectedItem as SpellDockGroup)?.Id; LoadDockEditor(); };
        var groupPicker = new C.DockPanel();
        var deleteGroup = Button("×", () =>
        {
            if (selected is null || SelectedDock is not { } group) return;
            string overlayId = selected.Id;
            if (!ConfirmGroupDeletion((W.Window?)settingsOwner ?? this, new("Delete docked group?", group.EditorName, "This removes the group from this spell overlay. Its saved buttons remain in your library and in other groups."))) return;
            if (selected?.Id != overlayId) return;
            Replace(selected with { DockGroups = selected.DockGroups?.Where(g => g.Id != group.Id).ToArray() }); LoadDockEditor();
        });
        deleteGroup.FontSize = 21; deleteGroup.ToolTip = "Delete this docked group…"; deleteGroup.IsEnabled = SelectedDock is not null;
        W.Automation.AutomationProperties.SetName(deleteGroup, "Delete selected docked group");
        C.DockPanel.SetDock(deleteGroup, C.Dock.Right); groupPicker.Children.Add(deleteGroup); groupPicker.Children.Add(dockGroupsChoice);
        DockEditorHost.Children.Add(groupPicker);
        if (SelectedDock is not { } current) { DockEditorHost.Children.Add(Hint("Add a group, then use + Button inside a column.")); loadingDock = false; return; }
        dockTitle = new(current.Name, "docked group", 20, allowEmpty: true) { HorizontalAlignment = W.HorizontalAlignment.Left, Margin = new(0, 16, 0, 12) };
        dockTitle.Changed += value => { if (!loadingDock && SelectedDock is { } group) UpdateDock(group with { Name = value }); };
        DockEditorHost.Children.Add(dockTitle);
        var edge = new C.StackPanel { Orientation = C.Orientation.Horizontal, Margin = new(0, 0, 0, 18) };
        edge.Children.Add(new C.TextBlock { Text = "Dock edge", VerticalAlignment = W.VerticalAlignment.Center, Margin = new(0, 0, 12, 0) });
        dockSideChoice = new C.ComboBox { ItemsSource = Enum.GetValues<SpellDockSide>(), SelectedItem = current.Side, MinHeight = 32, MinWidth = 120 };
        W.Automation.AutomationProperties.SetName(dockSideChoice, "Dock edge");
        dockSideChoice.SelectionChanged += (_, _) => { if (!loadingDock && SelectedDock is { } group && dockSideChoice.SelectedItem is SpellDockSide side) UpdateDock(group with { Side = side }); };
        edge.Children.Add(dockSideChoice); DockEditorHost.Children.Add(edge);
        dockOrder = new ButtonColumnsEditor();
        dockOrder.Changed += entries => { if (SelectedDock is { } group) UpdateDock(group.WithLayout(entries)); };
        dockOrder.ColumnAdded += AddDockColumn;
        dockOrder.ColumnRenamed += (column, name) =>
        {
            if (SelectedDock is not { } group) return;
            var names = ButtonColumns.Names(group.ColumnNames, group.Columns); names[column] = name; UpdateDock(group with { ColumnNames = names });
        };
        dockOrder.EmptyColumnRemoved += column =>
        {
            if (SelectedDock is not { } group || group.Columns <= 1) return;
            UpdateDock(group.WithLayout(ButtonColumns.RemoveEmpty(group.Layout, column)) with { Columns = group.Columns - 1,
                ColumnNames = ButtonColumns.Names(group.ColumnNames, group.Columns).Where((_, i) => i != column).ToArray() }); FillDockOrder();
        };
        dockOrder.EditRequested += id =>
        {
            FlushAutoSave();
            if (settingsOwner is not null) settingsOwner.ShowStandaloneLibrary(id);
            else if (!review) TeamBarApplication.Current?.ShowStandaloneLibrary(id);
        };
        dockOrder.AddRequested += AddSavedDockButton;
        DockEditorHost.Children.Add(dockOrder); FillDockOrder();
        loadingDock = false;
    }
    private void AddDockColumn()
    {
        if (SelectedDock is not { } group || group.Columns >= ButtonColumns.MaxColumns) return;
        UpdateDock(group with { Columns = group.Columns + 1, ColumnNames = ButtonColumns.Names(group.ColumnNames, group.Columns + 1) }); FillDockOrder();
    }
    private void AddSavedDockButton(int column) => Act(() =>
    {
        if (SelectedDock is not { } original || column < 0 || column >= original.Columns) return;
        string overlayId = selected!.Id;
        if (!review) { savedDockButtons = ReadDockButtons(); dockLibraryError = null; }
        if (dockLibraryError is not null) throw new IOException(dockLibraryError);
        string? id = ChooseDockButton((W.Window?)settingsOwner ?? this, ButtonColumns.Names(original.ColumnNames, original.Columns)[column], savedDockButtons, original.ButtonIds);
        if (id is null || selected?.Id != overlayId || SelectedDock is not { } group || group.Id != original.Id || column >= group.Columns
            || group.ButtonIds.Contains(id) || !savedDockButtons.Any(b => b.Value.Id == id)) return;
        UpdateDock(group.WithLayout([.. group.Layout, new(id, column)])); FillDockOrder();
    });
    private void UpdateDock(SpellDockGroup group)
    {
        if (selected is null) return;
        Replace(selected with { DockGroups = (selected.DockGroups ?? []).Select(g => g.Id == group.Id ? group : g).ToArray() });
        if (dockGroupsChoice is not null)
        {
            bool wasLoading = loadingDock; loadingDock = true;
            dockGroupsChoice.ItemsSource = selected.DockGroups; dockGroupsChoice.SelectedItem = SelectedDock; loadingDock = wasLoading;
        }
    }
    private void FillDockOrder()
    {
        if (dockOrder is not null && SelectedDock is { } group)
            dockOrder.SetLayout(group.Layout, group.Columns, id => savedDockButtons.FirstOrDefault(b => b.Value.Id == id)?.Value.ManagementName ?? "Missing button", group.ColumnNames);
    }
}
