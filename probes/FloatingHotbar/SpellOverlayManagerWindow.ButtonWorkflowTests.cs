using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using Interop = System.Windows.Interop;
using I = System.Windows.Input;
using DropRectangle = System.Windows.Shapes.Rectangle;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private const string RestartedDockTitle = "Party Utility";

    internal static IEnumerable<W.DependencyObject> OverlayTestControls(W.DependencyObject parent)
    {
        for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = M.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in OverlayTestControls(child)) yield return descendant;
        }
    }

    private async Task TestOverlayButtonWorkflow(string overlayId, string profile, Action<bool, string> check)
    {
        var bar = floating[overlayId];
        static T Control<T>(W.Window window, string name) where T : W.UIElement
        {
            window.UpdateLayout();
            return OverlayTestControls(window).OfType<T>().First(c => W.Automation.AutomationProperties.GetName(c) == name);
        }
        static void Click(W.Window window, string name)
        {
            var button = Control<C.Button>(window, name);
            button.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent, button));
        }
        OverlayButtonEditor Editor() => bar.OwnedWindows.OfType<OverlayButtonEditor>().Single(w => w.IsVisible);
        void CreateButton(string name, string key)
        {
            Click(Editor(), "+  Create button…");
            var editor = Editor();
            Control<C.TextBox>(editor, "Button name").Text = name;
            Click(editor, "Record shortcut");
            nint handle = new Interop.WindowInteropHelper(editor).Handle;
            var message = new Interop.MSG { hwnd = handle, message = 0x0100, lParam = InputKeys.Scan(key) << 16 };
            Interop.ComponentDispatcher.RaiseThreadMessage(ref message);
            message.message = 0x0101;
            Interop.ComponentDispatcher.RaiseThreadMessage(ref message);
            Click(editor, "Add to bar");
            if (editor.IsVisible) throw new InvalidOperationException($"Creating {name} failed: {editor.ErrorMessage.Text}");
        }
        async Task Draw()
        {
            while (polling) await Task.Delay(10);
            await Poll();
            bar.UpdateLayout();
        }
        await Draw();
        bar.ToggleButtonEditing();
        Click(bar, "Add a button on the left");
        Click(Editor(), "Assist fixture");
        await Draw();
        var linked = savedLibrary.Overlays.Single(d => d.Id == overlayId).DockGroups!.Single(g => g.Side == SpellDockSide.Left);
        check(linked.ButtonIds.SequenceEqual(["assist"]) && !IsVisible, "The overlay edge picker links the chosen saved button while Settings stays closed.");
        Click(bar, "Add a button on the right");
        Click(Editor(), "Assist fixture");
        await Draw();
        Click(bar, "Edit Assist fixture");
        var editor = Editor();
        Control<C.TextBox>(editor, "Button name").Text = "My assist";
        Click(editor, "Done");
        await Draw();
        SpellDockGroup[] Groups() => SpellOverlayStore.Load(path).Library.Overlays.Single(d => d.Id == overlayId).DockGroups!;
        check(Groups().Count(g => g.ButtonIds.SequenceEqual(["assist"])) == 2
            && StandaloneButtons.Load(profile).Single().Value.Label == "My assist"
            && OverlayTestControls(bar).OfType<C.Button>().Count(b => W.Automation.AutomationProperties.GetName(b) == "Edit My assist") == 2,
            "Editing a shared button updates both visible placements and preserves its single saved definition.");
        Click(bar, "Edit My assist");
        Click(Editor(), "Remove from this bar");
        await Draw();
        var empty = Groups().Single(g => g.ButtonIds.Length == 0);
        check(Groups().Count(g => g.ButtonIds.SequenceEqual(["assist"])) == 1 && StandaloneButtons.Load(profile).Length == 1,
            "Removing a button from one group leaves its other placement and its saved definition available.");

        // Reuse the empty named group, then exercise the add controls on the live overlay.
        SpellDockGroup SavedGroup() => Groups().Single(g => g.Id == empty.Id);
        Click(bar, "Add a button on the " + empty.Side.ToString().ToLowerInvariant());
        CreateButton("Utility fixture", "F2");
        await Draw();
        string firstId = SavedGroup().ButtonIds.Single();
        check(firstId != "assist" && StandaloneButtons.Load(profile).Single(b => b.Value.Id == firstId).Value.Rules.Single().Key == "F2",
            "Create button saves a new definition with its recorded shortcut and adds it to the chosen edge.");
        string addColumn = "Add a column to " + empty.Name;
        var beforeCancel = File.ReadAllBytes(path);
        Click(bar, addColumn);
        Click(Editor(), "Close editor");
        check(File.ReadAllBytes(path).SequenceEqual(beforeCancel) && SavedGroup().Columns == 1 && StandaloneButtons.Load(profile).Length == 2,
            "Closing the new-column picker keeps the saved layout and button library intact.");

        Click(bar, addColumn);
        Click(Editor(), "My assist");
        await Draw();
        var twoColumns = SavedGroup();
        check(twoColumns.Columns == 2 && twoColumns.PositionedLayout.ToHashSet().SetEquals([new ButtonPlacement(firstId, 0, 0), new("assist", 1, 0)]),
            "Picking a synced button for a new column saves exactly one new column with the button in its first row.");
        Click(bar, $"Add a row to {empty.Name}, column 2");
        check(!Control<C.Button>(Editor(), "My assist").IsEnabled, "A saved button already in this group cannot be placed twice.");
        CreateButton("Target fixture", "F3");
        await Draw();
        var withRow = SavedGroup();
        var extra = StandaloneButtons.Load(profile).Single(b => b.Value.Id != "assist" && b.Value.Id != firstId);
        check(withRow.Columns == 2 && withRow.PositionedLayout.ToHashSet().SetEquals([.. twoColumns.PositionedLayout, new ButtonPlacement(extra.Value.Id, 1, 1)]),
            "Adding a row to the second column preserves the first column and places the new button beneath its chosen column.");

        // Exercise the actual floating header, manager save callback, and reload from disk.
        var groupsBeforeRename = Groups();
        var buttonsBeforeRename = StandaloneButtons.Load(profile).ToDictionary(b => b.Value.Id, b => b.Bytes);
        string titleField = $"{withRow.Side} group name";
        C.TextBox Header() => Control<C.TextBox>(bar, titleField);
        C.TextBox TypeTitle(string value)
        {
            var field = Header();
            field.Focus();
            field.Text = value;
            return field;
        }
        static void PressKey(C.TextBox field, I.Key key)
        {
            var e = new I.KeyEventArgs(I.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(field), 0, key)
                { RoutedEvent = I.Keyboard.PreviewKeyDownEvent };
            field.RaiseEvent(e);
            if (!e.Handled) { e.RoutedEvent = I.Keyboard.KeyDownEvent; field.RaiseEvent(e); }
        }

        var beforeEscape = File.ReadAllBytes(path);
        var draftHeader = TypeTitle("Abandoned draft");
        draftHeader.CaretIndex = 4;
        var readButtons = ReadDockButtons;
        try
        {
            ReadDockButtons = () => readButtons().Select(b => b.Value.Id == firstId
                ? b with { Value = b.Value with { Label = "New button data from polling" } } : b).ToArray();
            await RefreshDockLibrary();
            await Draw();
            check(ReferenceEquals(Header(), draftHeader) && draftHeader.Text == "Abandoned draft" && draftHeader.CaretIndex == 4 &&
                File.ReadAllBytes(path).SequenceEqual(beforeEscape),
                "A dock refresh with changed button data preserves the focused title field, its draft, and its caret without saving.");
        }
        finally { ReadDockButtons = readButtons; }
        PressKey(Header(), I.Key.Escape);
        check(Header().Text == withRow.Name && File.ReadAllBytes(path).SequenceEqual(beforeEscape),
            "Escape restores the group's previous title without writing the overlay library.");

        PressKey(TypeTitle("Assist & Support"), I.Key.Enter);
        await Draw();
        var expectedGroups = groupsBeforeRename.Select(g => g.Id == withRow.Id ? g with { Name = "Assist & Support" } : g);
        var buttonsAfterRename = StandaloneButtons.Load(profile);
        check(!IsVisible && JsonSerializer.Serialize(Groups()) == JsonSerializer.Serialize(expectedGroups) &&
            buttonsAfterRename.Length == buttonsBeforeRename.Count &&
            buttonsAfterRename.All(b => buttonsBeforeRename.TryGetValue(b.Value.Id, out var prior) && b.Bytes.SequenceEqual(prior)),
            "Enter saves the exact group title while Settings stays closed and preserves IDs, side, scale, columns, placements, neighboring groups, and the saved buttons.");
        bar.ToggleButtonEditing();
        await Draw();
        check(OverlayTestControls(bar).OfType<C.TextBlock>().Any(t => t.IsVisible && t.Text == "Assist & Support") &&
            !OverlayTestControls(bar).OfType<C.TextBox>().Any(t => t.IsVisible && W.Automation.AutomationProperties.GetName(t) == titleField),
            "The normal overlay displays the entered capitalization and hides the editable name field.");

        bar.ToggleButtonEditing();
        TypeTitle("  ");
        var neighbor = Groups().Single(g => g.Id != withRow.Id);
        Control<C.TextBox>(bar, $"{neighbor.Side} group name").Focus();
        await Draw();
        check(SavedGroup().Name == "" && SavedGroup().Layout.SequenceEqual(withRow.Layout),
            "Leaving the title field saves a blank title without removing its buttons.");
        bar.ToggleButtonEditing();
        await Draw();
        string blankGroupLabel = SavedGroup().EditorName;
        check(!OverlayTestControls(bar).OfType<C.TextBlock>().Any(t => t.IsVisible &&
            (t.Text == "Assist & Support" || t.Text == "Group name" || t.Text == blankGroupLabel)) &&
            OverlayTestControls(bar).OfType<C.Border>().Any(b => b.IsVisible && W.Automation.AutomationProperties.GetName(b) == "Target fixture"),
            "A group with a blank title keeps its dock buttons visible and omits placeholder text in normal mode.");

        bar.ToggleButtonEditing();
        check(Header().Text == "", "Edit mode offers a name field even when the group has no title.");
        TypeTitle(RestartedDockTitle);
        bar.ToggleButtonEditing();
        await Draw();
        check(SavedGroup().Name == RestartedDockTitle &&
            OverlayTestControls(bar).OfType<C.TextBlock>().Any(t => t.IsVisible && t.Text == RestartedDockTitle),
            "Done editing commits the new title of a previously unnamed group.");

        await bar.TestDockDragging(firstId, extra.Value.Id, SavedGroup, Draw, check);
        var buttonsAfterDrag = StandaloneButtons.Load(profile);
        check(buttonsAfterDrag.Length == buttonsBeforeRename.Count &&
            buttonsAfterDrag.All(b => buttonsBeforeRename[b.Value.Id].SequenceEqual(b.Bytes)) &&
            JsonSerializer.Serialize(Groups().Single(g => g.Id == neighbor.Id)) == JsonSerializer.Serialize(neighbor),
            "Reordering saved buttons leaves their shortcuts and their placements in other groups intact.");

        bar.ToggleButtonEditing();
        Click(bar, $"Add a column to {RestartedDockTitle} at row 3");
        CreateButton("Lower fixture", "F4");
        await Draw();
        var lower = StandaloneButtons.Load(profile).Single(b => b.Value.Label == "Lower fixture");
        bool AtCell(string id, int column, int row) => SavedGroup().PositionedLayout.Contains(new ButtonPlacement(id, column, row));
        check(AtCell(lower.Value.Id, 1, 2) && SavedGroup().PositionedLayout.Count(p => p.Column == 1) == 1,
            "Creating beside the third button leaves the first two positions of the new column empty.");
        string middleCell = $"Add a button to {RestartedDockTitle}, column 2, row 2";
        Click(bar, middleCell);
        CreateButton("Middle fixture", "F5");
        await Draw();
        var middleButton = StandaloneButtons.Load(profile).Single(b => b.Value.Label == "Middle fixture");
        check(AtCell(middleButton.Value.Id, 1, 1) && AtCell(lower.Value.Id, 1, 2),
            "Creating in the middle of an existing column leaves the lower button in its row.");
        Click(bar, "Edit Middle fixture");
        Click(Editor(), "Remove from this bar");
        await Draw();
        check(AtCell(lower.Value.Id, 1, 2) && SavedGroup().PositionedLayout.Count(p => p.Column == 1) == 1,
            "Removing an upper button from the sparse column keeps its lower neighbor aligned.");
        Click(bar, middleCell);
        Click(Editor(), "Middle fixture");
        await Draw();
        check(AtCell(middleButton.Value.Id, 1, 1) && AtCell(lower.Value.Id, 1, 2) &&
            SavedGroup().PositionedLayout.All(p => p.Column != 1 || p.Row != 0),
            "Picking a saved button uses the selected empty cell and keeps the space above it.");
        var saved = SavedGroup();
        var shifted = saved.WithLayout(saved.PositionedLayout.Select(p => p.ButtonId == lower.Value.Id ? p with { Row = 3 } : p).ToArray());
        check(!SameDockGroups([saved], [shifted]) && !SameDockGroups([saved], [saved with { Name = "Pending rename" }]) &&
            !SameDockGroups([saved], [saved with { Scale = saved.Scale + .1 }]),
            "The overlay still detects pending Settings edits to a button row, its group title, or its scale.");
        bar.ToggleButtonEditing();
        await Draw();
    }
}

