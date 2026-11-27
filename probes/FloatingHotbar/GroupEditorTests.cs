using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

internal static class GroupEditorTests
{
    internal static void Run(string output, string sourceProfile)
    {
        using var access = GameDataAccess.FixtureScope(true);
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (Directory.Exists(directory) || File.Exists(output)) throw new IOException("Use a new group-editor test report.");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks.Add(message); }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new W.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        byte[] original = File.ReadAllBytes(sourceProfile); var buttons = StandaloneButtons.Load(sourceProfile);
        Check(buttons.Length >= 2, "Review uses the existing saved-button library.");
        var team = TeamEditing.Load(sourceProfile) with { Buttons = [], Groups = [] };
        string profile = Path.Combine(directory, "team.json"); TeamEditing.Save(profile, team, []);
        foreach (var button in buttons) StandaloneButtons.Save(StandaloneButtons.PathFor(profile, button.Value.Id), button.Value, null);
        var clients = team.Characters.Select((c, i) => new DetectedClient(100 + i, 200 + i, 300 + i, i,
            new(c.Name, Array.IndexOf(Identity.Classes, team.Personas.FirstOrDefault(p => p.CharacterId == c.Id)?.Class) + 1, 130))).ToArray();
        var settings = new MemoryUserSettingsStore(new() { Theme = AppearanceTheme.Dark, EnableScreenDetection = true, EnableSpellBarData = true });
        var window = new SetupWindow(profile, "input-free-fixture", () => new(clients, []), userSettings: settings);
        try { window.TestGroupRedesign(directory, Check); }
        finally { window.CloseForExit(); }
        Check(original.SequenceEqual(File.ReadAllBytes(sourceProfile)) && buttons.All(b => b.Bytes.SequenceEqual(File.ReadAllBytes(b.Path))), "Source team and saved buttons remain byte-for-byte unchanged.");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
    }
    internal static void Screenshot(W.FrameworkElement element, string file)
    {
        element.UpdateLayout();
        var image = new M.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, M.PixelFormats.Pbgra32);
        var visual = new M.DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            var bounds = new W.Rect(0, 0, element.ActualWidth, element.ActualHeight);
            draw.DrawRectangle((M.Brush)element.FindResource("Surface"), null, bounds);
            draw.DrawRectangle(new M.VisualBrush(element) { Stretch = M.Stretch.Fill }, null, bounds);
        }
        image.Render(visual); var encoder = new M.Imaging.PngBitmapEncoder(); encoder.Frames.Add(M.Imaging.BitmapFrame.Create(image));
        using var stream = File.Create(file); encoder.Save(stream);
    }
}

