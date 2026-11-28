using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    internal static async Task TestDockedControls(Action<bool, string> check, string? buttonPreviewPath = null)
    {
        var (baseDefinition, capture, catalog) = SpellOverlayTests.GroupFixture();
        var definition = baseDefinition with { Character = "Fellwort", PersonaClass = "Shaman", Locked = false, X = 0, Y = 0, DockGroups = SpellDockTests.Groups() };
        string? opened = null; int saves = 0;
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1], (_, change) => { saves++; return definition = change.Apply(definition); }, id => opened = id);
        var requests = new List<SpellClickRequest>();
        try
        {
            var clients = SpellDockTests.Clients(); var buttons = SpellDockTests.Buttons();
            display.SetDockButtons(buttons, clients);
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var frame = new GameFrame("dock-fixture", "Fixture", new(area.X, area.Y, 1800, 1200));
            var state = SpellOverlayRules.Evaluate(baseDefinition, capture, catalog: catalog);
            void Draw() { display.Render(definition, state); display.Place(definition, frame); display.UpdateLayout(); }
            async Task Settle() => await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            Draw(); await Settle();
            W.Rect Bounds(W.FrameworkElement element) => element.TransformToAncestor(display).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            static W.Rect InPanel(C.Border panel, W.FrameworkElement element) =>
                element.TransformToAncestor(panel).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            static bool Three(double gap) => Math.Abs(gap - 3) < 1; // Layout rounding varies with monitor DPI.
            static double BottomGap(C.Border panel, W.FrameworkElement child) =>
                panel.ActualHeight - panel.BorderThickness.Bottom - child.TranslatePoint(new(0, child.ActualHeight), panel).Y;
            bool HasCompactFooter(string groupId) => Math.Abs(display.dockRows.Where(r => r.Group == groupId)
                .Min(r => BottomGap(display.dockPanels[groupId].Panel, r.Border)) - 3) < 1;
            var initialBody = Bounds(display.shell); var initialRight = Bounds(display.dockPanels["emergency"].Panel);
            check(display.dockRows.Count == 4 && Bounds(display.dockPanels["support"].Panel).Top >= initialBody.Bottom
                && initialRight.Left >= initialBody.Right, "docked groups are laid out below and beside the spell body in one native click surface");
            check(display.dockPanels.All(pair => HasCompactFooter(pair.Key) && pair.Value.Grip.IsVisible &&
                display.dockRows.Where(r => r.Group == pair.Key).All(r => Bounds(pair.Value.Grip).Bottom <= Bounds(r.Border).Top)),
                "Unlocked dock sections leave three DIPs below the last button and keep their resize handles above all buttons.");
            foreach (var group in definition.DockGroups!)
            {
                var dockPanel = display.dockPanels[group.Id].Panel;
                var faces = display.dockRows.Where(r => r.Group == group.Id).Select(r => InPanel(dockPanel, r.Border)).ToArray();
                var title = SpellOverlayManagerWindow.OverlayTestControls(dockPanel).OfType<C.TextBlock>().Single(t => t.Text == group.Name);
                check(Three(faces.Min(r => r.Left) - dockPanel.BorderThickness.Left) &&
                    Three(dockPanel.ActualWidth - dockPanel.BorderThickness.Right - faces.Max(r => r.Right)) &&
                    Three(InPanel(dockPanel, title).Top - dockPanel.BorderThickness.Top) &&
                    faces.All(r => r.Width >= 103 && r.Height >= 41),
                    $"The {group.Name} dock has three-DIP side and top insets and retains full-size buttons.");
            }
            var hidden = buttons.Select(b => b.Value.Id == "invis" ? b with { Value = b.Value with { ShowFor = [new(Class: "Cleric")] } } : b).ToArray();
            display.SetDockButtons(hidden, clients); Draw();
            check(display.dockRows.All(r => r.Button.Id != "invis")
                && C.Grid.GetColumn(display.dockRows.Single(r => r.Group == "support" && r.Button.Id == "assist").Border) == 1,
                "hiding a persona-specific button leaves its neighbor in the assigned column");
            display.SetDockButtons(buttons, clients); Draw();
            check(display.BeginLayoutEdit(false, new(100, 100), "support"), "unlocked group exposes an independent resize gesture");
            display.UpdateLayoutEdit(new(150, 125)); display.EndLayoutEdit(false); await Settle(); Draw();
            check(saves == 1 && definition.DockGroups![0].Scale > .9 && Math.Abs(Bounds(display.shell).Width - initialBody.Width) < 2
                && Math.Abs(Bounds(display.dockPanels["emergency"].Panel).Width - initialRight.Width) < 2,
                "group resizing saves once without resizing the spell body or its neighbor");
            double groupWidth = Bounds(display.dockPanels["support"].Panel).Width;
            definition = definition with { Scale = 1.3 }; Draw(); await Settle();
            check(Math.Abs(Bounds(display.dockPanels["support"].Panel).Width - groupWidth) < 2 && Bounds(display.shell).Width > initialBody.Width,
                "changing spell-body size preserves independently sized dock groups");
            foreach (var side in Enum.GetValues<SpellDockSide>())
            {
                definition = definition with { DockGroups = [definition.DockGroups![0] with { Side = side }] }; Draw();
                var group = Bounds(display.dockPanels["support"].Panel); var body = Bounds(display.shell);
                check(side switch { SpellDockSide.Left => group.Right <= body.Left + 1, SpellDockSide.Right => group.Left >= body.Right - 1,
                    SpellDockSide.Top => group.Bottom <= body.Top + 1, _ => group.Top >= body.Bottom - 1 }, "group docks on the requested edge: " + side);
            }
            display.ToggleLayoutLock(true); Draw();
            check(display.dockPanels.All(pair => HasCompactFooter(pair.Key) && !pair.Value.Grip.IsVisible)
                && !display.BeginLayoutEdit(false, new(100, 100), "support"),
                "Locked dock sections keep the three-DIP bottom inset and disable their resize handles.");
            var named = definition.DockGroups!.Single();
            definition = definition with { DockGroups = [named with { Name = "" }] }; Draw();
            var untitledPanel = display.dockPanels[named.Id].Panel;
            check(Three(display.dockRows.Min(r => InPanel(untitledPanel, r.Border).Top) - untitledPanel.BorderThickness.Top),
                "A locked dock with a blank title leaves three DIPs above its first row.");
            definition = definition with { DockGroups = [named] }; Draw();
            var gear = ((C.Primitives.UniformGrid)display.LayoutLock!.Content).Children.OfType<C.Button>().First();
            gear.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(opened == definition.Id && saves == 2, "settings gear opens its own bar without altering the lock or saved layout");
            display.SetClickContext(capture with { Target = null });
            display.SetClickConnection(true, false, "Ready", request => { requests.Add(request); return Task.FromResult<SpellClickReply?>(new(true, "Fixture only", false)); });
            var row = display.dockRows[0].Border;
            var point = row.PointToScreen(new(row.ActualWidth / 2, row.ActualHeight / 2)); var screen = new System.Drawing.Point((int)point.X, (int)point.Y);
            display.RoutedSpellMouse(0x201, screen); check(requests.Count == 0, "docked button down alone cannot dispatch");
            display.RoutedSpellMouse(0x202, screen); await Settle();
            check(requests.Count == 1 && requests[0].Kind == "dock-tap" && requests[0].ButtonId == "invis" && requests[0].GroupId == "support",
                "docked button sends one saved-button request on release even with no target");
            display.RoutedSpellMouse(0x202, screen); check(requests.Count == 1, "docked button release without another press cannot repeat");
            display.RoutedSpellMouse(0x201, screen);
            display.SetDockButtons(SpellDockTests.WithConflict(buttons), clients); Draw(); display.RoutedSpellMouse(0x202, screen);
            var conflictedRow = display.dockRows.Single(r => r.Button.Id == "invis");
            var conflictColor = ((System.Windows.Media.SolidColorBrush)conflictedRow.Border.BorderBrush).Color;
            var expectedColor = FloatingPainter.ConflictColor(display.theme);
            check(conflictedRow.Button.HasConflict && !conflictedRow.Button.Available && conflictedRow.Border.Opacity == 1
                && conflictColor.R == expectedColor.R && conflictColor.G == expectedColor.G && conflictColor.B == expectedColor.B
                && display.dockRows.Single(r => r.Button.Id == "assist").Button.Available && requests.Count == 1,
                "a new dock conflict cancels a held gesture and visibly marks only the disabled button red");
            display.RoutedSpellMouse(0x201, screen); display.RoutedSpellMouse(0x202, screen);
            check(requests.Count == 1, "clicking a red conflicted dock button never sends an input request");
            display.SetDockButtons(buttons, clients); Draw();
            check(display.dockRows.All(r => r.Button.Available && !r.Button.HasConflict),
                "repairing a mapping restores normal dock button appearance without toggling the overlay");
            display.RoutedSpellMouse(0x201, screen); display.SetDockButtons([], clients); Draw(); display.RoutedSpellMouse(0x202, screen);
            check(requests.Count == 1 && display.dockRows.All(r => !r.Button.Available), "removing a saved button cancels a held click and disables its placeholder");

            var editorRequests = new List<(SpellDockSide Side, string? Group, string? Id, int Column, bool AddColumn, int? Row)>();
            W.Window? panel = null;
            display.OpenButtonEditor = (owner, anchor, edge, group, id, column, addColumn, targetRow) =>
            {
                editorRequests.Add((edge, group, id, column, addColumn, targetRow));
                panel = new W.Window { Width = 230, Height = 100, WindowStyle = W.WindowStyle.None, ShowInTaskbar = false };
                owner.ShowButtonEditor(panel, anchor, edge);
            };
            display.SetDockButtons(buttons, clients); Draw();
            row = display.dockRows.First().Border;
            point = row.PointToScreen(new(row.ActualWidth / 2, row.ActualHeight / 2)); screen = new((int)point.X, (int)point.Y);
            display.SetClickConnection(true, false, "Ready", request => { requests.Add(request); return Task.FromResult<SpellClickReply?>(new(true, "Fixture")); });
            display.RoutedSpellMouse(0x201, screen);
            var pencil = ((C.Primitives.UniformGrid)display.LayoutLock.Content).Children.OfType<C.Button>()
                .Single(b => W.Automation.AutomationProperties.GetName(b) == "Edit buttons");
            pencil.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            display.RoutedSpellMouse(0x202, screen);
            check(definition.Locked && display.editingButtons && !display.ClickSurface && display.clickShield is null && requests.Count == 1,
                "pencil editing preserves the movement lock and cancels a pending gameplay click");
            display.UpdateLayout(); await Settle();
            C.Button[] Edges() => display.dockRoot.Children.OfType<C.Button>().ToArray();
            static string Name(C.Button b) => W.Automation.AutomationProperties.GetName(b);
            C.Button[] InGroup(string id) => SpellOverlayManagerWindow.OverlayTestControls(display.dockPanels[id].Panel).OfType<C.Button>().ToArray();
            var support = definition.DockGroups!.Single();
            var emptySides = Enum.GetValues<SpellDockSide>().Where(s => s != support.Side)
                .Select(s => "Add a button on the " + s.ToString().ToLowerInvariant());
            check(Edges().Select(Name).ToHashSet().SetEquals(emptySides) && Edges().Length == 3 && Name(pencil) == "Done editing buttons",
                "edit mode offers one edge picker per empty direction and omits the rail on the occupied direction");
            check(Edges().All(edge => display.dockPanels.Values.All(group => !Bounds(edge).IntersectsWith(Bounds(group.Panel)))),
                "each empty-direction rail stays clear of populated groups");
            var groupControls = InGroup(support.Id);
            var groupBounds = Bounds(display.dockPanels[support.Id].Panel);
            var groupRows = display.dockRows.Where(r => r.Group == support.Id).ToArray();
            for (int column = 0; column < support.Columns; column++)
            {
                var addRow = groupControls.Single(b => Name(b) == $"Add a row to {support.Name}, column {column + 1}");
                var rowPickerBounds = Bounds(addRow);
                check(Math.Abs(BottomGap(display.dockPanels[support.Id].Panel, addRow) - 3) < 1,
                    $"The edit control below column {column + 1} ends three DIPs inside the group border.");
                var rows = groupRows.Where(r => C.Grid.GetColumn(r.Border) == column).Select(r => Bounds(r.Border)).ToArray();
                check(Equals(addRow.Content, "+") && Equals(addRow.ToolTip, Name(addRow)) && groupBounds.Contains(rowPickerBounds)
                    && rows.All(r => rowPickerBounds.Top + 1 >= r.Bottom
                        && Math.Abs((rowPickerBounds.Left + rowPickerBounds.Right - r.Left - r.Right) / 2) <= 1),
                    $"the row + is centered below column {column + 1} and identifies its action on hover");
            }
            var addColumnButton = groupControls.Single(b => Name(b) == $"Add a column to {support.Name}");
            var editPanel = display.dockPanels[support.Id].Panel;
            var editTitle = SpellOverlayManagerWindow.OverlayTestControls(editPanel).OfType<C.TextBox>().Single();
            check(Three(InPanel(editPanel, editTitle).Top - editPanel.BorderThickness.Top) &&
                Three(InPanel(editPanel, editTitle).Left - editPanel.BorderThickness.Left) &&
                Three(editPanel.ActualWidth - editPanel.BorderThickness.Right - InPanel(editPanel, addColumnButton).Right),
                "The editable group name and right-hand add control follow the three-DIP inset.");
            var columnPickerBounds = Bounds(addColumnButton);
            var rightmostButton = Bounds(groupRows.First(r => C.Grid.GetColumn(r.Border) == support.Columns - 1).Border);
            check(Equals(addColumnButton.Content, "+") && Equals(addColumnButton.ToolTip, Name(addColumnButton))
                && groupBounds.Contains(columnPickerBounds) && columnPickerBounds.Left + 1 >= rightmostButton.Right
                && columnPickerBounds.Bottom > rightmostButton.Top && columnPickerBounds.Top < rightmostButton.Bottom,
                "the column + sits to the right of the last button, alongside the button row, and identifies its action on hover");

            definition = definition with { DockGroups = [support, new("empty-side", "Empty side", SpellDockSide.Left, []),
                new("empty-sibling", "Empty sibling", support.Side, []), new("missing", "Missing", SpellDockSide.Right, ["missing-id"])] };
            Draw(); await Settle();
            check(!display.dockPanels.ContainsKey("empty-side") && Edges().Count(b => Name(b) == "Add a button on the left") == 1
                && !display.dockPanels.ContainsKey("empty-sibling")
                && display.dockRows.Any(r => r.Group == "missing") && Edges().All(b => Name(b) != "Add a button on the right"),
                "empty groups disappear on both occupied and empty sides, with one add rail per empty side and missing references available to repair");
            var oneColumn = (support with { Columns = 6 }).WithLayout([new("invis", 4), new("assist", 4), new("heal", 4)]);
            definition = definition with { DockGroups = [oneColumn] };
            Draw(); await Settle();
            var singleRowPlus = InGroup(support.Id).Single(b => Name(b).StartsWith("Add a row to ", StringComparison.Ordinal));
            var columnPluses = InGroup(support.Id).Where(b => Name(b).StartsWith($"Add a column to {support.Name}", StringComparison.Ordinal)).ToArray();
            check(C.Grid.GetColumn(singleRowPlus) == 0 && C.Grid.GetRow(singleRowPlus) == 3 &&
                columnPluses.All(b => C.Grid.GetColumn(b) == 1 && C.Grid.GetRowSpan(b) == 1) &&
                columnPluses.Select(C.Grid.GetRow).Order().SequenceEqual([0, 1, 2])
                && display.dockRows.All(row => C.Grid.GetColumn(row.Border) == 0),
                "three stacked buttons have one append control below them and one new-column target beside each button");
            var staggered = support.WithLayout([new("invis", 0, 0), new("assist", 1, 2)]);
            definition = definition with { DockGroups = [staggered] };
            Draw(); await Settle();
            display.ToggleButtonEditing(); Draw(); await Settle();
            var upper = display.dockRows.Single(r => r.Button.Id == "invis").Border;
            var lower = display.dockRows.Single(r => r.Button.Id == "assist").Border;
            var playGrid = (C.Grid)lower.Parent;
            check(C.Grid.GetRow(lower) == 2 && playGrid.RowDefinitions[1].ActualHeight >= 42 &&
                Bounds(lower).Top > Bounds(upper).Bottom && !playGrid.Children.OfType<C.Button>().Any(),
                "The normal overlay preserves a completely empty row between staggered buttons and hides its edit targets.");
            var nearlyFull = support.WithLayout([new("invis", 0, 0), new("assist", 1, 0), new("heal", 0, 1)]);
            var filled = nearlyFull.WithLayout(ButtonColumns.Insert(nearlyFull.Layout, new("cure", 1, 1), null));
            definition = definition with { DockGroups = [filled] };
            display.SetDockButtons(hidden, clients); Draw(); await Settle();
            check(display.dockRows.All(r => r.Button.Id != "invis") &&
                C.Grid.GetRow(display.dockRows.Single(r => r.Button.Id == "heal").Border) == 1 &&
                C.Grid.GetRow(display.dockRows.Single(r => r.Button.Id == "cure").Border) == 1,
                "Filling the last empty cell keeps both lower buttons aligned when a conditional button above them disappears.");
            display.SetDockButtons(buttons, clients);
            display.ToggleButtonEditing();
            var sparse = (support with { Columns = 6 }).WithLayout([new("invis", 1), new("assist", 4)]);
            definition = definition with { DockGroups = [sparse] };
            Draw(); await Settle();
            check(InGroup(support.Id).Count(b => Name(b).StartsWith("Add a row to ", StringComparison.Ordinal)) == 2
                && InGroup(support.Id).Count(b => Name(b) == $"Add a column to {support.Name}" && b.IsEnabled) == 1
                && display.dockRows.Select(r => C.Grid.GetColumn(r.Border)).SequenceEqual([0, 1])
                && Math.Abs(Bounds(display.dockPanels[support.Id].Panel).Width - groupBounds.Width) < 1,
                "leading, middle and trailing empty columns use no overlay space or row controls");
            var full = support with { Columns = 6, ButtonIds = ["invis", "assist", "heal", "cure", "travel", "rally"] };
            definition = definition with { DockGroups = [full] };
            Draw(); await Settle();
            check(InGroup(support.Id).Count(b => Name(b).StartsWith("Add a row to ", StringComparison.Ordinal)) == 6
                && InGroup(support.Id).All(b => Name(b) != $"Add a column to {support.Name}"),
                "six occupied columns keep all six row pickers and omit the add-column control");
            definition = definition with { DockGroups = [sparse] };
            Draw(); await Settle();
            if (buttonPreviewPath is not null)
            {
                var preview = definition with { Name = "Wayfarer", Character = "Alder", PersonaClass = "Shadowknight",
                    Scale = 1, Opacity = 1, Width = 340,
                    DockGroups = [new("combat", "Combat", SpellDockSide.Top, ["assist", "cure", "heal"], Columns: 2),
                        new("travel", "Travel", SpellDockSide.Left, ["invis"]), new("empty", "Empty group", SpellDockSide.Bottom, [])] };
                display.SetDockButtons(buttons.Select(b => b with { Value = b.Value with { Icon = "swords" } }).ToArray(), clients);
                display.Render(preview, new("a gnoll thug", "", [])); display.Place(preview, frame);
                await Settle();
                void SavePreview(string path)
                {
                    var png = new W.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(W.Media.Imaging.BitmapFrame.Create(SpellOverlayManagerWindow.OverlayBitmap(display)));
                    using var file = new FileStream(path, FileMode.CreateNew); png.Save(file);
                }
                SavePreview(buttonPreviewPath);
                var face = (C.Button)display.dockRows.Single(r => r.Group == "combat" && r.Button.Id == "cure").Border.Child;
                var origin = face.TranslatePoint(new W.Point(face.ActualWidth / 2, face.ActualHeight / 2), display);
                display.BeginDockButtonPress(face, preview.DockGroups![0], "cure", origin);
                display.UpdateDockButtonDrag(origin + new W.Vector(W.SystemParameters.MinimumHorizontalDragDistance + 1, 0));
                display.UpdateLayout();
                var slot = SpellOverlayManagerWindow.OverlayTestControls(display).OfType<W.Shapes.Rectangle>()
                    .Single(r => r.Tag is DockButtonMove { TargetGroup.Id: "combat", Column: 0, BeforeButtonId: "heal" });
                var rightSlot = display.dockDropSlots.Single(r => r.Tag is DockButtonMove { TargetGroup.Id: "combat", NewColumn: true, Row: 0 });
                var dragPanel = display.dockPanels["combat"].Panel;
                check(Three(InPanel(dragPanel, slot).Left - dragPanel.BorderThickness.Left) &&
                    Three(dragPanel.ActualWidth - dragPanel.BorderThickness.Right - InPanel(dragPanel, rightSlot).Right),
                    "The left and right dotted drop targets retain the compact inset while dragging.");
                var target = slot.TranslatePoint(new W.Point(slot.ActualWidth * .8, slot.ActualHeight / 2), display);
                display.UpdateDockButtonDrag(target); display.UpdateLayout();
                SavePreview(Path.ChangeExtension(buttonPreviewPath, "drag.png"));
                display.EndDockButtonDrag();
                display.SetDockButtons(buttons, clients); Draw();
                await Settle();
            }
            var add = Edges().Single(b => Name(b) == "Add a button on the left");
            add.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(editorRequests.Single() == (SpellDockSide.Left, (string?)null, (string?)null, 0, false, (int?)null) && panel is { IsVisible: true } && panel.Owner == display,
                "edge add opens an editor owned by the overlay on the requested side");
            var firstPanel = panel!;
            var rowPicker = InGroup(support.Id).Single(b => Name(b) == $"Add a row to {support.Name}, column 2");
            rowPicker.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(!firstPanel.IsVisible && editorRequests.Last() == (support.Side, support.Id, (string?)null, 1, false, (int?)null),
                "the second visible row + in a sparse saved group targets its second compacted column");
            var columnPicker = InGroup(support.Id).Single(b => Name(b) == $"Add a column to {support.Name}");
            columnPicker.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(editorRequests.Last() == (support.Side, support.Id, (string?)null, support.Columns, true, 0),
                "the right-hand + in a sparse saved group targets the next contiguous column");
            var lastPicker = panel!;
            var editable = display.dockRows[0];
            ((C.Button)editable.Border.Child).RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(!lastPicker.IsVisible && panel != lastPicker && panel!.IsVisible && editorRequests.Last().Side == support.Side
                && editorRequests.Last().Group == editable.Group && editorRequests.Last().Id == editable.Button.Id && !editorRequests.Last().AddColumn,
                "clicking a placed button edits exactly its group reference and replaces the previous panel");
            display.RoutedSpellMouse(0x201, screen); display.RoutedSpellMouse(0x202, screen);
            check(requests.Count == 1, "an edit-mode routed press and release cannot dispatch spell or dock input");
            check(EditorPosition(new(-100,40,20,20), new(500,300), new(-1000,0,1000,800), SpellDockSide.Right) == new System.Drawing.Point(-605,40),
                "a popover at the right screen edge opens to the left on a negative-coordinate monitor");
            display.Place(definition, null);
            check(!display.IsVisible && !display.LayoutLock.IsVisible && !panel!.IsVisible && !display.editingButtons,
                "the overlay and its active editor close together when the character's game disappears");
        }
        finally { display.Close(); }
    }
}

