namespace TeamBar;

internal sealed class GameViewTile : Form
{
    private enum PreviewAction { None, Promote, ClickGame, EndLayout }
    private readonly GameViewSession session;
    private readonly int pid;
    private nint thumbnail;
    private Rectangle sourceFrame;
    private Rectangle sourceVisibleFrame;
    private Rectangle displayedClient;
    private Rectangle sourceClient;
    private Size sourceSurface;
    private readonly bool useSlotResolution;
    private readonly ToolTip? selectionHint;
    private int pressedButtons;
    private PreviewAction pendingAction;
    private Rectangle clickTolerance;
    private bool ending;
    internal nint Source { get; }
#if !TEAMBAR_PUBLIC_RELEASE
    internal string? ThumbnailDiagnostics { get; private set; }
#endif
    internal bool TryGetDisplayedClient(out Rectangle visible)
    {
        visible = Rectangle.Empty;
        if (ending || displayedClient.Width <= 0 || displayedClient.Height <= 0 || !GameViewWindows.SameOwner(Source, pid)
            || GameViewWindows.Minimized(Source) || !GameViewWindows.SourceLinkAlive(thumbnail)) return false;
        visible = RectangleToScreen(displayedClient);
        return true;
    }

    internal GameViewTile(GameViewSession session, nint source, int pid, string name, bool useSlotResolution, string? hint = null)
    {
        this.session = session; Source = source; this.pid = pid;
        this.useSlotResolution = useSlotResolution;
        Text = $"Team Bar · {name}";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        SetStyle(ControlStyles.Selectable, false);
        if (hint is not null)
        {
            selectionHint = new ToolTip();
            selectionHint.SetToolTip(this, hint);
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } // NOACTIVATE | TOOLWINDOW
    }

    internal void Open(Rectangle location, Size referenceSize)
    {
        if (GameViewWindows.SourceSize(Source, pid) != referenceSize)
            throw new InvalidOperationException("A game window changed resolution during setup. Let its picture settle, then apply the layout again.");
        Bounds = location;
        GameViewWindows.Place(Handle, location);
        GameViewWindows.ConfigureTile(Handle);
        thumbnail = GameViewWindows.Register(Handle, Source);
        SetPicture();
        // Map the tile only after its first full-size picture is configured.
        Show();
    }

    private void SetPicture()
    {
        var frame = GameViewPlacements.Bounds(Source);
        var visibleFrame = GameViewPlacements.VisibleFrame(Source, frame);
        var client = GameViewPlacements.ClientBounds(Source, pid);
        var destination = ClientRectangle;
        var surface = GameViewWindows.ThumbnailSize(thumbnail);
        Rectangle? sourceCrop = null;
        Rectangle clientDestination;
        if (visibleFrame == client && frame != client)
        {
            // A clipped window can still supply its entire framed bitmap to DWM.
            // Crop in source pixels before scaling so each slot edge is a game pixel.
            var bitmap = GameViewWindows.ThumbnailBounds(surface, frame, visibleFrame, client);
            sourceCrop = new Rectangle(client.X - bitmap.X, client.Y - bitmap.Y, client.Width, client.Height);
            clientDestination = destination;
        }
        else if (useSlotResolution && frame == client && client == RectangleToScreen(destination))
        {
            clientDestination = destination;
        }
        else
        {
            var bitmap = GameViewWindows.ThumbnailBounds(surface, frame, visibleFrame, client);
            // Native selectors align with the actual game. Scaled previews fill the slot with
            // the painted frame even when the bitmap includes transparent resize handles.
            var painted = bitmap.Contains(visibleFrame) && visibleFrame.Contains(client) ? visibleFrame : bitmap;
            destination = useSlotResolution ? RectangleToClient(bitmap) : Map(bitmap, painted, destination);
            clientDestination = Map(client, bitmap, destination);
        }
        GameViewWindows.UpdateThumbnail(thumbnail, destination, sourceCrop);
        sourceFrame = frame;
        sourceVisibleFrame = visibleFrame;
        sourceClient = client;
        sourceSurface = surface;
        displayedClient = clientDestination;
#if !TEAMBAR_PUBLIC_RELEASE
        ThumbnailDiagnostics = $"DWM surface {surface}, window {frame}, visible frame {visibleFrame}, client {client}, crop {sourceCrop}, destination {destination}, client destination {displayedClient}";
#endif
    }

    internal void RefreshSource()
    {
        if (!MatchesSourceGeometry()) SetPicture();
    }

    private static Rectangle Map(Rectangle rect, Rectangle from, Rectangle to)
    {
        // Positive insets mark the client. Round them inward on all four edges:
        // a pixel that blends half a game pixel with the caption or border is outside the anchor.
        // Negative insets extend the whole thumbnail bitmap past the tile.
        static int Inset(int pixels, int length, int targetLength)
        {
            double scaled = (double)pixels * targetLength / length;
            return (int)(pixels >= 0 ? Math.Ceiling(scaled) : Math.Round(scaled, MidpointRounding.AwayFromZero));
        }
        return Rectangle.FromLTRB(
            to.Left + Inset(rect.Left - from.Left, from.Width, to.Width),
            to.Top + Inset(rect.Top - from.Top, from.Height, to.Height),
            to.Right - Inset(from.Right - rect.Right, from.Width, to.Width),
            to.Bottom - Inset(from.Bottom - rect.Bottom, from.Height, to.Height));
    }

