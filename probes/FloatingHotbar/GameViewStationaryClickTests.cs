namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestStationaryPreviewClicks(GameViewSession session, GameViewTile tile, Rectangle picture,
        NativeGameSizeTrace receiver, Action<bool, string> check)
    {
        var games = session.SourceHandles;
        var frames = games.Select(VisibleFrame).ToArray();
        var surfaces = session.SurfaceHandles;
        var native = GameViewPlacements.ClientBounds(tile.Source, Environment.ProcessId);
        var initialForeground = Native.GetForegroundWindow();
        var referencePoint = Center(native);
        var reference = OwnerAt(referencePoint);
        var savedPointer = Cursor.Position;
        var savedMode = session.StationaryPreviewClicks;
        var origin = new Point(picture.Right - 5, picture.Bottom - 7);
        var local = tile.PointToClient(origin);
        if (initialForeground == tile.Source || FixtureMouseInput(initialForeground) == null ||
            reference != games[0] || OwnerAt(origin) != tile.Handle ||
            Control.MouseButtons != MouseButtons.None || GetCapture() != 0)
            throw new InvalidOperationException("Stationary click tests require the reference slot to be visible, a clear preview, and a different foreground fixture.");

        var expected = new Point((int)Math.Round((origin.X - picture.Left) * (native.Width - 1d) / (picture.Width - 1)),
            (int)Math.Round((origin.Y - picture.Top) * (native.Height - 1d) / (picture.Height - 1)));
        var received = new List<(int Msg, nint Flags, Point Client, Point Cursor, nint Foreground, nint Reference, bool ButtonHeld)>();
        receiver.ObserveMouse = m => received.Add((m.Msg, m.WParam,
            new((short)m.LParam.ToInt64(), (short)(m.LParam.ToInt64() >> 16)), Cursor.Position, Native.GetForegroundWindow(),
            OwnerAt(referencePoint), (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0));
        try
        {
            session.StationaryPreviewClicks = true;
            Cursor.Position = origin;
            Application.DoEvents();
            foreach (bool right in new[] { false, true })
            {
                received.Clear();
                receiver.Input.Clear();
                if (right)
                {
                    // Exercise a Shift-modified right click without sending keyboard input to the desktop.
                    nint coordinates = (nint)((ushort)local.X | (ushort)local.Y << 16);
                    SendMessageW(tile.Handle, RightDown, 6, coordinates);
                    SendMessageW(tile.Handle, RightUp, 4, coordinates);
                    PumpUntil(() => receiver.Input.Any(m => m.Message == RightUp), "The queued right click did not reach the fixture.");
                }
                else NativeGesture(tile.Handle, local, previewSource: tile.Source, settled: () => true);
                Application.DoEvents();
                int modifiers = right ? 4 : 0, button = right ? 2 : 1;
                check(received.Select(m => m.Msg).SequenceEqual(new[] { PointerMove, right ? RightDown : LeftDown, right ? RightUp : LeftUp }) &&
                    received.Select(m => m.Flags).SequenceEqual(new nint[] { modifiers, modifiers | button, modifiers }) &&
                    received.All(m => m.Client == expected && m.Cursor == origin && m.Foreground == tile.Source &&
                        m.Reference == reference && !m.ButtonHeld) &&
                    receiver.Input.Count == 2 && games.Where(g => g != tile.Source).All(g => FixtureMouseInput(g)!.Count == 0) &&
                    session.SourceHandles.SequenceEqual(games) && session.SurfaceHandles.SequenceEqual(surfaces) &&
                    games.Select(VisibleFrame).SequenceEqual(frames) && Cursor.Position == origin &&
                    Native.GetForegroundWindow() == tile.Source && OwnerAt(referencePoint) == reference,
                    right
                        ? "A stationary Shift+right click in the already-focused game preserves its foreground, the visible reference, and the mapped click and modifier state."
                        : "A stationary left click focuses the selected game before delivering the mapped click, keeping the pointer and every slot in place.");
            }
        }
        finally
        {
            receiver.ObserveMouse = null;
            receiver.Input.Clear();
            session.StationaryPreviewClicks = savedMode;
            if (Native.GetForegroundWindow() == tile.Source) GameViewWindows.Focus(initialForeground);
            if (Cursor.Position == origin && Control.MouseButtons == MouseButtons.None) Cursor.Position = savedPointer;
        }
    }
}
