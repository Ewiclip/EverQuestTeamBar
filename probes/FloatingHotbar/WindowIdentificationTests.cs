using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TeamBar;

internal static class WindowIdentificationTests
{
    internal static void Run(string output)
    {
        string folder = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(folder)) throw new IOException("Use a new identification-test output.");
        Directory.CreateDirectory(folder);
        using var permission = GameDataAccess.FixtureScope(false);
        var checks = new List<string>();
        void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); checks.Add(message); }
        using var process = Process.GetCurrentProcess();
        var screen = Screen.PrimaryScreen!.WorkingArea;
        using var first = new Form { Text = "Identification fixture · first game window", TopMost = true, StartPosition = FormStartPosition.Manual,
            Bounds = new(screen.Left + 25, screen.Top + 30, Math.Min(600, screen.Width - 50), Math.Min(380, screen.Height - 60)), BackColor = Color.FromArgb(35, 43, 54) };
        // Consume a hidden STARTUPINFO show command used by the test launcher.
        first.Show(); first.Hide(); first.Show(); first.Activate(); Application.DoEvents();
        var a = new GameSession(process.Id, process.StartTime.ToUniversalTime().Ticks, first.Handle.ToInt64(), first.Left, "Fixture window");
        Check(WindowIdentification.SameSession(a, a with { Left = a.Left + 200 })
            && !WindowIdentification.SameSession(a, a with { StartedUtcTicks = a.StartedUtcTicks + 1 })
            && !WindowIdentification.SameSession(a, a with { Window = a.Window + 1 })
            && !WindowIdentification.SameSession(a, a with { ProcessId = a.ProcessId + 1 }),
            "Identification permits window movement but rejects recycled processes and window handles.");
        long now = 0; string message = ""; bool available = true;
        Rectangle? Frame(GameSession _) => available ? first.Bounds : null;
        nint foreground = Native.GetForegroundWindow();
        using var labels = new WindowIdentification([new(7, a)], text => message = text, Frame, () => now);
        Application.DoEvents();
        Check(labels.VisibleCount == 1, $"A numbered label appears while game-data memory reading is off. ({message}; fixture={first.Handle}, visible={first.Visible}, bounds={first.Bounds}, point={GetAncestor(Native.WindowFromPoint(new Native.Point { X = first.Left + 100, Y = first.Top + 100 }), 2)})");
        Check(Native.GetForegroundWindow() == foreground,
            $"Showing a numbered label does not change the foreground window. (before={foreground}, after={Native.GetForegroundWindow()}, fixture={first.Handle}, badge={string.Join(',', labels.Handles)})");
        nint handle = labels.Handles.Single();
        long style = GetWindowLongPtrW(handle, -20).ToInt64();
        Check((style & (0x08000000 | 0x20 | 0x80000 | 0x80)) == (0x08000000 | 0x20 | 0x80000 | 0x80),
            "Labels are layered, click-through, non-activating tool windows.");
        var badge = (Form)Control.FromHandle(handle)!;
        var center = new Point(badge.Left + badge.Width / 2, badge.Top + badge.Height / 2);
        Check(GetAncestor(Native.WindowFromPoint(new Native.Point { X = center.X, Y = center.Y }), 2) == first.Handle,
            "Pointer hit testing passes through the label to the fixture window.");
        using (var bitmap = new Bitmap(badge.Width, badge.Height))
        { badge.DrawToBitmap(bitmap, new(Point.Empty, badge.Size)); bitmap.Save(Path.Combine(folder, "window-7-badge.png")); }
        labels.Refresh();
        Check(labels.VisibleCount == 1, "An existing click-through label does not obscure its own next placement check.");
        var before = badge.Location;
        first.Left += 70; Application.DoEvents(); labels.Refresh();
        Check(badge.Left == before.X + 70, "The identification follows a moved window during its brief lifetime.");
        available = false; labels.Refresh();
        Check(labels.VisibleCount == 0 && message.Contains("unavailable"), "An unavailable or minimized window loses its label immediately.");
        available = true; labels.Refresh();
        Check(labels.VisibleCount == 1, "A window that returns before expiry keeps its original row number.");
        using var covering = new Form { Text = "Identification fixture · covering window", TopMost = true, StartPosition = FormStartPosition.Manual,
            Bounds = first.Bounds, BackColor = Color.Gray };
        covering.Show(); Application.DoEvents(); labels.Refresh();
        Check(labels.VisibleCount == 0 && message.Contains("Window 7"), "A covered game cannot display its number over a different window.");
        using (var tileLabel = new WindowIdentification([new(7, a)], _ => { }, Frame, () => now,
            _ => (covering.Handle.ToInt64(), covering.Bounds)))
        {
            Application.DoEvents();
            Check(tileLabel.VisibleCount == 1 && covering.Bounds.Contains(((Form)Control.FromHandle(tileLabel.Handles.Single())!).Bounds),
                "A game displayed in a layout is identified on its visible tile even when the original window is covered.");
        }
        using var assignment = new Form { Text = "Identification fixture · assignments", TopMost = true, StartPosition = FormStartPosition.Manual,
            Bounds = new(first.Right + 20, first.Top, 240, 150) };
        assignment.Show(); assignment.Activate(); Application.DoEvents();
        foreground = Native.GetForegroundWindow();
        WindowIdentification.Reveal(a, assignment.Handle, _ => a); Application.DoEvents(); labels.Refresh();
        Check(labels.VisibleCount == 1 && Native.GetForegroundWindow() == foreground,
            "Per-row identification raises a covered window behind the assignment dialog without activating it.");
        assignment.Hide();
        covering.Hide(); first.Activate(); Application.DoEvents(); labels.Refresh();
        Check(labels.VisibleCount == 1, "Identification recovers when the game is visible again.");
        now = 7999; labels.Refresh(); Check(labels.Active, "Labels remain visible through the requested eight-second interval.");
        now = 8000; labels.Refresh();
        Check(!labels.Active && !IsWindow(handle), "The deadline destroys all label windows and stops polling.");
        using var replacement = new WindowIdentification([new(7, a)], _ => { }, Frame, () => now);
        var replacementHandles = replacement.Handles; replacement.Dispose();
        Check(replacementHandles.All(h => !IsWindow(h)), "Closing or repeating identification removes the previous labels immediately.");
        Check(MonitorIdentification.Label(@"\\.\DISPLAY12") == "Screen 12" && MonitorIdentification.Label("Projector") == "Projector",
            "Screen labels follow Windows display identifiers, including nonconsecutive numbers and named displays.");
        var left = first.Bounds with { Width = first.Width / 2 };
        var right = left with { X = left.Right };
        GameViewMonitor[] monitors = [new(@"\\.\DISPLAY42", right, right), new(@"\\.\DISPLAY37", left, left, Primary: true)];
        foreground = Native.GetForegroundWindow();
        using (var monitorLabels = new MonitorIdentification(monitors, _ => { }))
        {
            Application.DoEvents();
            var handles = monitors.Select(m => FindWindowW(null, "Team Bar · " + MonitorIdentification.Label(m.Device))).ToArray();
            for (int i = 0; i < handles.Length; i++)
            {
                var label = Control.FromHandle(handles[i]) as IdentificationBadge;
                var area = monitors[i].Bounds;
                Check(label is not null && label.IsShowing && Math.Abs(label.Left + label.Width / 2 - (area.Left + area.Width / 2)) <= 1 &&
                    Math.Abs(label.Top + label.Height / 2 - (area.Top + area.Height / 2)) <= 1,
                    $"The badge for {MonitorIdentification.Label(monitors[i].Device)} is centered on that monitor.");
                Check((GetWindowLongPtrW(handles[i], -20).ToInt64() & (0x08000000 | 0x20)) == (0x08000000 | 0x20),
                    "Physical monitor labels pass clicks through without taking focus.");
            }
            Check(Native.GetForegroundWindow() == foreground, "Identifying monitors leaves the foreground window in place.");
            var deadline = Environment.TickCount64 + 10_000;
            while (IsWindow(handles[0]) && Environment.TickCount64 < deadline)
            { Application.DoEvents(); Thread.Sleep(25); }
            Check(handles.All(h => !IsWindow(h)), "All monitor numbers automatically disappear.");
        }
        Check(!Enum.GetValues<MemoryFeature>().Any(GameDataAccess.IsEnabled), "Identification never enables memory reading.");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, gameMemoryRead = false });
    }
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? className, string title);
}
