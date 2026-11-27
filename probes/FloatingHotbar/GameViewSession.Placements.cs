using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TeamBar;

// Fit actual EQ client areas to their native slots, and recover original frames
// and placements even if setup fails halfway through or Team Bar exits unexpectedly.
internal sealed partial class GameViewPlacements
{
    private sealed record Entry(int Pid, long Started, long Window, Rectangle Original, Rectangle Parked,
        GamePosition OriginalPlacement, Point NormalOffset, SavedState? State = null, bool Positioning = false,
        SavedFrame? Frame = null, Size? OriginalClientSize = null, bool RetainedFrame = false, Rectangle? ClientClip = null,
        bool RefreshOnRestore = false, bool RestoreDefaultRegion = false, bool AddedCaption = false,
        bool ChangedPopup = false, bool OffscreenCaption = false, bool PinnedByLayout = false,
        bool SuppressedFrameRendering = false, bool NativeEqFullscreen = false, Rectangle? ParkedNormal = null,
        bool OwnsFittedMaximum = false, NativeSlotClip? SlotClip = null);
    private sealed record SavedState(uint Flags, uint Show, Point Min, Point Max);
    private sealed record SavedFrame(uint Styles, uint ExtendedStyles);
    private readonly List<Entry> entries = [];
    private readonly bool persist;
#if !TEAMBAR_PUBLIC_RELEASE
    internal static string? EditorFixtureDirectory { get; set; }
    private static string Journal => EditorFixtureDirectory is { } fixture ? Path.Combine(fixture, "game-view-recovery.json") : InstallationJournal;
#else
    private static string Journal => InstallationJournal;
#endif
    private static string InstallationJournal => Path.Combine(InstalledAppData.DataDirectory,
        ScreenInstallation.RecoveryName("game-view-recovery", ScreenInstallation.Current));

    internal GameViewPlacements(bool persist) => this.persist = persist;

    internal static bool RequiresWindowedMode(nint window) =>
        ClientSessions.IsEverQuestWindow(window) && (ReadStyle(window, styleIndex) & caption) != caption;

    // Legacy journals can own a squeezed fullscreen window. Match EQ's old
    // fullscreen signature before restoring that window to its original monitor.
    private static bool IsNativeEqFullscreen(nint window, Rectangle frame, Rectangle client) =>
        frame == client &&
        ReadStyle(window, styleIndex) == 0x14000000 && // EQ fullscreen: WS_VISIBLE | WS_CLIPSIBLINGS
        ReadRegion(window).Kind == 0 && ClientSessions.IsEverQuestWindow(window);

    internal static Rectangle? EqFullscreenDisplay(nint window, int pid)
    {
        var frame = Bounds(window);
        return IsNativeEqFullscreen(window, frame, ClientBounds(window, pid)) ? Screen.FromRectangle(frame).Bounds : null;
    }

    private static void RestoreFullscreenDisplay(nint window, int pid, string name, Rectangle display)
    {
        if (EqFullscreenDisplay(window, pid) is null || Bounds(window) == display) return;
        PositionOuter(window, name, display);
        GameViewWindows.FlushComposition();
        if (Bounds(window) != display || ClientBounds(window, pid) != display)
            throw new InvalidOperationException($"EQ could not restore {name} to its fullscreen display.");
    }

    private static bool StartedFullscreen(nint window, Entry entry) => entry.NativeEqFullscreen ||
        (entry.RetainedFrame && entry.Frame is { Styles: 0 } && entry.OriginalClientSize == entry.Original.Size &&
         ClientSessions.IsEverQuestWindow(window));

    internal bool OriginallyTopmost(nint window) =>
        GameViewStacking.IsTopmost(window) && !entries.Any(e => e.Window == window && e.PinnedByLayout);

    internal void SetPinned(nint window, int pid, bool pinned, nint? belowWhenPinned = null)
    {
        int index = entries.FindIndex(e => e.Window == window && e.Pid == pid);
        if (index < 0 || !GameViewWindows.SameOwner(window, pid))
            throw new InvalidOperationException("A game window changed while applying its stacking order.");
        var entry = entries[index];
        bool wasTop = GameViewStacking.IsTopmost(window);
        if (pinned && !wasTop)
        {
            if (!entry.PinnedByLayout)
            {
                entries[index] = entry with { PinnedByLayout = true };
                Save(); // Crash recovery must own the change before the HWND is promoted.
            }
            GameViewStacking.SetBand(window, true, belowWhenPinned);
        }
        else if (!pinned && entry.PinnedByLayout)
        {
            if (wasTop) GameViewStacking.SetBand(window, false);
            entries[index] = entry with { PinnedByLayout = false };
            Save();
        }
    }