public partial class SetupWindow
{
    internal void TestGroupRedesign(string directory, Action<bool, string> check)
    {
        Show(); long deadline = Environment.TickCount64 + 10000;
        while (refreshing) { if (Environment.TickCount64 > deadline) throw new TimeoutException("Fixture discovery timed out."); Settle(); Thread.Sleep(5); }
        var source = StandaloneButtons.Load(profilePath); var originalBytes = source.ToDictionary(b => b.Path, b => b.Bytes);
        BarLibraryMode.IsChecked = true; AddGroup(); FlushAutoSave(); Settle();
        check(selectedGroup is { Columns: 1, Buttons.Length: 0 }, "New groups start with one empty column.");
        check(!SettingsDescendants(GroupEditorHost).OfType<C.Slider>().Any() && !SettingsDescendants(GroupEditorHost).OfType<C.ComboBox>().Any(), "Group editing removes size sliders and column/add-button dropdowns.");
        groupTitle!.BeginEdit(); groupName!.Text = "Support"; groupTitle.Commit(); FlushAutoSave();
        check(TeamEditing.Load(profilePath).Groups!.Single().Name == "Support" && !groupName.IsVisible, "The pencil editor saves a renamed group and returns to its title.");
        GroupEditorTests.Screenshot(GroupEditorHost, Path.Combine(directory, "empty-group-dark.png"));
        ChangeGroup(selectedGroup! with { Scale = 1.35 });
        var first = source.FirstOrDefault(b => b.Value.Label == "All Invis") ?? source[0];
        var second = source.First(b => b.Value.Id != first.Value.Id);
        ChooseGroupButton = (_, _, _, _) => null; var before = selectedGroup;
        AddSavedGroupButton(0); check(selectedGroup == before, "Canceling the picker leaves the group unchanged.");
        ChooseGroupButton = (_, column, library, included) => { check(column == "Column 1" && library.Length == source.Length && !included.Any(), "The column's + Button opens the saved library for that column."); return first.Value.Id; };
        SettingsDescendants(groupColumnsEditor!).OfType<C.Button>().Single(b => W.Automation.AutomationProperties.GetName(b) == "Add button to Column 1").RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        ChooseGroupButton = (_, _, _, _) => first.Value.Id; AddSavedGroupButton(0);
        check(selectedGroup!.Buttons.Length == 1 && selectedGroup.Buttons[0].Column == 0, "Adding a saved button creates one reference and prevents duplicates.");
        ChooseGroupButton = (_, _, _, _) => second.Value.Id; AddSavedGroupButton(0);
        AddGroupColumn(); Settle();
        var titles = SettingsDescendants(groupColumnsEditor!).OfType<EditableTitle>().ToArray();
        titles[0].BeginEdit(); titles[0].Editor.Text = "Utilities"; titles[0].Commit();
        titles[1].BeginEdit(); titles[1].Editor.Text = "Combat"; titles[1].Commit(); FlushAutoSave();
        check(TeamEditing.Load(profilePath).Groups!.Single() is { Scale: 1.35, ColumnNames: ["Utilities", "Combat"] }, "Column names persist while existing group size is preserved.");
        groupColumnsEditor!.MoveWithKeyboard(second.Value.Id, -1, 0);
        check(selectedGroup.Buttons[0].ButtonId == second.Value.Id, "Keyboard reordering moves only the selected reference.");
        groupColumnsEditor.MoveWithKeyboard(second.Value.Id, 0, 1); FlushAutoSave();
        check(selectedGroup.Buttons.Single(b => b.ButtonId == second.Value.Id).Column == 1, "Keyboard movement moves a button into a different column.");
        var persisted = TeamEditing.Load(profilePath).Groups!.Single(); LoadGroupEditor(); Settle();
        check(selectedGroup.ColumnNames!.SequenceEqual(persisted.ColumnNames!), "Reloading the editor retains column labels and layout.");
        GroupEditorTests.Screenshot(GroupEditorHost, Path.Combine(directory, "groups-dark.png"));
        RenderSettings(Path.Combine(directory, "groups-full-dark.png"));
        SavedButtonPicker.TestPicker(this, source, first.Value.Id, directory, check);
        LightThemeChoice.IsChecked = true; Settle();
        GroupEditorTests.Screenshot(GroupEditorHost, Path.Combine(directory, "groups-light.png"));
        using (var scope = new PickerReviewScope(this, source, first.Value.Id, directory)) { }
        Width = 980; Height = 720; Settle(); RenderSettings(Path.Combine(directory, "groups-compact.png"));
        while (selectedGroup!.Columns < 6) AddGroupColumn(); AddGroupColumn(); Settle();
        check(selectedGroup.Columns == 6 && !SettingsDescendants(groupColumnsEditor!).OfType<C.Button>().Single(b => W.Automation.AutomationProperties.GetName(b) == "Add column").IsEnabled, "The inline control enforces the existing six-column limit.");
        TestColumnScrolling(check);
        FlushAutoSave(); check(originalBytes.All(p => p.Value.SequenceEqual(File.ReadAllBytes(p.Key))), "Group edits never modify saved button mappings.");
    }
    private void TestColumnScrolling(Action<bool, string> check)
    {
        var editor = groupColumnsEditor!;
        var scroll = (ColumnScrollViewer)editor.Content;
        var content = (C.StackPanel)scroll.Content;
        var originalColumns = content.Children.Cast<W.UIElement>().ToArray();
        scroll.ScrollToLeftEnd(); Settle();
        check(scroll.ScrollableWidth > 240, "Six columns provide a horizontal scrolling test at the compact editor size.");
        check(scroll.MoveWheel(-120, System.Windows.Input.ModifierKeys.None, content, 3)
            && scroll.MoveWheel(-120, System.Windows.Input.ModifierKeys.None, content, 3), "Normal wheel input is handled by the horizontal column workspace.");
        Settle(); check(Math.Abs(scroll.HorizontalOffset - 240) < .1, "Rapid wheel events accumulate instead of losing motion before the next layout.");
        check(content.Children.Cast<W.UIElement>().SequenceEqual(originalColumns), "Scrolling preserves column controls instead of rebuilding the editor.");
        scroll.ScrollToLeftEnd(); Settle();
        check(!scroll.MoveWheel(120, System.Windows.Input.ModifierKeys.None, content, 3)
            && !scroll.MoveWheel(-120, System.Windows.Input.ModifierKeys.Control, content, 3), "Wheel input at the edge and Ctrl-wheel remain available to their normal handlers.");
        // Exercise actual routed preview input, without sending OS mouse events.
        content.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = W.UIElement.PreviewMouseWheelEvent });
        Settle(); check(scroll.HorizontalOffset > 0, "Routed mouse-wheel input reaches the column scroll handler.");
        editor.SetLayout(Enumerable.Range(0, 48).Select(i => new ButtonPlacement("fixture-" + i, i / 8)).ToArray(), 6, id => id);
        Settle();
        var inner = SettingsDescendants(editor).OfType<C.ScrollViewer>().First(s => s != scroll && s.ScrollableHeight > 0);
        check(!scroll.MoveWheel(-120, System.Windows.Input.ModifierKeys.None, inner, 3), "A tall button list retains ordinary vertical wheel scrolling.");
        check(scroll.MoveWheel(-120, System.Windows.Input.ModifierKeys.Shift, inner, 3), "Shift-wheel moves across columns even over a tall button list.");
        Settle();
        editor.SetLayout([], 1, id => id); Settle();
        check(!scroll.MoveWheel(-120, System.Windows.Input.ModifierKeys.None, editor, 3), "A group that fits on screen does not consume wheel input.");
        FillGroupColumns(); Settle();
    }
    private sealed class PickerReviewScope : IDisposable
    {
        internal PickerReviewScope(W.Window owner, SavedButton[] buttons, string included, string directory)
        {
            var picker = new SavedButtonPicker(owner, "Combat", buttons, [included]);
            try { picker.Show(); picker.UpdateLayout(); GroupEditorTests.Screenshot(picker.PickerRoot, Path.Combine(directory, "picker-light.png")); }
            finally { picker.Close(); }
        }
        public void Dispose() { }
    }
}

