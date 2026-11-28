using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private readonly C.Grid dockRoot = new();
    private readonly Dictionary<SpellDockSide, C.StackPanel> dockSides = [];
    private readonly List<(C.Border Border, string Group, SpellDockButton Button)> dockRows = [];
    private readonly Dictionary<string, (C.Border Panel, C.Border Grip)> dockPanels = [];
    private SavedButton[] dockButtons = [];
    private DetectedClient[] dockClients = [];
    private string? dockError;
    private string dockLayoutKey = "";
    private EqIconArtwork? dockArtwork;
    private (C.TextBox Field, string GroupId, string Original)? dockTitleEdit;
    private void InitializeDockLayout()
    {
        for (int i = 0; i < 5; i++) { dockRoot.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); dockRoot.RowDefinitions.Add(new() { Height = W.GridLength.Auto }); }
        C.Grid.SetColumn(shell, 2); C.Grid.SetRow(shell, 2); dockRoot.Children.Add(shell);
        foreach (var side in Enum.GetValues<SpellDockSide>())
        {
            var stack = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Top, HorizontalAlignment = W.HorizontalAlignment.Left };
            C.Grid.SetColumn(stack, side == SpellDockSide.Left ? 1 : side == SpellDockSide.Right ? 3 : 2);
            C.Grid.SetRow(stack, side == SpellDockSide.Top ? 1 : side == SpellDockSide.Bottom ? 3 : 2);
            dockSides.Add(side, stack); dockRoot.Children.Add(stack);
        }
    }
    internal void SetDockButtons(SavedButton[] buttons, DetectedClient[] clients, string? error = null)
    { dockButtons = buttons; dockClients = clients; dockError = error; }
    private void RenderDockGroups(SpellOverlayDefinition definition)
    {
        var groups = (definition.DockGroups ?? []).Where(g => g.ButtonIds.Length > 0).ToArray();
        var entries = groups.SelectMany(g => g.ButtonIds).Distinct().ToDictionary(id => id, id => SpellDockRules.Describe(id, dockButtons, dockClients));
        var facesById = dockButtons.Where(b => entries.ContainsKey(b.Value.Id)).ToDictionary(b => b.Value.Id, b => new { b.Value.Icon, b.Value.ManagementName });
        var artwork = EqIconArtwork.Shared;
        string key = System.Text.Json.JsonSerializer.Serialize(new { groups, definition.Scale, edit = CanEdit, editingButtons, dockError, entries, facesById });
        if (key == dockLayoutKey && ReferenceEquals(dockArtwork, artwork)) return;
        if (editingButtons && IsMouseCaptureWithin) return;
        if (dockTitleEdit is { } active)
        {
            // Roster and availability updates rebuild the dock. Keep the live text field and its caret until the edit ends.
            if (editingButtons && groups.Any(g => g.Id == active.GroupId)) return;
            FinishDockTitle(save: false);
        }
        dockArtwork = artwork;
        dockLayoutKey = key; CancelSpellClick(); dockRows.Clear(); dockPanels.Clear(); dockDropGrids.Clear();
        foreach (var stack in dockSides.Values) stack.Children.Clear();
        foreach (var old in dockRoot.Children.OfType<C.Button>().ToArray()) dockRoot.Children.Remove(old);
        if (editingButtons)
        {
            var occupiedSides = groups.Select(g => g.Side).ToHashSet();
            foreach (var side in Enum.GetValues<SpellDockSide>().Where(side => !occupiedSides.Contains(side)))
            {
                var add = MakeAddButton($"Add a button on the {side.ToString().ToLowerInvariant()}", side);
                C.Grid.SetColumn(add, side == SpellDockSide.Left ? 0 : side == SpellDockSide.Right ? 4 : 2);
                C.Grid.SetRow(add, side == SpellDockSide.Top ? 0 : side == SpellDockSide.Bottom ? 4 : 2);
                dockRoot.Children.Add(add);
            }
        }
        foreach (var savedGroup in groups)
        {
            var group = savedGroup.CompactColumns();
            if (!editingButtons && !group.ButtonIds.Any(id => entries[id].Visible)) continue;
            var stack = new C.StackPanel();
            var header = new C.Grid { Margin = new(3, 3, 3, 0) };
            if (editingButtons) header.Children.Add(DockTitleField(group));
            else if (!string.IsNullOrWhiteSpace(group.Name))
                header.Children.Add(new C.TextBlock { Text = group.Name, FontSize = 11, Foreground = Brush(theme.Accent), Margin = new(0, 0, CanEdit ? 22 : 0, 8), TextTrimming = W.TextTrimming.CharacterEllipsis });
            var grip = new C.Border { Width = 22, Height = 22, Background = M.Brushes.Transparent, HorizontalAlignment = W.HorizontalAlignment.Right,
                VerticalAlignment = W.VerticalAlignment.Top, Cursor = I.Cursors.SizeNWSE,
                ToolTip = "Resize only this button group", Child = new C.TextBlock { Text = "⤢", Foreground = Brush(theme.Accent), FontSize = 20 } };
            W.Automation.AutomationProperties.SetName(grip, "Resize " + group.EditorName);
            if (CanEdit) header.Children.Add(grip);
            if (header.Children.Count > 0) stack.Children.Add(header);
            var buttons = new C.Grid();
            if (editingButtons) dockDropGrids.Add(buttons);
            for (int col = 0; col < group.Columns; col++) buttons.ColumnDefinitions.Add(new());
            var placements = group.PositionedLayout;
            // Legacy bars packed visible buttons together as recipients logged in and out.
            var visibleRows = !editingButtons && !group.Layout.Any(p => p.Row.HasValue) ? new int[group.Columns] : null;
            foreach (var placement in placements)
            {
                string id = placement.ButtonId;
                var entry = entries[id];
                if (!entry.Visible && !editingButtons) continue;
                var savedFace = facesById.GetValueOrDefault(id);
                string managementName = savedFace?.ManagementName ?? entry.Label;
                var conflictColor = FloatingPainter.ConflictColor(theme);
                var conflictBrush = new M.SolidColorBrush(M.Color.FromRgb(conflictColor.R, conflictColor.G, conflictColor.B));
                var conflictFill = FloatingPainter.Mix(FloatingThemes.Color(theme.Button), conflictColor, .16f);
                var button = new C.Border { Background = entry.HasConflict ? new M.SolidColorBrush(M.Color.FromRgb(conflictFill.R, conflictFill.G, conflictFill.B)) : Brush(theme.Button),
                    BorderBrush = entry.HasConflict ? conflictBrush : Brush(theme.ButtonBorder), BorderThickness = new(entry.HasConflict ? 2 : 1),
                    CornerRadius = new(5), Padding = new(8), Margin = new(3), MinHeight = 42, Opacity = editingButtons || entry.HasConflict || entry.Available && dockError is null ? 1 : .5,
                    ToolTip = editingButtons ? $"Click to edit {managementName} · Drag to move" : dockError ?? entry.Hint, Cursor = editingButtons || entry.Available ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow };
                var face = DockButtonFace(entry.Label, savedFace?.Icon, artwork, entry.HasConflict ? conflictBrush : Brush(theme.Text));
                if (editingButtons)
                {
                    var editButton = new C.Button { Content = face, Template = EditControlTemplate(), Background = M.Brushes.Transparent,
                        BorderThickness = new(0), Foreground = Brush(theme.Accent), Padding = new(8), Cursor = System.Windows.Input.Cursors.Hand };
                    W.Automation.AutomationProperties.SetName(editButton, "Edit " + managementName);
                    button.Padding = new(0); button.Child = editButton;
                    button.Tag = new DockDropTarget(savedGroup, placement.Column, id);
                    editButton.PreviewMouseLeftButtonDown += (_, e) => BeginDockButtonPress(editButton, savedGroup, id, e.GetPosition(this));
                    editButton.Click += (_, _) => RequestButtonEditor(button, group.Side, group.Id, id, placement.Column);
                }
                else button.Child = face;
                W.Automation.AutomationProperties.SetName(button, managementName);
                int row = visibleRows is null ? placement.Row!.Value : visibleRows[placement.Column]++;
                while (buttons.RowDefinitions.Count <= row) buttons.RowDefinitions.Add(new() { Height = W.GridLength.Auto, MinHeight = 48 });
                C.Grid.SetColumn(button, placement.Column); C.Grid.SetRow(button, row);
                buttons.Children.Add(button); dockRows.Add((button, group.Id, dockError is null ? entry : entry with { Available = false }));
            }
            double addColumnWidth = editingButtons && group.Columns < ButtonColumns.MaxColumns ? 32 : 0;
            if (editingButtons) AddColumnControls(buttons, savedGroup, placements, addColumnWidth);
            stack.Children.Add(buttons);
            var panel = new C.Border { Width = group.Columns * 112 + addColumnWidth, Background = Brush(theme.Bar), BorderBrush = Brush(theme.BarBorder),
                BorderThickness = new(1), CornerRadius = new(9), Child = stack };
            // The outer overlay Viewbox scales with the spell body. Inverse-scale
            // group layout so its own saved size remains independent of that body.
            panel.LayoutTransform = new M.ScaleTransform(group.Scale / definition.Scale, group.Scale / definition.Scale);
            panel.Margin = group.Side switch { SpellDockSide.Left => new(0, 0, 8, 8), SpellDockSide.Right => new(8, 0, 0, 8), SpellDockSide.Top => new(0, 0, 0, 8), _ => new(0, 8, 0, 0) };
            dockSides[group.Side].Children.Add(panel); dockPanels[group.Id] = (panel, grip);
        }
        measureNeeded = true;
    }

    private C.Grid DockTitleField(SpellDockGroup group)
    {
        var field = new C.TextBox { Text = group.Name, MaxLength = 40, FontSize = 11, Padding = new(5, 3, 5, 3),
            Background = Brush(theme.Button), Foreground = Brush(theme.Accent), CaretBrush = Brush(theme.Accent),
            BorderBrush = Brush(theme.ButtonBorder), BorderThickness = new(1), ToolTip = "Group name · Enter to save · Esc to cancel" };
        W.Automation.AutomationProperties.SetName(field, $"{group.Side} group name");
        var placeholder = new C.TextBlock { Text = "Group name", FontSize = 11, Margin = new(6, 0, 0, 0),
            VerticalAlignment = W.VerticalAlignment.Center, Foreground = Brush(theme.DetailText ?? theme.Text), Opacity = .7,
            IsHitTestVisible = false, Visibility = field.Text.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed };
        field.TextChanged += (_, _) => placeholder.Visibility = field.Text.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        field.GotKeyboardFocus += (_, _) => dockTitleEdit = (field, group.Id, field.Text);
        field.LostKeyboardFocus += (_, _) => { if (dockTitleEdit?.Field == field) FinishDockTitle(save: true); };
        field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (I.Key.Enter or I.Key.Escape)) return;
            e.Handled = true;
            FinishDockTitle(save: e.Key == I.Key.Enter);
            I.Keyboard.ClearFocus();
        };
        var header = new C.Grid { Margin = new(0, 0, 0, 5) };
        header.Children.Add(field); header.Children.Add(placeholder);
        return header;
    }

    private bool FinishDockTitle(bool save)
    {
        if (dockTitleEdit is not { } active) return true;
        dockTitleEdit = null;
        var text = active.Field.Text.Trim();
        if (!save || text == active.Original) { active.Field.Text = active.Original; return true; }
        bool saved = CommitLayout(new(GroupId: active.GroupId, GroupName: text, ExpectedGroupName: active.Original));
        active.Field.Text = saved ? text : currentDefinition?.DockGroups?.FirstOrDefault(g => g.Id == active.GroupId)?.Name ?? active.Original;
        buttonEditError = saved ? null : layoutError;
        UpdateClickHint();
        return saved;
    }

    private W.FrameworkElement DockButtonFace(string name, string? icon, EqIconArtwork artwork, M.Brush foreground)
    {
        bool hasName = !string.IsNullOrWhiteSpace(name);
        var label = new C.TextBlock { Foreground = foreground, FontWeight = W.FontWeights.SemiBold, TextWrapping = W.TextWrapping.Wrap,
            TextAlignment = W.TextAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center };
        if (artwork.Get(icon) is { } image)
        {
            var thumbnail = new C.Image { Source = image, Width = 24, Height = 24, Stretch = M.Stretch.Uniform,
                HorizontalAlignment = W.HorizontalAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center, IsHitTestVisible = false };
            M.RenderOptions.SetBitmapScalingMode(thumbnail, M.BitmapScalingMode.HighQuality);
            if (!hasName) return thumbnail;
            label.Text = name;
            thumbnail.Margin = new(0, 0, 5, 0);
            var content = new C.DockPanel(); content.Children.Add(thumbnail); content.Children.Add(label);
            return content;
        }
        string glyph = OverlayButtonIcons.Glyph(icon);
        if (glyph.Length == 0) label.Text = name;
        else
        {
            label.Inlines.Add(new W.Documents.Run(hasName ? glyph + "  " : glyph) { FontFamily = new("Segoe UI Symbol"), Foreground = Brush(theme.Accent) });
            if (hasName) label.Inlines.Add(new W.Documents.Run(name));
            else label.FontSize = 18;
        }
        return label;
    }

    private void AddColumnControls(C.Grid grid, SpellDockGroup group, ButtonPlacement[] placements, double addColumnWidth)
    {
        int height = grid.RowDefinitions.Count;
        int columns = grid.ColumnDefinitions.Count;
        var occupied = placements.Select(p => (p.Column, p.Row!.Value)).ToHashSet();
        grid.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        for (int col = 0; col < columns; col++)
        {
            for (int row = 0; row < height; row++)
            {
                if (occupied.Contains((col, row))) continue;
                AddAt($"Add a button to {group.EditorName}, column {col + 1}, row {row + 1}", col, row, row,
                    W.VerticalAlignment.Center);
            }
            AddAt($"Add a row to {group.EditorName}, column {col + 1}", col, height,
                occupied.Contains((col, height - 1)) ? null : height, W.VerticalAlignment.Top);
        }
        if (addColumnWidth > 0)
        {
            grid.ColumnDefinitions.Add(new() { Width = new W.GridLength(addColumnWidth) });
            for (int row = 0; row < height; row++)
            {
                string action = $"Add a column to {group.EditorName}" + (row == 0 ? "" : $" at row {row + 1}");
                AddAt(action, columns, row, row, W.VerticalAlignment.Stretch, newColumn: true);
            }
        }

        void AddAt(string action, int column, int gridRow, int? targetRow, W.VerticalAlignment alignment, bool newColumn = false)
        {
            var add = MakeAddButton(action, group.Side, group.Id, column, newColumn, targetRow);
            add.Tag = new DockDropTarget(group, column, NewColumn: newColumn, Row: targetRow);
            add.Margin = gridRow == height ? new(3, 2, 3, 3) : new(3);
            add.VerticalAlignment = alignment;
            C.Grid.SetColumn(add, column); C.Grid.SetRow(add, gridRow);
            grid.Children.Add(add);
        }
    }
    private string? ResizingDock => dockPanels.FirstOrDefault(p => p.Value.Grip.IsMouseOver).Key;
}
