using System.Runtime.InteropServices;

namespace TeamBar;

internal sealed partial class GameViewSession
{
    private sealed class FixedClick(GameViewSwapCover picture, PreviewCursorReturn cursor) : IDisposable
    {
        internal readonly GameViewSwapCover Picture = picture;
        internal readonly PreviewCursorReturn CursorReturn = cursor;
        internal bool RestorePointer;
        internal readonly CancellationTokenSource Cancel = new();
        internal Task<string?> Work = Task.FromResult<string?>(null);
        public void Dispose() { Picture.Dispose(); Cancel.Dispose(); }
    }

    private FixedClick? fixedClick;
    internal bool StationaryPreviewClicks { get; set; } =
        Environment.GetEnvironmentVariable("TEAMBAR_STATIONARY_PREVIEW_CLICKS") == "1";

    internal void ClickFixedPreview(GameViewTile tile, Point clientPoint, Point origin, bool right, int modifierKeys = 0,
        Rectangle? releaseTolerance = null)
    {
        if (fixedClick != null)
        {
            SetPreviewNotice("The previous preview click is still finishing.");
            return;
        }
        if (!IsFixedPreview(tile) || plan is not { Cover: >= 0 } current) return;
        int index = Array.IndexOf(previews, tile);
        if (index < 0 || current.At(index) is not { } player || current.At(current.Cover) is not { } reference) return;
        if (!player.SupportsPreviewInput)
        {
            SetPreviewNotice("Use the master slot or this slot's native resolution to click the game.");
            return;
        }
        var bounds = GameViewPlacements.ClientBounds(player.Handle, player.Pid);
        var target = new Point(bounds.Left + clientPoint.X, bounds.Top + clientPoint.Y);
        nint preview = tile.Handle;
        nint initialForeground = Native.GetForegroundWindow();
        var tolerance = releaseTolerance ?? new Rectangle(origin, new Size(1, 1));
        string? CheckPointer(out Point position)
        {
            position = Cursor.Position;
            return PreviewPointerProblem(tolerance, position, preview, PointerOwner(origin), PointerOwner(position), IdleMouse(), CaptureAllows(0));
        }
        string? CheckBeforeFocus(out Point position)
        {
            bool foregroundHeld = GameViewWindows.StillOwnsForeground(initialForeground);
            var pointerProblem = CheckPointer(out position);
            if (!foregroundHeld) return "Foreground focus changed while preparing the preview click.";
            if (pointerProblem != null) return pointerProblem;
            return IsFixedPreview(tile) ? null : "The assigned preview or its game geometry changed before the click.";
        }
        string? problem = CheckBeforeFocus(out _);
        if (!bounds.Contains(target) || problem != null)
        {
            SetPreviewNotice(problem ?? "The mapped click is outside the game client.");
            return;
        }

        if (StationaryPreviewClicks)
        {
            if (initialForeground != player.Handle)
            {
                // Hold the reference picture during activation when its mapped point is exposed.
                // Posted input also works when another UI covers that point.
                using var hold = GameViewSwapCover.TryHoldReference(reference.Handle, current.Slots[current.Cover],
                    target, player.Handle);
                if (CheckBeforeFocus(out _) is { } focusProblem)
                {
                    SetPreviewNotice(focusProblem);
                    return;
                }
                try
                {
                    if (!GameViewWindows.FocusForPreviewClick(player.Handle, player.Pid))
                    {
                        SetPreviewNotice($"Windows declined to focus {player.Name}. No stationary click was queued.");
                        return;
                    }
                }
                finally
                {
                    // Activation can raise the parked window. Restore its normal position
                    // behind the reference before the receiver can process the queued input.
                    if (!ended && plan == current) UpdateStacking(current, previews);
                }
            }
            if (!IsFixedPreview(tile) || CheckPointer(out _) != null ||
                GameViewPlacements.ClientBounds(player.Handle, player.Pid) != bounds)
            {
                SetPreviewNotice("The preview or pointer changed during focus. No stationary click was queued.");
                return;
            }
            SetPreviewNotice(QueueFocusedStationaryClick(player, clientPoint, right, modifierKeys));
            return;
        }

        var picture = GameViewSwapCover.TryHoldReference(reference.Handle, current.Slots[current.Cover], target, player.Handle);
        if (picture == null)
        {
            SetPreviewNotice("The matching point in the reference slot is covered. Clear it and click again.");
            return;
        }

        CancelCursorReturn();
        var initialClip = Cursor.Clip;
        var click = new FixedClick(picture, new(tile, player, reference, bounds, origin, target, initialClip));
        fixedClick = click;
        try
        {
            if (CheckBeforeFocus(out var returnOrigin) is { } preflightProblem)
            {
                FinishFixedClick(click, preflightProblem);
                return;
            }
            click.CursorReturn.SetPreviewOrigin(returnOrigin);
            // A game can move or confine the cursor as soon as it takes focus,
            // including when a later staging check declines the click.
            click.RestorePointer = true;
            uint? activationInput = initialForeground != player.Handle ? GameViewPlacements.LastInputTick() : null;
            if (!GameViewWindows.FocusForPreviewClick(player.Handle, player.Pid))
            {
                FinishFixedClick(click, "The game could not take focus for this preview click.");
                return;
            }
            if (!stacking.StageFixedClick(player.Handle, player.Pid, reference.Handle) ||
                Native.GetForegroundWindow() != player.Handle || PointerOwner(target) != player.Handle)
            {
                FinishFixedClick(click, "The game could not be placed beneath the held reference picture.");
                return;
            }
            // Keep both UI threads responsive. The paired release must run even if
            // a layout change or closing Settings interrupts the hold.
            bool physicalRight = right ^ SystemInformation.MouseButtonsSwapped;
            click.Work = Task.Run(() => ProjectClick(click, player, preview, bounds, returnOrigin, target, physicalRight,
                initialClip, activationInput, tolerance));
            _ = click.Work.ContinueWith(completed => foregroundDispatcher.BeginInvoke(
                () => FinishFixedClick(click, completed.GetAwaiter().GetResult())), TaskScheduler.Default);
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            FinishFixedClick(click, error.Message);
        }
    }