internal sealed partial class SpellOverlayWindow
{
    internal async Task TestDockDragging(string firstId, string lastId, Func<SpellDockGroup> read, Func<Task> draw, Action<bool, string> check)
    {
        ToggleButtonEditing(); await draw();
        var original = read();
        C.Button Face(string id) => (C.Button)dockRows.Single(r => r.Group == original.Id && r.Button.Id == id).Border.Child;
        C.Button Add(string name) => SpellOverlayManagerWindow.OverlayTestControls(dockPanels[original.Id].Panel)
            .OfType<C.Button>().Single(b => W.Automation.AutomationProperties.GetName(b) == name);
        W.Point At(W.FrameworkElement element, double height = .5) =>
            element.TranslatePoint(new W.Point(element.ActualWidth / 2, element.ActualHeight * height), this);
        DropRectangle[] Slots() => SpellOverlayManagerWindow.OverlayTestControls(this).OfType<DropRectangle>()
            .Where(r => r.IsVisible && r.Tag is DockButtonMove).ToArray();
        DropRectangle Slot(int column, string? before, bool newColumn = false, int? row = null) => Slots().Single(r =>
            r.Tag is DockButtonMove move && move.TargetGroup.Id == original.Id && move.Column == column &&
            move.BeforeButtonId == before && move.NewColumn == newColumn && move.Row == row);
        void Press(string id)
        {
            var face = Face(id);
            BeginDockButtonPress(face, currentDefinition!.DockGroups!.Single(g => g.Id == original.Id), id, At(face));
        }
        void StartDrag(string id)
        {
            Press(id);
            if (!UpdateDockButtonDrag(At(Face(id)) + new W.Vector(W.SystemParameters.MinimumHorizontalDragDistance + 1, 0)))
                throw new InvalidOperationException("Fixture drag did not capture the overlay.");
            UpdateLayout();
        }
        void Drop(int column, string? before, bool newColumn = false, int? row = null)
        {
            var destination = At(Slot(column, before, newColumn, row));
            UpdateDockButtonDrag(destination);
            if (!EndDockButtonDrag(destination) || buttonEditError is not null)
                throw new InvalidOperationException("Fixture drop failed: " + buttonEditError);
        }
        string[] Column(int index) => read().PositionedLayout.Where(p => p.Column == index).OrderBy(p => p.Row).Select(p => p.ButtonId).ToArray();

        var edited = Face("assist");
        edited.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent, edited));
        var openEditor = OwnedWindows.OfType<OverlayButtonEditor>().Single(w => w.IsVisible);
        Activate();
        Press(firstId);
        UpdateDockButtonDrag(At(Face(firstId)) + new W.Vector(1, 0));
        check(openEditor.IsVisible && Slots().Length == 0 && dockDragPreviewLayer.Children.Count == 0,
            "Ordinary pointer jitter leaves the open shortcut editor alone and shows no drag targets.");
        UpdateDockButtonDrag(At(Face(firstId)) + new W.Vector(W.SystemParameters.MinimumHorizontalDragDistance + 1, 0));
        UpdateLayout();
        var allSlots = Slots();
        var middle = Slot(1, lastId);
        UpdateDockButtonDrag(At(middle));
        var preview = dockDragPreviewLayer.Children.OfType<C.Image>().Single();
        check(!openEditor.IsVisible && preview.IsVisible && !dockDragPreviewLayer.IsHitTestVisible &&
            preview.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 0, PixelHeight: > 0 },
            "Starting the drag closes the item editor and displays a pointer-transparent picture of its button.");
        check(allSlots.All(slot => slot.StrokeDashArray is { Count: > 0 } && slot.ActualWidth > 0 && slot.ActualHeight > 0) &&
            At(Slot(1, "assist")).Y < At(Face("assist"), 0).Y &&
            At(Face("assist"), 1).Y < At(middle).Y && At(middle).Y < At(Face(lastId), 0).Y &&
            At(Slot(1, null)).Y > At(Face(lastId), 1).Y && At(Slot(2, null, true, 1)).X > At(middle).X &&
            At(Slot(2, null, true, 0)).Y < At(Slot(2, null, true, 1)).Y &&
            FindDockDropSlot(At(Face("assist"))) is null && FindDockDropSlot(At(middle))?.BeforeButtonId == lastId,
            "The dotted drop spaces occupy real gaps before, between and after the two buttons, and mark the new column on the right.");
        var footer = Slot(1, null);
        var groupPanel = dockPanels[original.Id].Panel;
        double footerBottom = footer.TranslatePoint(new(0, footer.ActualHeight), groupPanel).Y;
        check(Math.Abs(groupPanel.ActualHeight - groupPanel.BorderThickness.Bottom - footerBottom - 3) < 1
            && footer.ActualHeight >= DropSlotHeight && ReferenceEquals(FindDockDropSlot(At(footer)), footer.Tag),
            "The trailing dotted target remains hittable and ends three DIPs inside the section border.");
        var sourcePosition = At(Face(firstId));
        var from = dockDragPreviewLayer.TranslatePoint(new W.Point(dockDragPreviewLayer.ActualWidth / 2,
            dockDragPreviewLayer.ActualHeight / 4), this);
        UpdateDockButtonDrag(from);
        double fromX = C.Canvas.GetLeft(preview), fromY = C.Canvas.GetTop(preview);
        var to = from + new W.Vector(7, 5);
        UpdateDockButtonDrag(to);
        check(Math.Abs((C.Canvas.GetLeft(preview) - fromX) - (to.X - from.X)) < 1 &&
            Math.Abs((C.Canvas.GetTop(preview) - fromY) - (to.Y - from.Y)) < 1 && At(Face(firstId)) == sourcePosition,
            "The preview follows both pointer coordinates while the original button stays in position.");
        EndDockButtonDrag(); await draw();
        check(read().Layout.SequenceEqual(original.Layout) && Slots().Length == 0 && dockDragPreviewLayer.Children.Count == 0 &&
            !OwnedWindows.OfType<OverlayButtonEditor>().Any(w => w.IsVisible),
            "Canceling restores the edit layout, removes the preview and all slots, and keeps the item menu closed.");

        var addButton = Add("Add a column to " + original.Name);
        addButton.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent, addButton));
        var picker = OwnedWindows.OfType<OverlayButtonEditor>().Single(w => w.IsVisible);
        Activate(); StartDrag("assist");
        check(!picker.IsVisible && Slots().All(r => ((DockButtonMove)r.Tag).TargetGroup.Id == original.Id),
            "A drag closes the add-button picker and omits slots in the other group that already contains the dragged button.");
        EndDockButtonDrag(); await draw();
        StartDrag(lastId); Drop(1, "assist"); await draw();
        var movedRow = dockRows.Single(r => r.Group == original.Id && r.Button.Id == lastId).Border;
        check(Column(1).SequenceEqual([lastId, "assist"]) && C.Grid.GetColumn(movedRow) == 1 && C.Grid.GetRow(movedRow) == 0,
            "Dropping above a button reorders the column on disk and immediately redraws its first row.");
        StartDrag(lastId); Drop(1, null); await draw();
        check(Column(1).SequenceEqual(["assist", lastId]),
            "Dropping in the trailing dotted slot places the dragged button last.");
        StartDrag(firstId); Drop(1, null); await draw();
        check(read().Columns == 1 && Column(0).SequenceEqual(["assist", lastId, firstId]),
            "Dropping in another column's trailing slot appends the button and collapses its emptied source column.");
        StartDrag(firstId); Drop(1, null, true, 1); await draw();
        check(read().Columns == 2 && Column(0).SequenceEqual(["assist", lastId]) &&
            read().PositionedLayout.Contains(new ButtonPlacement(firstId, 1, 1)) &&
            Math.Abs(At(Face(firstId)).Y - At(Face(lastId)).Y) < 1 &&
            !OwnedWindows.OfType<OverlayButtonEditor>().Any(w => w.IsVisible),
            "The second right-hand dotted target places a button beside the second row and leaves the row above it empty.");
        StartDrag(lastId); Drop(1, null, row: 0); await draw();
        check(read().PositionedLayout.Contains(new ButtonPlacement(lastId, 1, 0)) &&
            read().PositionedLayout.Contains(new ButtonPlacement(firstId, 1, 1)) &&
            Math.Abs(At(Face(lastId)).Y - At(Face("assist")).Y) < 1,
            "Dragging into a vacant cell aligns the moved button with its neighbor without displacing the existing button below.");
        StartDrag(lastId); Drop(0, null, row: 1); await draw();

        var source = Face(firstId);
        Press(firstId);
        check(source.CaptureMouse() && source.IsMouseCaptured,
            "The button face captures the initial mouse press before the drag threshold is crossed.");
        UpdateDockButtonDrag(At(source) + new W.Vector(W.SystemParameters.MinimumHorizontalDragDistance + 1, 0)); UpdateLayout();
        var gap = At(Slot(0, lastId));
        UpdateDockButtonDrag(gap);
        check(IsMouseCaptured && !source.IsMouseCaptured && ReferenceEquals(highlightedDockSlot, Slot(0, lastId)) &&
            At(Face("assist"), 1).Y < gap.Y && gap.Y < At(Face(lastId), 0).Y,
            "Dragging into a dotted space between rows transfers capture to the overlay and highlights that slot.");
        EndDockButtonDrag(gap); await draw();
        check(!IsMouseCaptured && Slots().Length == 0 && dockDragPreviewLayer.Children.Count == 0 && read().Columns == 1 &&
            Column(0).SequenceEqual(["assist", firstId, lastId]),
            "Releasing in the gap inserts between its two neighbors, removes the cue, and releases mouse capture.");

        var clicked = Face(firstId);
        var point = At(clicked);
        Press(firstId);
        UpdateDockButtonDrag(new W.Point(point.X + 1, point.Y));
        EndDockButtonDrag(point);
        clicked.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent, clicked));
        var editor = OwnedWindows.OfType<OverlayButtonEditor>().Single(w => w.IsVisible);
        check(editor.Title == "Edit shortcuts" && Column(0).SequenceEqual(["assist", firstId, lastId]),
            "A click with ordinary pointer jitter still opens the moved button's shortcut editor.");
        editor.Close();
        ToggleButtonEditing(); await draw();
    }
}
