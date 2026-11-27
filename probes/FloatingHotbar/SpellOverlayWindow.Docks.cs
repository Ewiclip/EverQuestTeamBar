using W = System.Windows;
using C = System.Windows.Controls;
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
        dockArtwork = artwork;
        dockLayoutKey = key; CancelSpellClick(); dockRows.Clear(); dockPanels.Clear();
        foreach (var stack in dockSides.Values) stack.Children.Clear();
        foreach (var old in dockRoot.Children.OfType<C.Button>().ToArray()) dockRoot.Children.Remove(old);
        if (editingButtons)
        {
            var occupiedSides = groups.Select(g => g.Side).ToHashSet();
            foreach (var side in Enum.GetValues<SpellDockSide>().Where(side => !occupiedSides.Contains(side)))
            {
                var add = MakeAddButton("+", $"Add a button on the {side.ToString().ToLowerInvariant()}", side);
                C.Grid.SetColumn(add, side == SpellDockSide.Left ? 0 : side == SpellDockSide.Right ? 4 : 2);
                C.Grid.SetRow(add, side == SpellDockSide.Top ? 0 : side == SpellDockSide.Bottom ? 4 : 2);
                dockRoot.Children.Add(add);
            }
        }
        foreach (var group in groups)
        {
            if (!editingButtons && !group.ButtonIds.Any(id => entries[id].Visible)) continue;
            var stack = new C.StackPanel();
            if (!string.IsNullOrWhiteSpace(group.Name))
                stack.Children.Add(new C.TextBlock { Text = group.Name.ToUpperInvariant(), FontSize = 11, Foreground = Brush(theme.Accent), Margin = new(0, 0, CanEdit ? 22 : 0, 8), TextTrimming = W.TextTrimming.CharacterEllipsis });
            var buttons = new C.Grid();
            for (int col = 0; col < group.Columns; col++) buttons.ColumnDefinitions.Add(new());
            var columnRows = new int[group.Columns];
            foreach (var placement in group.Layout)
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
                    ToolTip = editingButtons ? "Edit " + managementName : dockError ?? entry.Hint, Cursor = editingButtons || entry.Available ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow };
                var face = DockButtonFace(entry.Label, savedFace?.Icon, artwork, entry.HasConflict ? conflictBrush : Brush(theme.Text));
                if (editingButtons)
                {
                    var editButton = new C.Button { Content = face, Template = EditControlTemplate(), Background = M.Brushes.Transparent,
                        BorderThickness = new(0), Foreground = Brush(theme.Accent), Padding = new(8), Cursor = System.Windows.Input.Cursors.Hand };
                    W.Automation.AutomationProperties.SetName(editButton, "Edit " + managementName);
                    button.Padding = new(0); button.Child = editButton;
                    editButton.Click += (_, _) => RequestButtonEditor(button, group.Side, group.Id, id, placement.Column);
                }
                else button.Child = face;
                W.Automation.AutomationProperties.SetName(button, managementName);
                int row = columnRows[placement.Column]++;
                while (buttons.RowDefinitions.Count <= row) buttons.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
                C.Grid.SetColumn(button, placement.Column); C.Grid.SetRow(button, row);
                buttons.Children.Add(button); dockRows.Add((button, group.Id, dockError is null ? entry : entry with { Available = false }));
            }
            if (editingButtons) AddColumnControls(buttons, group, columnRows);
            stack.Children.Add(buttons);
            var grid = new C.Grid(); grid.Children.Add(stack);
            var grip = new C.Border { Width = 22, Height = 22, Background = M.Brushes.Transparent, HorizontalAlignment = W.HorizontalAlignment.Right,
                VerticalAlignment = W.VerticalAlignment.Bottom, Cursor = System.Windows.Input.Cursors.SizeNWSE, Visibility = CanEdit ? W.Visibility.Visible : W.Visibility.Collapsed,
                ToolTip = "Resize only this button group", Child = new C.TextBlock { Text = "⤢", Foreground = Brush(theme.Accent), FontSize = 20 } };
            if (!editingButtons) stack.Margin = new(0, 0, 0, 18);
            W.Automation.AutomationProperties.SetName(grip, "Resize " + group.EditorName); grid.Children.Add(grip);
            var panel = new C.Border { Width = group.Columns * 112 + 20, Background = Brush(theme.Bar), BorderBrush = Brush(theme.BarBorder),
                BorderThickness = new(1), CornerRadius = new(9), Padding = new(10), Child = grid };
            // The outer overlay Viewbox scales with the spell body. Inverse-scale
            // group layout so its own saved size remains independent of that body.
            panel.LayoutTransform = new M.ScaleTransform(group.Scale / definition.Scale, group.Scale / definition.Scale);
            panel.Margin = group.Side switch { SpellDockSide.Left => new(0, 0, 8, 8), SpellDockSide.Right => new(8, 0, 0, 8), SpellDockSide.Top => new(0, 0, 0, 8), _ => new(0, 8, 0, 0) };
            dockSides[group.Side].Children.Add(panel); dockPanels[group.Id] = (panel, grip);
        }
        measureNeeded = true;
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

    private void AddColumnControls(C.Grid grid, SpellDockGroup group, int[] columnRows)
    {
        grid.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        for (int column = 0; column < group.Columns; column++)
        {
            var controls = new C.DockPanel { Margin = new(2, 1, 2, 0), VerticalAlignment = W.VerticalAlignment.Top };
            if (column == group.Columns - 1 && group.Columns < ButtonColumns.MaxColumns)
            {
                var addColumn = MakeAddButton("+ Column", $"Add a column to {group.EditorName}", group.Side, group.Id,
                    group.Columns, addColumn: true);
                C.DockPanel.SetDock(addColumn, C.Dock.Right);
                controls.Children.Add(addColumn);
            }
            controls.Children.Add(MakeAddButton("+ Row", $"Add a row to {group.EditorName}, column {column + 1}",
                group.Side, group.Id, column));
            C.Grid.SetColumn(controls, column); C.Grid.SetRow(controls, columnRows[column]);
            grid.Children.Add(controls);
        }
    }
    private string? ResizingDock => dockPanels.FirstOrDefault(p => p.Value.Grip.IsMouseOver).Key;
}
