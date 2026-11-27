using C = System.Windows.Controls;
using I = System.Windows.Input;
using Interop = System.Windows.Interop;

namespace TeamBar;

internal partial class OverlayButtonEditor
{
    private sealed class EditRow(ButtonRule rule)
    {
        internal TargetSelector Target = rule.Target;
        internal string Chord = rule.Key;
        internal C.Button Record = null!;
    }

    private static readonly Dictionary<int, string> PhysicalKeys = InputKeys.Names.ToDictionary(InputKeys.Scan);
    private readonly HashSet<int> consumedKeys = [];
    private EditRow? recording;
    private nint editorHandle;
    private bool Recording => recording is not null;

    private static void SetKeycaps(EditRow row)
    {
        row.Record.ToolTip = "Click, then press this recipient's shortcut";
        if (row.Chord.Length == 0) { row.Record.Content = "Record shortcut"; return; }
        var caps = new C.StackPanel { Orientation = C.Orientation.Horizontal };
        foreach (string key in row.Chord.Split('+', StringSplitOptions.TrimEntries))
        {
            var cap = new C.Border { CornerRadius = new(3), Padding = new(4, 1, 4, 1), Margin = new(1, 0, 1, 0),
                BorderThickness = new(1), Child = new C.TextBlock { Text = key, FontSize = 11 } };
            cap.SetResourceReference(C.Border.BorderBrushProperty, "Edge");
            caps.Children.Add(cap);
        }
        row.Record.Content = caps;
        NameControl(row.Record, "Record shortcut · " + row.Chord);
    }

    private void BeginRecording(EditRow row)
    {
        bool cancel = recording == row;
        StopRecording();
        if (cancel) return;
        recording = row; row.Record.Focus();
        row.Record.Content = "Press shortcut…"; row.Record.ToolTip = "Click again to cancel recording";
        row.Record.SetResourceReference(C.Control.BorderBrushProperty, "Accent");
    }
    private void StopRecording()
    {
        if (recording is not { } row) return;
        recording = null; SetKeycaps(row);
        row.Record.SetResourceReference(C.Control.BorderBrushProperty, "Line");
    }

    private void AttachRecorder(object? sender, EventArgs e)
    {
        editorHandle = new Interop.WindowInteropHelper(this).Handle;
        // WPF translates Tab and system keys before normal control KeyDown. Read
        // the message at that stage, exclusively for this editor's own HWND.
        Interop.ComponentDispatcher.ThreadFilterMessage += FilterRecordingKey;
        Closed += (_, _) => Interop.ComponentDispatcher.ThreadFilterMessage -= FilterRecordingKey;
    }
    private void FilterRecordingKey(ref Interop.MSG msg, ref bool handled)
    {
        if (handled || msg.hwnd != editorHandle || !IsActive || (!Recording && consumedKeys.Count == 0)) return;
        switch (msg.message)
        {
            case 0x0100: case 0x0104: // WM_KEYDOWN, WM_SYSKEYDOWN
            case 0x0101: case 0x0105: // WM_KEYUP, WM_SYSKEYUP
                handled = ConsumeKey(msg.message, (int)msg.wParam, msg.lParam.ToInt64(), I.Keyboard.Modifiers);
                break;
            case 0x0102: case 0x0103: case 0x0106: case 0x0107: // char and dead-char messages
                handled = true;
                break;
        }
    }

    private bool ConsumeKey(int message, int virtualKey, long detail, I.ModifierKeys modifiers)
    {
        int scan = (int)(detail >> 16 & 0xff);
        if ((detail & (1L << 24)) != 0) scan |= 0x80; // extended Enter, arrows and other keypad distinctions
        if (message is 0x0101 or 0x0105) return consumedKeys.Remove(scan) || Recording;
        if (recording is not { } row) return consumedKeys.Contains(scan);
        consumedKeys.Add(scan);
        if ((detail & (1L << 30)) != 0 || virtualKey is 0x10 or 0x11 or 0x12 or >= 0xa0 and <= 0xa5) return true;
        if (!PhysicalKeys.TryGetValue(scan, out string? name) || modifiers.HasFlag(I.ModifierKeys.Windows))
        {
            Error("Choose a supported key, optionally with Ctrl, Shift or Alt.");
            return true;
        }
        int mask = (modifiers.HasFlag(I.ModifierKeys.Shift) ? 1 : 0) | (modifiers.HasFlag(I.ModifierKeys.Control) ? 2 : 0)
            | (modifiers.HasFlag(I.ModifierKeys.Alt) ? 4 : 0);
        row.Chord = SpellOverlayKey.Format(name, mask);
        Error(""); StopRecording();
        return true;
    }
}
