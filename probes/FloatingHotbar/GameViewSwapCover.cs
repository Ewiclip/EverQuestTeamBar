using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TeamBar;

// Shows the incoming games' last frames while EQ rebuilds their windowed renderers.
internal sealed class GameViewSwapCover : IDisposable
{
    private readonly record struct Seat(nint Surface, nint Game, Rectangle Slot, Rectangle Client);
    private sealed record HeldReference(nint Reference, nint Selected, nint InitialForeground, Point ClickPoint);
    private readonly HeldReference? held;
    private volatile bool referenceVisible;
    private readonly List<Picture> pictures = [];
    private Dispatcher dispatcher = null!;
    private DispatcherTimer? focusWatch;
    private int disposeStarted;
    private static readonly object frameLock = new();
    private static Task? pendingFrame;

    internal static bool IsCover(nint window) => NativeWindow.FromHandle(window) is Picture;

    private GameViewSwapCover(HeldReference? held) => this.held = held;

    internal bool CoversReferencePoint(Point point) => referenceVisible && held?.ClickPoint == point;

    internal static GameViewSwapCover? TryHoldReference(nint referenceGame, Rectangle referenceSlot, Point nativeScreenPoint,
        nint selectedGame)
    {
        if (referenceGame == 0 || selectedGame == 0 || selectedGame == referenceGame ||
            !referenceSlot.Contains(nativeScreenPoint) || WindowAt(nativeScreenPoint) != referenceGame) return null;
        var hold = new HeldReference(referenceGame, selectedGame, Native.GetForegroundWindow(), nativeScreenPoint);
        // No seats are exchanged. The held reference picture is click-through in its original z-order band.
        return TryShow([(referenceGame, referenceSlot)], referenceSlot, referenceSlot, hold);
    }

    internal static GameViewSwapCover? TryShow(IReadOnlyList<(nint Surface, Rectangle Slot)> seats,
        Rectangle master, Rectangle selected) => TryShow(seats, master, selected, null);