    private static string QueueFocusedStationaryClick(Client player, Point point, bool right, int modifierKeys)
    {
        // Mouse messages use signed 16-bit client coordinates. The parked game's
        // client is the destination even when another window covers it.
        if (point.X is < 0 or > short.MaxValue || point.Y is < 0 or > short.MaxValue)
            return "The mapped click is outside the range of Windows mouse messages.";
        nint coordinates = (point.Y << 16) | point.X;
        int modifiers = modifierKeys & 0x0C; // MK_SHIFT | MK_CONTROL
        uint down = right ? 0x0204u : 0x0201u;
        uint up = right ? 0x0205u : 0x0202u;
        int pressed = modifiers | (right ? 2 : 1);

        int? Post(uint message, int keys)
        {
            if (!GameViewWindows.SameOwner(player.Handle, player.Pid)) return 1400; // ERROR_INVALID_WINDOW_HANDLE
            return Native.PostMessageW(player.Handle, message, keys, coordinates) ? null : Marshal.GetLastPInvokeError();
        }

        string focusLost = $"Windows could not confirm foreground focus for {player.Name}. No stationary click was queued.";
        if (Native.GetForegroundWindow() != player.Handle) return focusLost;
        var pressError = Post(0x0200, modifiers);
        if (pressError is null)
        {
            if (Native.GetForegroundWindow() != player.Handle) return focusLost;
            pressError = Post(down, pressed);
        }
        if (pressError.HasValue) return $"Windows declined the stationary click for {player.Name} (error {pressError}).";
        var releaseError = Post(up, modifiers);
        // A full target queue can accept down and initially refuse up. Retry only
        // the rejected release, always addressed to the same recipient.
        for (int retry = 0; releaseError == 1816 && retry < 3; retry++)
        {
            Thread.Sleep(5);
            releaseError = Post(up, modifiers);
        }
        if (releaseError.HasValue) return $"Windows declined the stationary click release for {player.Name} (error {releaseError}).";
        return $"Queued stationary {(right ? "right" : "left")} click for {player.Name} at ({point.X}, {point.Y}) with foreground focus verified.";
    }

