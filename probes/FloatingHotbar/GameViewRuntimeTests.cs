using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private const int PointerMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, RightDown = 0x0204, RightUp = 0x0205;
    private const int EnterSizeMove = 0x0231, ExitSizeMove = 0x0232, Sizing = 0x0214, WindowSize = 0x0005;
    private const int SystemCommand = 0x0112, MaximizeCommand = 0xF030, RestoreCommand = 0xF120;
    private sealed record SizingSample(int Message, int Parameter, Size Client, bool Framed, Rectangle Frame);
    private static readonly Color[] EdgeColors = [Color.LimeGreen, Color.OrangeRed, Color.DodgerBlue, Color.Gold];
    private static string? lastSample;
    internal static bool IsTeamBarControl(nint window) => Control.FromHandle(window) is not Source;

    // No client discovery: every source, preview and input recipient belongs to this fixture.
    private class Source(Color color, Point origin, bool framed = false, bool passive = false, bool showInactive = false) : Form
    {
        protected override bool ShowWithoutActivation => passive || showInactive;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; if (passive) p.ExStyle |= 0x08000000; return p; }
        }
        private readonly Color[] cornerColors = [.. EdgeColors];
        internal List<(int Message, Point Point)> Input { get; } = [];
        internal List<SizingSample>? ResizeTrace;
        internal Action? NextResizeEnd;
        internal Point? LastMoveEndOrigin { get; private set; }
        internal string InputReport => string.Join(", ", Input.Select(p => $"0x{p.Message:X}({p.Point.X},{p.Point.Y})"));
        internal bool RefuseResize;
        internal Rectangle VisibleFrame => GameViewRuntimeTests.VisibleFrame(Handle);
        internal bool TrackFrameChanges;
        internal bool PaintHudMarker;
        internal int PositionChanges { get; private set; }
        internal int StationaryRaises { get; private set; }
        internal int FrameRemovalAttempts { get; private set; }
        protected override void OnLocationChanged(EventArgs e)
        {
            PositionChanges++;
            base.OnLocationChanged(e);
        }

        internal void Open(string title = "Team Bar layout fixture", bool taskbarEntry = false)
        {
            FormBorderStyle = taskbarEntry ? FormBorderStyle.FixedDialog : framed ? FormBorderStyle.Sizable : FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = taskbarEntry;
            Text = title;
            BackColor = color;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Location = origin;
            ClientSize = new(320, 180);
            Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int width = ClientSize.Width / 5, height = ClientSize.Height / 12;
            for (int i = 0; i < EdgeColors.Length; i++)
            {
                int x = i % 2 == 0 ? 0 : ClientSize.Width - width;
                int y = i < 2 ? 0 : ClientSize.Height - height;
                using var brush = new SolidBrush(cornerColors[i]);
                e.Graphics.FillRectangle(brush, x, y, width, height);
            }
            if (PaintHudMarker)
                e.Graphics.FillRectangle(Brushes.White, HudMarker(ClientSize));
        }

        internal void RepaintCorner(int corner, Color marker, Point pixel)
        {
            cornerColors[corner] = marker;
            Invalidate(new Rectangle(pixel, new Size(1, 1)));
            Update();
            GameViewWindows.FlushComposition();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ExitSizeMove && IsHandleCreated && m.HWnd == Handle)
            {
                LastMoveEndOrigin = PointToScreen(Point.Empty);
                var completed = NextResizeEnd;
                NextResizeEnd = null;
                completed?.Invoke();
            }
            if (m.Msg == 0x46) // WM_WINDOWPOSCHANGING
            {
                var pos = Marshal.PtrToStructure<WindowPos>(m.LParam);
                const uint moveSizeOrderActivate = 0x0001 | 0x0002 | 0x0004 | 0x0010;
                if (pos.After == 0 && (pos.Flags & moveSizeOrderActivate) == (0x0001 | 0x0002 | 0x0010))
                    StationaryRaises++;
            }
            if (ResizeTrace is { } trace && m.Msg is EnterSizeMove or ExitSizeMove or Sizing or WindowSize or SystemCommand)
            {
                long dimensions = m.LParam.ToInt64();
                var size = m.Msg == WindowSize
                    ? new Size((ushort)dimensions, (ushort)(dimensions >> 16))
                    : ClientSize;
                bool decorated = (GetWindowLongPtrW(m.HWnd, -16).ToInt64() & 0x00C40000) != 0;
                trace.Add(new(m.Msg, unchecked((int)m.WParam), size, decorated, GameViewPlacements.Bounds(m.HWnd)));
            }
            if (m.Msg is LeftDown or LeftUp or RightDown or RightUp || (m.Msg == PointerMove && (m.WParam.ToInt64() & 2) != 0))
                Input.Add((m.Msg, new((short)m.LParam.ToInt64(), (short)(m.LParam.ToInt64() >> 16))));
            base.WndProc(ref m);
            if (TrackFrameChanges && m.Msg == 0x7c && m.WParam == -16) // WM_STYLECHANGING, GWL_STYLE
            {
                var change = Marshal.PtrToStructure<StyleChange>(m.LParam);
                const uint caption = 0x00c00000;
                if ((change.Old & caption) == caption && (change.New & caption) != caption) FrameRemovalAttempts++;
            }
            if (RefuseResize && m.Msg == 0x46) // WM_WINDOWPOSCHANGING
            {
                var pos = Marshal.PtrToStructure<WindowPos>(m.LParam);
                pos.Flags |= 1; // SWP_NOSIZE, as for a game which silently refuses the requested resolution.
                Marshal.StructureToPtr(pos, m.LParam, false);
            }
        }
    }

    private static bool SawResolutionRefresh(List<SizingSample> trace, Size expected)
    {
        var commands = trace.Where(e => e.Message == SystemCommand && (e.Parameter & 0xFFF0) is MaximizeCommand or RestoreCommand).ToList();
        if (commands.Count != 2 || (commands[0].Parameter & 0xFFF0) != MaximizeCommand ||
            (commands[1].Parameter & 0xFFF0) != RestoreCommand) return false;
        int first = trace.IndexOf(commands[0]), second = trace.IndexOf(commands[1]);
        return trace.Take(first).Any(e => e.Message == ExitSizeMove) &&
            trace.Skip(first + 1).Take(second - first - 1).Any(e => e.Message == WindowSize && e.Parameter == 2 && e.Framed) &&
            trace.Skip(second + 1).Any(e => e.Message == WindowSize && e.Parameter == 0 && e.Client == expected && e.Framed);
    }

    private static void PumpUntil(Func<bool> match, string description)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            if (match()) return;
            Thread.Sleep(15);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(4));
        throw new InvalidOperationException(description + (lastSample is null ? "" : " Last fixture pixel: " + lastSample));
    }

    private static void AcquireFixtureFocus(Form source)
    {
        if (Native.GetForegroundWindow() == source.Handle || SetForegroundWindow(source.Handle)) return;
        using var prompt = new Form { Text = "Team Bar · Start layout test", ClientSize = new(240, 68),
            StartPosition = FormStartPosition.CenterScreen, TopMost = true, MinimizeBox = false, MaximizeBox = false };
        using var start = new Button { Text = "Start window test", Dock = DockStyle.Fill, DialogResult = DialogResult.OK };
        using var timeout = new System.Windows.Forms.Timer { Interval = 60000 };
        timeout.Tick += (_, _) => prompt.Close();
        prompt.Controls.Add(start);
        prompt.AcceptButton = start;
        timeout.Start();
        if (prompt.ShowDialog(source) != DialogResult.OK)
            throw new InvalidOperationException("The fixture needs one click to exercise real Windows focus changes.");
    }

    private static nint OwnerAt(Point p) => GetAncestor(WindowFromPoint(p), 2);
    private static bool ThemedFrame(nint window)
    {
        const int nonClientRenderingEnabled = 1;
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, nonClientRenderingEnabled, out int enabled, sizeof(int)));
        return enabled != 0;
    }
    private static (int Kind, Rectangle Box) WindowShape(nint h)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new InvalidOperationException("Could not allocate the fixture's window shape.");
        try
        {
            if (GetWindowRgn(h, region) == 0) return (0, Rectangle.Empty);
            int kind = GetRgnBox(region, out var bounds);
            if (kind == 0) throw new InvalidOperationException("Could not read the fixture's window shape.");
            return (kind, Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        }
        finally { DeleteObject(region); }
    }
    private static Rectangle BoundsOf(nint h) => (Control.FromHandle(h) as Form
        ?? throw new InvalidOperationException("Missing fixture surface.")).Bounds;
    private static Rectangle VisibleFrame(nint window)
    {
        const int extendedFrameBounds = 9;
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, extendedFrameBounds, out RegionBounds frame, Marshal.SizeOf<RegionBounds>()));
        var visible = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
        var shape = WindowShape(window);
        if (shape.Kind == 0) return visible;
        var clip = shape.Box;
        clip.Offset(GameViewPlacements.Bounds(window).Location);
        return Rectangle.Intersect(visible, clip);
    }
    private static Rectangle PreferredArea(Screen monitor)
    {
        var work = monitor.WorkingArea;
        return new Rectangle(work.Right - 820, work.Top + (work.Height - 378) / 2, 756, 378);
    }

    private static bool ExternalOverlayAt(Point pixel)
    {
        var h = OwnerAt(pixel);
        GetWindowThreadProcessId(h, out uint process);
        return h != 0 && process != Environment.ProcessId && (GetWindowLongPtrW(h, -20).ToInt64() & 8) != 0;
    }

    // Preserve the specific edge or seam being tested and choose a clear
    // coordinate along it. An overlapping fixture still fails the gesture.
    private static int UncoveredCoordinate(int length, params Func<int, Point>[] screenPoints)
    {
        foreach (int part in new[] { 4, 2, 6, 1, 3, 5, 7 })
        {
            int coordinate = length * part / 8;
            if (screenPoints.All(pointAt => !ExternalOverlayAt(pointAt(coordinate)))) return coordinate;
        }
        throw new InvalidOperationException("Other topmost windows are covering the fixture's gesture points.");
    }

    private static (Screen, Rectangle) ClearFixtureArea(string? display = null)
    {
        foreach (var screen in Screen.AllScreens.OrderByDescending(s => s.Primary))
        {
            if (display != null && !screen.DeviceName.Equals(display, StringComparison.OrdinalIgnoreCase)) continue;
            var work = screen.WorkingArea;
            var preferred = PreferredArea(screen);
            // Test actual hit targets. A transparent overlay's outer rectangle
            // can cover an entire display while only a few buttons take input.
            bool Available(Rectangle region)
            {
                if (!work.Contains(region)) return false;
                for (int y = region.Top; y < region.Bottom; y += 20)
                    for (int x = region.Left; x < region.Right; x += 20)
                    {
                        if (ExternalOverlayAt(new Point(x, y))) return false;
                    }
                return true;
            }
            if (Available(preferred)) return (screen, preferred);
            for (int y = work.Top + 24; y + preferred.Height <= work.Bottom; y += 64)
                for (int x = work.Left + 24; x + preferred.Width <= work.Right; x += 64)
                {
                    var candidate = new Rectangle(x, y, preferred.Width, preferred.Height);
                    if (Available(candidate)) return (screen, candidate);
                }
        }
        throw new InvalidOperationException("The desktop has no uncovered area for the window fixture.");
    }
    private static string WindowDescription(nint hwnd)
    {
        if (hwnd == 0) return "no window";
        var cls = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string name;
        try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { name = "unavailable"; }
        return $"HWND {hwnd} ({name}, PID {pid}, class {cls})";
    }
    private static Color DesktopPixel(Point point)
    {
        using var pixel = new Bitmap(1, 1);
        using (var g = Graphics.FromImage(pixel)) g.CopyFromScreen(point, Point.Empty, pixel.Size);
        return pixel.GetPixel(0, 0);
    }

    private static bool TintedEdgeShowsMarker(Source game, Color expected, Color actual, Point point)
    {
        int corner = Array.IndexOf(EdgeColors, expected);
        var pixel = game.PointToClient(point);
        var client = game.ClientRectangle;
        if (corner < 0 || !client.Contains(pixel) || (pixel.X > 1 && pixel.Y > 1 &&
            pixel.X < client.Width - 2 && pixel.Y < client.Height - 2)) return false;

        // A transparent desktop edge highlight can tint the physical pixel while
        // hit testing still reaches the game. Modulate the actual corner brush
        // at that exact pixel to distinguish composition from missing game rows.
        Color dark, light;
        try
        {
            game.RepaintCorner(corner, Color.Black, pixel);
            if (OwnerAt(point) != game.Handle) return false;
            dark = DesktopPixel(point);
            game.RepaintCorner(corner, Color.White, pixel);
            if (OwnerAt(point) != game.Handle) return false;
            light = DesktopPixel(point);
        }
        finally { game.RepaintCorner(corner, expected, pixel); }
        if (OwnerAt(point) != game.Handle) return false;
        var restored = DesktopPixel(point);

        static bool Matches(int overBlack, int overWhite, int marker, int before, int after)
        {
            int range = overWhite - overBlack;
            int composed = overBlack + (marker * range + 127) / 255;
            return range >= 48 && Math.Abs(before - composed) <= 2 && Math.Abs(after - composed) <= 2;
        }
        return Matches(dark.R, light.R, expected.R, actual.R, restored.R) &&
               Matches(dark.G, light.G, expected.G, actual.G, restored.G) &&
               Matches(dark.B, light.B, expected.B, actual.B, restored.B);
    }

    private static bool ColorVisible(nint surface, Color expected, Point point)
    {
        var leaf = WindowFromPoint(point);
        var owner = GetAncestor(leaf, 2);
        var actual = DesktopPixel(point);
        int red = Math.Abs(actual.R - expected.R), green = Math.Abs(actual.G - expected.G), blue = Math.Abs(actual.B - expected.B);
        // DWM resamples thumbnail edges. An adjacent native frame can also
        // shade the seam slightly, even when Windows hit-tests the correct HWND.
        int tolerance = Control.FromHandle(surface) is GameViewTile ? 24 : 12;
        bool colorMatches = Math.Max(red, Math.Max(green, blue)) <= tolerance;
        bool matches = owner == surface && (colorMatches ||
            Control.FromHandle(surface) is Source source && TintedEdgeShowsMarker(source, expected, actual, point));
        lastSample = matches ? null : $"Read {actual.R},{actual.G},{actual.B} at {point}; expected {expected.R},{expected.G},{expected.B}.";
        if (owner != surface)
        {
            lastSample += $" Expected {WindowDescription(surface)}, hit {WindowDescription(owner)}.";
            if (leaf != owner) lastSample += $" Child {WindowDescription(leaf)}.";
        }
        if (!matches && Control.FromHandle(surface) is GameViewTile tile) lastSample += " " + tile.ThumbnailDiagnostics;
        return matches;
    }

    // Sample the outermost row and column near each corner. A plain center pixel can hide clipping, a taskbar strip or missing frame pixels.
    private static bool EdgesVisible(nint surface, Rectangle slot)
    {
        for (int i = 0; i < EdgeColors.Length; i++)
        {
            int x = i % 2 == 0 ? slot.Left + slot.Width / 10 : slot.Right - slot.Width / 10;
            int y = i < 2 ? slot.Top + slot.Height / 24 : slot.Bottom - slot.Height / 24;
            int xEdge = i % 2 == 0 ? slot.Left : slot.Right - 1;
            int yEdge = i < 2 ? slot.Top : slot.Bottom - 1;
            if (!ColorVisible(surface, EdgeColors[i], new(xEdge, y)) || !ColorVisible(surface, EdgeColors[i], new(x, yEdge))) return false;
        }
        return true;
    }

    private static bool AllEdgesVisible(GameViewSession session, Rectangle[] slots)
    {
        var faces = session.SurfaceHandles;
        if (Enumerable.Range(0, faces.Length).All(i => session.TryGetVisibleClientRect(session.SourceHandles[i], out var picture) &&
            slots[i].Contains(picture) && EdgesVisible(faces[i], picture))) return true;
        var sources = session.SourceHandles;
        for (int i = 0; i < faces.Length; i++)
        {
            if (Control.FromHandle(sources[i]) is not Source source) continue;
            var shape = WindowShape(source.Handle);
            var client = source.RectangleToScreen(source.ClientRectangle);
            lastSample += $" Slot {i + 1} {slots[i]}, surface {faces[i]} bounds {BoundsOf(faces[i])}, source {source.Handle} frame {source.Bounds}, client {client}, region {shape.Kind} {shape.Box}.";
            if (Control.FromHandle(faces[i]) is GameViewTile tile) lastSample += $" {tile.ThumbnailDiagnostics}.";
        }
        return false;
    }

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static void PreviewSelection(GameViewSession session, int index)
    {
        nint preview = session.SurfaceHandles[index];
        if (Control.FromHandle(preview) is not GameViewTile) throw new InvalidOperationException("Only an owned passive preview can receive the test selection.");
        var size = BoundsOf(preview).Size;
        nint coords = (nint)((size.Height / 2 << 16) | (size.Width / 2));
        SendMessageW(preview, LeftDown, 1, coords);
        if (!session.IsActive || !IsWindow(preview)) throw new InvalidOperationException("Preview closed before releasing selection: " + session.EndReason);
        SendMessageW(preview, LeftUp, 0, coords);
        Application.DoEvents();
        if (!session.IsActive) throw new InvalidOperationException("Failed to promote preview: " + session.EndReason);
    }

    private static Point ClickFixedPreview(GameViewSession session, int index, Point desktop, bool right = false, Source? blocker = null)
    {
        nint source = session.SourceHandles[index];
        if (FixtureMouseInput(source) is null || Control.FromHandle(session.SurfaceHandles[index]) is not GameViewTile tile ||
            !session.TryGetVisibleClientRect(source, out var picture) || !picture.Contains(desktop))
            throw new InvalidOperationException("The test point must be inside an owned fixture's visible preview client.");
        var client = GameViewPlacements.ClientBounds(source, Environment.ProcessId);
        var local = tile.PointToClient(desktop);
        static int Scale(int pixel, int from, int to) => from == 1 ? (to - 1) / 2 :
            (int)Math.Round((double)pixel * (to - 1) / (from - 1));
        var projected = new Point(Scale(desktop.X - picture.Left, picture.Width, client.Width),
            Scale(desktop.Y - picture.Top, picture.Height, client.Height));
        var nativePoint = new Point(client.Left + projected.X, client.Top + projected.Y);
        bool originalBand = GameViewStacking.IsTopmost(source);
        if (blocker is not null)
        {
            blocker.Bounds = new Rectangle(nativePoint.X - 18, nativePoint.Y - 18, 36, 36);
            PumpUntil(() => OwnerAt(nativePoint) == blocker.Handle, "The fixture overlay must cover the mapped point.");
        }
        var previousOwner = OwnerAt(nativePoint);
        if (FixtureMouseInput(previousOwner) is null || (blocker is null && !session.SourceHandles.Contains(previousOwner)))
            throw new InvalidOperationException("The mapped pointer destination must be inside this fixture's own layout.");
        Func<bool>? decline = blocker is null ? null : () => session.PreviewNotice?.Contains("covered", StringComparison.OrdinalIgnoreCase) == true;
        using var stop = new CancellationTokenSource();
        using var ready = new ManualResetEventSlim();
        var polling = Task.Factory.StartNew(() =>
        {
            long began = 0, held = 0;
            bool observedUp = false, pressedAtDestination = false, reachedPoint = false;
            nint lastOwner = 0, lastForeground = 0;
            ready.Set();
            while (!stop.IsCancellationRequested)
            {
                if (AtRequestedPixel(Cursor.Position, nativePoint))
                {
                    reachedPoint = true;
                    bool down = (GetAsyncKeyState(right ? 2 : 1) & 0x8000) != 0;
                    pressedAtDestination |= down;
                    lastForeground = Native.GetForegroundWindow();
                    lastOwner = OwnerAt(nativePoint);
                    if (lastForeground == source && lastOwner == source)
                    {
                        if (down)
                        {
                            long now = Environment.TickCount64;
                            if (began == 0) began = now;
                            held = Math.Max(held, now - began);
                        }
                        else if (began != 0) observedUp = true;
                    }
                }
                Thread.Sleep(2);
            }
            return (held, observedUp, pressedAtDestination, reachedPoint, lastOwner, lastForeground);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        (long held, bool observedUp, bool pressedAtDestination, bool reachedPoint, nint lastOwner, nint lastForeground) sample;
        Exception? failure = null;
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("The physical mouse observer did not start.");
            NativeGesture(tile.Handle, local, right ? local : null, previewSource: source,
                settled: () => OwnerAt(nativePoint) == previousOwner && GameViewStacking.IsTopmost(source) == originalBand,
                declined: decline);
        }
        catch (InvalidOperationException error) { failure = error; }
        finally
        {
            stop.Cancel();
            sample = polling.GetAwaiter().GetResult();
        }
        if (failure is not null || (blocker is null && (sample.held < 10 || !sample.observedUp)))
            throw new InvalidOperationException($"Projected click failed. {failure?.Message} " +
                $"Notice '{session.PreviewNotice}', ended '{session.EndReason}', origin {desktop} on tile {tile.Handle}, " +
                $"mapped {nativePoint} on game {source} (current owner {OwnerAt(nativePoint)}, original {previousOwner}), " +
                $"cursor {Cursor.Position}, foreground {Native.GetForegroundWindow()}, capture {GetCapture()}. " +
                $"Observed at destination: reached {sample.reachedPoint}, button went down {sample.pressedAtDestination}, " +
                $"held {sample.held} ms, release {sample.observedUp}, owner {sample.lastOwner}, foreground {sample.lastForeground}. " +
                $"Source messages: {string.Join(", ", FixtureMouseInput(source)!)}.", failure);
        if (blocker is not null && sample.pressedAtDestination)
            throw new InvalidOperationException("The desktop mouse button went down over a blocked destination.");
        return projected;
    }

    // Insert one complete gesture into the desktop input stream. The foreground
    // must be ours, every point must hit this exact fixture, and no user button may
    // be held. The native game windows in a real session are never eligible targets.
    private static bool AtRequestedPixel(Point actual, Point requested) =>
        Math.Abs(actual.X - requested.X) <= 1 && Math.Abs(actual.Y - requested.Y) <= 1;

    private static MousePacket Motion(Point point)
    {
        var desktop = SystemInformation.VirtualScreen;
        return new() { Mouse = new() {
            X = (int)(((long)(point.X - desktop.Left) * 65536 + 32768) / desktop.Width),
            Y = (int)(((long)(point.Y - desktop.Top) * 65536 + 32768) / desktop.Height),
            Flags = 0x8000 | 0x4000 | 0x2000 | 1 // ABSOLUTE | VIRTUALDESK | MOVE_NOCOALESCE | MOVE
        } };
    }

    private static Point NativeGesture(Source target, Point start, Point? dragEnd = null)
        => NativeGesture(target.Handle, start, dragEnd);

    private static List<(int Message, Point Point)>? FixtureMouseInput(nint window) =>
        Control.FromHandle(window) is Source source ? source.Input : (NativeWindow.FromHandle(window) as NativeGameSizeTrace)?.Input;

    private static Point NativeGesture(nint target, Point start, Point? dragEnd = null, nint previewSource = 0,
        Func<bool>? settled = null, Func<bool>? declined = null)
    {
        if (previewSource != 0 && (Control.FromHandle(target) is not GameViewTile tile || tile.Source != previewSource))
            throw new InvalidOperationException("The preview must belong to the fixture and display the intended source.");
        var input = FixtureMouseInput(previewSource != 0 ? previewSource : target) ??
            throw new InvalidOperationException("Mouse input is restricted to owned fixture windows.");
        var bounds = GameViewPlacements.ClientBounds(target, Environment.ProcessId);
        Point ToScreen(Point point) => new(bounds.Left + point.X, bounds.Top + point.Y);
        Point a = ToScreen(start), b = ToScreen(dragEnd ?? start);
        if (FixtureMouseInput(Native.GetForegroundWindow()) is null || Control.MouseButtons != MouseButtons.None
            || GetCapture() != 0 || OwnerAt(a) != target || OwnerAt(b) != target)
            throw new InvalidOperationException($"Desktop input no longer targets the isolated fixture at {a}, {b}. " +
                $"Start owner {WindowDescription(OwnerAt(a))}, foreground {WindowDescription(Native.GetForegroundWindow())}, " +
                $"capture {GetCapture()}, mouse buttons {Control.MouseButtons}.");
        bool drag = dragEnd.HasValue;
        MousePacket Button(uint flag) => new() { Mouse = new() { Flags = flag } };
        MousePacket[] packets = drag
            ? [Button(8), Motion(b), Button(16)]
            : [Button(2), Button(4)];
        var cursor = Cursor.Position;
        int previous = input.Count;
        try
        {
            // Absolute SendInput moves use normalized coordinates which can round on
            // a multi-monitor desktop. Place the first pixel exactly, then submit the
            // actual button gesture through the hardware input stream.
            Cursor.Position = a;
            Application.DoEvents();
            if (Cursor.Position != a || OwnerAt(a) != target || FixtureMouseInput(Native.GetForegroundWindow()) is null)
                throw new InvalidOperationException($"Could not position the pointer at {a}. Actual {Cursor.Position}, HWND {OwnerAt(a)}.");
            if (SendInput((uint)packets.Length, packets, Marshal.SizeOf<MousePacket>()) != packets.Length)
                throw new InvalidOperationException("Windows did not deliver the fixture input batch.");
            PumpUntil(() => declined?.Invoke() ?? input.Skip(previous).Any(entry => entry.Message == (drag ? RightUp : LeftUp)),
                declined is null ? "The fixture never received the hardware-path release for its gesture." :
                "The layout did not report blocking the covered preview click.");
            if (previewSource != 0)
                PumpUntil(() => AtRequestedPixel(Cursor.Position, b) && Control.MouseButtons == MouseButtons.None &&
                    settled!(), "The preview cursor or the original window order was not restored after the gesture.");
            var end = Cursor.Position;
            if (!AtRequestedPixel(end, b)) throw new InvalidOperationException($"Fixture cursor ended at {end}, expected {b}.");
            var current = GameViewPlacements.ClientBounds(target, Environment.ProcessId);
            return new Point(end.X - current.Left, end.Y - current.Top);
        }
        finally
        {
            var at = Cursor.Position;
            if ((at == a || AtRequestedPixel(at, b)) && Control.MouseButtons == MouseButtons.None) Cursor.Position = cursor;
        }
    }

    internal static void Run(string output, bool multiScreenOnly = false)
    {
        var checks = new List<string>();
        void Check(bool value, string text)
        {
            if (!value) throw new InvalidOperationException(text);
            checks.Add(text);
        }
        if (multiScreenOnly)
        {
            var primary = Screen.PrimaryScreen ?? throw new InvalidOperationException("Interactive display required.");
            TestStacking(PreferredArea(primary), Check);
            TestMultiScreen(primary, PreferredArea(primary), Check);
            if (Screen.AllScreens.Length > 1) TestCrossMonitorPromotion(Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "cross-monitor-promotion")
        {
            TestCrossMonitorPromotion(Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "hud-demotion")
        {
            TestWindowedHudRoundTrips(PreferredArea(Screen.PrimaryScreen ?? throw new InvalidOperationException("Display required.")), Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "preview-activation")
        {
            TestPreviewActivation(Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        var (monitor, area) = ClearFixtureArea(Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_DISPLAY"));
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "fixed-tab-click")
        {
            TestFixedPreviewTabClick(area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "swap-buttons")
        {
            TestSlotSwapButtons(area, Check);
            GameViewSwapButtonTests.Run(area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "parallel-renderer-swap")
        {
            TestParallelRendererSwap(area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "smooth-swap")
        {
            TestSmoothSwap(area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "framed-preview-edges")
        {
            var foreground = Native.GetForegroundWindow();
            var passiveLayout = new GameViewLayout("Inactive edge fixture", GameViewKind.Corner, monitor.DeviceName, true, ["Master", "Preview"]);
            var passiveSlots = GameViewGeometry.Slots(GameViewKind.Corner, area, 2);
            string? diagnostics;
            using (var session = GameViewSession.StartForTest(passiveLayout, passiveSlots, [0, 0]))
            using (var source = new Source(Color.IndianRed, passiveSlots[0].Location, framed: true, passive: true))
            {
                source.Open();
                source.Bounds = passiveSlots[0];
                using var picture = new GameViewTile(session, source.Handle, Environment.ProcessId, "Inactive edge fixture",
                    useSlotResolution: false) { TopMost = true };
                picture.Open(passiveSlots[1], source.ClientSize);
                PumpUntil(() => picture.TryGetDisplayedClient(out var displayed) && EdgesVisible(picture.Handle, displayed),
                    "The inactive framed thumbnail lost an edge. " + picture.ThumbnailDiagnostics);
                diagnostics = picture.ThumbnailDiagnostics;
            }
            Check(Native.GetForegroundWindow() == foreground, "The passive framed preview preserved the original foreground window.");
            checks.Add("The game's four corner colors reached their first and last displayed rows and columns in a matched framed thumbnail.");
            CollectorFormat.WriteNew(output, new { passed = true, checks, diagnostics, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "title-bars")
        {
            TestLayoutTitleBars(monitor, area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "split-edges")
        {
            TestFixedSplitEdges(monitor, area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "fixed-focus")
        {
            TestFocusAfterDisablingSwaps(area, Check);
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        if (Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") is "fullscreen" or "dropped-seams")
        {
            TestEqFullscreenPreflight(area, Check, seamsOnly: Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_CASE") == "dropped-seams");
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
            return;
        }
        var slots = GameViewGeometry.Slots(GameViewKind.Corner, area, 2);
        // The smooth-swap check samples the physical desktop, including a transparent
        // overlay. Finish it before opening the other tests' always-on-top windows.
        TestSmoothSwap(area, Check);
        TestSlotSwapButtons(area, Check);
        GameViewSwapButtonTests.Run(area, Check);
        TestParallelRendererSwap(area, Check);
        TestFocusAfterDisablingSwaps(area, Check);
        using var first = new Source(Color.DarkCyan, new(area.Left + 35, area.Top + 100));
        using var second = new Source(Color.IndianRed, new(area.Left + 310, area.Top + 64), framed: true);
        first.Open();
        second.Open();
        var positions = new[] { first.Bounds, second.Bounds };
        nint[] sources = [first.Handle, second.Handle];
        var savedStyle = GetWindowLongPtrW(second.Handle, -16).ToInt64();
        using var ownProcess = Process.GetCurrentProcess();
        var secondIdentity = new DetectedClient(Environment.ProcessId, ownProcess.StartTime.ToUniversalTime().Ticks,
            second.Handle.ToInt64(), second.Left, new("Two", 1, 1), true);
        var firstIdentity = secondIdentity with { Window = first.Handle.ToInt64(), Left = first.Left, Character = new("One", 1, 1) };
        var originalAnchor = AnchorPositions.ReadFrame(secondIdentity);
        var layout = new GameViewLayout("Fixture", GameViewKind.Corner, monitor.DeviceName, true, ["One", "Two"]);

        // An ordinary app lies behind the original second game. It must become
        // physically reachable in the space around the slots, even after a swap.
        var originalClient = second.RectangleToScreen(second.ClientRectangle);
        var exposed = new Point(originalClient.Right - 23, originalClient.Top + originalClient.Height / 2);
        Check(area.Contains(exposed) && slots.All(slot => !slot.Contains(exposed)), "The exterior hit-test point lies outside both corner slots.");
        using var ordinary = new Source(Color.MediumPurple, new(exposed.X - 40, exposed.Y - 30));
        ordinary.Open();
        ordinary.ClientSize = new(80, 60);
        var overlayPoint = new Point(slots[0].Left + slots[0].Width / 3, slots[0].Top + slots[0].Height / 3);
        using var overlay = new Form { TopMost = true, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(overlayPoint.X - 8, overlayPoint.Y - 8, 16, 16) };
        overlay.Show();
        AcquireFixtureFocus(second);
        // The focus prompt can leave an unrelated app between these ordinary-band
        // fixtures. Establish the controlled stack after it closes, without moving
        // any external window or changing foreground focus.
        const uint keepBoundsAndFocus = 0x0001 | 0x0002 | 0x0010 | 0x0200;
        foreach (var fixture in new[] { ordinary, first, second })
            if (!Native.SetWindowPos(fixture.Handle, 0, 0, 0, 0, 0, keepBoundsAndFocus))
                throw new InvalidOperationException("Could not arrange the test windows before applying the layout.");
        PumpUntil(() => ColorVisible(second.Handle, second.BackColor, exposed), "The source must cover the ordinary window before Apply.");
        var resizeTrace = second.ResizeTrace = [];
        using (var session = GameViewSession.StartForTest(layout, area, sources))
        {
            var fittedClient = second.RectangleToScreen(second.ClientRectangle);
            Check(second.VisibleFrame == slots[0] && slots[0].Contains(fittedClient) && fittedClient.Top > slots[0].Top &&
                GetWindowLongPtrW(second.Handle, -16).ToInt64() == savedStyle,
                "The decorated source keeps its own frame and client insets inside the master slot.");
            Check(resizeTrace.Any(e => e.Message == WindowSize && e.Client == fittedClient.Size && e.Framed),
                "The source receives its fitted client dimensions with its frame present.");
            resizeTrace.Clear();
            var surfaces = session.SurfaceHandles;
            Check(surfaces[0] == first.Handle && Control.FromHandle(surfaces[1]) is GameViewTile,
                "Slot 1 is the real game window and slot 2 is a passive preview.");
            Check(first.RectangleToScreen(first.ClientRectangle) == slots[0] && BoundsOf(surfaces[1]) == slots[1] &&
                session.TryGetVisibleClientRect(second.Handle, out var previewClient) && slots[1].Contains(previewClient) &&
                AnchorPositions.ReadFrame(secondIdentity, session)?.Area == previewClient,
                "The native borderless master fills its slot and the preview anchors follow its displayed game area.");
            PumpUntil(() => AllEdgesVisible(session, slots), "Both displayed game areas should reach their measured edges.");
            Check(OwnerAt(overlayPoint) == overlay.Handle, "Existing Team Bar controls stay clickable above the native game.");
            PumpUntil(() => ColorVisible(ordinary.Handle, ordinary.BackColor, exposed), "The underlying ordinary app must appear in unused monitor space.");
            second.BackColor = Color.DarkGoldenrod;
            second.Refresh();
            PumpUntil(() => ColorVisible(surfaces[1], second.BackColor, Center(slots[1])), "The passive preview did not display the background source repaint.");
            checks.Add("A background repaint reaches the passive preview, including its complete four-sided game picture.");
            var sourcePoint = new Point(first.ClientSize.Width / 2, first.ClientSize.Height / 2);
            Application.DoEvents();
            first.Input.Clear();
            second.Input.Clear();
            ordinary.Input.Clear();
            NativeGesture(first, sourcePoint);
            Check(first.Input.SequenceEqual([(LeftDown, sourcePoint), (LeftUp, sourcePoint)]),
                $"The main game receives a real desktop click once at {sourcePoint}. Received {first.InputReport}.");
            first.Input.Clear();
            PreviewSelection(session, 1);
            Check(session.SourceHandles.SequenceEqual(sources.Reverse()) && session.SurfaceHandles[0] == second.Handle
                && Control.FromHandle(session.SurfaceHandles[1]) is GameViewTile && Native.GetForegroundWindow() == second.Handle,
                "Promoting a preview places its real game in the main slot with keyboard focus.");
            Check(!first.Input.Any() && !second.Input.Any(), "The promoting click never reaches either game.");
            Check(OwnerAt(overlayPoint) == overlay.Handle && AnchorPositions.ReadFrame(secondIdentity, session)?.Area == fittedClient
                && AnchorPositions.ReadFrame(firstIdentity, session)?.Area == slots[1],
                "Character-following bars track the exchanged slots and remain clickable.");
            PumpUntil(() => AllEdgesVisible(session, slots),
                "Both pictures must still cover all four exterior edges after promotion.");
            Check(second.VisibleFrame == slots[0] && second.PointToScreen(Point.Empty) == fittedClient.Location,
                "The promoted game retains its native caption and input origin.");
            var from = new Point(70, 100);
            var to = new Point(114, 111);
            var physicalEnd = NativeGesture(second, from, to);
            Check(second.Input.Contains((RightDown, from)) && second.Input.Contains((PointerMove, physicalEnd)) && second.Input.Contains((RightUp, physicalEnd))
                && first.Input.Count == 0, $"The promoted game receives a right-drag from {from} to {to}. Received {second.InputReport}.");
            second.Input.Clear();
            PumpUntil(() => ColorVisible(ordinary.Handle, ordinary.BackColor, exposed), "Refocusing the game must keep the ordinary app reachable.");
            var ordinaryAt = ordinary.PointToClient(exposed);
            NativeGesture(ordinary, ordinaryAt);
            Check(ordinary.Input.SequenceEqual([(LeftDown, ordinaryAt), (LeftUp, ordinaryAt)]) && !first.Input.Any() && !second.Input.Any(),
                $"An uncovered click at {ordinaryAt} reaches only the ordinary app. Received {ordinary.InputReport}.");
            ordinary.Hide();
            PumpUntil(() => Control.FromHandle(OwnerAt(exposed)) is null, "Unused space must expose the desktop or another external application.");
            // Windows focus must wake the swap even between health checks.
            session.PauseLayoutMonitorForTest();
            AcquireFixtureFocus(first);
            PumpUntil(() => session.SourceHandles[0] == first.Handle && session.SurfaceHandles[0] == first.Handle,
                "Selecting a game through Windows should promote the focused native game.");
            checks.Add("A Windows foreground change promotes the corresponding native game.");
            Check(!resizeTrace.Any(e => e.Message == SystemCommand && (e.Parameter & 0xFFF0) is MaximizeCommand or RestoreCommand),
                "Selecting and promoting characters leaves both native game resolutions unchanged.");
        }
        Check(first.Bounds == positions[0] && second.Bounds == positions[1] && GetWindowLongPtrW(second.Handle, -16).ToInt64() == savedStyle
            && WindowShape(second.Handle).Kind == 0, "Ending restores both original frames, window shapes and the original title-bar style.");
        Check(resizeTrace.Any(e => e.Message == WindowSize && e.Client == originalClient.Size && e.Framed),
            "Swaps add no resize notices, and End closes a single resize cycle after Windows restores the original client resolution.");
        Check(SawResolutionRefresh(resizeTrace, originalClient.Size),
            "Ending reinitializes the framed game's original resolution through the same native maximize and restore.");
        second.ResizeTrace = null;
        Check(originalAnchor is not null && AnchorPositions.ReadFrame(secondIdentity) == originalAnchor,
            "After ending the layout, character anchors use the original window again.");
        overlay.Hide();
        var uppermost = Screen.AllScreens.OrderBy(s => s.Bounds.Top).First();
        var cornerSlot = new Rectangle(uppermost.WorkingArea.Left + 40, uppermost.WorkingArea.Top, 390, 225);
        var cornerLayout = new GameViewLayout("Final move", GameViewKind.EqualSplit, uppermost.DeviceName, false, ["Two"]);
        second.ResizeTrace = [];
        using (var atCorner = GameViewSession.StartForTest(cornerLayout, [cornerSlot], [second.Handle]))
        {
            Check(second.VisibleFrame == cornerSlot && second.LastMoveEndOrigin == second.PointToScreen(Point.Empty) &&
                GetWindowLongPtrW(second.Handle, -16).ToInt64() == savedStyle,
                "At the monitor edge, the native title bar stays in its slot and the game receives its final input origin.");
            second.ResizeTrace.Clear();
            atCorner.ApplyForTest(cornerLayout, [cornerSlot], [second.Handle]);
            Check(second.ResizeTrace.Count == 0, "An unchanged corner layout sends no further move/size notifications.");
        }
        second.ResizeTrace = null;
        using (var third = new Source(Color.CadetBlue, new(area.Left + 50, area.Top + 50)))
        {
            third.Open();
            using var trio = GameViewSession.StartForTest(layout with { Characters = ["One", "Two", "Three"] }, area,
                [first.Handle, second.Handle, third.Handle]);
            PreviewSelection(trio, 2);
            var threeSlots = GameViewGeometry.Slots(GameViewKind.Corner, area, 3);
            Check(trio.SourceHandles.SequenceEqual([third.Handle, second.Handle, first.Handle]) && trio.SurfaceHandles[0] == third.Handle
                && third.Input.Count == 0 && trio.SurfaceHandles.Skip(1).All(h => Control.FromHandle(h) is GameViewTile),
                "A three-slot layout promotes the third native game and keeps the middle preview in its slot.");
            PumpUntil(() => AllEdgesVisible(trio, threeSlots),
                "All three pictures must reach the edges after selecting the third character.");
            checks.Add("All three views retain their edge pixels after the third character is promoted.");
        }
        using (var moved = GameViewSession.StartForTest(layout, area, sources))
        {
            var newPosition = new Point(first.Left + 15, first.Top + 10);
            first.Location = newPosition;
            PumpUntil(() => !moved.IsActive, "An external source move must end the layout.");
            Check(first.Location == newPosition && second.Bounds == positions[1],
                "Ending on a user move keeps the new position and restores the other source.");
        }
        first.Bounds = positions[0];
        second.ClientSize = new(210, 145);
        var smallerOriginal = second.Bounds;
        var savedMaximized = WindowLayouts.Capture("Two", second.Handle) with { Maximized = true };
        second.WindowState = FormWindowState.Maximized;
        PumpUntil(() => IsZoomed(second.Handle) && WindowLayouts.Capture("Two", second.Handle) == savedMaximized,
            "The second source must start maximized with its original normal rectangle.");
        second.ResizeTrace = [];
        using (var maximized = GameViewSession.StartForTest(layout, area, sources))
        {
            Check(!IsZoomed(second.Handle) && second.VisibleFrame == slots[0],
                "A maximized source temporarily fits the main game slot.");
            Check(second.ResizeTrace.Any(e => e.Message == WindowSize && e.Parameter == 0) &&
                !second.ResizeTrace.Any(e => e.Message == SystemCommand && (e.Parameter & 0xFFF0) == MaximizeCommand),
                "Applying to an already maximized game restores its renderer without maximizing it a second time.");
            second.ResizeTrace = null;
            PumpUntil(() => ColorVisible(maximized.SurfaceHandles[1], second.BackColor, Center(slots[1])),
                "The temporarily restored preview must show the real game color.");
        }
        PumpUntil(() => IsZoomed(second.Handle) && WindowLayouts.Capture("Two", second.Handle) == savedMaximized,
            "Ending must restore both maximized state and saved normal rectangle.");
        second.WindowState = FormWindowState.Normal;
        PumpUntil(() => !IsZoomed(second.Handle) && second.Bounds == smallerOriginal, "Unmaximizing must restore the true original frame.");
        Check(GetWindowLongPtrW(second.Handle, -16).ToInt64() == savedStyle,
            "The maximized source restores its normal geometry and original window style.");
        second.Bounds = positions[1];
        using (var customShape = new Region(new Rectangle(0, 0, 160, 140)))
        {
            customShape.Exclude(new Rectangle(20, 20, 15, 15));
            second.Region = customShape;
            try
            {
                var originalShape = WindowShape(second.Handle);
                bool customRejected = false;
                try { using var invalid = GameViewSession.StartForTest(layout, area, sources); }
                catch (InvalidOperationException) { customRejected = true; }
                Check(customRejected && originalShape.Kind == 3 && WindowShape(second.Handle) == originalShape
                    && first.Bounds == positions[0] && second.Bounds == positions[1],
                    "A pre-existing custom region is preserved and rejected before moving any fixture window.");
            }
            finally { second.Region = null; }
        }
        bool rejected = false;
        second.RefuseResize = true;
        try { using var invalid = GameViewSession.StartForTest(layout, area, sources); }
        catch (InvalidOperationException) { rejected = true; }
        finally { second.RefuseResize = false; }
        Check(rejected && first.Bounds == positions[0] && second.Bounds == positions[1] && GetWindowLongPtrW(second.Handle, -16).ToInt64() == savedStyle,
            "A later source refusing the tile size rolls back the earlier source, the partial placement and original chrome.");
        TestFixedSplitEdges(monitor, area, Check);
        TestLayoutTitleBars(monitor, area, Check);
        using (var left = new Source(Color.Sienna, first.Location, framed: true))
        using (var right = new Source(Color.DarkBlue, second.Location, framed: true))
        {
            left.Open();
            right.Open();
            var original = new[] { left.Bounds, right.Bounds };
            var grid = layout with { Kind = GameViewKind.Grid, SwapOnClick = false, MatchMasterResolution = false, Characters = ["One", "", "Three", ""] };
            var cells = GameViewGeometry.Slots(grid.Kind, area, 4);
            nint[] vacant = [0, 0, 0, 0], solo = [left.Handle, 0, 0, 0], pair = [left.Handle, 0, right.Handle, 0];
            using (var sparse = GameViewSession.StartForTest(grid, area, vacant))
            {
                Check(sparse.IsActive && sparse.OccupiedCount == 0 && sparse.SurfaceHandles.SequenceEqual(vacant),
                    "A layout can wait for its first login with all four numbered slots empty.");
                sparse.ApplyForTest(grid, area, solo);
                var trace = left.ResizeTrace = [];
                int moves = left.PositionChanges;
                AcquireFixtureFocus(left);
                sparse.ApplyForTest(grid, area, pair, background: true);
                Check(sparse.SourceHandles.SequenceEqual(pair) && left.VisibleFrame == cells[0] && right.VisibleFrame == cells[2] &&
                    left.PositionChanges == moves && trace.Count == 0 && Native.GetForegroundWindow() == left.Handle,
                    "A background login fills slot three while preserving slot one, its keyboard focus and both empty seats. " +
                    $"Seats {string.Join(",", sparse.SourceHandles)}, expected {string.Join(",", pair)}; " +
                    $"bounds {left.Bounds}/{right.Bounds}, expected {cells[0]}/{cells[2]}; moves {moves} → {left.PositionChanges}, " +
                    $"resize messages {trace.Count}, foreground {Native.GetForegroundWindow()}, expected {left.Handle}.");
                right.WindowState = FormWindowState.Minimized;
                PumpUntil(() => sparse.SourceHandles.SequenceEqual(solo), "Minimizing a game should vacate its numbered seat.");
                Check(sparse.IsActive && left.PositionChanges == moves && trace.Count == 0 && left.VisibleFrame == cells[0],
                    "Minimizing the third seat leaves the first game untouched.");
                right.WindowState = FormWindowState.Normal;
                sparse.ApplyForTest(grid, area, pair, background: true);
                sparse.ApplyForTest(grid, area, pair);
                Check(sparse.SourceHandles.SequenceEqual(pair) && right.VisibleFrame == cells[2] && left.PositionChanges == moves && trace.Count == 0,
                    "Returning to the same seat and reapplying the active grid send no placement or resize changes to its neighbor.");
                var translated = area;
                translated.Offset(-18, -12);
                var shifted = GameViewGeometry.Slots(grid.Kind, translated, 4);
                sparse.ApplyForTest(grid, translated, pair);
                Check(left.VisibleFrame == shifted[0] && right.VisibleFrame == shifted[2] && left.PositionChanges == moves + 1 &&
                    trace.Select(e => e.Message).SequenceEqual([EnterSizeMove, ExitSizeMove]) &&
                    left.LastMoveEndOrigin == left.PointToScreen(Point.Empty),
                    "Moving cells of the same size finishes one native move at the playable origin without recreating the rendering resolution.");
                trace.Clear();
                var rows = grid with { Kind = GameViewKind.Rows };
                var rowCells = GameViewGeometry.Slots(rows.Kind, translated, 4);
                sparse.ApplyForTest(rows, translated, pair);
                Check(left.VisibleFrame == rowCells[0] && right.VisibleFrame == rowCells[2] &&
                    SawResolutionRefresh(trace, left.ClientSize) && !trace.Any(s => s.Frame == original[0]),
                    "Switching from a grid to rows takes each occupied viewport directly to its new resolution.");
                sparse.ApplyForTest(rows, translated, solo);
                moves = left.PositionChanges;
                trace.Clear();
                sparse.ApplyForTest(rows, translated, pair, background: true);
                Check(left.PositionChanges == moves && trace.Count == 0 && left.VisibleFrame == rowCells[0] &&
                    right.VisibleFrame == rowCells[2] && sparse.SurfaceHandles.SequenceEqual(pair),
                    "A late login fits its own native frame without resizing or moving the occupied slot.");
                left.ResizeTrace = null;
            }
            Check(left.Bounds == original[0] && right.Bounds == original[1],
                "End restores the original placements from before any of the sparse layout transitions.");
        }
        var equal = GameViewGeometry.Slots(GameViewKind.EqualSplit, area, 2);
        using (var session = GameViewSession.StartForTest(layout with { Kind = GameViewKind.EqualSplit, SwapOnClick = false }, area, sources))
        {
            var pictures = new[] { first, second }.Select(s => s.RectangleToScreen(s.ClientRectangle)).ToArray();
            Check(session.SurfaceHandles.SequenceEqual(sources) && first.Bounds == equal[0] && second.VisibleFrame == equal[1] &&
                pictures[0] == equal[0] && equal[1].Contains(pictures[1]) && pictures[1].Top > equal[1].Top,
                "Fixed mode preserves each game's native choice of a borderless or decorated window.");
            PumpUntil(() => AllEdgesVisible(session, pictures),
                "The two fixed games must each display all four edges of the native game picture within their slots.");
            var last = new Point(second.ClientSize.Width - 1, second.ClientSize.Height - 1);
            Application.DoEvents();
            first.Input.Clear();
            second.Input.Clear();
            NativeGesture(second, Point.Empty);
            NativeGesture(second, last);
            Check(second.Input.SequenceEqual([(LeftDown, Point.Empty), (LeftUp, Point.Empty), (LeftDown, last), (LeftUp, last)])
                && first.Input.Count == 0 && Native.GetForegroundWindow() == second.Handle,
                $"An inactive fixed slot receives clicks at (0,0) and {last}. Received {second.InputReport}.");
            second.Input.Clear();
            var from = new Point(130, 100);
            var to = new Point(90, 77);
            var physicalEnd = NativeGesture(first, from, to);
            Check(first.Input.Contains((RightDown, from)) && first.Input.Contains((PointerMove, physicalEnd)) && first.Input.Contains((RightUp, physicalEnd))
                && second.Input.Count == 0 && session.SourceHandles.SequenceEqual(sources),
                $"The other fixed slot receives a right-drag from {from} to {to} with stationary views. Received {first.InputReport}.");
            int ended = 0;
            session.Ended += (_, _) => ended++;
            second.Close();
            PumpUntil(() => session.SourceHandles[1] == 0, "Closing a source must vacate just its slot.");
            Check(session.IsActive && ended == 0 && session.OccupiedCount == 1 && first.Bounds == equal[0],
                "Closing one client keeps the remaining game in place and the layout active.");
            session.Dispose();
            Check(ended == 1 && first.Bounds == positions[0] && IsWindow(first.Handle) &&
                (GetWindowLongPtrW(first.Handle, -16).ToInt64() & 0x00C00000) == 0,
                "Ending restores the remaining game's original borderless window exactly once.");
        }
        TestStacking(area, Check);
        TestMultiScreen(monitor, area, Check);
        CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false });
    }

    private static void TestStacking(Rectangle area, Action<bool, string> check)
    {
        // A fixture stands in for Explorer's topmost taskbar. Nothing reorders
        // Explorer or sends clicks to a player's games.
        var slots = GameViewGeometry.Slots(GameViewKind.EqualSplit, area, 2);
        var layout = new GameViewLayout("Stack", GameViewKind.EqualSplit, "Display", false, ["One", "Two"])
            { AvoidTaskbar = true };
        using var left = new Source(Color.DarkCyan, area.Location);
        using var right = new Source(Color.DarkGreen, area.Location);
        using var desktop = new Source(Color.Purple, area.Location);
        using var taskbar = new Form { TopMost = true, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = new(slots[1].Left + 30, slots[1].Bottom - 32, 100, 25) };
        using var settings = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = new(slots[0].Left + 12, slots[0].Top + 30, 120, 90) };
        using var overlay = new Form { TopMost = true, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = new(slots[1].Left + 15, slots[1].Top + 20, 100, 45) };
        left.Open(); right.Open(); desktop.Open(); taskbar.Show(); overlay.Show();
        nint[] games = [left.Handle, right.Handle];
        bool IsControl(nint h) => h == overlay.Handle || (settings.IsHandleCreated && h == settings.Handle);
        AcquireFixtureFocus(left);
        using (var session = GameViewSession.StartForTest(layout, slots, games,
            controlFilter: IsControl, taskbars: () => [taskbar.Handle]))
        {
            var overlap = layout with { AvoidTaskbar = false };
            session.ApplyForTest(overlap, slots, games, background: true);
            var taskbarPoint = Center(taskbar.Bounds);
            check(!GameViewStacking.IsTopmost(left.Handle) && GameViewStacking.IsTopmost(right.Handle) &&
                OwnerAt(taskbarPoint) == right.Handle, "Overlap covers the taskbar with the intersecting game only.");
            desktop.Bounds = slots[1];
            AcquireFixtureFocus(desktop);
            session.ApplyForTest(overlap, slots, games, background: true);
            check(!GameViewStacking.IsTopmost(right.Handle) && OwnerAt(taskbarPoint) == taskbar.Handle &&
                OwnerAt(Center(slots[1])) == desktop.Handle && Native.GetForegroundWindow() == desktop.Handle,
                "Switching to an ordinary app reveals it and the taskbar without stealing focus.");
            left.ResizeTrace = [];
            right.ResizeTrace = [];
            var frames = new[] { left.Bounds, right.Bounds };
            var pinned = layout with { AlwaysOnTop = true };
            session.ApplyForTest(pinned, slots, games, background: true);
            check(games.All(GameViewStacking.IsTopmost) && Native.GetForegroundWindow() == desktop.Handle &&
                OwnerAt(Center(slots[1])) == right.Handle && OwnerAt(Center(overlay.Bounds)) == overlay.Handle,
                "Pinned games cover an ordinary app while the existing overlay stays above them.");
            check(left.Bounds == frames[0] && right.Bounds == frames[1] && left.ResizeTrace.Count == 0 && right.ResizeTrace.Count == 0,
                "Changing Always on Top preserves both game rectangles without sending resize notifications.");
            settings.Show();
            AcquireFixtureFocus(right);
            PumpUntil(() => OwnerAt(Center(settings.Bounds)) == settings.Handle && GameViewStacking.IsTopmost(settings.Handle),
                "Settings opened during play must stay clickable above the pinned game.");
            var swap = pinned with { SwapOnClick = true, MasterSlot = 1 };
            session.ApplyForTest(swap, slots, games);
            check(session.SurfaceHandles.All(GameViewStacking.IsTopmost) && !GameViewStacking.IsTopmost(left.Handle),
                "The native master and its visible preview stay pinned while the parked game returns to its original band.");
            session.ApplyForTest(layout, slots, games, background: true);
            check(games.All(h => !GameViewStacking.IsTopmost(h)) && !GameViewStacking.IsTopmost(settings.Handle) &&
                GameViewStacking.IsTopmost(overlay.Handle), "Turning off pinning restores each game and Team Bar control's original band.");
            GameViewStacking.SetBand(left.Handle, true);
            session.ApplyForTest(pinned, slots, games, background: true);
        }
        check(GameViewStacking.IsTopmost(left.Handle) && !GameViewStacking.IsTopmost(right.Handle) &&
            !GameViewStacking.IsTopmost(settings.Handle) && GameViewStacking.IsTopmost(overlay.Handle),
            "Ending a pinned layout preserves an independently topmost game and overlay and releases ordinary settings.");
    }

    private static void TestMultiScreen(Screen monitor, Rectangle area, Action<bool, string> check)
    {
        // Use another physical display when present. Two disjoint regions also
        // exercise the same cross-screen planner on a single-display test machine.
        var firstArea = new Rectangle(area.Left, area.Top, 350, 350);
        var other = Screen.AllScreens.FirstOrDefault(s => s.DeviceName != monitor.DeviceName);
        var secondArea = other is null ? new Rectangle(area.Left + 380, area.Top, 370, 350)
            : new Rectangle(other.WorkingArea.X + 48, other.WorkingArea.Y + 48, 370, 350);
        string[] characters = Enumerable.Range(1, 7).Select(n => $"Client {n}").ToArray();
        var layout = new GameViewLayout("Two screens", GameViewKind.Grid, "screen-a", false, characters)
        {
            Screens = [new("screen-a", GameViewKind.Grid, 4), new("screen-b", GameViewKind.Rows, 3)],
            MatchMasterResolution = false,
        };
        bool secondConnected = true;
        Rectangle[] Resolve(GameViewLayout current) => GameViewGeometry.Slots(current, name =>
        {
            if (name != "screen-a" && !secondConnected) return null;
            var bounds = name == "screen-a" ? firstArea : secondArea;
            return current.AvoidTaskbar ? bounds with { Height = bounds.Height - 24 } : bounds;
        });
        var slots = Resolve(layout);
        var games = characters.Select((_, i) => new Source(Color.DarkSlateBlue, new(area.Left + i * 4, area.Top + i * 3))).ToArray();
        try
        {
            foreach (var game in games) game.Open();
            AcquireFixtureFocus(games[0]);
            var handles = games.Select(game => game.Handle).ToArray();
            var originals = games.Select(game => game.Bounds).ToArray();
            using (var session = GameViewSession.StartForTest(layout, slots, handles, Resolve))
            {
                check(session.SurfaceHandles.SequenceEqual(handles) && games.Select(g => g.Bounds).SequenceEqual(slots),
                    "Seven games occupy native seats across two screens with independent grid and row arrangements.");
                const int master = 5;
                var swap = layout with { SwapOnClick = true, MatchMasterResolution = true, MasterSlot = master };
                var priorRaises = games.Select(g => g.StationaryRaises).ToArray();
                session.ApplyForTest(swap, slots, handles);
                check(Enumerable.Range(0, games.Length).All(i => i == master || games[i].StationaryRaises == priorRaises[i]),
                    "Applying a swap layout brings the occupied master forward without raising the parked games in turn.");
                check(session.SurfaceHandles[master] == handles[master] && games.All(g => g.Bounds == slots[master]),
                    "The chosen second-screen seat becomes the native master without using the gap between screens.");
                var moved = games.Select(g => g.PositionChanges).ToArray();
                PreviewSelection(session, 0);
                check(session.SourceHandles[master] == handles[0] && session.SourceHandles[0] == handles[master]
                    && session.SurfaceHandles[master] == handles[0],
                    $"Slot one promotes across screens. Actual master {Array.IndexOf(handles, session.SourceHandles[master]) + 1}, " +
                    $"foreground fixture {Array.IndexOf(handles, Native.GetForegroundWindow()) + 1}.");
                check(games.Select(g => g.PositionChanges).SequenceEqual(moved), "Promoting across screens keeps the parked windows in place.");
                check(session.TryGetVisibleClientRect(handles[master], out var anchor) && anchor == slots[0],
                    $"The exchanged preview anchors at {slots[0]}. Actual {anchor}.");

                var useWorkArea = swap with { AvoidTaskbar = true };
                session.ApplyForTest(useWorkArea, Resolve(useWorkArea), handles, background: true);
                check(session.SourceHandles[master] == handles[0] && games[0].Bounds == Resolve(useWorkArea)[master],
                    "Changing taskbar overlap refits the screens and preserves the character promoted to the starred slot.");
                session.ApplyForTest(swap, slots, handles, background: true);

                var revisedNames = characters.ToArray();
                var revisedHandles = handles.ToArray();
                revisedNames[master] = revisedNames[2];
                revisedNames[2] = "";
                revisedHandles[master] = handles[2];
                revisedHandles[2] = 0;
                session.ApplyForTest(swap with { Characters = revisedNames }, slots, revisedHandles, background: true);
                check(session.SourceHandles[master] == handles[2] && session.SourceHandles[2] == 0 &&
                    session.SourceHandles[0] == handles[0],
                    "Saving a seat assignment after a live swap puts the replacement in the chosen slot and empties its former slot.");
                session.ApplyForTest(swap, slots, handles, background: true);
                PreviewSelection(session, 0);

                var offline = handles.ToArray();
                offline[0] = 0;
                session.ApplyForTest(swap, slots, offline, background: true);
                check(session.SourceHandles[master] == 0 && session.SourceHandles[0] == handles[master]
                    && games[0].Bounds == originals[0] && games[master].Bounds == slots[0],
                    "The promoted character leaving vacates the master and keeps the remaining characters in their displayed seats.");
                PreviewSelection(session, 0);
                check(session.SurfaceHandles[master] == handles[master] && session.SourceHandles[0] == 0 &&
                    games[master].Bounds == slots[master], "A selector on another screen can promote directly into an empty master.");
                session.ApplyForTest(swap, slots, handles, background: true);
                check(session.SourceHandles.SequenceEqual(handles) && session.SurfaceHandles[master] == handles[master]
                    && session.TryGetVisibleClientRect(handles[0], out var returning) && returning == slots[0],
                    "A returning character fills its assigned seat without displacing the master.");
                session.ApplyForTest(layout, slots, handles);
                check(session.SurfaceHandles.SequenceEqual(handles) && games.Select(g => g.Bounds).SequenceEqual(slots),
                    "Switching back to fixed positions restores every native seat on both screens.");
                secondConnected = false;
                PumpUntil(() => !session.IsActive, "Losing a selected display should end the layout.");
                check(session.EndReason?.Contains("screen-b", StringComparison.OrdinalIgnoreCase) == true,
                    "Disconnecting a selected display ends the layout and names the missing monitor.");
            }
            check(games.Select(game => game.Bounds).SequenceEqual(originals), "Ending a seven-game layout restores every original window.");
        }
        finally { foreach (var game in games) game.Dispose(); }
        TestSlotResolutions(area, check);
        TestWindowedHudRoundTrips(area, check);
        TestEqFullscreenPreflight(area, check);
        TestSlowResolutionChange(area, check);
        TestNonEqBorderlessAtTarget(area, check);
    }

    private static void TestSlowResolutionChange(Rectangle area, Action<bool, string> check)
    {
        // A WndProc on our own UI thread bypasses SendMessageTimeout's deadline.
        // Run this source on its own STA to model EQ finishing its renderer reset.
        var ready = new TaskCompletionSource<(Source Game, nint Window)>();
        int completed = 0;
        var owner = new Thread(() =>
        {
            try
            {
                using var game = new Source(Color.SteelBlue, area.Location);
                game.Open();
                game.ResizeTrace = [];
                game.NextResizeEnd = () =>
                {
                    Thread.Sleep(1350);
                    Interlocked.Increment(ref completed);
                };
                ready.SetResult((game, game.Handle));
                Application.Run(game);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();
        var (source, window) = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var placements = new GameViewPlacements(false);
        try
        {
            var target = new Rectangle(area.Left + 15, area.Top + 12, 380, 220);
            var measured = placements.PrepareSources([(window, Environment.ProcessId)], ["Slow renderer"], [target]);
            int exits = (int)source.Invoke(() => source.ResizeTrace!.Count(s => s.Message == ExitSizeMove))!;
            check(measured[0] == target && Volatile.Read(ref completed) == 1 && exits == 1,
                "A game taking over one second to finish a real resolution change receives one end-resize notice and keeps its slot.");
        }
        finally
        {
            try { placements.Restore(); }
            finally
            {
                source.BeginInvoke(new Action(source.Close));
                if (!owner.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("The slow resize fixture could not close.");
            }
        }
    }

    private static void TestSlotResolutions(Rectangle area, Action<bool, string> check)
    {
        // Deliberately different sizes and aspect ratios. Slot 1 is the preview and slot 2 the master.
        Rectangle[] slots = [
            new(area.Left + 510, area.Top, 228, 150),
            new(area.Left, area.Top, 490, 360),
            new(area.Left + 510, area.Top + 170, 204, 184)
        ];
        var matching = new GameViewLayout("Resolution", GameViewKind.MainStrip, "fixture", true, ["One", "Two", "Three"])
            { MasterSlot = 1, AlwaysOnTop = true };
        var local = matching with { MatchMasterResolution = false };
        using var one = new Source(Color.SteelBlue, area.Location, framed: true) { TopMost = true };
        using var two = new Source(Color.Firebrick, new(area.Left + 40, area.Top + 20));
        using var three = new Source(Color.DarkGreen, new(area.Left + 80, area.Top + 40));
        Source[] games = [one, two, three];
        foreach (var game in games) game.Open();
        var handles = games.Select(g => g.Handle).ToArray();
        var originalBounds = games.Select(g => g.Bounds).ToArray();
        var originalClients = games.Select(g => g.ClientSize).ToArray();
        AcquireFixtureFocus(two);
        using (var session = GameViewSession.StartForTest(matching, slots, handles))
        {
            static Rectangle Client(Source game) => game.RectangleToScreen(game.ClientRectangle);
            bool VisibleAt(Source game, Rectangle expected) =>
                session.TryGetVisibleClientRect(game.Handle, out var picture) && picture == expected;

            var masterClient = Client(one);
            var masterFrame = one.VisibleFrame;
            var previewSlot = slots[0];
            double scaledLeft = (double)(masterClient.Left - masterFrame.Left) * previewSlot.Width / masterFrame.Width;
            double scaledRight = (double)(masterFrame.Right - masterClient.Right) * previewSlot.Width / masterFrame.Width;
            check(games.All(g => g.VisibleFrame == slots[1]) && session.TryGetVisibleClientRect(one.Handle, out var framedPreview) &&
                previewSlot.Contains(framedPreview) && framedPreview.Top > previewSlot.Top && VisibleAt(three, slots[2]) &&
                Math.Abs(framedPreview.Left - previewSlot.Left - scaledLeft) <= 1 &&
                Math.Abs(previewSlot.Right - framedPreview.Right - scaledRight) <= 1,
                "Matching resolution keeps all native clients at the starred slot's resolution and scales their preview pictures.");
            session.ApplyForTest(local, slots, handles, background: true);
            check(games.Select(g => g.VisibleFrame).SequenceEqual(slots) && games.All(g => VisibleAt(g, Client(g))),
                "Turning matching off resizes every game to its own slot, including both previews with different aspect ratios.");
            var surfaces = session.SurfaceHandles;
            check(surfaces[1] == two.Handle && Control.FromHandle(surfaces[0]) is GameViewTile &&
                OwnerAt(Center(slots[0])) == surfaces[0], "Clicking a locally sized preview reaches its swap selector.");
            session.ApplyForTest(local with { AlwaysOnTop = false }, slots, handles);
            GameViewStacking.SetBand(one.Handle, true); // EQ raises its originally topmost window without activating it.
            session.ApplyForTest(local with { AlwaysOnTop = false }, slots, handles, background: true);
            check(OwnerAt(Center(slots[0])) == surfaces[0],
                "An originally topmost game that raises itself stays behind its own unscaled swap selector.");
            session.ApplyForTest(local, slots, handles);
            foreach (var game in games) game.ResizeTrace = [];
            session.ApplyForTest(local, slots, handles);
            session.ApplyForTest(local, slots, handles, background: true);
            check(surfaces.SequenceEqual(session.SurfaceHandles) && games.All(g => g.ResizeTrace!.Count == 0),
                "Reapplying locally sized screens and refreshing the roster leaves their render sizes and preview handles alone.");
            one.Input.Clear();
            two.Input.Clear();
            PreviewSelection(session, 0);
            check(session.SourceHandles.SequenceEqual([two.Handle, one.Handle, three.Handle]) &&
                one.VisibleFrame == slots[1] && Client(two) == slots[0] && Client(three) == slots[2] &&
                session.SurfaceHandles[2] == surfaces[2] && three.ResizeTrace!.Count == 0,
                "Promoting a locally sized game enlarges its render area, shrinks the former master, and preserves the third game.");
            var masterLastRow = one.PointToScreen(new Point(one.ClientSize.Width / 2, one.ClientSize.Height - 1));
            check(OwnerAt(masterLastRow) == one.Handle && one.PointToClient(masterLastRow) == new Point(one.ClientSize.Width / 2, one.ClientSize.Height - 1) &&
                OwnerAt(Center(slots[0])) == session.SurfaceHandles[0] && VisibleAt(two, Client(two)) && one.Input.Count == 0 && two.Input.Count == 0,
                "After promotion, the large game's final row belongs to its native client and the small selector consumes the swap click.");
            check(Client(one).Top > one.Bounds.Top && Client(two) == two.Bounds &&
                games.All(g => VisibleAt(g, Client(g))), "Each game keeps its own frame and every unscaled overlay anchors to its actual client pixels.");
            session.ApplyForTest(matching, slots, handles);
            check(session.SourceHandles[1] == one.Handle && games.All(g => g.VisibleFrame == slots[1]) && VisibleAt(two, slots[0]),
                "Turning matching back on preserves the promoted character and restores master-sized preview sources.");
            var fixedLayout = local with { SwapOnClick = false };
            var occupiedSeats = session.SourceHandles;
            var oldSelectors = session.SurfaceHandles.Except(handles).ToArray();
            session.ApplyForTest(fixedLayout, slots, handles, background: true);
            AcquireFixtureFocus(two);
            check(session.SurfaceHandles.SequenceEqual(occupiedSeats) && oldSelectors.All(h => !IsWindow(h)) &&
                Client(two) == slots[0] && one.VisibleFrame == slots[1] && Client(three) == slots[2] &&
                OwnerAt(Center(slots[0])) == two.Handle, "Turning off swapping freezes promoted seats and removes the preview input surfaces.");
            session.ApplyForTest(fixedLayout with { MatchMasterResolution = true }, slots, handles);
            var fixedSurfaces = session.SurfaceHandles;
            check(session.IsActive && session.SourceHandles.SequenceEqual(occupiedSeats) && fixedSurfaces[0] != two.Handle &&
                fixedSurfaces[2] != three.Handle && two.Bounds == slots[1] && three.Bounds == slots[1],
                "Enabling matched resolution scales both small games, including the focused game.");
            three.Input.Clear();
            var selectedPoint = ClickFixedPreview(session, 2, Center(slots[2]));
            check(session.SourceHandles.SequenceEqual(occupiedSeats) && session.SurfaceHandles.SequenceEqual(fixedSurfaces) &&
                three.Bounds == slots[1] && two.Bounds == slots[1] && Native.GetForegroundWindow() == three.Handle &&
                three.Input.Count == 2 && three.Input.All(e => AtRequestedPixel(e.Point, selectedPoint)),
                "The first click into a different fixed preview reaches the selected game and preserves both scaled views.");
        }
        var fixedMatch = matching with { SwapOnClick = false };
        Rectangle[] equalSeats = Enumerable.Range(0, 3).Select(i => new Rectangle(area.X + i * 246, area.Y, 230, 165)).ToArray();
        nint[] vacantStar = [handles[0], 0, handles[2]];
        using (var fixedSession = GameViewSession.StartForTest(fixedMatch, equalSeats, vacantStar))
        {
            check(fixedSession.SurfaceHandles.SequenceEqual(vacantStar) && one.VisibleFrame == equalSeats[0] && three.Bounds == equalSeats[2],
                "Three equal fixed seats can use the saved master's resolution when its character is offline.");
            var largerStar = equalSeats.ToArray();
            largerStar[1].Height += 20;
            bool refused = false;
            try { fixedSession.ApplyForTest(fixedMatch, largerStar, vacantStar); }
            catch (FixedSlotResolutionException) { refused = true; }
            check(refused && fixedSession.IsActive && fixedSession.SourceHandles.SequenceEqual(vacantStar) &&
                one.VisibleFrame == equalSeats[0] && three.Bounds == equalSeats[2],
                "Enlarging a vacant master rejects unequal matching without moving either occupied fixed seat.");
        }
        check(games.Select(g => g.Bounds).SequenceEqual(originalBounds) && games.Select(g => g.ClientSize).SequenceEqual(originalClients),
            "Ending a layout after resizing, promotion and resolution toggles restores all original bounds and client resolutions.");
    }

    private static void TestFixedSplitEdges(Screen monitor, Rectangle area, Action<bool, string> check)
    {
        // The editor passes the entire monitor here, including the taskbar area.
        // Exercise both sides of the shared seam and detect any attempt to change the native frame.
        using var left = new Source(Color.Maroon, area.Location, framed: true);
        using var right = new Source(Color.Teal, new(area.Left + 40, area.Top + 20), framed: true);
        left.Open();
        right.Open();
        // Acquire focus before Apply arranges both fixtures in front of the desktop.
        AcquireFixtureFocus(left);
        Source[] games = [left, right];
        var handles = games.Select(game => game.Handle).ToArray();
        var fullSlots = GameViewGeometry.Slots(GameViewKind.EqualSplit, monitor.Bounds, 2);
        var fixedLayout = new GameViewLayout("Painted split", GameViewKind.EqualSplit, monitor.DeviceName, false, ["One", "Two"])
            { MatchMasterResolution = false };
        static (Rectangle Frame, Rectangle Client, nint Style, nint ExtendedStyle, (int, Rectangle) Region) Snapshot(Source game) =>
            (game.Bounds, game.RectangleToScreen(game.ClientRectangle), GetWindowLongPtrW(game.Handle, -16),
                GetWindowLongPtrW(game.Handle, -20), WindowShape(game.Handle));
        var original = games.Select(Snapshot).ToArray();
        right.TrackFrameChanges = true;
        using (var session = GameViewSession.StartForTest(fixedLayout, monitor.Bounds, handles))
        {
            var shown = games.Select(Snapshot).ToArray();
            for (int i = 0; i < games.Length; i++)
            {
                var frame = shown[i];
                check(games[i].VisibleFrame == fullSlots[i] && frame.Client.Top > fullSlots[i].Top && fullSlots[i].Contains(frame.Client)
                    && (frame.Style.ToInt64() & 0x00C00000) == 0x00C00000 &&
                    session.TryGetVisibleClientRect(handles[i], out var anchor) && anchor == frame.Client,
                    $"Fixed slot {i + 1} contains its title bar and anchors to the measured game picture.");
            }
            check(left.VisibleFrame.Left == monitor.Bounds.Left && right.VisibleFrame.Right == monitor.Bounds.Right &&
                left.VisibleFrame.Right == right.VisibleFrame.Left &&
                left.VisibleFrame.Bottom == monitor.Bounds.Bottom && right.VisibleFrame.Bottom == monitor.Bounds.Bottom,
                "The two painted frames meet at the split and extend to the monitor edges when covering the taskbar.");
            check(right.FrameRemovalAttempts == 0 && session.SurfaceHandles.SequenceEqual(handles),
                "A fixed split keeps both captions visible and accepts input on the native game windows.");
            foreach (var game in games.Reverse())
            {
                int x = game.ClientSize.Width / 2;
                int clearY = UncoveredCoordinate(game.ClientSize.Height, y => game.PointToScreen(new Point(x, y)));
                NativeGesture(game, new Point(x, clearY));
                var elapsed = Stopwatch.StartNew();
                PumpUntil(() => !session.IsActive || elapsed.ElapsedMilliseconds >= 850, "The fixed split did not settle after changing focus.");
                var measured = games.Select(Snapshot).ToArray();
                check(session.IsActive && Native.GetForegroundWindow() == game.Handle && session.SourceHandles.SequenceEqual(handles)
                    && games.Select(g => g.VisibleFrame).SequenceEqual(fullSlots) && measured.SequenceEqual(shown),
                    $"Clicking {game.BackColor.Name} keeps both native halves and their window shapes fixed.");
            }
            int edgeY = UncoveredCoordinate(right.ClientSize.Height, y => right.PointToScreen(new Point(0, y)),
                y => right.PointToScreen(new Point(right.ClientSize.Width - 1, y)));
            var a = new Point(0, edgeY);
            var b = new Point(right.ClientSize.Width - 1, a.Y);
            left.Input.Clear();
            right.Input.Clear();
            NativeGesture(right, a);
            NativeGesture(right, b);
            check(right.Input.SequenceEqual([(LeftDown, a), (LeftUp, a), (LeftDown, b), (LeftUp, b)]) && left.Input.Count == 0,
                $"The first and last playable columns belong to the intended game. {right.InputReport}");
            int lastColumn = left.ClientSize.Width - 1;
            int leftY = UncoveredCoordinate(left.ClientSize.Height, y => left.PointToScreen(new Point(lastColumn, y)));
            var leftEdge = new Point(lastColumn, leftY);
            left.Input.Clear();
            right.Input.Clear();
            NativeGesture(left, leftEdge);
            check(left.Input.SequenceEqual([(LeftDown, leftEdge), (LeftUp, leftEdge)]) && right.Input.Count == 0,
                $"The last playable column of the left game accepts its click beside a focused right game. {left.InputReport}");

            var fixedTrace = left.ResizeTrace = [];
            session.ApplyForTest(fixedLayout with { AlwaysOnTop = true }, monitor.Bounds, handles);
            session.ApplyForTest(fixedLayout, monitor.Bounds, handles);
            var priorityFrames = games.Select(Snapshot).ToArray();
            check(fixedTrace.Count == 0 && games.Select(g => g.VisibleFrame).SequenceEqual(fullSlots) &&
                priorityFrames.SequenceEqual(shown) &&
                OwnerAt(left.PointToScreen(leftEdge)) == left.Handle && OwnerAt(right.PointToScreen(a)) == right.Handle,
                "Changing window priority preserves both pictures, hit targets and frame geometry without resizing.");
            left.ResizeTrace = null;
            // Both windows are moving to different sizes. Only the first accepts
            // its new slot, so it must roll back without losing the running split.
            right.RefuseResize = true;
            bool refused = false;
            try { session.ApplyForTest(fixedLayout with { Kind = GameViewKind.Rows }, monitor.Bounds, handles); }
            catch (InvalidOperationException) { refused = true; }
            finally { right.RefuseResize = false; }
            check(refused && session.IsActive && session.SourceHandles.SequenceEqual(handles) &&
                games.Select(g => g.VisibleFrame).SequenceEqual(fullSlots), "A failed arrangement change restores the active split for both games.");
        }
        check(games.Select(Snapshot).SequenceEqual(original),
            "Ending after a failed transition restores the original game windows, frames and shapes.");
        using (var moved = GameViewSession.StartForTest(fixedLayout, monitor.Bounds, handles))
        {
            var userPosition = new Point(right.Left + 24, right.Top + 24);
            right.Location = userPosition;
            PumpUntil(() => !moved.IsActive, "An external move at the seam must release the layout.");
            check(right.Location == userPosition && WindowShape(right.Handle) == original[1].Region &&
                Snapshot(left) == original[0],
                "Moving one game preserves its new position and releases any layout-owned shape on both windows.");
        }
    }

    private static void TestDroppedEqSeamClip(nint game, Source neighbor, Rectangle area, Action<bool, string> check, bool hideTitleBars = false)
    {
        // Both HWNDs belong to this fixture. The native EQ class accepts the first
        // SetWindowRgn; explicitly dropping its shape reproduces EQ clearing it later.
        var originalFrame = GameViewPlacements.Bounds(game);
        var originalClient = GameViewPlacements.ClientBounds(game, Environment.ProcessId);
        var originalShape = WindowShape(game);
        bool originalRendering = ThemedFrame(game);
        var neighborFrame = neighbor.Bounds;
        var left = area with { Width = 230, Height = 180 };
        Rectangle[] slots = [left, hideTitleBars ? left with { Y = left.Bottom } : left with { X = left.Right }];
        nint[] handles = [neighbor.Handle, game];
        var fixedLayout = new GameViewLayout("EQ seam fallback", GameViewKind.EqualSplit, "fixture", false, ["Neighbor", "EQ fixture"])
            { MatchMasterResolution = true, AlwaysOnTop = true, HideTitleBars = hideTitleBars };
        using var session = GameViewSession.StartForTest(fixedLayout, slots, handles);
        var shape = WindowShape(game);
        check(originalRendering && originalShape.Kind == 0 && shape.Kind == 2 && VisibleFrame(game) == slots[1],
            "The framed EQ fixture starts with a seam clip and its painted edges fill its assigned slot.");

        void DropRegion()
        {
            if (SetWindowRgn(game, 0, true) == 0) throw new InvalidOperationException("Could not release the fixture's seam clip.");
            GameViewWindows.FlushComposition();
        }
        DropRegion();
        PumpUntil(() => !session.IsActive || WindowShape(game) == shape, "The layout monitor must recover a dropped EQ seam clip.");
        if (hideTitleBars)
        {
            check(session.IsActive && WindowShape(game) == shape &&
                GameViewPlacements.ClientBounds(game, Environment.ProcessId) == slots[1],
                "The first client-only clip loss recovers the exact game picture without resizing it.");
            DropRegion();
            check(ThemedFrame(game) == originalRendering, "The first client-only retry preserved EQ's original DWM policy.");
            PumpUntil(() => !session.IsActive || WindowShape(game) == shape,
                "After a second client-only clip loss the ordinary frame-policy fallback must recover the seam.");
        }
        check(session.IsActive && VisibleFrame(game) == slots[1] && WindowShape(game) == shape && !ThemedFrame(game) &&
            session.TryGetVisibleClientRect(game, out var anchor) && anchor == GameViewPlacements.ClientBounds(game, Environment.ProcessId),
            $"The EQ fallback restores the seam clip and preserves the playable client and overlay anchor. {session.EndReason}");
        var rightClient = GameViewPlacements.ClientBounds(game, Environment.ProcessId);
        var leftClient = neighbor.RectangleToScreen(neighbor.ClientRectangle);
        Point gameEdge, neighborEdge;
        if (hideTitleBars)
        {
            check(rightClient == slots[1] && leftClient == slots[0],
                "Recovering a lost EQ clip keeps the title bar hidden and the two clients touching at the shared row.");
            int x = rightClient.Left + UncoveredCoordinate(rightClient.Width, col => new Point(rightClient.Left + col, rightClient.Top),
                col => new Point(rightClient.Left + col, leftClient.Bottom - 1));
            gameEdge = new(x, rightClient.Top);
            neighborEdge = new(x, leftClient.Bottom - 1);
        }
        else
        {
            int y = rightClient.Top + UncoveredCoordinate(rightClient.Height, row => new Point(rightClient.Left, rightClient.Top + row),
                row => new Point(leftClient.Right - 1, rightClient.Top + row));
            gameEdge = new(rightClient.Left, y);
            neighborEdge = new(leftClient.Right - 1, y);
        }
        foreach (nint front in handles)
        {
            const uint keepGeometryAndFocus = 0x0001 | 0x0002 | 0x0010 | 0x0200;
            check(Native.SetWindowPos(front, -1, 0, 0, 0, 0, keepGeometryAndFocus) &&
                OwnerAt(gameEdge) == game && OwnerAt(neighborEdge) == neighbor.Handle,
                $"Both playable pixels at the recovered seam hit the correct fixture with {(front == game ? "EQ" : "its neighbor")} in front.");
        }
        var trace = new NativeGameSizeTrace();
        trace.AssignHandle(game);
        try
        {
            session.ApplyForTest(fixedLayout, slots, handles);
            session.ApplyForTest(fixedLayout, slots, handles, background: true);
            check(trace.Sizes.Count == 0 && trace.MaximizeCommands == 0 && session.IsActive &&
                WindowShape(game) == shape && VisibleFrame(game) == slots[1], "Reapplying the recovered slot leaves EQ's renderer and seam alone.");
        }
        finally { trace.ReleaseHandle(); }

        // A native region can itself turn off DWM's NC rendering. Checking again
        // after removing it proves the fallback owns a per-window rendering policy.
        DropRegion();
        check(!ThemedFrame(game), "EQ's fallback frame policy stays in effect when its region is dropped again.");
        PumpUntil(() => !session.IsActive, "Losing the clip after the frame-policy fallback must release the layout.");
        check(ThemedFrame(game) == originalRendering && WindowShape(game) == originalShape &&
            GameViewPlacements.Bounds(game) == originalFrame && GameViewPlacements.ClientBounds(game, Environment.ProcessId) == originalClient &&
            neighbor.Bounds == neighborFrame, "Releasing the failed seam restores each game, its native frame rendering and the original region.");
    }

    private static void TestWindowedHudRoundTrips(Rectangle area, Action<bool, string> check)
    {
        TestWindowedHudRoundTrips(new(area.Left, area.Top, 520, 324), new(area.Right - 210, area.Top, 210, 150), false, check);
        var screens = Screen.AllScreens.OrderByDescending(s => (long)s.Bounds.Width * s.Bounds.Height).ToArray();
        if (screens.Length < 2) return;
        var secondary = screens[1].Bounds;
        TestWindowedHudRoundTrips(screens[0].Bounds, secondary with { Width = secondary.Width / 2 }, true, check);
    }

    private static void TestWindowedHudRoundTrips(Rectangle large, Rectangle small, bool hideTitleBars, Action<bool, string> check)
    {
        const string gameClass = "_EverQuestwndclass", character = "HUD fixture";
        var module = Native.GetModuleHandleW(null);
        RegisterFixtureClass(gameClass, module);
        nint game = 0;
        try
        {
            game = CreateWindowExW(0x80, gameClass, character, 0x14CF0000u,
                large.Left, large.Top, 320, 204, 0, 0, module, 0);
            if (game == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var original = GameViewPlacements.Bounds(game);
            var trace = new NativeGameSizeTrace();
            trace.AssignHandle(game);
            try
            {
                var placements = new GameViewPlacements(false);
                try
                {
                    var sources = new[] { (game, Environment.ProcessId) };
                    var main = placements.PrepareSources(sources, [character], [large], maximizedSource: game, hideTitleBars: hideTitleBars).Single();
                    check(IsZoomed(game) && (hideTitleBars ? main == large : VisibleFrame(game) == large) && placements.RestoredProfileFallbacks.Length == 0,
                        $"The decorated fixture entered the maximized HUD at its {main.Width} × {main.Height} client size.");
                    Point marker = new(main.Width - 12, main.Height - 12);
                    trace.MaximizedHudMarker = marker;
                    trace.MaximizedChats = new(main.Width / 3, main.Width * 3 / 8);
                    trace.RestoredChats = new(main.Width / 3, main.Width * 3 / 8);
                    var mainChats = trace.MaximizedChats.BoundsAt(main.Size);
                    for (int swap = 1; swap <= 3; swap++)
                    {
                        int restores = trace.RestoreCommands, maximizes = trace.MaximizeCommands;
                        trace.Sizes.Clear();
                        trace.MaximizedSizes.Clear();
                        var client = placements.PrepareSources(sources, [character], [small], hideTitleBars: hideTitleBars).Single();
                        check(!IsZoomed(game) && (hideTitleBars ? client == small : VisibleFrame(game) == small) && trace.RestoreCommands == restores + 1 &&
                            trace.MaximizeCommands == maximizes && trace.Sizes.Any(s => s.Decorated && s.Size == client.Size) &&
                            trace.MaximizedSizes.All(s => s.Width >= main.Width && s.Height >= main.Height),
                            $"Demotion {swap} restores once to the small client without resizing or reopening the large HUD at a smaller size. " +
                            $"Restore/maximize commands {trace.RestoreCommands - restores}/{trace.MaximizeCommands - maximizes}, " +
                            $"maximized sizes [{string.Join(", ", trace.MaximizedSizes)}], restored sizes [{string.Join(", ", trace.Sizes.Select(s => s.Size))}].");
                        var smallChats = trace.RestoredChats.BoundsAt(client.Size);
                        check(smallChats.Left.Width < mainChats.Left.Width && smallChats.Right.Width < mainChats.Right.Width,
                            $"The restored fixture HUD fits both chat windows inside the small {client.Width} × {client.Height} client.");
                        trace.MaximizedSizes.Clear();
                        var back = placements.PrepareSources(sources, [character], [large], maximizedSource: game, hideTitleBars: hideTitleBars).Single();
                        var backChats = trace.MaximizedChats.BoundsAt(back.Size);
                        check(IsZoomed(game) && back == main && trace.MaximizeCommands == maximizes + 1 && backChats == mainChats,
                            $"Promotion {swap} restores both bottom-center fixture chat windows. Expected {mainChats}, observed {backChats}.");
                        // The OS can first maximize to its normal work area. Its temporary height
                        // matters to bottom-edge controls independently of the two chat widths.
                        check(hideTitleBars ? trace.MaximizedHudMarker?.X == marker.X : trace.MaximizedHudMarker == marker,
                            $"Promotion {swap}: large client {main.Size}, maximized sizes [{string.Join(", ", trace.MaximizedSizes)}], " +
                            $"near-edge fixture marker {marker} became {trace.MaximizedHudMarker}.");
                    }
                }
                finally { placements.Restore(); }
                check(!IsZoomed(game) && GameViewPlacements.Bounds(game) == original,
                    "Ending the layout after repeated HUD swaps restores the original window and mode.");
            }
            finally { trace.ReleaseHandle(); }
        }
        finally
        {
            if (game != 0) DestroyWindow(game);
            UnregisterClassW(gameClass, module);
        }
    }

    private static void TestEqFullscreenPreflight(Rectangle area, Action<bool, string> check, bool seamsOnly = false)
    {
        // Register the same class in this fixture process only. No production game window is used.
        const string gameClass = "_EverQuestwndclass", character = "Fullscreen fixture";
        const int gameStyle = 0x14000000; // WS_VISIBLE | WS_CLIPSIBLINGS
        var monitor = Screen.FromRectangle(area).Bounds;
        var module = Native.GetModuleHandleW(null);
        RegisterFixtureClass(gameClass, module);
        nint game = 0;
        try
        {
            game = CreateWindowExW(0x80, gameClass, character, 0x80000000u, monitor.X, monitor.Y, monitor.Width, monitor.Height,
                0, 0, module, 0); // Create a hidden tool window, then match the live style and frame exactly.
            if (game == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SetWindowLongPtrW(game, -16, gameStyle);
            if (!Native.SetWindowPos(game, 1, monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0070)) // bottom, show, frame changed, no activation
                throw new InvalidOperationException("Windows could not place the fullscreen fixture.");
            GameViewWindows.FlushComposition();
            var placements = new GameViewPlacements(false);
            using var process = Process.GetCurrentProcess();
            using var regular = new Source(Color.OliveDrab, area.Location, framed: true);
            regular.Open();
            var original = regular.Bounds;
            var small = new Rectangle(area.X + 440, area.Y, 220, 170);
            var large = new Rectangle(area.X, area.Y, 430, 320);
            void RunSeamTests()
            {
                SetWindowLongPtrW(game, -16, gameStyle | 0x00CF0000); // WS_OVERLAPPEDWINDOW
                check(Native.SetWindowPos(game, 0, large.X, large.Y, large.Width, large.Height, 0x0034),
                    "The same EQ window can switch to a native captioned frame without changing its handle.");
                GameViewWindows.FlushComposition();
                TestDroppedEqSeamClip(game, regular, large, check);
                TestDroppedEqSeamClip(game, regular, large, check, hideTitleBars: true);
            }
            if (seamsOnly) { RunSeamTests(); return; }
            var matching = new GameViewLayout("Fullscreen preflight", GameViewKind.MainStrip, "fixture", true, ["Windowed fixture", character])
                { MasterSlot = 0 };
            var local = matching with { MatchMasterResolution = false };
            nint[] handles = [regular.Handle, game];

            bool NeedsWindowed(Action apply, params string[] affected)
            {
                try { apply(); return false; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Windowed") &&
                    affected.All(name => ex.Message.Contains(name, StringComparison.Ordinal)) &&
                    !ex.Message.Contains("Windowed fixture")) { return true; }
            }
            Rectangle Frame() => GameViewPlacements.Bounds(game);
            var half = monitor with { Width = monitor.Width / 2 };
            for (int flags = 0; flags < 32; flags++)
            {
                var settings = matching with {
                    SwapOnClick = (flags & 1) != 0, MatchMasterResolution = (flags & 2) != 0,
                    AvoidTaskbar = (flags & 4) != 0, AlwaysOnTop = (flags & 8) != 0, HideTitleBars = (flags & 16) != 0
                };
                var fullSlot = settings.AvoidTaskbar ? monitor with { Height = monitor.Height - 40 } : monitor;
                foreach (int master in new[] { 0, 1 })
                {
                    Rectangle[] slots = master == 1 ? [half, fullSlot] : [fullSlot, half];
                    check(NeedsWindowed(() =>
                        { using var invalid = GameViewSession.StartForTest(settings with { MasterSlot = master }, slots, handles); }, character) &&
                        Frame() == monitor && regular.Bounds == original && !GameViewStacking.IsTopmost(game),
                        $"Fullscreen EQ in {(master == 1 ? "the master" : "a secondary slot")} is declined before moving either window " +
                        $"(swap={settings.SwapOnClick}, match={settings.MatchMasterResolution}, avoid taskbar={settings.AvoidTaskbar}, on top={settings.AlwaysOnTop}).");
                }
            }
            check(NeedsWindowed(() =>
                { using var invalid = GameViewSession.StartForTest(matching, [monitor, half], [0, game]); }, character),
                "A vacant master cannot exempt the fullscreen game assigned to the other slot.");
            SetWindowLongPtrW(game, -16, unchecked((int)0x94000000)); // Visible popup without a native caption.
            check(NeedsWindowed(() =>
                { using var invalid = GameViewSession.StartForTest(local, [monitor, half], handles); }, character),
                "An EQ popup with a different fullscreen style still requires Windowed mode.");
            SetWindowLongPtrW(game, -16, gameStyle);

            const string secondCharacter = "Second fullscreen fixture";
            nint secondGame = CreateWindowExW(0x80, gameClass, secondCharacter, 0x80000000u,
                monitor.X, monitor.Y, monitor.Width, monitor.Height, 0, 0, module, 0);
            if (secondGame == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                SetWindowLongPtrW(secondGame, -16, gameStyle);
                var mixed = matching with { Characters = ["Windowed fixture", character, secondCharacter] };
                check(NeedsWindowed(() =>
                    { using var invalid = GameViewSession.StartForTest(mixed, [monitor, small, half], [regular.Handle, game, secondGame]); },
                    character, secondCharacter) && Frame() == monitor && regular.Bounds == original,
                    "A mixed three-character layout identifies every fullscreen client and leaves the Windowed client in place.");
            }
            finally { DestroyWindow(secondGame); }

            AcquireFixtureFocus(regular);
            nint[] waiting = [regular.Handle, 0];
            using (var session = GameViewSession.StartForTest(matching, [large, small], waiting))
            {
                var surfaces = session.SurfaceHandles;
                var placed = regular.Bounds;
                check(NeedsWindowed(() => session.ApplyForTest(matching, [large, small], handles, background: true), character) &&
                    session.IsActive && session.SourceHandles.SequenceEqual(waiting) && session.SurfaceHandles.SequenceEqual(surfaces) &&
                    regular.Bounds == placed && Frame() == monitor,
                    "A fullscreen arrival is declined during background refresh while the existing Windowed layout stays usable.");
            }
            check(regular.Bounds == original, "Ending the active Windowed layout restores the original fixture placement.");
            RunSeamTests();
            var windowed = new GameViewLayout("Windowed", GameViewKind.EqualSplit, "fixture", false, [character])
                { MatchMasterResolution = false };
            using (var session = GameViewSession.StartForTest(windowed, [small], [game]))
            {
                check(session.IsActive && VisibleFrame(game) == small, "Applying a smaller slot succeeds after EQ switches to a regular window.");
                var client = GameViewPlacements.ClientBounds(game, process.Id);
                var work = small with { Height = small.Height - 32 };
                var avoidTaskbar = windowed with { AvoidTaskbar = true };
                session.ApplyForTest(avoidTaskbar, [work], [game], background: true);
                check(VisibleFrame(game) == work && GameViewPlacements.ClientBounds(game, process.Id) == client with { Height = client.Height - 32 } &&
                    (GetWindowLongPtrW(game, -16).ToInt64() & 0x00C00000) == 0x00C00000,
                    "Reserving a 32-pixel taskbar in Windowed mode gives up exactly those 32 pixels and retains EQ's frame.");
                session.ApplyForTest(avoidTaskbar with { AlwaysOnTop = true }, [work], [game], background: true);
                check(VisibleFrame(game) == work && GameViewStacking.IsTopmost(game), "Always on Top preserves the working-area slot.");
                session.ApplyForTest(windowed, [small], [game], background: true);
                check(VisibleFrame(game) == small && GameViewPlacements.ClientBounds(game, process.Id) == client &&
                    !GameViewStacking.IsTopmost(game), "Re-enabling taskbar overlap restores the exact windowed render area.");
            }

            SendMessageW(game, SystemCommand, MaximizeCommand, 0);
            try
            {
                check(IsZoomed(game), "The EQ fixture is maximized with its native Windowed caption.");
                using var session = GameViewSession.StartForTest(windowed, [small], [game]);
                check(session.IsActive && VisibleFrame(game) == small,
                    "A maximized Windowed EQ client is eligible for layout positioning.");
            }
            finally { SendMessageW(game, SystemCommand, RestoreCommand, 0); }

            var beforeProfile = Frame();
            try
            {
                var work = Screen.FromRectangle(monitor).WorkingArea;
                foreach (var target in new[] { monitor, work, small })
                {
                    var picture = placements.PrepareSources([(game, process.Id)], [character], [target], maximizedSource: game).Single();
                    check(IsZoomed(game) && VisibleFrame(game) == target && target.Contains(picture) && placements.RestoredProfileFallbacks.Length == 0,
                        $"The maximized EQ interface keeps its exact {target.Width} x {target.Height} slot, including its taskbar policy.");
                }
                placements.PrepareSources([(game, process.Id)], [character], [small]);
                check(!IsZoomed(game) && VisibleFrame(game) == small, "A focus change can select the restored EQ interface without moving its slot.");
                placements.PrepareSources([(game, process.Id)], [character], [small], maximizedSource: game);
                check(IsZoomed(game) && VisibleFrame(game) == small, "Focusing the same fixed-size slot restores its maximized EQ interface.");
                var demoted = small with { Width = small.Width - 16 };
                var trace = new NativeGameSizeTrace();
                trace.AssignHandle(game);
                try
                {
                    placements.PrepareSources([(game, process.Id)], [character], [demoted]);
                    check(!IsZoomed(game) && VisibleFrame(game) == demoted && trace.RestoreCommands == 1 && trace.MaximizeCommands == 0,
                        "Demoting into a different-sized slot uses one real restore without re-entering the maximized HUD.");
                }
                finally { trace.ReleaseHandle(); }
            }
            finally { placements.Restore(); }
            check(!IsZoomed(game) && Frame() == beforeProfile, "Ending the layout restores the original desktop window mode and placement.");

            // The native caption belongs inside the lower slot even when another view touches its top edge.
            var upper = new Rectangle(area.X, area.Y, 230, 165);
            var lower = new Rectangle(area.X, upper.Bottom, 420, 200);
            Rectangle[] stacked = [upper, lower];
            var matchBelow = matching with { MasterSlot = 1 };
            var windowTrace = new NativeGameSizeTrace();
            windowTrace.AssignHandle(game);
            try
            {
                using var session = GameViewSession.StartForTest(matchBelow, stacked, handles);
                check(VisibleFrame(game) == lower && (GetWindowLongPtrW(game, -16).ToInt64() & 0x00C00000) == 0x00C00000 &&
                    GameViewPlacements.ClientBounds(game, process.Id).Top > lower.Top,
                    "A matched layout leaves the native caption inside the lower slot.");
                session.ApplyForTest(matchBelow with { MatchMasterResolution = false }, stacked, handles);
                windowTrace.Sizes.Clear();
                PreviewSelection(session, 0);
                var client = GameViewPlacements.ClientBounds(game, process.Id);
                check(VisibleFrame(game) == upper && upper.Contains(client) && client.Top > upper.Top && session.SourceHandles[0] == game &&
                    windowTrace.Sizes.Any(s => s.Size == client.Size && s.Decorated) &&
                    windowTrace.Sizes.All(s => s.Decorated) &&
                    session.TryGetVisibleClientRect(game, out var anchor) && anchor == client,
                    "A demoted windowed EQ client resizes inside its original frame and its overlay follows the measured client area.");
            }
            finally { windowTrace.ReleaseHandle(); }

            // EQ can change its display mode without recreating its top-level
            // HWND or changing the outer bounds that Team Bar just assigned.
            // WindowFromPoint and GetClientRect can then both report the half
            // slot while EQ renders and maps its UI at the full monitor size.
            void SetFixtureMode(bool fullscreen, Rectangle frame)
            {
                SetWindowLongPtrW(game, -16, fullscreen ? gameStyle : gameStyle | 0x00CF0000);
                if (!Native.SetWindowPos(game, 0, frame.X, frame.Y, frame.Width, frame.Height, 0x0034))
                    throw new InvalidOperationException("Could not change the fixture's simulated EQ display mode.");
                GameViewWindows.FlushComposition();
            }
            SetFixtureMode(false, large);
            using (var session = GameViewSession.StartForTest(windowed, [half], [game]))
            {
                check(VisibleFrame(game) == half && session.SurfaceHandles.Single() == game,
                    "The half-monitor regression starts with the real framed game as its play surface.");
                EnableWindow(game, false);
                try
                {
                    session.ApplyForTest(windowed, [half], [0], background: true);
                    var wait = Stopwatch.StartNew();
                    PumpUntil(() => wait.ElapsedMilliseconds > 550, "Wait for the layout monitor to check the disabled fixture.");
                    check(session.WaitingForGameDialog && session.IsActive && session.SourceHandles.Single() == game && VisibleFrame(game) == half,
                        "Opening an EQ dialog preserves its playable windowed seat through a monitor tick and a roster refresh.");
                }
                finally { EnableWindow(game, true); }
                session.ApplyForTest(windowed, [half], [game], background: true);
                check(!session.WaitingForGameDialog && session.IsActive && session.SourceHandles.Single() == game &&
                    VisibleFrame(game) == half, "Closing the EQ dialog resumes the layout in the same native slot.");
                SetFixtureMode(true, half);
                check(Frame() == half && GameViewPlacements.ClientBounds(game, process.Id) == half,
                    "Changing to fullscreen can leave the native client and its outer rectangle exactly half a monitor.");
                PumpUntil(() => !session.IsActive, "The layout must stop when a playable half switches to fullscreen in place.");
                check(session.EndReason?.Contains(character) == true && session.EndReason.Contains("Windowed") && Frame() == half,
                    "A change to fullscreen on the same HWND identifies the affected character and preserves its new frame.");
            }
            check(Frame() == half && GameViewPlacements.ClientBounds(game, process.Id) == half,
                "Releasing a previously Windowed game leaves EQ's chosen fullscreen geometry intact.");
            SetFixtureMode(true, half);
            check(NeedsWindowed(() => { using var session = GameViewSession.StartForTest(windowed, [half], [game]); }, character) &&
                Frame() == half && GameViewPlacements.ClientBounds(game, process.Id) == half,
                "A fresh Apply leaves an already squeezed fullscreen window untouched when Team Bar does not own its placement.");
            foreach (int master in new[] { 0, 1 })
            {
                SetFixtureMode(false, large);
                Rectangle[] slots = master == 1 ? [half, monitor] : [monitor, half];
                using var session = GameViewSession.StartForTest(matching with { MasterSlot = master }, slots, handles);
                SetFixtureMode(true, Frame());
                PumpUntil(() => !session.IsActive, "A master or preview changing to fullscreen must end the layout.");
                check(session.EndReason?.Contains(character) == true && session.EndReason.Contains("Windowed") &&
                    regular.Bounds == original, "A fullscreen change releases both windows even when the master resolution still fits.");
            }

            SetFixtureMode(false, large);
            using (var session = GameViewSession.StartForTest(windowed, [monitor], [game]))
            {
                session.ApplyForTest(windowed, [half], [game], background: true);
                check(session.IsActive && VisibleFrame(game) == half &&
                    (GetWindowLongPtrW(game, -16).ToInt64() & 0x00C00000) == 0x00C00000,
                    "Reapplying the layout succeeds after the same EQ client returns to Windowed mode.");
            }
            check(Frame() == large && (GetWindowLongPtrW(game, -16).ToInt64() & 0x00C00000) == 0x00C00000,
                "Ending the layout restores the player's newly selected Windowed position and size.");
        }
        finally
        {
            if (game != 0) DestroyWindow(game);
            UnregisterClassW(gameClass, module);
        }
    }

    private static void TestNonEqBorderlessAtTarget(Rectangle area, Action<bool, string> check)
    {
        var slot = new Rectangle(area.Left + 16, area.Top + 24, 372, 214);
        using var game = new Source(Color.SeaGreen, slot.Location);
        game.Open();
        nint window = game.Handle;
        // EQ's native borderless mode uses WS_VISIBLE | WS_CLIPSIBLINGS, without WS_POPUP.
        const int nativeStyle = 0x14000000;
        const uint frameEdges = 0x00020301;
        var extended = (nint)(nuint)((uint)GetWindowLongPtrW(window, -20).ToInt64() & ~frameEdges);
        SetWindowLongPtrW(window, -16, nativeStyle);
        SetWindowLongPtrW(window, -20, extended);
        const uint frameChangedWithoutActivationOrOrder = 0x0034;
        var monitor = Screen.FromRectangle(area).Bounds;
        check(Native.SetWindowPos(window, 0, monitor.X, monitor.Y, monitor.Width, monitor.Height, frameChangedWithoutActivationOrOrder) &&
            GameViewPlacements.ClientBounds(window, Environment.ProcessId) == monitor,
            "An ordinary borderless window with EQ's style and fullscreen dimensions still supports slot resizing.");
        bool placed = Native.SetWindowPos(window, 0, slot.X, slot.Y, slot.Width, slot.Height, frameChangedWithoutActivationOrOrder);
        GameViewWindows.FlushComposition();
        var rendering = ThemedFrame(window);
        int moves = game.PositionChanges;
        game.ResizeTrace = [];

        void CheckNativeWindow(string stage)
        {
            var bounds = GameViewPlacements.Bounds(window);
            var client = GameViewPlacements.ClientBounds(window, Environment.ProcessId);
            var style = GetWindowLongPtrW(window, -16);
            var actualExtended = GetWindowLongPtrW(window, -20);
            var shape = WindowShape(window);
            var upper = game.PointToClient(new Point(slot.Left + slot.Width / 2, slot.Top));
            var lower = game.PointToClient(new Point(slot.Left + slot.Width / 2, slot.Bottom - 1));
            check(game.Handle == window && bounds == slot && client == slot && style == nativeStyle && actualExtended == extended &&
                ThemedFrame(window) == rendering && shape.Kind == 0 && moves == game.PositionChanges && game.ResizeTrace.Count == 0 &&
                upper == new Point(slot.Width / 2, 0) && lower == new Point(slot.Width / 2, slot.Height - 1),
                $"{stage} preserves the non-EQ fixture's borderless style and mouse coordinates without moving or resizing it. " +
                $"Expected {slot}, actual frame {bounds}, client {client}, style {style:X}/{actualExtended:X}, region {shape}, " +
                $"upper {upper}, lower {lower}, moves {moves} -> {game.PositionChanges}, " +
                $"messages {string.Join(", ", game.ResizeTrace.Select(e => e.Message.ToString("X")))}.");
        }

        check(placed, "Windows placed the native borderless fixture at its requested slot.");
        CheckNativeWindow("Fixture setup");
        var layout = new GameViewLayout("Native borderless", GameViewKind.EqualSplit, "fixture", false, ["Native"])
            { AvoidTaskbar = true };
        using (var session = GameViewSession.StartForTest(layout, [slot], [window]))
        {
            check(session.IsActive && session.SurfaceHandles.SequenceEqual([window]) &&
                session.TryGetVisibleClientRect(window, out var visible) && visible == slot,
                "A non-EQ borderless fixture remains the displayed surface and fills its slot.");
            CheckNativeWindow("Starting a layout on an exact slot");
            session.ApplyForTest(layout, [slot], [window]);
            session.ApplyForTest(layout, [slot], [window], background: true);
            CheckNativeWindow("Reapplying the layout and refreshing its roster");
        }
        CheckNativeWindow("Ending the layout");
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowPos { internal nint Window, After; internal int X, Y, Width, Height; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleChange { internal uint Old, New; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MousePacket { internal uint Type; internal MouseData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct RegionBounds { internal int Left, Top, Right, Bottom; }
    // Two adjacent chat windows, each anchored at the bottom center. Once clamped
    // in one profile, their widths stay reduced on a later larger size in that profile.
    private sealed class BottomCenterChats(int leftWidth, int rightWidth)
    {
        private const int Gap = 12;

        internal void Fit(int clientWidth)
        {
            int available = Math.Max(0, (clientWidth - Gap) / 2);
            leftWidth = Math.Min(leftWidth, available);
            rightWidth = Math.Min(rightWidth, available);
        }

        internal (Rectangle Left, Rectangle Right) BoundsAt(Size client)
        {
            int center = client.Width / 2, y = client.Height - 100;
            return (new(center - Gap / 2 - leftWidth, y, leftWidth, 88), new(center + Gap / 2, y, rightWidth, 88));
        }
    }

    private sealed class NativeGameSizeTrace : NativeWindow
    {
        internal List<(Size Size, bool Decorated)> Sizes { get; } = [];
        internal int MaximizeCommands { get; private set; }
        internal int RestoreCommands { get; private set; }
        internal List<Size> MaximizedSizes { get; } = [];
        internal Point? MaximizedHudMarker;
        internal BottomCenterChats? MaximizedChats, RestoredChats;
        internal List<(int Message, Point Point)> Input { get; } = [];
        internal bool CaptureOnPress;
        internal bool ReleasedOwnCapture { get; private set; }
        internal Action? AfterCapturedPress;
        internal Action? AfterButtonUp;
        internal Action<Message>? ObserveMouse;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg is PointerMove or LeftDown or LeftUp or RightDown or RightUp) ObserveMouse?.Invoke(m);
            if (m.Msg is LeftDown or LeftUp or RightDown or RightUp)
                Input.Add((m.Msg, new((short)m.LParam.ToInt64(), (short)(m.LParam.ToInt64() >> 16))));
            if (CaptureOnPress && m.Msg is LeftDown or RightDown)
            {
                ReleasedOwnCapture = false;
                SetCapture(Handle);
                if (GetCapture() == Handle) AfterCapturedPress?.Invoke();
            }
            if (CaptureOnPress && m.Msg is LeftUp or RightUp && GetCapture() == Handle)
                ReleasedOwnCapture = ReleaseCapture();
            if (m.Msg is LeftUp or RightUp) AfterButtonUp?.Invoke();
            if (m.Msg == SystemCommand && (m.WParam.ToInt64() & 0xFFF0) == MaximizeCommand) MaximizeCommands++;
            if (m.Msg == SystemCommand && (m.WParam.ToInt64() & 0xFFF0) == RestoreCommand) RestoreCommands++;
            if (m.Msg == WindowSize && m.WParam == 2) // SIZE_MAXIMIZED
            {
                long dimensions = m.LParam.ToInt64();
                var size = new Size((ushort)dimensions, (ushort)(dimensions >> 16));
                MaximizedSizes.Add(size);
                MaximizedChats?.Fit(size.Width);
                // Simulate a control whose large-profile coordinates stay clamped after a too-small WM_SIZE.
                if (MaximizedHudMarker is { } marker && size.Width > 0 && size.Height > 0)
                    MaximizedHudMarker = new(Math.Min(marker.X, size.Width - 1), Math.Min(marker.Y, size.Height - 1));
            }
            if (m.Msg == WindowSize && m.WParam == 0) // SIZE_RESTORED
            {
                long dimensions = m.LParam.ToInt64();
                var size = new Size((ushort)dimensions, (ushort)(dimensions >> 16));
                RestoredChats?.Fit(size.Width);
                Sizes.Add((size, (GetWindowLongPtrW(m.HWnd, -16).ToInt64() & 0x00C00000) == 0x00C00000));
            }
            base.WndProc(ref m);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FixtureWindowClass
    {
        internal uint Style; internal nint Procedure; internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background, MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
    }
    private static void RegisterFixtureClass(string name, nint module)
    {
        var wc = new FixtureWindowClass
        {
            Instance = module, ClassName = name, Background = GetStockObject(4),
            Procedure = NativeLibrary.GetExport(Native.GetModuleHandleW("user32.dll"), "DefWindowProcW")
        };
        if (RegisterClassW(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref FixtureWindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle,
        string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out RegionBounds bounds, int size);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint h, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint h, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out RegionBounds bounds);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] MousePacket[] inputs, int bytes);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int brush);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