    private static GameViewSwapCover? TryShow(IReadOnlyList<(nint Surface, Rectangle Slot)> seats,
        Rectangle master, Rectangle selected, HeldReference? held)
    {
        if (seats.Count == 0) return null;
        var cover = new GameViewSwapCover(held);
        try
        {
            var sources = seats.Select(seat =>
            {
                nint game = Control.FromHandle(seat.Surface) is GameViewTile tile ? tile.Source : seat.Surface;
                GetWindowThreadProcessId(game, out var pid);
                return new Seat(seat.Surface, game, seat.Slot, GameViewPlacements.ClientBounds(game, (int)pid));
            }).ToArray();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The layout thread is about to wait on EQ. Keep hit testing and dismissal on
            // a responsive thread for the lifetime of these temporary no-activate windows.
            var thread = new Thread(() => cover.Run(sources, master, selected, ready))
                { IsBackground = true, Name = "Team Bar swap pictures" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return ready.Task.GetAwaiter().GetResult() ? cover : null;
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            // An unavailable compositor or insufficient bitmap memory should never block a swap.
        }
        return null;
    }

    private void Run(Seat[] seats, Rectangle master, Rectangle selected, TaskCompletionSource<bool> ready)
    {
        Exception? failure = null;
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var oldMaster = seats.Single(s => s.Slot == master);
            var incoming = seats.Single(s => s.Slot == selected);
            var windows = VisibleWindows(seats);
            if (held is not null && !VisibleParts(windows, held.Reference, held.Reference, master, out _)
                .Any(area => area.Contains(held.ClickPoint))) return;
            foreach (var seat in seats)
            {
                var source = seat == oldMaster ? incoming : seat == incoming ? oldMaster : seat;
                foreach (var part in VisibleParts(windows, seat.Surface, seat.Game, seat.Slot, out _))
                {
                    var picture = new Picture(seat, source, part, held is not null);
                    pictures.Add(picture); // Own the HWND even if preparation fails.
                    if (picture.Prepare()) continue;
                    if (held is not null) return;
                    picture.Dispose();
                    pictures.RemoveAt(pictures.Count - 1);
                }
            }
            // A window may have opened, or moved over a slot, during image acquisition.
            windows = VisibleWindows(seats);
            if (held is not null && !VisibleParts(windows, held.Reference, held.Reference, master, out _)
                .SequenceEqual(pictures.Select(p => p.Area))) return;
            foreach (var picture in pictures.ToArray())
            {
                var parts = VisibleParts(windows, picture.Surface, picture.Game, picture.Area, out nint decoration);
                if (parts.Contains(picture.Area)) picture.Reveal(decoration);
                if (picture.Shown) continue;
                if (held is not null) return;
                picture.Dispose();
                pictures.Remove(picture);
            }
            if (pictures.Count == 0) return;
            WaitForFrame();
            var allowed = seats.SelectMany(s => new[] { s.Surface, s.Game }).ToHashSet();
            if (held is not null)
            {
                if (!GameViewWindows.StillOwnsForeground(held.InitialForeground) || WindowAt(held.ClickPoint) != held.Reference) return;
                referenceVisible = true;
                allowed.Add(held.Selected);
            }
            bool awaitingSelection = held is not null;
            long activationDeadline = Environment.TickCount64 + 300;
            focusWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
            {
                var foreground = Native.GetForegroundWindow();
                if (held is null)
                {
                    if (foreground == 0 || allowed.Contains(foreground)) return;
                }
                else
                {
                    if (foreground == held.Selected) awaitingSelection = false;
                    // GetForegroundWindow can briefly return null between deactivation and
                    // activation on different input queues. Keep the picture during this gap.
                    bool focusIsOurs = awaitingSelection ? foreground == held.InitialForeground || allowed.Contains(foreground) ||
                        (foreground == 0 && Environment.TickCount64 <= activationDeadline)
                        : foreground == held.Selected;
                    nint under = WindowAt(held.ClickPoint);
                    if (focusIsOurs && (under == held.Reference || under == held.Selected)) return;
                    referenceVisible = false;
                }
                foreach (var picture in pictures) picture.Conceal();
                focusWatch!.Stop();
            }, dispatcher);
            ready.SetResult(true);
            Dispatcher.Run();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            referenceVisible = false;
            focusWatch?.Stop();
            ClearPictures();
            if (failure is null) ready.TrySetResult(false);
            else ready.TrySetException(failure);
        }
    }

    private readonly record struct VisibleWindow(nint Window, Rectangle Bounds, bool ColorKeyOverlay);
    private const int layered = 0x00080000, topmost = 0x08, transparent = 0x20, noActivate = 0x08000000;
    private static nint WindowAt(Point point) => GetAncestor(WindowFromPoint(point), 2); // GA_ROOT

    private static List<VisibleWindow> VisibleWindows(Seat[] seats)
    {
        var result = new List<VisibleWindow>();
        EnumWindows((window, data) =>
        {
            if (!IsWindowVisible(window) || GameViewWindows.Minimized(window) ||
                !GameViewPlacements.TryBounds(window, out var bounds) || !seats.Any(s => s.Slot.IntersectsWith(bounds)) ||
                (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)) return true;
            var style = GetWindowLongPtrW(window, -20).ToInt64();
            bool colorKeyOverlay = false;
            if ((style & layered) != 0 && GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags))
            {
                if ((flags & 2) != 0 && alpha == 0) return true; // LWA_ALPHA, fully invisible
                const int overlayStyle = topmost | transparent | noActivate;
                colorKeyOverlay = (flags & 1) != 0 && (style & overlayStyle) == overlayStyle; // LWA_COLORKEY
            }
            result.Add(new(window, GameViewPlacements.VisibleFrame(window, bounds), colorKeyOverlay));
            return true;
        }, 0);
        return result;
    }

    private static Rectangle[] VisibleParts(List<VisibleWindow> order, nint surface, nint game, Rectangle slot,
        out nint lowestDecoration)
    {
        lowestDecoration = 0;
        using var visible = new Region(slot);
        foreach (var (window, bounds, colorKeyOverlay) in order)
        {
            if (!slot.IntersectsWith(bounds)) continue;
            if (window == surface || window == game)
            {
                visible.Intersect(bounds);
                using var identity = new Matrix();
                return visible.GetRegionScans(identity).Select(Rectangle.Round).ToArray();
            }
            // Cursor decorations can own a mostly transparent desktop-sized HWND.
            // Keep their actual painted pixels live above the game's frozen image.
            if (colorKeyOverlay) lowestDecoration = window;
            else visible.Exclude(bounds);
        }
        return [];
    }

    private static void WaitForFrame()
    {
        // A suspended display need not produce a frame. Bound both the UI wait and the
        // number of outstanding DWM calls if the player swaps again while it is suspended.
        Task frame;
        lock (frameLock)
        {
            if (pendingFrame is null || pendingFrame.IsCompleted)
                pendingFrame = Task.Run(() =>
                {
                    try { GameViewWindows.FlushComposition(); } catch (InvalidOperationException) { }
                });
            frame = pendingFrame;
        }
        frame.Wait(100);
    }

    public void Dispose()
    {
        referenceVisible = false;
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0 || dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(() =>
        {
            // Show the native games and their correctly positioned UI before accepting clicks.
            WaitForFrame();
            ClearPictures();
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        });
    }

    private void ClearPictures()
    {
        foreach (var picture in pictures) picture.Dispose();
        pictures.Clear();
    }

    private sealed class Picture(Seat seat, Seat source, Rectangle area, bool holdReference) : NativeWindow, IDisposable
    {
        internal nint Surface => seat.Surface;
        internal nint Game => seat.Game;
        internal Rectangle Area => area;
        internal bool Shown { get; private set; }

        internal bool Prepare()
        {
            CreateHandle(new CreateParams
            {
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = noActivate | layered | (!holdReference || GameViewStacking.IsTopmost(seat.Game) ? topmost : 0) |
                    (seat == source ? transparent : 0) | 0x80, // TOOLWINDOW
                X = area.X, Y = area.Y, Width = area.Width, Height = area.Height
            });
            GameViewWindows.ConfigureTile(Handle);
            var screen = GetDC(0);
            if (screen == 0) return false;
            nint memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen);
                bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
                if (memory == 0 || bitmap == 0) return false;
                previous = SelectObject(memory, bitmap);
                if (previous == 0 || previous == -1) return false;
                // Capture only native window pixels. Keep destination chrome at its normal
                // size and stretch the incoming client exactly into the outgoing client.
                bool exchanging = seat != source && area.IntersectsWith(seat.Client);
                bool needsFrame = !exchanging || !seat.Client.Contains(area);
                if (needsFrame && !CopyPixels(seat.Game, area, memory, area)) return false;
                if (exchanging && !CopyPixels(source.Game, source.Client, memory, seat.Client) &&
                    !CopyPixels(seat.Game, area, memory, area)) return false;
                var origin = Point.Empty;
                var location = area.Location;
                var size = area.Size;
                return UpdateLayeredWindow(Handle, screen, ref location, ref size, memory, ref origin, 0, 0, 4); // ULW_OPAQUE
            }
            finally
            {
                if (previous != 0 && previous != -1) SelectObject(memory, previous);
                if (bitmap != 0) DeleteObject(bitmap);
                if (memory != 0) DeleteDC(memory);
                ReleaseDC(0, screen);
            }
        }

        private bool CopyPixels(nint game, Rectangle from, nint memory, Rectangle to)
        {
            if (!GameViewPlacements.TryBounds(game, out var frame) || !frame.Contains(from)) return false;
            var native = GetWindowDC(game);
            if (native == 0) return false;
            try
            {
                // Paint in whole-client coordinates, clipped by this picture's bitmap, so
                // fragments on either side of a Team Bar overlay align to the same pixel grid.
                int x = to.X - area.X, y = to.Y - area.Y;
                int sx = from.X - frame.X, sy = from.Y - frame.Y;
                if (to.Size == from.Size)
                    return BitBlt(memory, x, y, to.Width, to.Height, native, sx, sy, 0x00CC0020);
                SetStretchBltMode(memory, 3); // COLORONCOLOR, preserve UI pixel colors.
                return StretchBlt(memory, x, y, to.Width, to.Height, native, sx, sy, from.Width, from.Height, 0x00CC0020);
            }
            finally { ReleaseDC(game, native); }
        }

        protected override void WndProc(ref Message m)
        {
            if (seat != source)
            {
                if (m.Msg == 0x84) { m.Result = 1; return; } // WM_NCHITTEST: HTCLIENT
                if (m.Msg == 0x21) { m.Result = 4; return; } // WM_MOUSEACTIVATE: MA_NOACTIVATEANDEAT
                if (m.Msg is >= 0x200 and <= 0x20e)
                {
                    m.Result = m.Msg is >= 0x20b and <= 0x20d ? 1 : 0;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        internal void Reveal(nint lowestDecoration)
        {
            nint after = lowestDecoration != 0 ? lowestDecoration : -1;
            if (holdReference)
            {
                // Keep Settings, buttons and other apps above the reference. A new
                // ordinary window above the games can also cover the held picture.
                bool pinned = GameViewStacking.IsTopmost(seat.Game);
                nint previous = GetWindow(seat.Game, 3); // GW_HWNDPREV
                while (previous != 0 && !IsWindowVisible(previous)) previous = GetWindow(previous, 3);
                after = previous != 0 && GameViewStacking.IsTopmost(previous) == pinned ? previous : pinned ? -1 : 0;
            }
            Shown = Native.SetWindowPos(Handle, after, 0, 0, 0, 0,
                0x0003 | 0x0010 | 0x0040 | 0x0200); // NOMOVE | NOSIZE | NOACTIVATE | SHOWWINDOW | NOOWNERZORDER
        }

        internal void Conceal()
        {
            if (!Shown) return;
            ShowWindow(Handle, 0); // SW_HIDE, remove the input shield as well as the picture.
            Shown = false;
        }

        public void Dispose()
        {
            if (Handle != 0) DestroyHandle();
        }
    }

    private delegate bool EnumProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint screen, ref Point location,
        ref Size size, nint source, ref Point origin, uint colorKey, nint blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint screen);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint screen, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int width, int height,
        nint source, int srcX, int srcY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dst, int x, int y, int width, int height,
        nint source, int srcX, int srcY, int srcWidth, int srcHeight, uint operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attr, out int value, int length);
}