    private void CancelFixedClick()
    {
        if (fixedClick is not { } click) return;
        click.Cancel.Cancel();
        click.Work.GetAwaiter().GetResult();
        FinishFixedClick(click, null);
    }

    private void FinishFixedClick(FixedClick click, string? notice)
    {
        if (fixedClick != click) return;
        fixedClick = null;
        try
        {
            if (!ended && plan != null) UpdateStacking(plan, previews);
            SetPreviewNotice(notice);
        }
        catch (InvalidOperationException error) { Stop(error.Message); }
        finally
        {
            click.Dispose();
            if (click.RestorePointer && !ended) BeginCursorReturn(click.CursorReturn);
        }
    }

    internal static string? PreviewPointerProblem(Rectangle tolerance, Point position, nint preview, nint releaseOwner,
        nint currentOwner, bool idle, bool captureFree)
    {
        if (!tolerance.Contains(position)) return "The pointer moved outside this click's drag tolerance.";
        if (preview != releaseOwner || preview != currentOwner) return "Another window covers the clicked point on the preview.";
        if (!idle) return "Another mouse button is down while preparing the preview click.";
        return captureFree ? null : "Another window or menu has mouse capture.";
    }

    internal static bool IsActivationWarp(Point position, Point from, Rectangle bounds, Rectangle initialClip,
        Rectangle clip, uint? inputBefore, uint? inputNow)
    {
        if (!inputBefore.HasValue || inputBefore != inputNow) return false;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        return position == center || (clip == bounds && clip != initialClip && !clip.Contains(from) &&
            position == new Point(Math.Clamp(from.X, clip.Left, clip.Right - 1), Math.Clamp(from.Y, clip.Top, clip.Bottom - 1)));
    }

    private static string? ProjectClick(FixedClick click, Client player, nint preview, Rectangle bounds,
        Point origin, Point target, bool right, Rectangle initialClip, uint? activationInput, Rectangle tolerance)
    {
        const string interrupted = "Preview click interrupted.";
        bool pressed = false;
        uint down = right ? 8u : 2u, up = right ? 16u : 4u;
        bool CanDeliver(bool allowCapture) => !click.Cancel.IsCancellationRequested &&
            Native.GetForegroundWindow() == player.Handle && click.Picture.CoversReferencePoint(target) &&
            GameViewWindows.Available(player.Handle, player.Pid) &&
            GameViewPlacements.ClientBounds(player.Handle, player.Pid) == bounds && PointerOwner(target) == player.Handle &&
            CaptureAllows(allowCapture ? player.Handle : 0);

        bool Dwell(int milliseconds, bool allowCapture)
        {
            long until = Environment.TickCount64 + milliseconds;
            do
            {
                if (Cursor.Position != target || !CanDeliver(allowCapture)) return false;
                if (click.Cancel.Token.WaitHandle.WaitOne(6)) return false;
            } while (Environment.TickCount64 < until);
            return Cursor.Position == target && CanDeliver(allowCapture);
        }

        try
        {
            var position = Cursor.Position;
            bool onPreview = tolerance.Contains(position) && PointerOwner(position) == preview;
            bool activationWarp = position != origin && IsActivationWarp(position, origin, bounds, initialClip,
                Cursor.Clip, activationInput, GameViewPlacements.LastInputTick());
            if ((!onPreview && !activationWarp) || PointerOwner(origin) != preview || !IdleMouse() || !CanDeliver(false))
                return "The destination changed before the pointer could be positioned.";
            if (onPreview && position != origin) click.CursorReturn.SetPreviewOrigin(position);
            if (!MovePointer(position, target)) return "Windows could not position the pointer for this click.";
            // A newly activated game can recenter on its first frame after our move. Reposition
            // once during settling, before any button packet, and only without intervening input.
            uint? movedAt = activationInput.HasValue ? GameViewPlacements.LastInputTick() : null;
            if (!Dwell(activationInput.HasValue ? 64 : 24, false))
            {
                position = Cursor.Position;
                if (!CanDeliver(false) || !IdleMouse() || position == target ||
                    !IsActivationWarp(position, target, bounds, initialClip, Cursor.Clip, movedAt, GameViewPlacements.LastInputTick()) ||
                    !MovePointer(position, target) || !Dwell(24, false))
                    return "The pointer did not settle on the selected game control.";
            }
            if (!IdleMouse()) return "Another mouse button was pressed before the preview click.";
            if (!MouseButtonPacket(down)) return "Windows declined the preview click.";
            pressed = true;
            bool held = Dwell(88, true);
            // EQ's immediate mouse poll needs both states at the same native
            // point. Give it time to sample up before returning to the preview.
            if (!MouseButtonPacket(up)) return "Windows could not release the preview click.";
            pressed = false;
            if (!held) return interrupted;
            Dwell(64, true);
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            return error.Message;
        }
        finally
        {
            if (pressed) MouseButtonPacket(up);
        }
    }

