namespace TeamBar;

internal sealed partial class GameViewSession
{
    private sealed class PreviewCursorReturn(GameViewTile tile, Client player, Client reference, Rectangle gameClient,
        Point origin, Point target, Rectangle originalClip)
    {
        private Point previewOrigin = origin;
        private Point destination = origin;
        private Point? constrainedPosition;
        private bool placed;

        // Called by the gesture worker before moving the pointer. Restoration starts only
        // after the worker completes; use the last observed position on the original preview.
        internal void SetPreviewOrigin(Point position) => previewOrigin = destination = position;

        // Run on the layout thread, after the parked game and the preview have
        // their usual stacking order again. This operation sends no click.
        internal bool Restore()
        {
            var foreground = Native.GetForegroundWindow();
            if (tile.IsDisposed || !tile.Visible || !tile.Bounds.Contains(previewOrigin) ||
                (foreground != player.Handle && GetAncestor(foreground, 3) != player.Handle) ||
                !GameViewWindows.SameOwner(player.Handle, player.Pid) || !IdleMouse()) return false;

            var position = Cursor.Position;
            if (position == destination)
            {
                placed = true;
                return true;
            }
            var delta = new Size(position.X - target.X, position.Y - target.Y);
            bool atTarget = Math.Abs(delta.Width) <= 2 && Math.Abs(delta.Height) <= 2;
            var center = new Point(gameClient.Left + gameClient.Width / 2, gameClient.Top + gameClient.Height / 2);
            bool recentered = Math.Abs(position.X - center.X) <= 1 && Math.Abs(position.Y - center.Y) <= 1;
            var clip = Cursor.Clip;
            bool gameClip = clip == gameClient && clip != originalClip && !clip.Contains(destination);
            // Reapplying ClipCursor after a successful return puts the pointer
            // at the closest game edge, even when the mouse hasn't moved.
            bool atGameEdge = gameClip && position == new Point(
                Math.Clamp(destination.X, clip.Left, clip.Right - 1),
                Math.Clamp(destination.Y, clip.Top, clip.Bottom - 1));
            // Carry hand movement made during the click back into the preview. Subpixel
            // quantization and a one-pixel game correction keep the exact origin.
            if (!atTarget && !recentered && !atGameEdge && position != constrainedPosition)
            {
                var adjusted = previewOrigin + delta;
                var owner = PointerOwner(position);
                if (placed || (owner != player.Handle && owner != reference.Handle) ||
                    !gameClient.Contains(position) || !tile.Bounds.Contains(adjusted) ||
                    !originalClip.Contains(adjusted)) return false;
                destination = adjusted;
            }
            if (!originalClip.Contains(destination) || !System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(destination)))
                return false;
            if (!clip.Contains(destination))
            {
                // EQ can confine the pointer to its client while it handles the
                // release. Only undo a client clip introduced by this gesture.
                if (!gameClip) return true;
                Cursor.Clip = originalClip == SystemInformation.VirtualScreen ? Rectangle.Empty : originalClip;
                if (!Cursor.Clip.Contains(destination)) return true;
            }
            Cursor.Position = destination;
            var actual = Cursor.Position;
            placed |= actual == destination;
            constrainedPosition = actual == destination ? null : actual;
            return true;
        }
    }

    private PreviewCursorReturn? returningCursor;
    private readonly System.Windows.Forms.Timer cursorReturnTimer = new() { Interval = 16 };
    private long cursorReturnUntil;

    private void BeginCursorReturn(PreviewCursorReturn cursor)
    {
        CancelCursorReturn();
        returningCursor = cursor;
        cursorReturnUntil = Environment.TickCount64 + 350;
        // A game can reapply its stored mouse position on a later frame. Also
        // check after the first successful return, until normal movement resumes.
        CheckCursorReturn();
        if (returningCursor != null) cursorReturnTimer.Start();
    }

    private void CheckCursorReturn()
    {
        if (returningCursor == null || Environment.TickCount64 > cursorReturnUntil || !returningCursor.Restore())
            CancelCursorReturn();
    }

    internal void CancelCursorReturn()
    {
        cursorReturnTimer.Stop();
        returningCursor = null;
    }
}