    internal bool MatchesSourceGeometry() => GameViewWindows.ThumbnailSize(thumbnail) == sourceSurface &&
        GameViewPlacements.ClientBounds(Source, pid) == sourceClient && GameViewPlacements.Bounds(Source) == sourceFrame &&
        GameViewPlacements.VisibleFrame(Source, sourceFrame) == sourceVisibleFrame;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = 3; return; } // WM_MOUSEACTIVATE: receive the click without taking keyboard focus.
        if (thumbnail != 0 && !ending)
        {
            if (m.Msg is 0x215 or 0x1f) ClearPress(); // WM_CAPTURECHANGED / WM_CANCELMODE
            if (m.Msg is >= 0x200 and <= 0x20e)
            {
                try { PreviewClick(m); }
                catch (Exception ex) { session.Stop(ex.Message); }
                m.Result = m.Msg is >= 0x20b and <= 0x20d ? 1 : 0;
                return;
            }
        }
        base.WndProc(ref m);
    }

    private void PreviewClick(Message m)
    {
        int msg = m.Msg;
        int button = msg switch
        {
            0x201 or 0x202 or 0x203 => 1,
            0x204 or 0x205 or 0x206 => 2,
            0x207 or 0x208 or 0x209 => 16,
            0x20b or 0x20c or 0x20d => (m.WParam.ToInt64() >> 16) == 1 ? 32 : 64,
            _ => 0
        };
        long lp = m.LParam.ToInt64();
        var point = new Point((short)lp, (short)(lp >> 16));
        if (button == 0)
        {
            if (msg == 0x200 && pendingAction == PreviewAction.ClickGame && !clickTolerance.Contains(point))
                pendingAction = PreviewAction.None;
            return;
        }
        bool down = msg is 0x201 or 0x203 or 0x204 or 0x206 or 0x207 or 0x209 or 0x20b or 0x20d;
        if (down)
        {
            session.CancelCursorReturn();
            if (pressedButtons == 0)
            {
                if (!ClientRectangle.Contains(point)) return;
                if (button == 2 && (m.WParam.ToInt64() & 12) == 12)
                    pendingAction = PreviewAction.EndLayout;
                else if (session.IsFixedPreview(this))
                {
                    if (button is not (1 or 2) || !displayedClient.Contains(point)) return;
                    var tolerance = SystemInformation.DragSize;
                    clickTolerance = new Rectangle(point.X - tolerance.Width / 2, point.Y - tolerance.Height / 2,
                        tolerance.Width, tolerance.Height);
                    pendingAction = PreviewAction.ClickGame;
                }
                else pendingAction = PreviewAction.Promote;
                Capture = true;
            }
            else pendingAction = PreviewAction.None;
            pressedButtons |= button;
            return;
        }

        if ((pressedButtons & button) == 0) return;
        pressedButtons &= ~button;
        if (pressedButtons != 0) return;
        // Windows has already delivered the initiating button-up to this preview.
        // Only now may the session replace it or bring a playable game forward.
        var action = Capture && ClientRectangle.Contains(point) ? pendingAction : PreviewAction.None;
        ClearPress();
        if (action == PreviewAction.EndLayout) session.Dispose();
        else if (action == PreviewAction.Promote) session.PromoteOnClick(this);
        else if (action == PreviewAction.ClickGame && clickTolerance.Contains(point) && displayedClient.Contains(point) &&
            session.IsFixedPreview(this) && MatchesSourceGeometry())
        {
            // Use the same client crop as the compositor. The non-client frame
            // and transparent resize handles are outside this transform.
            static int Project(int pixel, int from, int to) =>
                from == 1 ? (to - 1) / 2 : (int)Math.Round((double)pixel * (to - 1) / (from - 1));
            var native = new Point(Project(point.X - displayedClient.X, displayedClient.Width, sourceClient.Width),
                Project(point.Y - displayedClient.Y, displayedClient.Height, sourceClient.Height));
            var gesture = RectangleToScreen(Rectangle.Intersect(clickTolerance, displayedClient));
            session.ClickFixedPreview(this, native, PointToScreen(point), button == 2, (int)m.WParam, gesture);
        }
    }

    private void ClearPress()
    {
        pressedButtons = 0;
        pendingAction = PreviewAction.None;
        if (IsHandleCreated && Capture) Capture = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !ending)
        {
            ending = true;
            selectionHint?.Dispose();
            ClearPress();
            if (thumbnail != 0) { GameViewWindows.Unregister(thumbnail); thumbnail = 0; }
        }
        base.Dispose(disposing);
    }
}
