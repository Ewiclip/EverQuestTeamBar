namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestPreviewCursorReturn(GameViewSession session, GameViewTile tile, NativeGameSizeTrace receiver,
        Point origin, Action<bool, string> check)
    {
        var source = tile.Source;
        var native = GameViewPlacements.ClientBounds(source, Environment.ProcessId);
        var local = tile.PointToClient(origin);
        var seats = session.SourceHandles;
        var previousOwner = OwnerAt(Center(native));
        var initialClip = Cursor.Clip;
        if (!initialClip.Contains(origin) || !initialClip.Contains(native) || native.Contains(origin))
            throw new InvalidOperationException("Cursor return tests need separate, reachable fixture game and preview areas.");

        bool Restacked() => OwnerAt(Center(native)) == previousOwner && session.SourceHandles.SequenceEqual(seats);
        bool ClickedOnce() => receiver.Input.Count == 2 && receiver.Input[0].Message == LeftDown &&
            receiver.Input[1].Message == LeftUp && receiver.Input[0].Point == receiver.Input[1].Point;
        void ReleaseFixtureClip()
        {
            if (Cursor.Clip == native)
                Cursor.Clip = initialClip == SystemInformation.VirtualScreen ? Rectangle.Empty : initialClip;
        }

        // A game may keep its cursor confined for a few frames after releasing mouse capture.
        using var releaseClip = new System.Windows.Forms.Timer { Interval = 150 };
        bool clipped = false, released = false;
        releaseClip.Tick += (_, _) =>
        {
            releaseClip.Stop();
            ReleaseFixtureClip();
            released = true;
        };
        receiver.Input.Clear();
        receiver.CaptureOnPress = true;
        receiver.AfterCapturedPress = () =>
        {
            Cursor.Clip = native;
            clipped = Cursor.Clip == native;
        };
        receiver.AfterButtonUp = releaseClip.Start;
        try
        {
            NativeGesture(tile.Handle, local, previewSource: source, settled: () => released && Restacked());
            check(clipped && released && Cursor.Clip == initialClip && ClickedOnce() && receiver.ReleasedOwnCapture,
                "A game that confines the pointer to its native client through button-up still returns it to the clicked preview pixel.");
        }
        finally
        {
            releaseClip.Stop();
            receiver.CaptureOnPress = false;
            receiver.AfterCapturedPress = receiver.AfterButtonUp = null;
            ReleaseFixtureClip();
            if (GetCapture() == source) ReleaseCapture();
        }

        // Simulate one late game frame restoring its last native pointer position after
        // Team Bar has already returned it. Observe the return again and require it to stick.
        Point nativeClick = default;
        long gameWarp = 0;
        receiver.Input.Clear();
        receiver.AfterButtonUp = () => nativeClick = Cursor.Position;
        try
        {
            NativeGesture(tile.Handle, local, previewSource: source, settled: () =>
            {
                if (gameWarp == 0)
                {
                    if (!native.Contains(nativeClick) || Native.GetForegroundWindow() != source) return false;
                    Cursor.Position = nativeClick;
                    gameWarp = Environment.TickCount64;
                    return false;
                }
                return Environment.TickCount64 - gameWarp >= 100 && Restacked();
            });
            check(gameWarp != 0 && ClickedOnce(),
                "A late game frame warping the pointer back to the delivered click is corrected without a second button press.");
        }
        finally { receiver.AfterButtonUp = null; }

        // A later game frame can confine the mouse again. Windows then puts
        // the returned cursor at the client edge nearest the preview, which
        // can be far from both the game control and the center of the client.
        var nearestEdge = new Point(Math.Clamp(origin.X, native.Left, native.Right - 1),
            Math.Clamp(origin.Y, native.Top, native.Bottom - 1));
        bool reappliedClip = false, snappedToEdge = false;
        receiver.Input.Clear();
        var beforeReclip = Cursor.Position;
        try
        {
            NativeGesture(tile.Handle, local, previewSource: source, settled: () =>
            {
                if (!Restacked()) return false;
                if (reappliedClip) return Cursor.Clip == initialClip;
                if (Native.GetForegroundWindow() != source || Cursor.Position != origin) return false;
                Cursor.Clip = native;
                reappliedClip = true;
                snappedToEdge = Cursor.Clip == native && AtRequestedPixel(Cursor.Position, nearestEdge) &&
                    !AtRequestedPixel(Cursor.Position, nativeClick) && !AtRequestedPixel(Cursor.Position, Center(native));
                return false;
            });
            check(reappliedClip && snappedToEdge && ClickedOnce() && Cursor.Clip == initialClip,
                "A later client clip that snaps the returned pointer to a distant edge is cleared and the preview pointer returns again.");
        }
        finally
        {
            ReleaseFixtureClip();
            if (snappedToEdge && AtRequestedPixel(Cursor.Position, nearestEdge) && Control.MouseButtons == MouseButtons.None &&
                Native.GetForegroundWindow() == source) Cursor.Position = beforeReclip;
        }

        receiver.Input.Clear();
        bool drifted = false;
        receiver.AfterButtonUp = () =>
        {
            // A one-pixel cursor adjustment is enough to break an exact comparison.
            var at = Cursor.Position;
            var shifted = new Point(at.X + 1, at.Y);
            if (!native.Contains(shifted) || Native.GetForegroundWindow() != source) return;
            Cursor.Position = shifted;
            drifted = Cursor.Position == shifted;
        };
        try
        {
            NativeGesture(tile.Handle, local, previewSource: source, settled: Restacked);
            check(drifted && ClickedOnce(),
                "A one-pixel adjustment on button-up leaves the cursor at the clicked location in the small preview.");
        }
        finally { receiver.AfterButtonUp = null; }

        // Once the pointer is back, moving to another control in the same small
        // game must remain possible during any late-frame return checks.
        receiver.Input.Clear();
        Point userPoint = new(origin.X + 32, origin.Y - 12);
        if (OwnerAt(origin) != tile.Handle || OwnerAt(userPoint) != tile.Handle || Control.MouseButtons != MouseButtons.None)
            throw new InvalidOperationException("The directed movement test needs a clear, owned preview.");
        var savedCursor = Cursor.Position;
        try
        {
            Cursor.Position = origin;
            session.ClickFixedPreview(tile, new Point(nativeClick.X - native.Left, nativeClick.Y - native.Top), origin, right: false);
            PumpUntil(() => ClickedOnce() && Cursor.Position == origin, "The cursor did not return before the movement test.");
            Cursor.Position = userPoint;
            long movedAt = Environment.TickCount64;
            PumpUntil(() => Environment.TickCount64 - movedAt >= 400, "The movement test did not settle.");
            check(Cursor.Position == userPoint && ClickedOnce() && Restacked(),
                "Moving to a different control in the preview after the return leaves the pointer there.");
        }
        finally
        {
            if (Control.MouseButtons == MouseButtons.None && (Cursor.Position == userPoint || Cursor.Position == origin))
                Cursor.Position = savedCursor;
        }
    }
}
