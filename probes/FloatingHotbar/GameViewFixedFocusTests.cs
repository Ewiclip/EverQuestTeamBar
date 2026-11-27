using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestFixedPreviewTabClick(Rectangle area, Action<bool, string> check)
    {
        using var master = new Source(Color.SteelBlue, area.Location, framed: true, showInactive: true) { TopMost = true };
        using var game = new Source(Color.IndianRed, new(area.X + 425, area.Y), showInactive: true);
        var origin = WaitForTabFixtureStart(master);
        if (Control.MouseButtons != MouseButtons.None || Control.ModifierKeys != Keys.None || GetCapture() != 0)
            throw new InvalidOperationException("The mouse, modifier keys and capture must be idle for the tab fixture.");
        master.TopMost = false;
        game.Open();
        if (Native.GetForegroundWindow() != master.Handle)
            throw new InvalidOperationException("The test window lost foreground focus. No click was attempted.");
        Rectangle[] slots = [new(area.X, area.Y, 410, 300), new(area.X + 430, area.Y, 250, 170)];
        var layout = new GameViewLayout("Fixed preview tabs", GameViewKind.MainStrip, "fixture", false, ["Master", "Chat"])
        { MasterSlot = 0, MatchMasterResolution = true, AvoidTaskbar = true, AlwaysOnTop = true };
        using var session = GameViewSession.StartForTest(layout, slots, [master.Handle, game.Handle],
            controlFilter: _ => false, taskbars: () => []);
        CheckFirstPreviewTabClick(session, game, 1, check);
        check(master.Input.Count == 0, "The reference window receives no game click.");
        check(AtRequestedPixel(Cursor.Position, origin), "The cursor returned to the Start button's desktop position.");
    }

    private static Point WaitForTabFixtureStart(Source master)
    {
        using var instruction = new Label { Text = "Run one click on a scaled test tab. Keep the pointer still until the windows close.",
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Dock = DockStyle.Fill };
        using var start = new Button { Text = "Start tab test", Height = 48, Dock = DockStyle.Bottom };
        bool started = false;
        start.Click += (_, _) => started = true;
        master.Controls.Add(instruction);
        master.Controls.Add(start);
        master.Open("Team Bar · Scaled tab click test", taskbarEntry: true);
        Console.WriteLine($"Waiting for Start tab test. PID {Environment.ProcessId}, window {master.Handle}, bounds {master.Bounds}.");
        var elapsed = Stopwatch.StartNew();
        while (!started && !master.IsDisposed && elapsed.Elapsed < TimeSpan.FromMinutes(2))
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        if (!started || master.IsDisposed || Native.GetForegroundWindow() != master.Handle)
            throw new InvalidOperationException("Start was not clicked in the foreground test window. No test input was sent.");
        master.Input.Clear();
        return Cursor.Position;
    }

    private static void CheckFirstPreviewTabClick(GameViewSession session, Source game, int slot, Action<bool, string> check)
    {
        var sources = session.SourceHandles.ToArray();
        if (sources[slot] != game.Handle || !session.TryGetVisibleClientRect(game.Handle, out var picture))
            throw new InvalidOperationException("Expected a scaled preview of the fixture window.");
        using var tabs = new TabControl { Location = new(16, 15), Size = new(195, 110) };
        tabs.TabPages.Add("General");
        tabs.TabPages.Add("Combat");
        game.Controls.Add(tabs);
        tabs.SelectedIndex = 0;
        int changes = 0;
        tabs.SelectedIndexChanged += (_, _) => changes++;
        // The native tab control, rather than its parent form, receives this gesture.
        // Share its observations with the fixture's hardware-button/release checks.
        void Record(MouseEventArgs e, int message)
        {
            if (e.Button == MouseButtons.Left) game.Input.Add((message, game.PointToClient(tabs.PointToScreen(e.Location))));
        }
        tabs.MouseDown += (_, e) => Record(e, LeftDown);
        tabs.MouseUp += (_, e) => Record(e, LeftUp);
        GameViewWindows.Focus(sources[0]);
        var hit = game.PointToClient(tabs.PointToScreen(Center(tabs.GetTabRect(1))));
        var size = game.ClientSize;
        var visibleHit = new Point(picture.X + hit.X * picture.Width / size.Width, picture.Y + hit.Y * picture.Height / size.Height);
        game.Input.Clear();
        ClickFixedPreview(session, slot, visibleHit);
        check(tabs.SelectedIndex == 1 && changes == 1 && game.Input.Count(e => e.Message == LeftDown) == 1 &&
            game.Input.Count(e => e.Message == LeftUp) == 1 && sources.SequenceEqual(session.SourceHandles) && game.ClientSize == size,
            "One physical left click on the scaled Combat tab selects it exactly once while the source stays at its original resolution and slot.");
    }

    private static void TestFocusAfterDisablingSwaps(Rectangle area, Action<bool, string> check)
    {
        // Only a window with EQ's native class exercises the maximized-UI selection path.
        const string gameClass = "_EverQuestwndclass";
        var module = Native.GetModuleHandleW(null);
        RegisterFixtureClass(gameClass, module);
        nint game = 0, last = 0;
        var trace = new NativeGameSizeTrace();
        var masterTrace = new NativeGameSizeTrace();
        try
        {
            game = CreateWindowExW(0x80, gameClass, "Fixed focus fixture", 0x14CF0000u,
                area.X, area.Y, 330, 225, 0, 0, module, 0);
            if (game == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            trace.AssignHandle(game);
            last = CreateWindowExW(0x80, gameClass, "Fixed master fixture", 0x14CF0000u,
                area.X + 10, area.Y + 12, 310, 210, 0, 0, module, 0);
            if (last == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            masterTrace.AssignHandle(last);
            using var first = new Source(Color.SteelBlue, area.Location, framed: true);
            first.Open();
            Rectangle[] slots = [new(area.X, area.Y, 410, 300), new(area.X + 430, area.Y, 256, 152),
                new(area.X + 430, area.Y + 170, 230, 178)];
            nint[] handles = [first.Handle, game, last];
            nint[] frozen = [last, first.Handle, game];
            foreach (bool match in new[] { false, true })
            {
                var layout = new GameViewLayout("Fixed after swapping", GameViewKind.MainStrip, "fixture", true, ["One", "Two", "Three"])
                {
                    Screens = [new("full", GameViewKind.EqualSplit, 1), new("split", GameViewKind.EqualSplit, 2)],
                    MasterSlot = 0, MatchMasterResolution = match, AlwaysOnTop = true
                };
                var fixedLayout = layout with { SwapOnClick = false };
                AcquireFixtureFocus(first);
                using var session = GameViewSession.StartForTest(layout, slots, handles);
                PreviewSelection(session, 1);
                PreviewSelection(session, 2);
                session.ApplyForTest(fixedLayout, slots, handles, background: true);
                check(session.SourceHandles.SequenceEqual(frozen) && VisibleFrame(game) == slots[match ? 0 : 2],
                    $"Disabling swaps after two promotions preserves the occupied seats and the selected game's expected resolution (match={match}).");

                first.ResizeTrace = [];
                trace.Sizes.Clear();
                trace.Input.Clear();
                var masterMode = IsZoomed(last);
                masterTrace.Sizes.Clear();
                int masterMaximizes = masterTrace.MaximizeCommands;
                int maximizeBefore = trace.MaximizeCommands;
                var clickPoint = Center(HudMarker(GameViewPlacements.ClientBounds(game, Environment.ProcessId).Size));
                if (match)
                {
                    if (!session.TryGetVisibleClientRect(game, out var picture)) throw new InvalidOperationException("Missing fixed preview.");
                    var tile = (GameViewTile)Control.FromHandle(session.SurfaceHandles[2])!;
                    var inside = tile.PointToClient(Center(picture));
                    static nint Coordinates(Point p) => (nint)((ushort)p.X | (ushort)p.Y << 16);
                    SendMessageW(tile.Handle, LeftDown, 1, Coordinates(inside));
                    SendMessageW(tile.Handle, 0x001f, 0, 0); // WM_CANCELMODE
                    SendMessageW(tile.Handle, LeftUp, 0, Coordinates(inside));
                    SendMessageW(tile.Handle, LeftDown, 1, Coordinates(inside));
                    SendMessageW(tile.Handle, LeftUp, 0, Coordinates(new(-4, inside.Y)));
                    Application.DoEvents();
                    check(trace.Input.Count == 0 && masterTrace.Input.Count == 0 && first.Input.Count == 0,
                        "Cancelling a preview click or releasing across its left edge never clicks the source or a sibling.");
                    TestStationaryPreviewClicks(session, tile, picture, trace, check);
                    clickPoint = ClickFixedPreview(session, 2, new(picture.Right - 11, picture.Bottom - 8));
                }
                else NativeGesture(game, clickPoint);
                // Exercise both the normal focus monitor and a later background roster refresh.
                var elapsed = Stopwatch.StartNew();
                PumpUntil(() => elapsed.ElapsedMilliseconds >= 550, "The fixed-slot focus monitor did not settle.");
                session.ApplyForTest(fixedLayout, slots, handles, background: true);
                check(session.IsActive && session.SourceHandles.SequenceEqual(frozen) && Native.GetForegroundWindow() == game &&
                    VisibleFrame(game) == slots[match ? 0 : 2] && trace.Input.Count == 2 &&
                    trace.Input[0].Message == LeftDown && trace.Input[1].Message == LeftUp &&
                    trace.Input.All(entry => AtRequestedPixel(entry.Point, clickPoint)) &&
                    first.Input.Count == 0 && masterTrace.Input.Count == 0,
                    $"The first playable secondary click reaches its original client coordinate and keeps every assignment fixed (match={match}).");
                check(trace.Sizes.Count == 0 && trace.MaximizeCommands == maximizeBefore,
                    $"Focusing the secondary sends no maximize or size notifications (match={match}).");
                if (match)
                {
                    trace.Input.Clear();
                    if (!session.TryGetVisibleClientRect(game, out var picture)) throw new InvalidOperationException("Missing fixed preview.");
                    Point rightPoint;
                    trace.CaptureOnPress = true;
                    try { rightPoint = ClickFixedPreview(session, 2, new(picture.Left + 9, picture.Bottom - 6), right: true); }
                    finally
                    {
                        trace.CaptureOnPress = false;
                        if (GetCapture() == game) ReleaseCapture();
                    }
                    check(trace.Input.Count == 2 && trace.Input[0].Message == RightDown && trace.Input[1].Message == RightUp &&
                        trace.Input.All(entry => AtRequestedPixel(entry.Point, rightPoint)) &&
                        trace.ReleasedOwnCapture && first.Input.Count == 0 && masterTrace.Input.Count == 0,
                        "A game control can poll the mapped right press, take capture, and receive its own release before the preview pointer returns.");
                    trace.Input.Clear();
                    var fixedSurface = session.SurfaceHandles[2];
                    var origin = new Point(picture.Left + 9, picture.Bottom - 6);
                    var fixedTile = (GameViewTile)Control.FromHandle(fixedSurface)!;
                    TestPreviewCursorReturn(session, fixedTile, trace, origin, check);
                    trace.Input.Clear();
                    bool reappliedDuringPress = false;
                    trace.CaptureOnPress = true;
                    trace.AfterCapturedPress = () => first.BeginInvoke(() =>
                    {
                        reappliedDuringPress = (GetAsyncKeyState(1) & 0x8000) != 0;
                        session.ApplyForTest(fixedLayout, slots, handles);
                    });
                    try
                    {
                        NativeGesture(fixedSurface, fixedTile.PointToClient(origin), previewSource: game,
                            settled: () => OwnerAt(Center(slots[0])) == last);
                    }
                    finally
                    {
                        trace.AfterCapturedPress = null;
                        trace.CaptureOnPress = false;
                        if (GetCapture() == game) ReleaseCapture();
                    }
                    check(reappliedDuringPress && trace.ReleasedOwnCapture && (GetAsyncKeyState(1) & 0x8000) == 0 &&
                        trace.Input.Count == 2 && trace.Input[0].Message == LeftDown && trace.Input[1].Message == LeftUp &&
                        first.Input.Count == 0 && masterTrace.Input.Count == 0 && session.SourceHandles.SequenceEqual(frozen),
                        "Reapplying the layout during a captured press delivers one release and restores the pointer without clicking a sibling.");
                    trace.Input.Clear();
                    if (OwnerAt(origin) != fixedSurface || Control.MouseButtons != MouseButtons.None || GetCapture() != 0)
                        throw new InvalidOperationException("The owned preview must be clear for the capture test.");
                    var savedCursor = Cursor.Position;
                    try
                    {
                        Cursor.Position = origin;
                        SetCapture(first.Handle);
                        if (GetCapture() != first.Handle) throw new InvalidOperationException("The sibling fixture did not acquire capture.");
                        session.ClickFixedPreview(fixedTile, rightPoint, origin, right: false);
                        var settle = Stopwatch.StartNew();
                        PumpUntil(() => settle.ElapsedMilliseconds > 300, "The capture test did not settle.");
                        check(GetCapture() == first.Handle && Cursor.Position == origin && trace.Input.Count == 0 &&
                            first.Input.Count == 0 && masterTrace.Input.Count == 0,
                            "A different window already holding mouse capture keeps capture and receives no projected click.");
                    }
                    finally
                    {
                        if (GetCapture() == first.Handle) ReleaseCapture();
                        if (Cursor.Position == origin && Control.MouseButtons == MouseButtons.None) Cursor.Position = savedCursor;
                    }
                    CheckFirstPreviewTabClick(session, first, 1, check);
                    first.Input.Clear();
                    using var blocker = new Source(Color.Orange, area.Location, passive: true) { TopMost = true };
                    blocker.Open();
                    ClickFixedPreview(session, 2, origin, blocker: blocker);
                    check(blocker.Input.Count == 0 && trace.Input.Count == 0 && masterTrace.Input.Count == 0 && first.Input.Count == 0 &&
                        session.SourceHandles.SequenceEqual(frozen) && session.SurfaceHandles[2] == fixedSurface,
                        "An overlay over the parking slot's mapped point blocks the preview click without clicking, resizing or exchanging any game.");
                }
                GameViewWindows.Focus(last);
                session.ApplyForTest(fixedLayout, slots, handles, background: true);
                check(session.IsActive && session.SourceHandles.SequenceEqual(frozen) && VisibleFrame(game) == slots[match ? 0 : 2] &&
                    session.SurfaceHandles[0] == last && (session.SurfaceHandles[2] != game) == match &&
                    trace.Sizes.Count == 0 && trace.MaximizeCommands == maximizeBefore &&
                    first.ResizeTrace.Count == 0 && masterTrace.Sizes.Count == 0 && masterTrace.MaximizeCommands == masterMaximizes &&
                    IsZoomed(last) == masterMode,
                    $"Returning focus keeps both siblings steady and leaves the secondary at its configured background resolution (match={match}).");
            }

            // Each of these logical displays has a single seat. Use fixture-sized
            // rectangles on the spare display while exercising the actual EQ class.
            Rectangle[] fullSeats = [slots[0], slots[2]];
            nint[] pair = [last, game];
            var fullLayout = new GameViewLayout("Two full displays", GameViewKind.EqualSplit, "full1", false, ["Left", "Right"])
            {
                Screens = [new("full1", GameViewKind.EqualSplit, 1), new("full2", GameViewKind.EqualSplit, 1)],
                MasterSlot = 0, MatchMasterResolution = false, HideTitleBars = true, AvoidTaskbar = true
            };
            AcquireFixtureFocus(first);
            GameViewWindows.Focus(last);
            using (var session = GameViewSession.StartForTest(fullLayout, fullSeats, pair))
            {
                int rightBefore = trace.MaximizeCommands;
                int leftBefore = masterTrace.MaximizeCommands;
                var leftMode = IsZoomed(last);
                var rightMode = IsZoomed(game);
                trace.Sizes.Clear();
                masterTrace.Sizes.Clear();
                GameViewWindows.Focus(game);
                session.ApplyForTest(fullLayout, fullSeats, pair, background: true);
                AcquireFixtureFocus(first);
                session.ApplyForTest(fullLayout, fullSeats, pair, background: true);
                GameViewWindows.Focus(last);
                session.ApplyForTest(fullLayout, fullSeats, pair, background: true);
                check(session.IsActive && masterTrace.MaximizeCommands == leftBefore && trace.MaximizeCommands == rightBefore &&
                    trace.Sizes.Count == 0 && masterTrace.Sizes.Count == 0 && IsZoomed(last) == leftMode && IsZoomed(game) == rightMode &&
                    session.SourceHandles.SequenceEqual(pair) && pair.Select(VisibleFrame).SequenceEqual(fullSeats),
                    "Moving focus between dedicated monitors and another app preserves both native UI modes and sends no resize.");
            }
        }
        finally
        {
            if (masterTrace.Handle != 0) masterTrace.ReleaseHandle();
            if (trace.Handle != 0) trace.ReleaseHandle();
            if (last != 0) DestroyWindow(last);
            if (game != 0) DestroyWindow(game);
            UnregisterClassW(gameClass, module);
        }
    }
}
