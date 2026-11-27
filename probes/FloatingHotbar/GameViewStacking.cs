using System.Runtime.InteropServices;

namespace TeamBar;

// Only alter the layout's windows and Team Bar's own UI. Explorer's taskbars
// remain available when another application becomes active or the layout ends.
internal sealed class GameViewStacking(GameViewPlacements placements, Func<nint, bool>? controlFilter = null,
    Func<nint[]>? findTaskbars = null, Func<nint>? foregroundWindow = null) : IDisposable
{
    internal readonly record struct Seat(nint Source, int Pid, nint Surface, Rectangle Slot, nint Button = 0);
    private const string controlProperty = "TeamBar.ScreenLayout.OriginalBand";
    private const uint keepGeometryAndFocus = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private readonly HashSet<nint> protectedControls = [];

    internal static bool IsTopmost(nint window) => (GetWindowLongPtrW(window, -20).ToInt64() & 8) != 0;
    internal static void SetBand(nint window, bool pinned, nint? belowWhenPinned = null)
    {
        OrderAfter(window, pinned ? belowWhenPinned ?? -1 : -2);
        if (pinned) return;
        // HWND_NOTOPMOST places the game at the very top of the ordinary
        // band. An app just activated by Alt+Tab must stay above that game.
        var foreground = Native.GetForegroundWindow();
        if (foreground != 0 && foreground != window && !IsTopmost(foreground) &&
            GetAncestor(window, 3) != foreground && Above(WindowOrder(), window, foreground))
            OrderAfter(window, foreground);
    }

    private static void OrderAfter(nint window, nint after)
    {
        if (!Native.SetWindowPos(window, after, 0, 0, 0, 0, keepGeometryAndFocus))
            throw new InvalidOperationException("Windows could not apply the layout's window stacking order.");
    }

    private static List<nint> WindowOrder()
    {
        var order = new List<nint>();
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && !GameViewWindows.Minimized(window)) order.Add(window);
            return true;
        }, 0);
        return order;
    }

    private static nint[] Taskbars()
    {
        var result = new List<nint>();
        foreach (var name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            nint window = 0;
            while ((window = FindWindowExW(0, window, name, null)) != 0) result.Add(window);
        }
        return result.ToArray();
    }

    internal void Apply(GameViewLayout layout, Seat[] seats, nint parkingWindow)
    {
        var order = WindowOrder();
        var games = seats.Select(s => s.Source).ToHashSet();
        var controls = order.Where(w => !games.Contains(w) && GameViewWindows.SameOwner(w, Environment.ProcessId) &&
            Control.FromHandle(w) is not (GameViewTile or GameViewSwapButton or GameViewCaptionCover) && !GameViewSwapCover.IsCover(w) &&
            (controlFilter?.Invoke(w) ?? true)).ToArray();
        var foreground = foregroundWindow?.Invoke() ?? Native.GetForegroundWindow();
        bool playing = games.Contains(GetAncestor(foreground, 3)) || games.Contains(foreground) || controls.Contains(foreground);
        var bars = new Dictionary<nint, Rectangle>();
        if (!layout.AvoidTaskbar && playing)
            foreach (var bar in findTaskbars?.Invoke() ?? Taskbars())
                if (order.Contains(bar) && GameViewPlacements.TryBounds(bar, out var bounds)) bars[bar] = bounds;
        bool parkedTopmost = parkingWindow != 0 && seats.Any(s => s.Source != s.Surface && placements.OriginallyTopmost(s.Source));
        bool changed = false;
        foreach (var seat in seats)
        {
            bool preview = seat.Surface != seat.Source;
            bool pinned = layout.AlwaysOnTop || bars.Values.Any(seat.Slot.IntersectsWith) ||
                (parkingWindow == seat.Source && parkedTopmost) || (parkingWindow == 0 && preview && placements.OriginallyTopmost(seat.Source));
            bool nativeWasTop = IsTopmost(seat.Source);
            placements.SetPinned(seat.Source, seat.Pid, pinned && !preview);
            changed |= nativeWasTop != IsTopmost(seat.Source);
            if (preview && IsTopmost(seat.Surface) != pinned)
            {
                SetBand(seat.Surface, pinned);
                changed = true;
            }
        }

        if (changed) order = WindowOrder();
        foreach (var seat in seats)
        {
            // Each native window stays behind the surface displaying its slot.
            // Master-resolution previews share a parking window when the master is occupied.
            nint cover = seat.Source != seat.Surface && parkingWindow != 0 ? parkingWindow : seat.Surface;
            if (seat.Source != cover && Above(order, seat.Source, cover))
            {
                OrderAfter(seat.Source, cover);
                order = WindowOrder();
            }
            if (bars.Any(bar => seat.Slot.IntersectsWith(bar.Value) && Above(order, bar.Key, seat.Surface)))
            {
                SetBand(seat.Surface, true);
                order = WindowOrder();
            }
        }

        var pinnedSurfaces = seats.Where(s => IsTopmost(s.Surface)).Select(s => s.Surface).ToArray();
        if (pinnedSurfaces.Length == 0)
        {
            if (protectedControls.Count > 0) RememberControls(controls);
            ReleaseControls();
        }
        else ProtectControls(controls, pinnedSurfaces, order);
        PositionSlotControls(seats);
    }

    private void PositionSlotControls(Seat[] seats)
    {
        foreach (var seat in seats)
        {
            // Owner and popup may belong to different processes. Reconcile the
            // caption after pinning the game and protecting Settings and overlays.
            nint anchor = placements.PositionCaptionCover(seat.Surface);
            nint button = seat.Button;
            if (button == 0 || !IsWindowVisible(button)) continue;
            bool pinned = IsTopmost(seat.Surface);
            if (IsTopmost(button) != pinned) SetBand(button, pinned);
            // Put the button immediately above its game, caption cover or preview. Windows
            // above that surface (including Settings and unrelated apps) win.
            nint previous = GetWindow(anchor, 3); // GW_HWNDPREV
            if (previous == button) continue;
            nint after = previous != 0 && IsTopmost(previous) == pinned ? previous : (pinned ? -1 : 0);
            OrderAfter(button, after);
        }
    }

    private static bool Above(List<nint> order, nint a, nint b)
    {
        int aIndex = order.IndexOf(a), bIndex = order.IndexOf(b);
        return aIndex >= 0 && bIndex >= 0 && aIndex < bIndex;
    }

    internal bool StageFixedClick(nint source, int pid, nint reference)
    {
        if (source == reference || Native.GetForegroundWindow() != source ||
            !GameViewWindows.Available(source, pid)) return false;

        var order = WindowOrder();
        int index = order.IndexOf(reference);
        if (index < 0 || !order.Contains(source)) return false;
        bool pinned = IsTopmost(reference);
        if (!pinned && placements.OriginallyTopmost(source)) return false;
        bool wasPinned = IsTopmost(source);
        bool adjacent = index > 0 && order[index - 1] == source;
        // Insert beneath the window preceding the reference, excluding the
        // source itself. Inserting after a topmost HWND selects its band.
        int preceding = index - (adjacent ? 2 : 1);
        nint previous = preceding >= 0 ? order[preceding] : 0;
        nint after = previous != 0 && IsTopmost(previous) == pinned ? previous : (pinned ? -1 : 0);
        // Journal a temporary promotion and go directly under the cover in one
        // SetWindowPos. Raising to HWND_TOPMOST first could flash over the picture.
        placements.SetPinned(source, pid, pinned, after);
        bool promoted = pinned && !wasPinned;
        if (!promoted && (!adjacent || pinned != wasPinned)) OrderAfter(source, after);
        order = WindowOrder();
        index = order.IndexOf(reference);
        return index > 0 && order[index - 1] == source && IsTopmost(source) == pinned &&
            IsTopmost(reference) == pinned && Native.GetForegroundWindow() == source;
    }

    private void ProtectControls(nint[] controls, nint[] surfaces, List<nint> order)
    {
        bool reorder = controls.Any(w => !IsTopmost(w) || surfaces.Any(s => Above(order, s, w)));
        if (reorder || protectedControls.Count > 0) RememberControls(controls);
        if (reorder)
            foreach (var window in controls.Reverse()) SetControlBand(window, true);
    }

    private void RememberControls(nint[] controls)
    {
        protectedControls.RemoveWhere(w => !HasControlMarker(w));
        // Capture before raising anything: Windows can promote owned dialogs
        // together with their owner. A property dies with the HWND, so a reused
        // handle never inherits a closed window's saved state.
        foreach (var window in controls)
        {
            if (!GameViewWindows.SameOwner(window, Environment.ProcessId) || GetPropW(window, controlProperty) != 0) continue;
            bool originallyTopmost = IsTopmost(window);
            if (originallyTopmost && GetPropW(GetWindow(window, 4), controlProperty) == 1) // GW_OWNER
            {
                // A newly opened owned dialog can inherit its parent's temporary
                // pin. An explicitly topmost wizard or overlay keeps its own pin.
                originallyTopmost = Control.FromHandle(window) is Form form ? form.TopMost :
                    System.Windows.Interop.HwndSource.FromHwnd(window)?.RootVisual is System.Windows.Window w && w.Topmost;
            }
            if (!SetPropW(window, controlProperty, originallyTopmost ? 2 : 1))
            {
                if (!GameViewWindows.SameOwner(window, Environment.ProcessId) || !IsWindowVisible(window)) continue;
                throw new InvalidOperationException("Windows could not save the stacking order of a Team Bar window.");
            }
            protectedControls.Add(window);
        }
    }

    private static bool HasControlMarker(nint window) => GameViewWindows.SameOwner(window, Environment.ProcessId) &&
        GetPropW(window, controlProperty) != 0;

    private static void SetControlBand(nint window, bool pinned)
    {
        if (!HasControlMarker(window)) return;
        try { SetBand(window, pinned); }
        catch (InvalidOperationException) when (!HasControlMarker(window) || !IsWindowVisible(window)) { }
    }

    private void ReleaseControls()
    {
        if (protectedControls.Count == 0) return;
        var live = protectedControls.Where(w => GameViewWindows.SameOwner(w, Environment.ProcessId))
            .Select(w => (Window: w, Original: GetPropW(w, controlProperty))).Where(c => c.Original != 0).ToArray();
        // Demoting an owner can demote its dialogs too. Restore intrinsically
        // topmost controls last, including pre-existing spell and hotkey bars.
        foreach (var c in live.OrderBy(c => c.Original.ToInt64()))
            if (IsTopmost(c.Window) != (c.Original == 2)) SetControlBand(c.Window, c.Original == 2);
        foreach (var c in live) RemovePropW(c.Window, controlProperty);
        protectedControls.Clear();
    }

    public void Dispose() => ReleaseControls();

    private delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetPropW(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemovePropW(nint window, string name);
}
