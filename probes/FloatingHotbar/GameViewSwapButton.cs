using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TeamBar;

// A slot owns its button position. The occupant supplies the label/action.
internal sealed class GameViewSwapButton : Form
{
    private readonly Action swap;
    private readonly Action<RelativePosition> moved;
    private readonly Part body, corner;
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private Color accent;
    private Rectangle slot;
    private string character = "";
    private RelativePosition? savedPosition;
    private Part? pressed;
    private Point downPoint, downLocation;
    private bool unlocked, physicalDrag;
    private long lastMovement;
    private bool dragging => unlocked && pressed == body;
    internal bool Locked => !unlocked;

    internal GameViewSwapButton(Action swap, Action<RelativePosition> moved, FloatingTheme? theme = null)
    {
        this.swap = swap;
        this.moved = moved;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        body = new Part(this) { Name = "SwapAction" };
        corner = new Part(this) { Name = "SwapPositionLock", Cursor = Cursors.Hand };
        Controls.Add(body);
        Controls.Add(corner);
        SetTheme(theme ?? FloatingThemes.ForAppTheme(AppearanceTheme.Dark));
        body.Paint += (_, e) => { if (body.Text.Length == 0) DrawIcon(e.Graphics, body.ClientRectangle, false); };
        corner.Paint += (_, e) => DrawIcon(e.Graphics, corner.ClientRectangle, true);
        body.Click += (_, _) => { if (Visible && !unlocked) swap(); };
        corner.Click += (_, _) => { if (Visible) SetUnlocked(!unlocked); };
        timer.Tick += (_, _) =>
        {
            // A nonactivating window's capture is limited when another process
            // owns the foreground. Keep a physical drag tracking outside the chip.
            if (dragging && physicalDrag)
            {
                var cursor = Cursor.Position;
                if ((GetAsyncKeyState(1) & 0x8000) != 0) DragTo(cursor);
                else Finish(body, cursor);
            }
            CheckAutoRelock(Environment.TickCount64);
        };
        UpdateControls();
    }

