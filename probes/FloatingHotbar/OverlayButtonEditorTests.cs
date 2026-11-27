using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;
using Interop = System.Windows.Interop;

namespace TeamBar;

internal static class OverlayButtonEditorTests
{
    internal static void Run(Action<bool, string> check, string output) => OverlayButtonEditor.TestOverlayEditor(check, output);
}

internal partial class OverlayButtonEditor
{
    internal static void TestOverlayEditor(Action<bool, string> check, string output)
    {
        var theme = FloatingThemes.Presets.Single(p => p.Id == "dark");
        CharacterProfile[] characters = [new("oak", "Oak"), new("birch", "Birch")];
        PersonaProfile[] personas = [new("oak-shaman", "oak", "Shaman")];
        var value = new StandaloneButton(1, "assist", "Assist & Attack", characters, personas,
            [new(new(CharacterId: "birch"), "Ctrl+Shift+Y"), new(new(PersonaId: "oak-shaman"), "Alt+3")],
            [new(Class: "Shaman")], FollowCharacterId: "oak", Floating: false) { Icon = "swords" };
        SavedButton[] saved = [new("fixture", value, []), new("fixture2", value with { Id = "follow", Label = "Follow Oak", Icon = "follow" }, [])];
        List<W.DependencyObject> Descendants(W.DependencyObject root)
        {
            var result = new List<W.DependencyObject> { root };
            for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
                result.AddRange(Descendants(M.VisualTreeHelper.GetChild(root, i)));
            return result;
        }
        void Layout(OverlayButtonEditor panel)
        {
            var root = panel.PanelRoot;
            root.Measure(new W.Size(panel.Width, double.PositiveInfinity));
            root.Arrange(new W.Rect(0, 0, panel.Width, root.DesiredSize.Height));
            root.UpdateLayout();
        }
        T Find<T>(OverlayButtonEditor panel, string name) where T : W.UIElement
        {
            Layout(panel);
            return Descendants(panel.PanelRoot).OfType<T>().Single(c => W.Automation.AutomationProperties.GetName(c) == name);
        }
        void Click(OverlayButtonEditor panel, string name) =>
            Find<C.Button>(panel, name).RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent));

        SavedButton? picked = null; bool independent = false;
        var picker = (OverlayButtonEditor)CreatePicker(theme, saved, ["assist"], (b, i) => { picked = b; independent = i; }, () => { });
        try
        {
            check(!Find<C.Button>(picker, "Assist & Attack").IsEnabled, "Overlay picker blocks a duplicate synced placement in the same dock group.");
            GroupEditorTests.Screenshot(picker.PanelRoot, output + ".button-picker.png");
            Click(picker, "Independent copy");
            Find<C.TextBox>(picker, "Search saved buttons").Text = "attack";
            check(Find<C.Button>(picker, "Assist & Attack").IsEnabled
                && !Descendants(picker.PanelRoot).OfType<C.Button>().Any(b => W.Automation.AutomationProperties.GetName(b) == "Follow Oak"),
                "Typing in the overlay picker filters saved buttons, and an independent copy is allowed in the same group.");
            Click(picker, "Assist & Attack");
            check(picked == saved[0] && independent, "Adding a copy passes the original saved button and independent mode to placement storage.");
        }
        finally { picker.Close(); }

        StandaloneButton? result = null;
        var editor = (OverlayButtonEditor)CreateEditor(theme, value, false, 2, (b, i) => { result = b; independent = i; });
        editor.currentArtwork = () => new EqIconArtwork(Path.GetFullPath(output) + ".no-eq-install");
        editor.readIconCatalog = _ => EqIconCatalog.FromNames([], []);
        try
        {
            Layout(editor);
            GroupEditorTests.Screenshot(editor.PanelRoot, output + ".button-shortcuts.png");
            Click(editor, "Record shortcut · Alt+3");
            var modifiers = I.ModifierKeys.Control | I.ModifierKeys.Shift | I.ModifierKeys.Alt;
            long scanY = InputKeys.Scan("Y") << 16;
            check(editor.ConsumeKey(0x104, 0x12, 0x38 << 16, modifiers) && editor.Recording,
                "Holding a modifier leaves the selected recipient listening for its main key.");
            check(editor.ConsumeKey(0x104, 0x59, scanY, modifiers) && !editor.Recording
                && editor.ConsumeKey(0x104, 0x59, scanY | (1L << 30), modifiers)
                && editor.ConsumeKey(0x105, 0x59, scanY, modifiers),
                "Recording a three-modifier shortcut consumes the chord, repeat and release without creating a second action.");
            check(!editor.ConsumeKey(0x100, 0x59, scanY, I.ModifierKeys.None), "Ordinary typing remains available outside shortcut recording.");
            Click(editor, "Choose button icon"); Click(editor, "Symbols"); Click(editor, "Melody");
            Find<C.TextBox>(editor, "Button name").Text = " ";
            Click(editor, "Make independent"); Click(editor, "Done");
            check(independent && result?.Rules[1].Key == "Ctrl+Shift+Alt+Y" && result.Rules[0] == value.Rules[0]
                && result.Rules[1].Target == value.Rules[1].Target && result.Icon == "melody" && result.Label == "" && result.ShowFor == value.ShowFor
                && result.FollowCharacterId == value.FollowCharacterId && !result.Floating && value.Rules[1].Key == "Alt+3",
                "An independent icon-only edit saves the selected shortcut and blank label while preserving the original, ordering, visibility and anchor.");
            foreach (string key in InputKeys.Names)
            {
                var row = new EditRow(new(new(Class: "Shaman"), "")) { Record = new() };
                editor.BeginRecording(row);
                int scan = InputKeys.Scan(key);
                long detail = ((long)(scan & 0x7f) << 16) | ((scan & 0x80) != 0 ? 1L << 24 : 0);
                // All physical inputs are synthetic and addressed only to the recorder.
                editor.ConsumeKey(0x100, 0, detail, I.ModifierKeys.None);
                editor.ConsumeKey(0x101, 0, detail, I.ModifierKeys.None);
                if (row.Chord != key || editor.Recording) throw new InvalidDataException("Recorder lost key " + key);
            }
            check(true, "All supported physical keys round-trip through the overlay recorder, including keypad Enter, Escape, Tab and extended navigation.");
        }
        finally { editor.Close(); }
        int saves = 0;
        var creating = (OverlayButtonEditor)CreateEditor(theme, value with { Label = "", Rules = [new(new(CharacterId: "oak"), "")] },
            true, 0, (_, _) => saves++);
        try
        {
            Click(creating, "Add to bar"); Find<C.TextBox>(creating, "Button name").Text = "Test";
            Click(creating, "Add to bar");
            check(saves == 0 && creating.ErrorMessage.Text.Contains("shortcut"), "Incomplete in-overlay drafts stay open and never reach persistence.");
        }
        finally { creating.Close(); }

        // Exercise the real HWND-scoped WPF filter with messages raised only in
        // this fixture dispatcher. These messages never enter the OS input queue.
        StandaloneButton? recorded = null;
        var shown = (OverlayButtonEditor)CreateEditor(theme, value, false, 1, (b, _) => recorded = b);
        var other = new W.Window { ShowInTaskbar = false };
        try
        {
            nint otherHandle = new Interop.WindowInteropHelper(other).EnsureHandle();
            shown.Left = -10000; shown.Top = -10000; shown.Show(); shown.Activate();
            shown.Dispatcher.Invoke(() => { }, W.Threading.DispatcherPriority.ApplicationIdle);
            check(shown.IsActive, "Shown fixture editor can activate and take keyboard focus for shortcut recording.");
            Click(shown, "Record shortcut · Alt+3");
            bool Raise(nint hwnd, int message, int key, int scan)
            {
                var msg = new Interop.MSG { hwnd = hwnd, message = message, wParam = key, lParam = scan << 16 };
                return Interop.ComponentDispatcher.RaiseThreadMessage(ref msg);
            }
            Raise(otherHandle, 0x0100, 'X', InputKeys.Scan("X"));
            check(shown.Recording && shown.recording!.Chord == "Alt+3", "An unrelated window's message cannot update the selected overlay recipient.");
            nint editorHwnd = new Interop.WindowInteropHelper(shown).Handle;
            bool down = Raise(editorHwnd, 0x0100, 'Y', InputKeys.Scan("Y"));
            bool up = Raise(editorHwnd, 0x0101, 'Y', InputKeys.Scan("Y"));
            Click(shown, "Done");
            check(down && up && recorded?.Rules[1].Key == "Y" && recorded.Rules[1].Target == value.Rules[1].Target
                && recorded.Rules[0] == value.Rules[0] && !shown.IsVisible,
                "A focused editor consumes its own key messages and saves the recorded shortcut only on the selected recipient.");
        }
        finally { if (shown.IsVisible) shown.Close(); other.Close(); }
        TestIconGallery(theme, value, check, output);
    }
}