    // WS_POPUP and WS_OVERLAPPEDWINDOW. Preserve unrelated styles, including visibility,
    // disabled state, and any z-order changes made by the player.
    private const uint popup = 0x80000000, frameStyles = popup | 0x00cf0000;
    // WS_EX_DLGMODALFRAME, WINDOWEDGE, CLIENTEDGE, STATICEDGE.
    private const uint frameExStyles = 0x00020301;
    private const int styleIndex = -16, extendedIndex = -20;
    private const uint frameChanged = 0x0020, noActivationOrOrder = 0x0010 | 0x0004 | 0x0200;

    private Entry Capture(nint h, int pid, string character, Rectangle target)
    {
        var client = ClientBounds(h, pid); // Also verifies the live game session.
        var original = Bounds(h);
        var saved = ReadPlacement(h);
        var normal = saved.Normal.Rectangle;
        var state = new SavedState(saved.Flags, saved.Show, saved.Min, saved.Max);
        var frame = new SavedFrame(ReadStyle(h, styleIndex) & frameStyles, ReadStyle(h, extendedIndex) & frameExStyles);
        if ((frame.Styles & caption) != caption && ClientSessions.IsEverQuestWindow(h))
            throw new GameViewWindowedModeException([character]);
        using var process = Process.GetProcessById(pid);
        var screen = Screen.FromRectangle(target);
        // Normal placements for tool windows already use physical screen coordinates.
        var offset = WorkOffset(h, screen);
        if (!HasDefaultRegion(h, pid, ReadRegion(h)))
            throw new InvalidOperationException($"{character}'s window shape changed before the layout was applied.");
        if (persist && saved.Show != 3 && (frame.Styles & caption) == caption &&
            GameViewLaunchGuard.Shared.TryFind(ClientSessions.LaunchSettings, original, client.Size, out int safeTop))
        {
            (original, normal) = ReachableRestoreBounds(original, normal, client, safeTop, Screen.AllScreens.Select(s => s.Bounds));
        }
        var placement = new GamePosition("Game", saved.Show == 3, normal.Left, normal.Top, normal.Right, normal.Bottom);
        // New layouts move the native frame. Older journals can still own a style, clip or DWM override.
        var entry = new Entry(pid, process.StartTime.ToUniversalTime().Ticks, h.ToInt64(), original, target,
            placement, offset, state, Positioning: true, Frame: frame, OriginalClientSize: client.Size,
            RetainedFrame: true, RestoreDefaultRegion: false);
        return entry;
    }

    internal static (Rectangle Frame, Rectangle Normal) ReachableRestoreBounds(Rectangle frame, Rectangle normal,
        Rectangle client, int safeTop, IEnumerable<Rectangle> displays)
    {
        if (!frame.Contains(client) || client.Top <= frame.Top || safeTop <= frame.Top) return (frame, normal);
        // The outer resize grips can extend past both monitor edges even when
        // the entire useful width of the caption is on this display.
        var title = Rectangle.FromLTRB(client.Left, frame.Top, client.Right, client.Top);
        var restoredTitle = new Rectangle(title.Left, safeTop, title.Width, title.Height);
        if (displays.Any(s => s.IntersectsWith(title)) || !displays.Any(s => s.Contains(restoredTitle))) return (frame, normal);
        // WINDOWPLACEMENT.Normal uses workspace coordinates. Preserve its offset from the screen-space frame.
        int dy = safeTop - frame.Top;
        frame.Offset(0, dy);
        normal.Offset(0, dy);
        return (frame, normal);
    }

