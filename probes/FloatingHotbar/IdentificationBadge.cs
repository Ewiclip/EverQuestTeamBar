using System.Runtime.InteropServices;

namespace TeamBar;

internal sealed class IdentificationBadge : Form
{
    private readonly string marker, heading, caption;
    internal bool IsShowing => IsHandleCreated && IsWindowVisible(Handle);
    internal Size BadgeSize => new((int)Math.Ceiling(250 * DeviceDpi / 96d), (int)Math.Ceiling(112 * DeviceDpi / 96d));
    internal IdentificationBadge(string marker, string heading, string caption)
    {
        this.marker = marker;
        this.heading = heading;
        this.caption = caption;
        Text = $"Team Bar · {heading}";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(19, 31, 40); Opacity = .96; DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, false);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x80000; return p; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) { message.Result = -1; return; } // HTTRANSPARENT
        if (message.Msg == 0x21) { message.Result = 3; return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }
    internal void Place(Rectangle bounds)
    {
        // Apply topmost only here with NOACTIVATE. Form.TopMost would
        // activate a newly created handle on this UI thread.
        if (!IsShowing || Bounds != bounds)
            SetWindowPos(Handle, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
    }
    internal void HideBadge() { if (IsShowing) ShowWindow(Handle, 0); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        using var border = new Pen(Color.FromArgb(124, 220, 197), Math.Max(2, 2 * scale));
        e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        using var large = new Font("Segoe UI", 44 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var title = new Font("Segoe UI", 17 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var detail = new Font("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, marker, large, new Rectangle((int)(12 * scale), (int)(13 * scale), (int)(65 * scale), (int)(82 * scale)), Color.FromArgb(124, 220, 197), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, heading.ToUpperInvariant(), title, new Point((int)(88 * scale), (int)(32 * scale)), Color.White);
        TextRenderer.DrawText(e.Graphics, caption, detail, new Rectangle((int)(88 * scale), (int)(57 * scale), (int)(153 * scale), (int)(40 * scale)), Color.FromArgb(182, 200, 211), TextFormatFlags.WordBreak);
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
