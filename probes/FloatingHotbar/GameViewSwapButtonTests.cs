using System.Runtime.InteropServices;

namespace TeamBar;

internal static class GameViewSwapButtonTests
{
    internal static void Run(Rectangle area, Action<bool, string> check)
    {
        int swaps = 0;
        var moves = new List<RelativePosition>();
        nint focused = Native.GetForegroundWindow();
        var slot = new Rectangle(area.Left + 10, area.Top + 10, 320, 180);
        using var widget = new GameViewSwapButton(() => swaps++, moves.Add);
        widget.Present(slot, "Cleric", "Cleric", null);
        var body = widget.Controls["SwapAction"]!;
        var corner = widget.Controls["SwapPositionLock"]!;
        void Pointer(Control part, uint message, Point screen)
        {
            var local = part.PointToClient(screen);
            var packed = (nint)((ushort)local.X | ((ushort)local.Y << 16));
            SendMessageW(part.Handle, message, message == 0x202 ? 0 : 1, packed);
        }
        Point Middle(Control p) => p.PointToScreen(new(p.Width / 2, p.Height / 2));
        void Click(Control p)
        {
            var at = Middle(p);
            Pointer(p, 0x201, at);
            Pointer(p, 0x202, at);
        }

        check(widget.Locked && body.Text == "Cleric" && corner.Left > body.Left && slot.Contains(widget.Bounds) &&
            Native.GetForegroundWindow() == focused, "The slot's character label and corner lock open without taking focus.");
        Click(body);
        check(swaps == 1 && moves.Count == 0 && Native.GetForegroundWindow() == focused,
            "A locked swap button activates once on release without taking focus itself.");
        Click(corner);
        Click(body);
        var initial = widget.Location;
        Pointer(body, 0x201, Middle(body));
        Pointer(body, 0x200, new(slot.Right + 50, slot.Bottom + 50));
        widget.Present(slot, "Cleric", "Cleric", null);
        widget.CheckAutoRelock(Environment.TickCount64 + 16_000);
        check(swaps == 1 && !widget.Locked && moves.Count == 0 && widget.Right == slot.Right && widget.Bottom == slot.Bottom,
            "Unlocking and dragging clamp to the slot, and regular layout refreshes leave an active drag alone.");
        Pointer(body, 0x202, new(slot.Right + 50, slot.Bottom + 50));
        check(moves.SequenceEqual([new RelativePosition(1, 1)]) && swaps == 1,
            "A completed drag saves one normalized position without activating a swap.");
        widget.CheckAutoRelock(Environment.TickCount64 + 16_000);
        check(widget.Locked, "An idle swap button locks after 15 seconds.");

        Click(corner);
        Pointer(body, 0x201, Middle(body));
        Pointer(body, 0x200, new(slot.Left - 20, slot.Top - 20));
        widget.Relock();
        Pointer(body, 0x202, Middle(body));
        check(widget.Locked && widget.Right == slot.Right && widget.Bottom == slot.Bottom && moves.Count == 1 && swaps == 1,
            "An intervening swap cancels the unfinished drag and its pending button-up.");
        Pointer(body, 0x201, Middle(body));
        widget.Present(slot, "Bard", null, moves[0]);
        Pointer(body, 0x202, Middle(body));
        check(widget.Locked && swaps == 1 && body.Text.Length == 0 && body.AccessibleName == "Swap to Bard" &&
            widget.Right == slot.Right, "Replacing the occupant shows the swap icon, keeps the slot's position, and cancels an old press.");
        var resized = slot with { Width = slot.Width - 90, Height = slot.Height - 40 };
        widget.Present(resized, "Bard", "My custom text", moves[0]);
        check(body.Text == "My custom text" && widget.Right == resized.Right && widget.Bottom == resized.Bottom &&
            widget.Location != initial, "Custom text stays anchored inside a resized slot.");
        Click(corner);
        Click(corner);
        check(widget.Locked && swaps == 1, "The corner padlock can immediately lock the swap button again.");
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