    private static void PositionOuter(nint h, string character, Rectangle bounds, bool exactSize = false)
    {
        const uint skipSizeNegotiation = 0x0400; // SWP_NOSENDCHANGING; Windows still delivers the resulting WM_SIZE.
        uint flags = noActivationOrOrder | (exactSize ? skipSizeNegotiation : 0u);
        if (!Native.SetWindowPos(h, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
            throw new InvalidOperationException($"Windows could not position {character}'s native frame.");
    }

    internal static Point OriginBeforeMaximize(Size frame, Rectangle display) =>
        new(display.Left + (display.Width - frame.Width) / 2, display.Top + (display.Height - frame.Height) / 2);

    // EQ stores separate layouts for a maximized and a restored desktop window.
    // Setting a maximized window's size and position together can snap it back
    // to the monitor's work area, even when the requested slot covers the taskbar.
    private bool FitMaximized(Request request, bool batchOwnsForeground)
    {
        var (h, pid, _, character, target, _, hideTitleBars, preserveHud) = request;
        var destination = Screen.FromRectangle(target);
        bool MaximizeOnDesktop()
        {
            bool answered = false;
            void Maximize()
            {
                answered = SendSystemCommand(h, pid, 0xF030); // SC_MAXIMIZE
                if (answered && !IsZoomed(h))
                {
                    // EQ can consume the system menu command. Its normal
                    // window placement still supports a native maximize.
                    var placement = ReadPlacement(h);
                    placement.Show = 3; // SW_SHOWMAXIMIZED
                    SetWindowPlacement(h, ref placement);
                }
            }
            if (batchOwnsForeground) Maximize();
            else PreserveForeground(window => window == h, Maximize);
            if (!answered)
                throw new InvalidOperationException($"{character}'s game did not respond while switching its desktop UI.");
            return IsZoomed(h);
        }
        if (!IsZoomed(h))
        {
            // Windows maximizes on the monitor containing the restored window. Move it there at
            // its existing size first so EQ doesn't load the previous monitor's maximized UI.
            if (Screen.FromHandle(h).DeviceName != destination.DeviceName)
            {
                var client = ClientBounds(h, pid);
                var origin = OriginBeforeMaximize(Bounds(h).Size, destination.Bounds);
                const uint noSize = 0x0001;
                if (!GameViewWindows.Available(h, pid) ||
                    !Native.SetWindowPos(h, 0, origin.X, origin.Y, 0, 0, noActivationOrOrder | noSize))
                    throw new InvalidOperationException($"Windows could not move {character} to its master screen.");
                if (!GameViewWindows.Available(h, pid))
                    throw new InvalidOperationException($"{character}'s window changed while moving to its master screen.");
                // A window can reject the move or resize itself on WM_DPICHANGED. In either
                // case use the existing restored-slot fallback and its visible status.
                if (Screen.FromHandle(h).DeviceName != destination.DeviceName || ClientBounds(h, pid).Size != client.Size)
                    return false;
            }
            if (!MaximizeOnDesktop()) return false;
        }

        GameViewWindows.FlushComposition();
        bool ExactFit()
        {
            if (!IsZoomed(h) || !FitsSlot(h, pid, target, hideTitleBars)) return false;
            nativeMaxima.Remove(request);
            return true;
        }
        MaximizedFootprint? MeasureNative()
        {
            if (!preserveHud || !IsZoomed(h) || Screen.FromHandle(h).DeviceName != destination.DeviceName) return null;
            var frame = Bounds(h);
            var client = ClientBounds(h, pid);
            var painted = hideTitleBars ? client : VisibleFrame(h, frame);
            // The native frame must span the work area. A maximized flag on an
            // arbitrary small or off-screen HWND is insufficient.
            return frame.Contains(destination.WorkingArea) && target.Contains(painted) && painted.Contains(client)
                ? new(painted, client) : null;
        }
        if (ExactFit()) return true;
        var natural = MeasureNative();
        var naturalFrame = Bounds(h);
        if (natural is not null && nativeMaxima.TryGetValue(request, out var previousMaximum))
        {
            if (previousMaximum.CoverCaption && NativeMaximizedFrame(request, natural.Client) is { } framed)
                natural = framed;
            nativeMaxima[request] = natural;
            return true;
        }
        // A move to another display can change the DPI and the native frame insets.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var outer = OuterForSlot(h, pid, target, hideTitleBars);
            if (Bounds(h).Size != outer.Size)
            {
                // Size then move so a maximized game can cover the taskbar.
                const uint noChanging = 0x0400; // SWP_NOSENDCHANGING
                var work = Screen.FromRectangle(target).WorkingArea;
                int x = target.Contains(work) ? work.Left + 1 : outer.Left;
                if (!Native.SetWindowPos(h, 0, x, outer.Y, outer.Width, outer.Height, noActivationOrOrder | noChanging))
                {
                    if (natural is not null) break;
                    throw new InvalidOperationException($"Windows could not size {character}'s maximized game to its slot.");
                }
            }
            if (Bounds(h).Location != outer.Location &&
                !Native.SetWindowPos(h, 0, outer.X, outer.Y, 0, 0, noActivationOrOrder | 0x0401)) // NOSENDCHANGING | NOSIZE
            {
                if (natural is not null) break;
                throw new InvalidOperationException($"Windows could not move {character}'s maximized game to its slot.");
            }
            GameViewWindows.FlushComposition();
            if (!IsZoomed(h)) break;
            if (ExactFit()) return true;
            if (OuterForSlot(h, pid, target, hideTitleBars) == outer) break;
        }
        if (!preserveHud) return false;
        request.VerifySession();
        if (!IsZoomed(h) && !MaximizeOnDesktop()) return false;
        GameViewWindows.FlushComposition();
        if (ExactFit()) return true;
        var measured = MeasureNative();
        if (measured is null && natural is not null)
        {
            // The sizing half of the fit can succeed while the final move is
            // refused. Return to the measured genuine maximum without restoring.
            PositionOuter(h, character, naturalFrame);
            GameViewWindows.FlushComposition();
            measured = MeasureNative();
        }
        if (measured is null) return false;
        nativeMaxima[request] = measured;
        return true;
    }

    private const uint caption = 0x00C00000;
    private const uint nativeChrome = caption | 0x00040000; // WS_CAPTION | WS_THICKFRAME

    private static bool IsNativeBorderless(nint h, int pid) =>
        (ReadStyle(h, styleIndex) & nativeChrome) == 0 && Bounds(h) == ClientBounds(h, pid) &&
        HasDefaultRegion(h, pid, ReadRegion(h));

    // Only called after setup verified and recorded an originally unset region.
    // Theme-generated full-frame shapes may appear after native size/style changes.
    private static bool HasDefaultRegion(nint h, int pid, WindowRegion region) =>
        region.Kind == 0 || HasNormalFrameShape(h, Bounds(h), ClientBounds(h, pid));

    private static bool HasDecorations(SavedFrame? frame) =>
        frame is not null && ((frame.Styles & 0x00C40000) != 0 || frame.ExtendedStyles != 0); // WS_CAPTION | WS_THICKFRAME

    private static Point WorkOffset(nint h, Screen screen) => (ReadStyle(h, extendedIndex) & 0x80) != 0 ? Point.Empty :
        new Point(screen.WorkingArea.Left - screen.Bounds.Left, screen.WorkingArea.Top - screen.Bounds.Top);

    private static void RestoreForLayout(nint h, string character, Rectangle target, WindowPlacement saved, Point offset)
    {
        if (saved.Show != 3) return;
        saved.Show = 4; // SW_SHOWNOACTIVATE
        saved.Flags &= ~2u; // WPF_RESTORETOMAXIMIZED
        saved.Normal = new Rect(target.Left - offset.X, target.Top - offset.Y, target.Right - offset.X, target.Bottom - offset.Y);
        if (!SetWindowPlacement(h, ref saved) || IsZoomed(h))
            throw new InvalidOperationException($"Windows could not restore {character}. Put this game in windowed mode and apply the layout again.");
    }

    internal static Rectangle ClientBounds(nint h, int pid)
    {
        var size = GameViewWindows.SourceSize(h, pid);
        var origin = Point.Empty;
        if (!ClientToScreen(h, ref origin)) throw new InvalidOperationException("Could not measure the game client position.");
        return new Rectangle(origin, size);
    }

    // EQ can keep rendering at its former resolution even after the native client
    // has resized. A real maximize and restore makes it recreate that surface.
    private static void RefreshNormalResolution(nint h, int pid, string? hiddenTitleCharacter = null) =>
        PreserveForeground(window => window == h, () => RefreshNormalRenderer(h, pid, hiddenTitleCharacter));

    private static void RefreshNormalRenderer(nint h, int pid, string? hiddenTitleCharacter)
    {
        var expected = ClientBounds(h, pid);
        var normal = ReadPlacement(h);
        normal.Show = 4; // SW_SHOWNOACTIVATE if Windows needs the rectangle reasserted.
        normal.Flags &= ~2u;
        if (IsZoomed(h)) throw new InvalidOperationException("The game needs a normal window to refresh its rendering resolution.");
        bool restored = false;
        try
        {
            if (!SendSystemCommand(h, pid, 0xF030) || !IsZoomed(h)) // SC_MAXIMIZE
                throw new InvalidOperationException("The game did not maximize to refresh its rendering resolution.");
            GameViewWindows.SourceSize(h, pid);
            GameViewWindows.FlushComposition();
            GameViewWindows.FlushComposition();
            restored = SendSystemCommand(h, pid, 0xF120) && !IsZoomed(h); // SC_RESTORE
            if (!restored) throw new InvalidOperationException("The game did not restore its normal rendering resolution.");
            GameViewWindows.FlushComposition();
            GameViewWindows.FlushComposition();
            if (ClientBounds(h, pid) != expected &&
                (!SetWindowPlacement(h, ref normal) || ClientBounds(h, pid) != expected))
            {
                // Windows can bring a caption above the top monitor edge back
                // on-screen during SC_RESTORE. Position the fitted client once.
                if (hiddenTitleCharacter is not null && !IsZoomed(h))
                {
                    PositionOuter(h, hiddenTitleCharacter, OuterForSlot(h, pid, expected, true), exactSize: true);
                    GameViewWindows.FlushComposition();
                }
                if (ClientBounds(h, pid) != expected)
                    throw new InvalidOperationException("The game did not retain the requested client rectangle after restoring.");
            }
        }
        finally
        {
            if (!restored && GameViewWindows.SameOwner(h, pid) && IsZoomed(h))
                SetWindowPlacement(h, ref normal);
        }
    }

    private static void PreserveForeground(Func<nint, bool> belongsToResize, Action change)
    {
        nint foreground = Native.GetForegroundWindow();
        var input = LastInputTick();
        try { change(); }
        finally
        {
            // A maximize system command can activate the resized game even on a
            // background login. Preserve the previous foreground unless the user
            // has supplied fresh input or Windows has already focused another app.
            nint active = Native.GetForegroundWindow();
            if (foreground != 0 && foreground != active && belongsToResize(active) &&
                input.HasValue && LastInputTick() == input) GameViewWindows.TryFocus(foreground);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInput { internal uint Size, Tick; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    internal static uint? LastInputTick()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? input.Tick : null;
    }

    private static bool SendSystemCommand(nint h, int pid, nuint command) => GameViewWindows.SameOwner(h, pid) &&
        SendMessageTimeoutW(h, 0x0112, command, 0, 0x0002 | 0x0020, 5000, out _) != 0; // WM_SYSCOMMAND

    private readonly record struct WindowRegion(int CopyKind, int Kind, Rectangle Box)
    {
        internal bool Matches(Rectangle clip) => Kind == 2 && Box == clip; // SIMPLEREGION
        public override string ToString() => $"copy={CopyKind}, kind={Kind}, box={Box}";
    }

    private static WindowRegion ReadRegion(nint h)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new InvalidOperationException("Windows could not inspect the game window shape.");
        try
        {
            int copied = GetWindowRgn(h, region); // ERROR (0) is returned for a window without an explicit region.
            if (copied == 0) return new(0, 0, Rectangle.Empty);
            int kind = GetRgnBox(region, out var box);
            if (kind == 0) throw new InvalidOperationException("Windows could not measure the game window shape.");
            return new(copied, kind, box.Rectangle);
        }
        finally { DeleteObject(region); }
    }

    // Window themes can assign a region when a caption is put back. Accept only
    // an uninterrupted full frame with, at most, symmetric rounded corner cuts.
    private static bool HasNormalFrameShape(nint h, Rectangle frame, Rectangle client)
    {
        nint rgn = CreateRectRgn(0, 0, 0, 0);
        if (rgn == 0) throw new InvalidOperationException("Windows could not inspect the restored frame.");
        try
        {
            if (GetWindowRgn(h, rgn) == 0 || GetRgnBox(rgn, out var box) == 0 ||
                box.Rectangle != new Rectangle(Point.Empty, frame.Size)) return false;
            using var shape = Region.FromHrgn(rgn);
            using var identity = new System.Drawing.Drawing2D.Matrix();
            // A rounded rectangle has one horizontal span per band. A notch in
            // an edge, a missing interior pixel or an asymmetric cut is custom.
            int limit = Math.Min(Math.Max(1, (client.Top - frame.Top + 1) / 2), Math.Min(frame.Width, frame.Height) / 4);
            uint bytes = GetRegionData(rgn, 0, 0);
            if (bytes == 0 || bytes > 32 + 16 * (2 * limit + 1)) return false;
            float bottom = 0, priorLeft = 0, priorRight = 0;
            foreach (var band in shape.GetRegionScans(identity))
            {
                float left = band.Left, right = frame.Width - band.Right;
                if (band.Top != bottom || band.Height <= 0 || band.Bottom > frame.Height ||
                    Math.Abs(left - right) > 1 || Math.Max(left, right) > limit) return false;
                if (left != 0 || right != 0)
                {
                    if (band.Bottom <= limit)
                    {
                        if (bottom != 0 && (left > priorLeft || right > priorRight)) return false;
                    }
                    else if (band.Top < frame.Height - limit || left < priorLeft || right < priorRight) return false;
                }
                bottom = band.Bottom;
                priorLeft = left;
                priorRight = right;
            }
            return bottom == frame.Height;
        }
        finally { DeleteObject(rgn); }
    }

    // SetWindowPos produces WM_SIZE but does not run the interactive sizing loop.
    // Renderers can defer their backbuffer reset until that loop's closing notice.
    private static void WithResizeNotifications(nint h, int pid, bool resized, bool restoring, Action update)
    {
        if (!resized) { update(); return; }
        if (!GameViewWindows.SameOwner(h, pid)) throw new InvalidOperationException("The game window closed before the resize.");
        bool measured = GetClientRect(h, out var before);
        bool updated = false;
        try
        {
            if (!SendSizingNotice(h, 0x0231) && !restoring) // WM_ENTERSIZEMOVE
                throw new InvalidOperationException("The game did not respond to the start of the window resize. Try again once it is responsive.");
            update();
            updated = true;
        }
        finally
        {
            // A timed-out handler can still have received the opening message.
            // Balance it after failed and successful changes. Restoration always
            // gets to return the frame even if a renderer ignores these notices.
            if (GameViewWindows.SameOwner(h, pid))
            {
                // A real resolution change can rebuild EQ's render surface inside
                // WM_EXITSIZEMOVE. Send it once and let the renderer finish before
                // validating or rolling back. Plain moves keep the short timeout.
                bool changedSize = measured && GetClientRect(h, out var after) && before.Rectangle.Size != after.Rectangle.Size;
                if (!SendSizingNotice(h, 0x0232, changedSize ? 5000u : 1000u) && updated && !restoring)
                    throw new InvalidOperationException("The game did not respond to the end of the window resize. Try again once it is responsive.");
            }
        }
    }

    private static bool SendSizingNotice(nint h, uint message, uint timeout = 1000) =>
        SendMessageTimeoutW(h, message, 0, 0, 0x0002 | 0x0020, timeout, out _) != 0; // ABORTIFHUNG | ERRORONEXIT

    private static uint ReadStyle(nint h, int index)
    {
        nint value = GetWindowLongPtrW(h, index);
        if (value == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new InvalidOperationException("Windows could not read the game window frame.");
        return unchecked((uint)value.ToInt64());
    }

    private static bool ChangeStyle(nint h, int index, uint mask, uint wanted)
    {
        uint current = ReadStyle(h, index), next = (current & ~mask) | (wanted & mask);
        if (next == current) return false;
        if (SetWindowLongPtrW(h, index, unchecked((nint)(nuint)next)) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new InvalidOperationException("Windows could not change the game window frame.");
        return true;
    }

    private const uint maximizedStyle = 0x01000000;
    // Older layout journals can own a maximized style bit that must be released on restore.
    private static void ClearFittedMaximum(nint h)
    {
        if (ChangeStyle(h, styleIndex, maximizedStyle, 0u) &&
            !Native.SetWindowPos(h, 0, 0, 0, 0, 0, frameChanged | noActivationOrOrder | 0x0003))
            throw new InvalidOperationException("Windows could not refresh the fitted game window's frame.");
    }

    private static void RestoreFrame(nint h, SavedFrame? frame, bool addedCaption, bool changedPopup)
    {
        if (frame is null) return; // Journals from builds that only moved windows.
        // Restore only those style bits this session actually changed.
        uint changedStyles = addedCaption ? frameStyles : ((frame.Styles ^ popup) & frameStyles) | (changedPopup ? popup : 0u);
        bool changed = ChangeStyle(h, styleIndex, changedStyles, frame.Styles);
        changed |= ChangeStyle(h, extendedIndex, frame.ExtendedStyles & frameExStyles, frame.ExtendedStyles);
        const uint keepPositionAndSize = 0x0001 | 0x0002;
        if (changed && !Native.SetWindowPos(h, 0, 0, 0, 0, 0, frameChanged | noActivationOrOrder | keepPositionAndSize))
            throw new InvalidOperationException("Windows could not restore the game window frame.");
    }

    internal static void RecoverPending()
    {
        if (!File.Exists(Journal)) return;
        Entry[] saved;
        using (var stream = File.OpenRead(Journal))
        {
            if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("The layout recovery file is too large.");
            saved = JsonSerializer.Deserialize<Entry[]>(stream, Program.Json)
                ?? throw new InvalidDataException("The layout recovery file is empty.");
        }
        if (saved.Any(e => e is null || e.OriginalPlacement is null || e.Window == 0 || e.Pid <= 0
            || e.Original.Width <= 0 || e.Original.Height <= 0 || e.Parked.Width <= 0 || e.Parked.Height <= 0))
            throw new InvalidDataException("The layout recovery file contains an invalid game window.");
        var recovery = new GameViewPlacements(true);
        recovery.entries.AddRange(saved);
        recovery.Restore();
    }

    internal void Restore()
    {
        Exception? error = null;
        foreach (var entry in entries.ToArray())
        {
            try
            {
                RestoreOne(entry);
                entries.RemoveAll(e => e.Window == entry.Window && e.Pid == entry.Pid && e.Started == entry.Started);
                current.Remove((nint)entry.Window);
            }
            catch (Exception ex) { error ??= ex; }
        }
        Save();
        if (error is not null) throw new InvalidOperationException("The layout could not restore every game window.", error);
    }

    private void RestoreOne(Entry entry)
    {
        nint h = (nint)entry.Window;
        ReleaseCaptionCover(h);
        if (!GameViewWindows.SameOwner(h, entry.Pid)) return;
        using var process = Process.GetProcessById(entry.Pid);
        if (process.StartTime.ToUniversalTime().Ticks != entry.Started) return;
        if (persist)
        {
            var actual = ClientSessions.ReadScreenWindow(entry.Pid);
            if (actual.Window != entry.Window || actual.StartedUtcTicks != entry.Started) return;
        }
        if (entry.SlotClip is not null)
        {
            int index = entries.IndexOf(entry);
            ReleaseSlotClip(h, index);
            entry = entries[index];
        }
        if (entry.PinnedByLayout && GameViewStacking.IsTopmost(h)) GameViewStacking.SetBand(h, false);
        bool minimized = GameViewWindows.Minimized(h);
        bool keepUserMove = !minimized && !entry.Positioning && Bounds(h) != entry.Parked;
        if (entry.SuppressedFrameRendering) RestoreSystemFrameRendering(h);
        if (entry.RetainedFrame && ClientSessions.IsEverQuestWindow(h))
        {
            bool wasFullscreen = StartedFullscreen(h, entry);
            if (RequiresWindowedMode(h))
            {
                // Only the older fullscreen-capable layouts could own a fullscreen
                // size. For a mode switch during a windowed layout, EQ owns its frame.
                if (wasFullscreen && !minimized && !keepUserMove)
                    RestoreFullscreenDisplay(h, entry.Pid, entry.OriginalPlacement.Character, Screen.FromRectangle(entry.Original).Bounds);
                return;
            }
            if (wasFullscreen) return; // EQ has switched to its windowed placement.
        }
        // An earlier opted-in layout may have been interrupted with this native
        // state bit still set. Its original desktop mode is restored below.
        if (entry.OwnsFittedMaximum) ClearFittedMaximum(h);
        if (!minimized && entry.OriginalClientSize == entry.Original.Size && entry.ClientClip is null && entry.Frame is { } originalFrame &&
            (originalFrame.Styles & nativeChrome) == 0 && entry.State is { } state && Bounds(h) == entry.Original &&
            IsNativeBorderless(h, entry.Pid) && ReadRegion(h).Kind == 0 &&
            (ReadStyle(h, styleIndex) & frameStyles) == originalFrame.Styles &&
            (ReadStyle(h, extendedIndex) & frameExStyles) == originalFrame.ExtendedStyles)
        {
            var placement = ReadPlacement(h);
            var normal = entry.OriginalPlacement;
            if (placement.Show == state.Show && placement.Flags == state.Flags && placement.Min == state.Min &&
                placement.Max == state.Max && placement.Normal.Rectangle == Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom))
                return; // EQ already owns these exact borderless bounds and placement.
        }
        bool resizing = !minimized && RestoresClientSize(h, entry, keepUserMove);
        bool refreshNormal = !minimized && !keepUserMove && HasDecorations(entry.Frame) && !entry.OriginalPlacement.Maximized &&
            (!entry.RetainedFrame || (ReadStyle(h, styleIndex) & caption) == caption) &&
            (resizing || entry.RefreshOnRestore || entry.OwnsFittedMaximum || !entry.RetainedFrame);
        if (refreshNormal && !entry.RefreshOnRestore)
        {
            // A crash between the maximize and restore during End must still own
            // this temporary state, even after the outer frame leaves its parked slot.
            int index = entries.IndexOf(entry);
            entry = entry with { Positioning = true, RefreshOnRestore = true,
                RestoreDefaultRegion = entry.RestoreDefaultRegion || (!entry.RetainedFrame && ReadRegion(h).Kind == 0) };
            entries[index] = entry;
            Save();
        }
        WithResizeNotifications(h, entry.Pid, resizing, restoring: true, () => RestorePlacement(h, entry, minimized, keepUserMove));
        if (refreshNormal) RefreshNormalResolution(h, entry.Pid);
        if (!minimized && !keepUserMove && entry.RestoreDefaultRegion && Bounds(h) == entry.Original &&
            HasNormalFrameShape(h, entry.Original, ClientBounds(h, entry.Pid)))
        {
            if (SetWindowRgn(h, 0, true) == 0)
                throw new InvalidOperationException("Windows could not remove the temporary frame shape.");
            GameViewWindows.FlushComposition();
            if (ReadRegion(h).Kind != 0 && HasNormalFrameShape(h, entry.Original, ClientBounds(h, entry.Pid)))
                throw new InvalidOperationException("Windows retained the temporary frame shape.");
        }
    }

    private static bool RestoresClientSize(nint h, Entry entry, bool keepUserMove)
    {
        if (!keepUserMove && entry.OriginalClientSize is { } size && GetClientRect(h, out var current))
            return size != current.Rectangle.Size;
        // Older journals only recorded the outer frame. Restoring its decorations
        // can change the size of the client even when we keep a user-moved frame.
        bool framed = !entry.RetainedFrame && entry.Frame is { } f && (f.Styles != popup || f.ExtendedStyles != 0);
        return framed || (!keepUserMove && Bounds(h).Size != entry.Original.Size);
    }

    private static void RestorePlacement(nint h, Entry entry, bool minimized, bool keepUserMove)
    {
        // Each source started with no explicit region. Only remove the rectangle
        // installed by this session; preserve a new shape set by another owner.
        if (entry.ClientClip is { } clip && ReadRegion(h).Matches(clip) && SetWindowRgn(h, 0, true) == 0)
            throw new InvalidOperationException("Windows could not restore the game's original window shape.");
        // Once setup has committed, a user move wins. During either synchronous
        // placement call, Windows can clamp the requested bounds before a crash;
        // the journal still owns that transient placement for this exact session.
        if (minimized)
        {
            var current = ReadPlacement(h);
            var normal = current.Normal.Rectangle;
            normal.Offset(entry.NormalOffset);
            if (!entry.RetainedFrame) RestoreFrame(h, entry.Frame, entry.AddedCaption, entry.ChangedPopup);
            if (!entry.Positioning && normal != (entry.ParkedNormal ?? entry.Parked)) return;
            current.Normal = new Rect(entry.OriginalPlacement.Left, entry.OriginalPlacement.Top,
                entry.OriginalPlacement.Right, entry.OriginalPlacement.Bottom);
            if (entry.State is { } state)
            {
                current.Max = state.Max;
                current.Flags = (current.Flags & ~2u) | (entry.OriginalPlacement.Maximized ? 2u : 0u);
            }
            if (!SetWindowPlacement(h, ref current)) throw new InvalidOperationException("Could not restore the minimized game position.");
            return;
        }
        if (!entry.RetainedFrame) RestoreFrame(h, entry.Frame, entry.AddedCaption, entry.ChangedPopup);
        if (keepUserMove) return;
        if (entry.State is not { } originalState)
        {
            WindowLayouts.Restore(h, entry.OriginalPlacement); // Existing journals predate full placement state.
            return;
        }
        var p = entry.OriginalPlacement;
        var restore = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>(), Flags = originalState.Flags,
            Show = originalState.Show == 1 ? 4u : originalState.Show, Min = originalState.Min, Max = originalState.Max,
            Normal = new Rect(p.Left, p.Top, p.Right, p.Bottom) };
        if (!SetWindowPlacement(h, ref restore)) throw new InvalidOperationException("Could not restore the original game position.");
    }

    private void Save()
    {
        if (!persist) return;
        var path = Journal;
        if (entries.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        WindowLayouts.SaveJson(path, entries.ToArray());
    }

    internal static bool TryBounds(nint h, out Rectangle result)
    {
        bool found = GetWindowRect(h, out var bounds);
        result = bounds.Rectangle;
        return found;
    }
    internal static Rectangle Bounds(nint h) => TryBounds(h, out var bounds) ? bounds :
        throw new InvalidOperationException("Could not read the original game position.");
    internal static Rectangle VisibleFrame(nint h, Rectangle outer)
    {
        const int extendedFrameBounds = 9;
        var visible = outer;
        if (DwmGetWindowAttribute(h, extendedFrameBounds, out var bounds, Marshal.SizeOf<Rect>()) == 0 &&
            bounds.Rectangle is { Width: > 0, Height: > 0 } painted && outer.Contains(painted))
            visible = painted;
        // With a window region DWM can report the whole outer rectangle again.
        if (GetWindowRgnBox(h, out var regionBox) is 2 or 3)
        {
            var clip = regionBox.Rectangle;
            clip.Offset(outer.Location);
            visible = Rectangle.Intersect(visible, clip);
        }
        return visible;
    }

    // SetWindowPos takes outer bounds, including invisible resize handles. The
    // player sees DWM's frame, whose insets vary with the theme, DPI and window state.
    private static Rectangle OuterForSlot(nint h, int pid, Rectangle slot, bool hideTitleBars)
    {
        var outer = Bounds(h);
        var visible = hideTitleBars ? ClientBounds(h, pid) : VisibleFrame(h, outer);
        return Rectangle.FromLTRB(slot.Left + (outer.Left - visible.Left), slot.Top + (outer.Top - visible.Top),
            slot.Right + (outer.Right - visible.Right), slot.Bottom + (outer.Bottom - visible.Bottom));
    }

    internal static bool OccupiesSlot(nint h, Rectangle slot) => VisibleFrame(h, Bounds(h)) == slot;
    private static bool FitsSlot(nint h, int pid, Rectangle slot, bool hideTitleBars) =>
        hideTitleBars ? ClientBounds(h, pid) == slot : OccupiesSlot(h, slot);
    private static WindowPlacement ReadPlacement(nint h)
    {
        var result = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(h, ref result)) throw new InvalidOperationException("Could not read the game window placement.");
        return result;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        internal int Left = left, Top = top, Right = right, Bottom = bottom;
        internal readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    { internal uint Length, Flags, Show; internal Point Min, Max; internal Rect Normal; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint h, int property, out Rect result, int bytes);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out Rect result);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint h, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint h, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Rect box);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(nint region, uint bytes, nint data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint h, uint message, nuint wparam, nint lparam,
        uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint h, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint h);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint h, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint h, ref WindowPlacement placement);
}
