using System.Runtime.InteropServices;

namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestSmoothSwap(Rectangle area, Action<bool, string> check)
    {
        TestForegroundSwapAfterCropLoss(area, check);
        TestHeldReferencePicture(area, check);
        Rectangle[] slots = [new(area.Left, area.Top, 430, 310), new(area.Left + 430, area.Top, 250, 150),
            new(area.Left + 430, area.Top + 150, 250, 160)];
        var layout = new GameViewLayout("Smooth swap", GameViewKind.MainStrip, "fixture", true, ["Master", "Selected", "Other"])
            { MatchMasterResolution = false, MasterSlot = 0, AlwaysOnTop = true };
        using var master = new Source(Color.IndianRed, area.Location, framed: true);
        using var selected = new Source(Color.SteelBlue, area.Location, framed: true) { PaintHudMarker = true };
        using var other = new Source(Color.DarkGreen, area.Location);
        Source[] games = [master, selected, other];
        foreach (var game in games) game.Open();
        AcquireFixtureFocus(master);
        nint[] handles = games.Select(g => g.Handle).ToArray();
        using var session = GameViewSession.StartForTest(layout, slots, handles);
        var before = session.SurfaceHandles;
        bool Pictures(Source[] expected) => slots.Select((slot, i) =>
            ColorVisible(session.SurfaceHandles[i], expected[i].BackColor, Center(slot))).All(v => v);
        PumpUntil(() => Pictures(games), "Each slot must show its original character before swapping.");
        other.ResizeTrace = [];

        // Make both renderers draw black at a known point inside native sizing.
        // Sampling from this callback observes the actual compositor during the swap.
        var expected = new[] { selected.BackColor, master.BackColor, other.BackColor };
        var samples = new List<Color[]>();
        var guarded = new List<bool>();
        var masterClient = master.RectangleToScreen(master.ClientRectangle);
        var smallClient = selected.RectangleToScreen(selected.ClientRectangle);
        var smallHud = Center(HudMarker(smallClient.Size));
        var heldHud = new Point(masterClient.Left + smallHud.X * masterClient.Width / smallClient.Width,
            masterClient.Top + smallHud.Y * masterClient.Height / smallClient.Height);
        // Near both title-bar and lower edges. Each colored patch occupies 1/5
        // of the client width and 1/12 of its height.
        Point[] clientCorners = Enumerable.Range(0, EdgeColors.Length).Select(i => new Point(
            (i % 2 == 0 ? masterClient.Left + masterClient.Width / 10 : masterClient.Right - masterClient.Width / 10),
            (i < 2 ? masterClient.Top + masterClient.Height / 24 : masterClient.Bottom - masterClient.Height / 24))).ToArray();
        var heldPixels = new List<Color[]>();
        void SampleDuringResize(Action? updateOverlay = null)
        {
            var oldMaster = master.BackColor;
            var oldSelected = selected.BackColor;
            try
            {
                master.BackColor = selected.BackColor = Color.Black;
                master.Refresh();
                selected.Refresh();
                GameViewWindows.FlushComposition();
                updateOverlay?.Invoke();
                samples.Add(slots.Select(s => CompositedSwapPixel(Center(s))).ToArray());
                guarded.Add(GameViewSwapCover.IsCover(OwnerAt(Center(slots[0]))) &&
                    GameViewSwapCover.IsCover(OwnerAt(Center(slots[1]))) && !GameViewSwapCover.IsCover(OwnerAt(Center(slots[2]))));
            }
            finally
            {
                master.BackColor = oldMaster;
                selected.BackColor = oldSelected;
                master.Refresh();
                selected.Refresh();
            }
        }
        Color[] firstPicture = [];
        void ObserveFirstNativeResize(object? sender, EventArgs e)
        {
            selected.ResizeBegin -= ObserveFirstNativeResize;
            master.ResizeBegin -= ObserveFirstNativeResize;
            firstPicture = slots.Select(slot => CompositedSwapPixel(Center(slot))).ToArray();
        }
        selected.ResizeBegin += ObserveFirstNativeResize;
        master.ResizeBegin += ObserveFirstNativeResize;
        master.NextResizeEnd = selected.NextResizeEnd = () => SampleDuringResize(() =>
            heldPixels.Add(clientCorners.Append(heldHud).Select(CompositedSwapPixel).ToArray()));
        PreviewSelection(session, 1);
        check(firstPicture.Select(color => color.ToArgb()).SequenceEqual(expected.Select(color => color.ToArgb())),
            $"Both destination pictures are visible before the first native sizing operation. Sampled {string.Join(", ", firstPicture)}.");
        check(samples.Count == 2, "The original master and the promoted game both exercised a native resize.");
        foreach (var sample in samples)
        {
            bool destinationsVisible = sample.Select((c, i) => Math.Abs(c.R - expected[i].R) <= 12 &&
                Math.Abs(c.G - expected[i].G) <= 12 && Math.Abs(c.B - expected[i].B) <= 12).All(v => v);
            check(destinationsVisible, $"Both destinations already show the incoming character while the native games redraw. " +
                $"Sampled {string.Join(", ", sample.Select(c => c.ToString()))}.");
        }
        check(heldPixels.All(frame => frame.Select(c => c.ToArgb()).SequenceEqual(EdgeColors.Append(Color.White).Select(c => c.ToArgb()))),
            $"The selected game's small-slot HUD and all four client corners fill the master's client during both redraws. " +
            $"Sampled {string.Join(", ", heldPixels.SelectMany(frame => frame))}.");
        check(guarded.All(value => value), "Only the two moving slots shield game clicks until their native clients are ready.");

        PumpUntil(() => Pictures([selected, master, other]), "The live games must replace their swap pictures after promotion.");
        check(session.SourceHandles.SequenceEqual(new[] { selected.Handle, master.Handle, other.Handle }) &&
            Native.GetForegroundWindow() == selected.Handle && before[2] == session.SurfaceHandles[2] &&
            other.ResizeTrace.Count == 0, "Swapping focuses the promoted game and leaves the third slot untouched.");
        var marker = Center(HudMarker(selected.ClientSize));
        check(selected.RectangleToScreen(selected.ClientRectangle) == masterClient &&
            master.RectangleToScreen(master.ClientRectangle) == smallClient &&
            !HudMarker(selected.ClientSize).Contains(selected.PointToClient(heldHud)) &&
            CompositedSwapPixel(selected.PointToScreen(marker)).ToArgb() == Color.White.ToArgb(),
            "After handoff the HUD uses its newly drawn master-resolution position and both native clients exactly fit.");
        NativeGesture(selected, marker);
        check(selected.Input.Count == 2 && selected.Input.All(message => AtRequestedPixel(message.Point, marker)) &&
            master.Input.Count == 0 && other.Input.Count == 0, "The first playable click after the swap reaches only the promoted HUD marker.");

        // Reject the next promotion before a new size can take effect. Inspect the
        // old surfaces before pumping events so a focus change cannot retry it.
        master.RefuseResize = true;
        bool rejected;
        try { rejected = !session.PromoteOnClick((GameViewTile)Control.FromHandle(session.SurfaceHandles[1])!); }
        catch (InvalidOperationException) { rejected = true; }
        finally { master.RefuseResize = false; }
        check(rejected && session.IsActive && session.SourceHandles[0] == selected.Handle,
            "Refusing a resize keeps the previous arrangement active.");
        AcquireFixtureFocus(selected);
        foreach (var game in games) game.Refresh();
        selected.BackColor = Color.DarkCyan;
        PumpUntil(() => Pictures([selected, master, other]), "A failed swap must release its still images and show live updates.");

        session.ApplyForTest(layout with { MatchMasterResolution = true }, slots, handles);
        foreach (var game in games) game.ResizeTrace = [];
        PreviewSelection(session, 1);
        PumpUntil(() => Pictures(games), "A matched swap must expose the selected character immediately.");
        check(games.All(game => game.ResizeTrace!.Count == 0), "A matched-resolution swap leaves every native renderer at its current size.");

        session.ApplyForTest(layout, slots, handles);
        using var overlay = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Bounds = new(slots[0].Left + 65, slots[0].Top + 65, 30, 28), ShowInTaskbar = false,
            TopMost = true, BackColor = Color.Purple };
        var sourceHud = selected.PointToScreen(smallHud);
        using var sourceOverlay = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Bounds = new(sourceHud.X - 10, sourceHud.Y - 10, 20, 20), ShowInTaskbar = false,
            TopMost = true, BackColor = Color.MediumVioletRed };
        overlay.Show();
        sourceOverlay.Show();
        var overlayPoint = Center(overlay.Bounds);
        PumpUntil(() => ColorVisible(overlay.Handle, overlay.BackColor, overlayPoint) &&
            ColorVisible(sourceOverlay.Handle, sourceOverlay.BackColor, sourceHud), "The controls over both games must be visible before promotion.");
        Color duringOverlay = Color.Empty, copiedHud = Color.Empty, originalControl = Color.Empty;
        nint overlayInputOwner = 0;
        selected.NextResizeEnd = () =>
        {
            overlay.BackColor = Color.Orange;
            overlay.Refresh();
            GameViewWindows.FlushComposition();
            duringOverlay = CompositedSwapPixel(overlayPoint);
            overlayInputOwner = OwnerAt(overlayPoint);
            copiedHud = CompositedSwapPixel(heldHud);
            originalControl = CompositedSwapPixel(sourceHud);
        };
        PreviewSelection(session, 1);
        check(duringOverlay.ToArgb() == Color.Orange.ToArgb() && overlayInputOwner == overlay.Handle,
            $"An open control over the changing master stays visible and clickable. Sampled {duringOverlay}.");
        check(copiedHud.ToArgb() == Color.White.ToArgb() && originalControl.ToArgb() == sourceOverlay.BackColor.ToArgb(),
            $"The incoming master shows the character's HUD underneath an opaque source control. The control stays at its original slot. " +
            $"Master {copiedHud}, source slot {originalControl}.");
        sourceOverlay.Close();
        overlay.Close();

        using var keyed = new SwapMarkerOverlay { Bounds = Rectangle.Union(slots[0], slots[2]) };
        keyed.Show();
        keyed.Refresh();
        GameViewStacking.SetBand(keyed.Handle, true);
        Point Mark() => keyed.PointToScreen(Center(keyed.Marker));
        var originalMark = Mark();
        PumpUntil(() =>
        {
            if (!Pictures([selected, master, other])) return false;
            var markerColor = CompositedSwapPixel(originalMark);
            if (markerColor.ToArgb() == Color.Purple.ToArgb()) return true;
            lastSample = $"Overlay marker {originalMark} displayed {markerColor}.";
            return false;
        }, "The color-key overlay must show its marker while leaving every slot visible.");
        samples.Clear();
        Color newMark = Color.Empty, oldMark = Color.Empty;
        master.NextResizeEnd = () => SampleDuringResize(() =>
        {
            keyed.MoveMarker();
            GameViewWindows.FlushComposition();
            newMark = CompositedSwapPixel(Mark());
            oldMark = CompositedSwapPixel(originalMark);
        });
        PreviewSelection(session, 1);
        var swappedColors = new[] { master.BackColor, selected.BackColor, other.BackColor };
        check(samples.Count == 1 && samples[0].Select(c => c.ToArgb()).SequenceEqual(swappedColors.Select(c => c.ToArgb())),
            $"A screen-sized click-through overlay allowed both incoming pictures and their sibling to stay visible during redraw. " +
            $"Sampled {string.Join(", ", samples.SelectMany(s => s))}.");
        check(newMark.ToArgb() == Color.Orange.ToArgb() && oldMark.ToArgb() == master.BackColor.ToArgb(),
            $"The moving overlay marker painted above the incoming game picture and left its old spot clear. New {newMark}, old {oldMark}.");
    }

    private static void TestHeldReferencePicture(Rectangle area, Action<bool, string> check)
    {
        using var reference = new Source(Color.Maroon, area.Location);
        using var selected = new Source(Color.SteelBlue, new Point(area.Left + 360, area.Top));
        using var other = new Source(Color.DarkGreen, new Point(area.Left + 360, area.Top + 190));
        foreach (var window in new[] { reference, selected, other }) window.Open();
        AcquireFixtureFocus(reference);
        var slot = reference.Bounds;
        var center = Center(slot);
        using var overlay = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Bounds = new(slot.Left + 40, slot.Top + 40, 30, 28),
            BackColor = Color.Purple
        };
        overlay.Show();
        var blocked = Center(overlay.Bounds);
        PumpUntil(() => OwnerAt(center) == reference.Handle && OwnerAt(blocked) == overlay.Handle &&
            CompositedSwapPixel(center).ToArgb() == reference.BackColor.ToArgb(), "The reference window and its overlay must be visible.");
        using var refused = GameViewSwapCover.TryHoldReference(reference.Handle, slot, blocked, selected.Handle);
        check(refused is null, "A reference point behind another window cannot start a held preview click.");
        using var picture = GameViewSwapCover.TryHoldReference(reference.Handle, slot, center, selected.Handle);
        if (picture is null) throw new InvalidOperationException("Could not hold the visible reference game.");
        var original = reference.BackColor;
        reference.BackColor = Color.Orange;
        reference.Refresh();
        overlay.BackColor = Color.Lime;
        overlay.Refresh();
        GameViewWindows.FlushComposition();
        check(picture.CoversReferencePoint(center) && CompositedSwapPixel(center).ToArgb() == original.ToArgb() &&
            OwnerAt(center) == reference.Handle && CompositedSwapPixel(blocked).ToArgb() == overlay.BackColor.ToArgb() &&
            OwnerAt(blocked) == overlay.Handle,
            "The held reference stays click-through, preserves its pixels, and keeps overlaid controls live.");
        AcquireFixtureFocus(other);
        PumpUntil(() => !picture.CoversReferencePoint(center) &&
            CompositedSwapPixel(center).ToArgb() == reference.BackColor.ToArgb(), "A separate window taking focus must expose the live reference.");
        check(Native.GetForegroundWindow() == other.Handle && OwnerAt(center) == reference.Handle,
            "Dismissing a held reference on focus loss preserves the new foreground and exposes the live game.");
    }

    private sealed class SwapMarkerOverlay : Form
    {
        internal Rectangle Marker = new(85, 90, 18, 18);
        private Color ink = Color.Purple;

        internal SwapMarkerOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = TransparencyKey = Color.Fuchsia;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var style = base.CreateParams; style.ExStyle |= 0x080800A0; return style; } // NOACTIVATE, LAYERED, TOOLWINDOW, TRANSPARENT
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var brush = new SolidBrush(ink);
            e.Graphics.FillRectangle(brush, Marker);
        }

        internal void MoveMarker()
        {
            Marker.Offset(42, 0);
            ink = Color.Orange;
            Refresh();
        }
    }

    private static Color CompositedSwapPixel(Point point)
    {
        using var pixel = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var target = Graphics.FromImage(pixel))
        using (var desktop = Graphics.FromHwnd(0))
        {
            var src = desktop.GetHdc();
            try
            {
                var dst = target.GetHdc();
                try
                {
                    // CAPTUREBLT includes the temporary layered picture over the native windows.
                    if (!BitBlt(dst, 0, 0, 1, 1, src, point.X, point.Y, 0x40cc0020))
                        throw new InvalidOperationException("Could not sample the composed swap picture.");
                }
                finally { target.ReleaseHdc(dst); }
            }
            finally { desktop.ReleaseHdc(src); }
        }
        return pixel.GetPixel(0, 0);
    }

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
