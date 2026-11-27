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
            var initialBody = Bounds(display.shell); var initialRight = Bounds(display.dockPanels["emergency"].Panel);
            check(display.dockRows.Count == 4 && Bounds(display.dockPanels["support"].Panel).Top >= initialBody.Bottom
                && initialRight.Left >= initialBody.Right, "docked groups are laid out below and beside the spell body in one native click surface");
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

            var editorRequests = new List<(SpellDockSide Side, string? Group, string? Id, int Column, bool AddColumn)>();
            W.Window? panel = null;
            display.OpenButtonEditor = (owner, anchor, edge, group, id, column, addColumn) =>
            {
                editorRequests.Add((edge, group, id, column, addColumn));
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
            for (int column = 0; column < support.Columns; column++)
            {
                var addRow = groupControls.Single(b => Name(b) == $"Add a row to {support.Name}, column {column + 1}");
                var rows = display.dockRows.Where(r => r.Group == support.Id && C.Grid.GetColumn(r.Border) == column).ToArray();
                check(Bounds(display.dockPanels[support.Id].Panel).Contains(Bounds(addRow))
                    && rows.All(r => Bounds(addRow).Top + 1 >= Bounds(r.Border).Bottom),
                    $"the row picker for column {column + 1} sits below its own buttons inside the group");
            }
            var addColumnButton = groupControls.Single(b => Name(b) == $"Add a column to {support.Name}");
            check(Bounds(display.dockPanels[support.Id].Panel).Contains(Bounds(addColumnButton))
                && display.dockRows.All(r => !Bounds(addColumnButton).IntersectsWith(Bounds(r.Border))),
                "the new-column control belongs to its group and leaves its current buttons unobstructed");

            definition = definition with { DockGroups = [support, new("empty-side", "Empty side", SpellDockSide.Left, []),
                new("empty-sibling", "Empty sibling", support.Side, []), new("missing", "Missing", SpellDockSide.Right, ["missing-id"])] };
            Draw(); await Settle();
            check(!display.dockPanels.ContainsKey("empty-side") && Edges().Count(b => Name(b) == "Add a button on the left") == 1
                && !display.dockPanels.ContainsKey("empty-sibling")
                && display.dockRows.Any(r => r.Group == "missing") && Edges().All(b => Name(b) != "Add a button on the right"),
                "empty groups disappear on both occupied and empty sides, with one add rail per empty side and missing references available to repair");
            definition = definition with { DockGroups = [support with { Columns = 6 }] };
            Draw(); await Settle();
            check(InGroup(support.Id).Count(b => Name(b).StartsWith("Add a row to ", StringComparison.Ordinal)) == 6
                && InGroup(support.Id).All(b => Name(b) != $"Add a column to {support.Name}" || !b.IsEnabled),
                "the six-column limit keeps every row picker available and prevents another column");
            definition = definition with { DockGroups = [support] };
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
                var png = new W.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(W.Media.Imaging.BitmapFrame.Create(SpellOverlayManagerWindow.OverlayBitmap(display)));
                using (var file = new FileStream(buttonPreviewPath, FileMode.CreateNew)) png.Save(file);
                display.SetDockButtons(buttons, clients); Draw();
                await Settle();
            }
            var add = Edges().Single(b => Name(b) == "Add a button on the left");
            add.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(editorRequests.Single() == (SpellDockSide.Left, (string?)null, (string?)null, 0, false) && panel is { IsVisible: true } && panel.Owner == display,
                "edge add opens an editor owned by the overlay on the requested side");
            var firstPanel = panel!;
            var rowPicker = InGroup(support.Id).Single(b => Name(b) == $"Add a row to {support.Name}, column 2");
            rowPicker.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(!firstPanel.IsVisible && editorRequests.Last() == (support.Side, support.Id, (string?)null, 1, false),
                "the row control addresses the selected group's second column");
            var columnPicker = InGroup(support.Id).Single(b => Name(b) == $"Add a column to {support.Name}");
            columnPicker.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(editorRequests.Last() == (support.Side, support.Id, (string?)null, support.Columns, true),
                "the column control opens a picker targeting the next column in the same group");
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
            UpdateDock(SelectedDock! with { Scale = 1.35 }); int previousColumns = SelectedDock!.Columns;
            dockTitle!.BeginEdit(); dockTitle.Editor.Text = " ";
            check(dockTitle.Commit() && SelectedDock is { Id: "support", Name: "" } && SelectedDock.EditorName.Contains("Bottom"),
                "Clearing the dock title preserves its selection and shows the edge in its settings label.");
            dockTitle.BeginEdit(); dockTitle.Editor.Text = "Renamed support"; dockTitle.Commit();
            dockSideChoice!.SelectedItem = SpellDockSide.Left; AddDockColumn();
            check(SelectedDock is { Name: "Renamed support", Scale: 1.35, Side: SpellDockSide.Left } && SelectedDock.Columns == previousColumns + 1 && selected.Scale == original.Scale,
                "group settings rename and add columns while preserving its existing size independently of the spell body");
            check(dockOrder!.EntryCount == 2 && savedDockButtons.Length == 4, "group editor retains ordered references and the saved button library");
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