public partial class SavedButtonPicker
{
    internal static void TestPicker(W.Window owner, SavedButton[] source, string included, string directory, Action<bool, string> check)
    {
        var picker = new SavedButtonPicker(owner, "Combat", source, [included]);
        try
        {
            picker.Show(); picker.UpdateLayout();
            check(picker.Choices.Items.Count == source.Length, "Picker lists the actual saved library.");
            picker.Choices.SelectedItem = picker.buttons.Single(b => b.Id == included);
            check(!picker.Add.IsEnabled && picker.buttons.Single(b => b.Id == included).Membership.Length > 0, "Already-added buttons can be inspected but cannot be added twice.");
            picker.Search.Text = "no-such-button-84723";
            check(picker.Choices.Items.Count == 0 && !picker.Add.IsEnabled && picker.Empty.IsVisible, "Empty searches clearly report no matches and disable Add.");
            picker.Search.Text = ""; picker.Choices.SelectedItem = picker.buttons.First(b => !b.Included);
            var selected = (PickerButtonInfo)picker.Choices.SelectedItem;
            check(selected.Mappings.SequenceEqual(PickerButtonInfo.From(source.Single(b => b.Value.Id == selected.Id).Value, false).Mappings), "Preview shows ordered real recipients and chords from the saved button.");
            picker.UpdateLayout(); GroupEditorTests.Screenshot(picker.PickerRoot, Path.Combine(directory, "picker-dark.png"));
            picker.Search.Text = source.Single(b => b.Value.Id == selected.Id).Value.Rules[0].Key;
            check(picker.Choices.Items.Count > 0, "Picker search includes key combinations.");
        }
        finally { picker.Close(); }
        var accept = new SavedButtonPicker(owner, "Combat", source, [included]);
        string expected = accept.buttons.First(b => !b.Included).Id;
        accept.Loaded += (_, _) => accept.Dispatcher.BeginInvoke(() => accept.Add.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent)));
        check(accept.ShowDialog() == true && accept.SelectedButtonId == expected, "Confirming the modal picker returns the selected saved button.");
    }
}
