using System.Runtime.InteropServices;

namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private sealed class PromotionTrace : NativeWindow
    {
        internal readonly List<(string Display, Size Client)> Maxima = [];
        internal Rectangle? OnlyMaximizedFrame;
        internal int RejectedPlacements;
        internal readonly List<uint> PositionChanges = [];

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SystemCommand && (m.WParam.ToInt64() & 0xFFF0) == MaximizeCommand)
                Maxima.Add((Screen.FromHandle(Handle).DeviceName, GameViewWindows.SourceSize(Handle, Environment.ProcessId)));
            if (m.Msg == 0x0047) PositionChanges.Add(Marshal.PtrToStructure<WindowPos>(m.LParam).Flags);
            base.WndProc(ref m);
            if (m.Msg == 0x0047 && OnlyMaximizedFrame is { } frame && IsZoomed(Handle) &&
                GameViewPlacements.Bounds(Handle) != frame) // WM_WINDOWPOSCHANGED
            {
                RejectedPlacements++;
                Native.SetWindowPos(Handle, 0, frame.Left, frame.Top, frame.Width, frame.Height, 0x0214);
            }
        }
    }

    private static void TestCrossMonitorPromotion(Action<bool, string> check)
    {
        var source = Screen.PrimaryScreen ?? throw new InvalidOperationException("A display is required for the promotion test.");
        var other = Screen.AllScreens.FirstOrDefault(s => s.DeviceName != source.DeviceName) ??
            throw new InvalidOperationException("The cross-monitor promotion test needs two displays.");
        const string className = "_EverQuestwndclass";
        nint module = Native.GetModuleHandleW(null);
        RegisterFixtureClass(className, module);
        nint window = 0;
        try
        {
            window = CreateWindowExW(0x80, className, "Promotion fixture", 0x10CF0000, source.WorkingArea.X + 80,
                source.WorkingArea.Y + 80, 360, 240, 0, 0, module, 0);
            if (window == 0) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            var placements = new GameViewPlacements(false);
            var trace = new PromotionTrace();
            trace.AssignHandle(window);
            try
            {
                foreach (var (from, to) in new[] { (source, other), (other, source) })
                {
                    var slot = new Rectangle(from.WorkingArea.X + 40, from.WorkingArea.Y + 40, 360, 240);
                    var small = placements.PrepareSources([(window, Environment.ProcessId)], ["Fixture"], [slot])[0];
                    trace.Maxima.Clear();
                    placements.PrepareSources([(window, Environment.ProcessId)], ["Fixture"], [to.WorkingArea], window);
                    check(trace.Maxima.SequenceEqual([(to.DeviceName, small.Size)]),
                        $"Promotion {from.DeviceName} to {to.DeviceName} requests maximize on the destination at the previous client size.");
                    check(IsZoomed(window) && VisibleFrame(window) == to.WorkingArea,
                        $"The promoted fixture finishes maximized in its intended slot on {to.DeviceName}.");
                }
                var smallSlot = new Rectangle(source.WorkingArea.X + 80, source.WorkingArea.Y + 80, 480, 300);
                Rectangle Small() => placements.PrepareSources([(window, Environment.ProcessId)], ["Fixture"], [smallSlot],
                    hideTitleBars: true)[0];
                Rectangle Large(bool hideTitle = true) => placements.PrepareSources([(window, Environment.ProcessId)], ["Fixture"], [source.Bounds],
                    window, hideTitleBars: hideTitle, preserveWholeMasterHud: true)[0];
                Small();
                SendMessageW(window, SystemCommand, MaximizeCommand, 0);
                GameViewWindows.FlushComposition();
                trace.OnlyMaximizedFrame = GameViewPlacements.Bounds(window);
                var nativeClient = GameViewPlacements.ClientBounds(window, Environment.ProcessId);
                SendMessageW(window, SystemCommand, RestoreCommand, 0);
                var fitted = Large();
                check(IsZoomed(window) && fitted == nativeClient && fitted != source.Bounds && VisibleFrame(window) == nativeClient &&
                    trace.RejectedPlacements > 0 && placements.RestoredProfileFallbacks.Length == 0 &&
                    placements.NativeMaximumNotice?.Contains($"{fitted.Width} × {fitted.Height}") == true &&
                    placements.MatchesActiveSlot(window, source.Bounds),
                    "A game that refuses the full-bleed size stays genuinely maximized, clipped to its actual client, with the measured size in status.");
                int refused = trace.RejectedPlacements;
                check(Small() == smallSlot && !IsZoomed(window) && placements.NativeMaximumNotice is null,
                    "The secondary still uses its exact restored slot and drops the master size notice.");
                check(Large() == nativeClient && IsZoomed(window) && trace.RejectedPlacements == refused,
                    "The next promotion goes straight to the known native maximum without retrying a refused resize.");
                int maximizesBefore = trace.Maxima.Count;
                var crop = WindowShape(window);
                void DropCrop()
                {
                    if (SetWindowRgn(window, 0, true) == 0)
                        throw new InvalidOperationException("The fixture could not remove the maximized window's region.");
                    GameViewWindows.FlushComposition();
                    trace.PositionChanges.Clear();
                }
                DropCrop();
                placements.VerifySlotClip(window, Environment.ProcessId, "Fixture");
                check(WindowShape(window) == crop && IsZoomed(window) && VisibleFrame(window) == nativeClient &&
                    GameViewPlacements.ClientBounds(window, Environment.ProcessId) == nativeClient &&
                    trace.PositionChanges.Any(flags => (flags & 8) != 0) && placements.CaptionCoverForTest(window) == 0 &&
                    trace.Maxima.Count == maximizesBefore,
                    $"The first lost crop is reapplied without redraw, resizing, a caption cover or another maximize. Flags: {string.Join(", ", trace.PositionChanges)}.");
                DropCrop();
                var nativeRendering = ThemedFrame(window);
                placements.VerifySlotClip(window, Environment.ProcessId, "Fixture");
                nint cover = placements.CaptionCoverForTest(window);
                check(cover != 0 && IsWindow(cover) && FixtureGetWindow(cover, 4) == window &&
                    (GetWindowLongPtrW(cover, -20).ToInt64() & 0x08000000) != 0 && CaptionIsBlack(cover),
                    "A second lost crop creates a flat black, nonactivating surface owned by the game.");
                var nativeFrame = VisibleFrame(window);
                var caption = Rectangle.FromLTRB(nativeFrame.Left, nativeFrame.Top, nativeFrame.Right, nativeClient.Top);
                check(GameViewPlacements.Bounds(cover) == caption && IsZoomed(window) && GameViewPlacements.ClientBounds(window, Environment.ProcessId) == nativeClient &&
                    ThemedFrame(window) == nativeRendering && placements.MatchesActiveSlot(window, source.Bounds) &&
                    trace.Maxima.Count == maximizesBefore, "The cover fits only the caption of the genuine maximized window and preserves the client and DWM policy.");
                var closePoint = new Point(caption.Right - 12, caption.Top + caption.Height / 2);
                var gamePoint = new Point(closePoint.X, nativeClient.Top);
                var closeCoords = (nint)((ushort)closePoint.X | ((int)(ushort)closePoint.Y << 16));
                check(OwnerAt(closePoint) == cover && OwnerAt(gamePoint) == window && SendMessageW(cover, 0x0084, 0, closeCoords) == 1 &&
                    SendMessageW(cover, 0x0021, window, (nint)((0x0201 << 16) | 1)) == 4,
                    "The cover consumes activation over the native close button. The first row of game pixels hits the game itself.");
                TestCaptionStacking(placements, window, cover, source.Bounds, nativeClient, closePoint, check);
                using (var otherApp = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(closePoint.X - 24, closePoint.Y - 8, 48, 16) })
                {
                    otherApp.Show();
                    Native.SetWindowPos(otherApp.Handle, 0, 0, 0, 0, 0, 0x0213);
                    check(OwnerAt(closePoint) == otherApp.Handle, "An unrelated foreground surface can cover the fixture caption.");
                    var foreground = Native.GetForegroundWindow();
                    for (int refresh = 0; refresh < 3; refresh++)
                        placements.VerifySlotClip(window, Environment.ProcessId, "Fixture");
                    check(Large() == nativeClient && placements.CaptionCoverForTest(window) == cover && OwnerAt(closePoint) == otherApp.Handle &&
                        Native.GetForegroundWindow() == foreground && trace.RejectedPlacements == refused && trace.Maxima.Count == maximizesBefore,
                        "Rechecking a background game keeps one cover behind the unrelated surface and leaves size, focus and placement alone.");
                }
                check(Small() == smallSlot && placements.CaptionCoverForTest(window) == 0 && !IsWindow(cover),
                    "Demotion removes the owned caption cover before exposing the small slot.");
                check(Large() == nativeClient && (cover = placements.CaptionCoverForTest(window)) != 0 &&
                    VisibleFrame(window) == nativeFrame && trace.RejectedPlacements == refused,
                    "The next promotion uses the remembered cover and original client size without another refused resize.");
                Large(hideTitle: false);
                check(placements.CaptionCoverForTest(window) == 0 && !IsWindow(cover),
                    "Showing native title bars removes the cover from the active master.");
                Large();
                cover = placements.CaptionCoverForTest(window);
                check(cover != 0, "Hiding the title again restores the remembered caption cover.");
                trace.OnlyMaximizedFrame = null;
                placements.PrepareSources([], [], []);
                check(!IsWindow(cover) && placements.CaptionCoverForTest(window) == 0, "Removing the layout destroys the last caption cover.");
                check(Large() == source.Bounds && IsZoomed(window) && placements.NativeMaximumNotice is null,
                    "A game that accepts the full-bleed master still uses the entire slot with its maximized HUD.");
            }
            finally
            {
                trace.ReleaseHandle();
                placements.Restore();
            }
        }
        finally
        {
            if (window != 0) DestroyWindow(window);
            UnregisterClassW(className, module);
        }
        TestReplacingDroppedCrop(source.WorkingArea, check);
    }

    private static void TestCaptionStacking(GameViewPlacements placements, nint game, nint cover, Rectangle slot,
        Rectangle client, Point captionPoint, Action<bool, string> check)
    {
        using var button = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(slot.Left + 80, captionPoint.Y - 4, 60, 8) };
        using var taskbar = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(slot.Left + 60, slot.Bottom - 25, 120, 20) };
        taskbar.Show();
        button.Show();
        // A background test desktop cannot take physical focus. Supply only the
        // foreground identity; all band changes and hit tests still use Windows.
        nint foreground = game;
        using var stacking = new GameViewStacking(placements, controlFilter: _ => false,
            findTaskbars: () => [taskbar.Handle], foregroundWindow: () => foreground);
        var normal = new GameViewLayout("Caption fixture", GameViewKind.Grid, Screen.FromHandle(game).DeviceName, true, ["Fixture"])
            { AvoidTaskbar = true };
        GameViewStacking.Seat[] seats = [new(game, Environment.ProcessId, game, slot, button.Handle)];
        void CheckBand(bool pinned, string label)
        {
            var clientPoint = new Point(captionPoint.X, client.Top);
            check(GameViewStacking.IsTopmost(game) == pinned && GameViewStacking.IsTopmost(cover) == pinned &&
                GameViewStacking.IsTopmost(button.Handle) == pinned && FixtureGetWindow(cover, 4) == game &&
                OwnerAt(captionPoint) == cover && OwnerAt(clientPoint) == game && OwnerAt(Center(button.Bounds)) == button.Handle &&
                placements.CaptionCoverForTest(game) == cover && GameViewPlacements.ClientBounds(game, Environment.ProcessId) == client, label);
        }

        // One UI-thread fixture would normally inherit its owner's band in
        // lockstep. Reproduce the sampled cross-process state with real HWNDs.
        // Reattach the original owner before the production stacking pass.
        check(SetWindowLongPtrW(cover, -8, 0) == game, "Detached only the fixture strip to isolate its z-order change.");
        try { placements.SetPinned(game, Environment.ProcessId, true); }
        finally { SetWindowLongPtrW(cover, -8, game); }
        check(GameViewStacking.IsTopmost(game) && !GameViewStacking.IsTopmost(cover) &&
            FixtureGetWindow(cover, 4) == game && OwnerAt(captionPoint) == game,
            "Fixture reproduces the exposed native Close button with a topmost game and an owned caption cover in the ordinary band.");
        stacking.Apply(normal with { AlwaysOnTop = true }, seats, 0);
        CheckBand(true, "Always on Top repairs a stale caption band, keeping the swap button above the strip and the first game row playable.");
        stacking.Apply(normal, seats, 0);
        CheckBand(false, "Clearing Always on Top returns game, cover and swap button to the normal band in playable order.");

        var overlap = normal with { AvoidTaskbar = false };
        stacking.Apply(overlap, seats, 0);
        CheckBand(true, "Taskbar overlap temporarily pins the game, its caption and swap button together.");
        foreground = taskbar.Handle;
        stacking.Apply(overlap, seats, 0);
        CheckBand(false, "Leaving the game while taskbar overlap is enabled restores the ordinary band without uncovering the game's title.");
    }

    private static bool CaptionIsBlack(nint window)
    {
        using var sample = new Bitmap(12, 8);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.Clear(Color.Magenta);
            var dc = graphics.GetHdc();
            try { SendMessageW(window, 0x0317, dc, 0x000C); } // WM_PRINT, PRF_CLIENT | PRF_ERASEBKGND
            finally { graphics.ReleaseHdc(dc); }
        }
        return sample.GetPixel(2, 2).ToArgb() == Color.Black.ToArgb() && sample.GetPixel(10, 6).ToArgb() == Color.Black.ToArgb();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern nint FixtureGetWindow(nint window, uint command);
}