    internal void SetTheme(FloatingTheme theme)
    {
        accent = FloatingThemes.Color(theme.Accent);
        BackColor = FloatingThemes.Color(theme.ButtonBorder);
        ForeColor = body.ForeColor = corner.ForeColor = FloatingThemes.Color(theme.Text);
        body.BackColor = FloatingThemes.Color(theme.Button);
        corner.BackColor = FloatingThemes.Color(theme.Bar);
        body.Invalidate();
        corner.Invalidate();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } // NOACTIVATE | TOOLWINDOW
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = 3; return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }

    internal void Present(Rectangle destination, string occupant, string? text, RelativePosition? position)
    {
        if (destination.Width <= 0 || destination.Height <= 0) { Hide(); return; }
        if (Visible && slot == destination && occupant == character && body.Text == (text ?? "") && savedPosition == position) return;
        Relock();
        slot = destination;
        character = occupant;
        body.Text = text ?? "";
        savedPosition = position;
        Text = $"Team Bar · Swap · {character}";
        body.AccessibleName = $"Swap to {character}";
        UpdateControls();
        // Establish the monitor DPI before measuring the label and lock.
        if (!IsHandleCreated) { Location = slot.Location; GameViewWindows.ConfigureTile(Handle); }
        int dpi = DeviceDpi;
        PlaceButton();
        if (DeviceDpi != dpi) PlaceButton();
        if (!Visible) Show();
    }

    private int Pixels(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    private void PlaceButton()
    {
        int lockWidth = Pixels(24);
        int labelWidth = body.Text.Length == 0 ? Pixels(35) :
            Math.Clamp(TextRenderer.MeasureText(body.Text, body.Font).Width + Pixels(12), Pixels(35), Pixels(260));
        var size = new Size(Math.Min(slot.Width, labelWidth + lockWidth + 2), Math.Min(slot.Height, Pixels(30)));
        var point = savedPosition is null ? ClampToSlot(new(slot.X + Pixels(8), slot.Y + Pixels(8)), size)
            : AnchorPositions.Project(savedPosition, size, slot);
        Bounds = new(point, size);
        int innerWidth = Math.Max(0, ClientSize.Width - 2), height = Math.Max(0, ClientSize.Height - 2);
        int cornerWidth = Math.Min(lockWidth, innerWidth / 2);
        body.Bounds = new(1, 1, innerWidth - cornerWidth, height);
        corner.Bounds = new(1 + body.Width, 1, cornerWidth, height);
    }

    private Point ClampToSlot(Point point, Size size) => new(
        Math.Clamp(point.X, slot.Left, Math.Max(slot.Left, slot.Right - size.Width)),
        Math.Clamp(point.Y, slot.Top, Math.Max(slot.Top, slot.Bottom - size.Height)));

    internal void Relock() => SetUnlocked(false);

    private void SetUnlocked(bool value)
    {
        CancelPress();
        if (unlocked == value) return;
        unlocked = value;
        lastMovement = Environment.TickCount64;
        timer.Enabled = value;
        UpdateControls();
    }

    internal void CheckAutoRelock(long now)
    {
        if (unlocked && pressed is null && now - lastMovement >= 15_000) Relock();
    }

    private void UpdateControls()
    {
        body.Cursor = unlocked ? Cursors.SizeAll : Cursors.Hand;
        tips.SetToolTip(body, unlocked ? "Drag to move this swap button" : $"Swap to {character}");
        corner.AccessibleName = unlocked ? "Lock swap button position" : "Unlock swap button position";
        tips.SetToolTip(corner, unlocked ? "Lock position · automatically locks after 15 seconds without movement" : "Unlock to move");
        corner.Invalidate();
    }

    private void Begin(Part part, Point screen)
    {
        CancelPress();
        pressed = part;
        downPoint = screen;
        downLocation = Location;
        part.Capture = true;
        if (!part.Capture) { CancelPress(); return; }
        physicalDrag = dragging && (GetAsyncKeyState(1) & 0x8000) != 0;
        timer.Interval = physicalDrag ? 16 : 250;
    }

    private void DragTo(Point screen)
    {
        var point = ClampToSlot(new(downLocation.X + screen.X - downPoint.X,
            downLocation.Y + screen.Y - downPoint.Y), Size);
        if (point == Location) return;
        Location = point;
        lastMovement = Environment.TickCount64;
    }

    private void Finish(Part part, Point screen)
    {
        if (pressed != part) return;
        bool drag = dragging;
        if (drag) DragTo(screen);
        ReleasePress();
        if (drag)
        {
            if (Location == downLocation) return;
            savedPosition = AnchorPositions.Normalize(Location, Size, slot);
            moved(savedPosition);
        }
        else if (part.RectangleToScreen(part.ClientRectangle).Contains(screen)) part.PerformClick();
    }

    private void ReleasePress()
    {
        var part = pressed;
        pressed = null;
        physicalDrag = false;
        timer.Interval = 250;
        if (part?.Capture == true) part.Capture = false;
    }

    private void CancelPress()
    {
        if (pressed is null) return;
        if (dragging) Location = downLocation;
        ReleasePress();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible && body is not null && !Disposing) Relock();
        base.OnVisibleChanged(e);
    }

    private void DrawIcon(Graphics g, Rectangle bounds, bool padlock)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        float scale = Math.Min(DeviceDpi / 96f, Math.Min(bounds.Width / 20f, bounds.Height / 20f));
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.Left + (bounds.Width - 16 * scale) / 2, bounds.Top + (bounds.Height - 18 * scale) / 2);
        g.ScaleTransform(scale, scale);
        using var pen = new Pen(accent, 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (padlock)
        {
            // The same open/closed shackle and rectangular body as SpellOverlayLockWindow.
            g.DrawLine(pen, 4, 8, 4, 5);
            g.DrawBezier(pen, 4, 5, 4, 0, 12, 0, 12, 5);
            if (!unlocked) g.DrawLine(pen, 12, 5, 12, 8);
            g.DrawRectangle(pen, 2, 8, 12, 9);
        }
        else
        {
            g.DrawLine(pen, 1, 6, 15, 6);
            g.DrawLines(pen, new Point[] { new(11, 2), new(15, 6), new(11, 10) });
            g.DrawLine(pen, 15, 12, 1, 12);
            g.DrawLines(pen, new Point[] { new(5, 8), new(1, 12), new(5, 16) });
        }
        g.Restore(state);
    }

    // Native buttons expose both actions to accessibility clients. Handle pointer
    // gestures explicitly so the button-up that completes a drag never swaps.
    private sealed class Part : Button
    {
        private readonly GameViewSwapButton owner;

        internal Part(GameViewSwapButton owner)
        {
            this.owner = owner;
            TabStop = false;
            UseMnemonic = false;
            AutoEllipsis = true;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = owner.ForeColor;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = 3; return; }
            if (m.Msg is 0x201 or 0x202 or 0x203 || (m.Msg == 0x200 && owner.dragging && owner.pressed == this))
            {
                long lp = m.LParam.ToInt64();
                var screen = PointToScreen(new(unchecked((short)lp), unchecked((short)(lp >> 16))));
                if (m.Msg == 0x202) owner.Finish(this, screen);
                else if (m.Msg == 0x200) owner.DragTo(screen);
                else owner.Begin(this, screen);
                m.Result = 0;
                return;
            }
            if ((m.Msg == 0x215 || m.Msg == 0x1f) && owner.pressed == this) owner.CancelPress();
            base.WndProc(ref m);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { CancelPress(); timer.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
