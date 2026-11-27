using System.Runtime.InteropServices;

namespace TeamBar;

// An owned popup covers only the native caption. The game retains its window style,
// maximized client size and input coordinates.
internal sealed class GameViewCaptionCover : Control
{
    private readonly nint game;
    private Rectangle captionBounds;
    internal new nint Handle => IsHandleCreated ? base.Handle : 0;

    internal GameViewCaptionCover(nint owner, Rectangle bounds)
    {
        game = owner;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        if (!IsWindow(game)) return;
        Bounds = bounds;
        try
        {
            GameViewWindows.ConfigureTile(base.Handle);
            Present(bounds);
        }
        catch (System.ComponentModel.Win32Exception) when (!IsWindow(game))
        {
            Dispose(); // The game closed between validation and native window creation.
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.Parent = game; // Owner of a top-level WS_POPUP, established before its first paint.
            p.Style = unchecked((int)0x80000000); // WS_POPUP, initially hidden
            p.ExStyle = 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return p;
        }
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Color.Black);

    internal void Present(Rectangle bounds)
    {
        captionBounds = bounds;
        RefreshStacking();
    }

    internal void RefreshStacking()
    {
        var bounds = captionBounds;
        var window = Handle;
        if (window == 0) return;
        if (GetWindow(window, 4) != game || !IsWindow(game)) // GW_OWNER
        {
            Dispose();
            return;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0 || !IsWindowVisible(game) || GameViewWindows.Minimized(game))
        {
            if (IsWindowVisible(window)) ShowWindow(window, 0); // SW_HIDE
            return;
        }

        // Insert immediately above the owner, underneath its dialogs, other apps
        // and Team Bar controls. Moving the strip must never raise the game.
        const uint noActivate = 0x10, noOwnerZOrder = 0x0200, show = 0x40, noZOrder = 0x04;
        bool pinned = GameViewStacking.IsTopmost(game);
        if (GameViewStacking.IsTopmost(window) != pinned)
        {
            // Windows can promote a foreign owner without promoting this popup.
            // Move bands while hidden so a popup never flashes in front of
            // another app or Team Bar's controls before it has its final place.
            const uint noSizeOrMove = 0x03, hide = 0x80;
            if (!Native.SetWindowPos(window, pinned ? -1 : -2, 0, 0, 0, 0,
                noActivate | noOwnerZOrder | noSizeOrMove | hide))
            {
                if (!IsWindow(game) || !IsWindow(window)) return;
                throw new InvalidOperationException("Windows could not match the title bar cover to its game's stacking order.");
            }
        }
        nint previous = GetWindow(game, 3); // GW_HWNDPREV
        uint flags = noActivate | noOwnerZOrder | show;
        nint after = previous;
        if (previous == window)
        {
            if (IsWindowVisible(window) && GameViewPlacements.TryBounds(window, out var actual) && actual == bounds) return;
            flags |= noZOrder;
        }
        else if (previous == 0 || GameViewStacking.IsTopmost(previous) != pinned)
            after = pinned ? -1 : 0;
        if (!Native.SetWindowPos(window, after, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags) &&
            IsWindow(game) && IsWindow(window))
            throw new InvalidOperationException("Windows could not position the game's title bar cover.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = 1; return; } // WM_NCHITTEST: HTCLIENT
        if (m.Msg == 0x21) { m.Result = 4; return; } // WM_MOUSEACTIVATE: MA_NOACTIVATEANDEAT
        if (m.Msg == 0x7b || m.Msg is >= 0x200 and <= 0x20e) // WM_CONTEXTMENU and mouse buttons/motion
        {
            m.Result = m.Msg is >= 0x20b and <= 0x20d ? 1 : 0;
            return;
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}