public partial class SpellOverlayManagerWindow
{
    private void TestAliasAndDockEditor(Action<bool, string> check)
    {
        var original = selected!; bool oldDirty = dirty; var oldButtons = savedDockButtons; var oldConfirm = ConfirmGroupDeletion;
        try
        {
            Replace(original with { DockGroups = SpellDockTests.Groups() }); savedDockButtons = SpellDockTests.Buttons(); LoadEditor();
            SelectRule(0); SpellAlias.Text = "My spell";
            check(selected!.Spells[0].Alias == "My spell" && selected.Spells[0].Name == original.Spells[0].Name
                && WatchList.Items.OfType<SpellWatchChoice>().First().Name == "My spell", "alias editor updates the watched label without replacing spell identity");
            SpellAlias.Text = ""; check(selected.Spells[0].Alias is null, "clearing the alias restores the normal name");
            dockSelection = "support"; LoadDockEditor();
            UpdateDock(SelectedDock! with { Scale = 1.35, ButtonIds = ["invis", "assist", "heal"] }); int previousColumns = SelectedDock!.Columns;
            var legacyCells = SelectedDock.PositionedLayout;
            dockTitle!.BeginEdit(); dockTitle.Editor.Text = " ";
            check(dockTitle.Commit() && SelectedDock is { Id: "support", Name: "" } && SelectedDock.EditorName.Contains("Bottom"),
                "Clearing the dock title preserves its selection and shows the edge in its settings label.");
            dockTitle.BeginEdit(); dockTitle.Editor.Text = "Renamed support"; dockTitle.Commit();
            dockSideChoice!.SelectedItem = SpellDockSide.Left; AddDockColumn();
            check(SelectedDock is { Name: "Renamed support", Scale: 1.35, Side: SpellDockSide.Left } && SelectedDock.Columns == previousColumns + 1 && selected.Scale == original.Scale,
                "group settings rename and add columns while preserving its existing size independently of the spell body");
            check(dockOrder!.EntryCount == 3 && savedDockButtons.Length == 4 &&
                SelectedDock!.PositionedLayout.ToHashSet().SetEquals(legacyCells),
                "Adding a column in Settings keeps all original cells of a legacy two-column group, including its second row.");
            var groupPicker = DockEditorHost.Children.OfType<C.DockPanel>().Single();
            var remove = groupPicker.Children.OfType<C.Button>().Single();
            var before = selected!.DockGroups!;
            DeleteRequest? request = null;
            ConfirmGroupDeletion = (_, r) => { request = r; return false; };
            remove.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(request?.Name == before.Single(g => g.Id == "support").Name && selected.DockGroups!.SequenceEqual(before),
                "canceling the dock-group X dialog preserves the selected group and its neighbors");
            ConfirmGroupDeletion = (_, _) => true;
            remove.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(selected.DockGroups!.SequenceEqual(before.Where(g => g.Id != "support")) && savedDockButtons.Length == 4,
                "confirming dock-group deletion removes only that group and preserves the saved button library");
        }
        finally { ConfirmGroupDeletion = oldConfirm; savedDockButtons = oldButtons; Replace(original); dirty = oldDirty; LoadEditor(); }
    }
}