    private static nint PointerOwner(Point point) => PointerRoot(Native.WindowFromPoint(new Native.Point { X = point.X, Y = point.Y }));
    private static bool IdleMouse() => AsyncKey(1) >= 0 && AsyncKey(2) >= 0 && AsyncKey(4) >= 0 &&
        AsyncKey(5) >= 0 && AsyncKey(6) >= 0;

    private static bool CaptureAllows(nint permitted)
    {
        var state = new PointerThreadInfo { Size = (uint)Marshal.SizeOf<PointerThreadInfo>() };
        return GetGUIThreadInfo(0, ref state) && (state.Flags & 0x1e) == 0 &&
            (state.Capture == 0 || permitted != 0 && PointerRoot(state.Capture) == permitted);
    }

    private static nint PointerRoot(nint window) => window == 0 ? 0 : GetAncestor(window, 2);
    private static bool MouseButtonPacket(uint flags)
    {
        var packet = new PointerInput { Mouse = new MouseInput { Flags = flags } };
        return SendInput(1, ref packet, Marshal.SizeOf<PointerInput>()) == 1;
    }

    private static bool MovePointer(Point from, Point point)
    {
        var desktop = SystemInformation.VirtualScreen;
        if (!desktop.Contains(point)) return false;
        // Center of the destination pixel in Windows' 65536-unit virtual desktop.
        static int Absolute(int pixel, int length) => (int)((pixel * 65536L + 32768) / length);
        var packet = new PointerInput { Mouse = new MouseInput {
            X = Absolute(point.X - desktop.Left, desktop.Width), Y = Absolute(point.Y - desktop.Top, desktop.Height),
            Flags = 0x8000 | 0x4000 | 0x2000 | 1 // ABSOLUTE | VIRTUALDESK | MOVE_NOCOALESCE | MOVE
        } };
        if (Cursor.Position != from || SendInput(1, ref packet, Marshal.SizeOf<PointerInput>()) != 1) return false;
        // Some multimonitor desktops round absolute device motion one pixel
        // short. Correct only that quantization, never chase a moving mouse.
        long deadline = Environment.TickCount64 + 60;
        Point arrived = Cursor.Position;
        while (arrived == from && from != point && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
            arrived = Cursor.Position;
        }
        if (arrived != point && Math.Abs(arrived.X - point.X) <= 1 && Math.Abs(arrived.Y - point.Y) <= 1)
            Cursor.Position = point;
        return Cursor.Position == point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInput { internal uint Type; internal MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, ref PointerInput input, int bytes);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref PointerThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short AsyncKey(int key);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
}
