using System.Runtime.InteropServices;

namespace TeamBar;

internal sealed partial class GameViewPlacements
{
    private sealed record NativeSlotClip(Rectangle Bounds, byte[]? PreviousRegion, Rectangle? AppliedFrame = null,
        int? PreviousFramePolicy = null, bool ClientOnly = false, bool RetriedCrop = false);

    private readonly Dictionary<nint, GameViewCaptionCover> captionCovers = [];
#if !TEAMBAR_PUBLIC_RELEASE
    internal nint CaptionCoverForTest(nint game) => captionCovers.GetValueOrDefault(game)?.Handle ?? 0;
#endif

    private void ReleaseCaptionCover(nint game)
    {
        if (captionCovers.Remove(game, out var cover)) cover.Dispose();
    }

    // Return the upper surface of the game after reconciling its current z-order.
    // A swap button for this slot belongs immediately above that surface.
    internal nint PositionCaptionCover(nint game)
    {
        if (!captionCovers.TryGetValue(game, out var cover)) return game;
        cover.RefreshStacking();
        return cover.Handle != 0 ? cover.Handle : game;
    }

    private void PresentCaptionCover(nint game, MaximizedFootprint frame)
    {
        // The strip ends at the first game pixel. Keep the whole client native,
        // including its input coordinates and saved maximized UI.
        var bounds = Rectangle.FromLTRB(frame.Painted.Left, frame.Painted.Top, frame.Painted.Right, frame.Client.Top);
        if (bounds.Height <= 0 || bounds.Width <= 0) { ReleaseCaptionCover(game); return; }
        if (captionCovers.TryGetValue(game, out var cover)) cover.Present(bounds);
        else captionCovers.Add(game, new GameViewCaptionCover(game, bounds));
    }

    // Keep a native resize grip from intercepting the neighboring game's clicks.
    // SetWindowRgn uses coordinates relative to the full HWND, including its frame.
    private void ApplySlotClip(nint h, int index, Rectangle paintedSlot, string character, bool clientOnly, Request? nativeMaster = null)
    {
        var entry = entries[index];
        var outer = Bounds(h);
        var clip = paintedSlot;
        clip.Offset(-outer.Left, -outer.Top);
        var currentShape = ReadRegion(h);
        if (entry.SlotClip is { } active)
        {
            if (active.Bounds == clip)
            {
                EnsureSlotClip(h, index, currentShape, character, nativeMaster);
                return;
            }
            ReleaseSlotClip(h, index);
            entry = entries[index];
            currentShape = ReadRegion(h);
        }
        if (outer == paintedSlot || currentShape.Matches(clip)) return;
        var client = ClientBounds(h, entry.Pid);
        if (!outer.Contains(paintedSlot) || (clientOnly ? client != paintedSlot : !paintedSlot.Contains(client)) ||
            !HasDefaultRegion(h, entry.Pid, currentShape))
            throw new InvalidOperationException($"Could not fit {character}'s native frame to its slot.");

        byte[]? previous = currentShape.Kind == 0 ? null : CaptureRegion(h);
        entries[index] = entry with { SlotClip = new NativeSlotClip(clip, previous, outer, ClientOnly: clientOnly) };
        Save(); // Recover if Team Bar exits before SetWindowRgn returns.
        try { SetRectangularSlotRegion(h, clip, redraw: true); }
        catch (InvalidOperationException) when (nativeMaster is not null)
        {
            KeepNativeMaximizedFrame(nativeMaster, index);
            return;
        }
        EnsureSlotClip(h, index, ReadRegion(h), character, nativeMaster);
    }

    internal bool CanReplaceDroppedSlotClip(nint window, int pid, Rectangle expectedClient)
    {
        if (!current.TryGetValue(window, out var request) || request.Pid != pid ||
            entries.Find(request.Owns) is not { Positioning: false, SlotClip: { } clip } entry) return false;
        var frame = Bounds(window);
        if (frame != entry.Parked || frame != (clip.AppliedFrame ?? entry.Parked) ||
            ClientBounds(window, pid) != expectedClient) return false;
        var region = ReadRegion(window);
        return !region.Matches(clip.Bounds) && HasDefaultRegion(window, pid, region);
    }

    internal void VerifySlotClip(nint window, int pid, string character)
    {
        int index = entries.FindIndex(e => e.Window == (long)window && e.Pid == pid && e.SlotClip is not null);
        if (index >= 0 && GameViewWindows.SameOwner(window, pid))
        {
            current.TryGetValue(window, out var request);
            EnsureSlotClip(window, index, ReadRegion(window), character,
                request is { HideTitleBars: true } && nativeMaxima.ContainsKey(request) ? request : null);
        }
    }

    private void EnsureSlotClip(nint h, int index, WindowRegion currentShape, string character, Request? nativeMaster)
    {
        var entry = entries[index];
        var clip = entry.SlotClip!;
        if (currentShape.Matches(clip.Bounds)) return;
        if (nativeMaster is not null)
        {
            // A nonclient repaint can discard the first region. Try it once
            // without requesting another frame repaint, at the same client size.
            nativeMaster.VerifySession();
            var expectedClient = nativeMaxima[nativeMaster].Client;
            if (!clip.RetriedCrop && Bounds(h) == clip.AppliedFrame && IsZoomed(h) &&
                ClientBounds(h, entry.Pid) == expectedClient && HasDefaultRegion(h, entry.Pid, currentShape))
            {
                entries[index] = entry with { SlotClip = clip with { RetriedCrop = true } };
                try { SetRectangularSlotRegion(h, clip.Bounds, redraw: false); }
                catch (InvalidOperationException) { KeepNativeMaximizedFrame(nativeMaster, index); return; }
                if (IsZoomed(h) && ReadRegion(h).Matches(clip.Bounds) && Bounds(h) == clip.AppliedFrame &&
                    ClientBounds(h, entry.Pid) == expectedClient) return;
            }
            KeepNativeMaximizedFrame(nativeMaster, index);
            return;
        }
        var frame = Bounds(h);
        if (frame != (clip.AppliedFrame ?? entry.Parked))
            throw new InvalidOperationException($"{character}'s window moved or resized. Reapply the layout to use its new position.");
        if (clip.PreviousFramePolicy.HasValue || !ClientSessions.IsEverQuestWindow(h) || RequiresWindowedMode(h) ||
            !HasDefaultRegion(h, entry.Pid, currentShape))
            throw new InvalidOperationException($"{character} did not keep its slot's window region.");

        var client = ClientBounds(h, entry.Pid);
        if (clip.ClientOnly)
        {
            var expectedClient = clip.Bounds;
            expectedClient.Offset(frame.Location);
            if (client != expectedClient)
                throw new InvalidOperationException($"{character}'s game client moved or resized. Reapply the layout to use its new position.");
            if (!clip.RetriedCrop)
            {
                // Activating EQ can discard the region while leaving every game
                // pixel in place. Restore the crop before changing DWM policy.
                clip = clip with { RetriedCrop = true };
                entries[index] = entry with { SlotClip = clip };
                try { SetRectangularSlotRegion(h, clip.Bounds, redraw: false); }
                catch (InvalidOperationException) when (GameViewWindows.SameOwner(h, entry.Pid))
                {
                    // The ordinary frame-policy fallback can still fit a native frame.
                }
                var afterRetry = ReadRegion(h);
                if (Bounds(h) != frame || ClientBounds(h, entry.Pid) != expectedClient)
                    throw new InvalidOperationException($"{character}'s game window moved or resized while recovering its slot.");
                if (afterRetry.Matches(clip.Bounds)) return;
                if (!HasDefaultRegion(h, entry.Pid, afterRetry))
                    throw new InvalidOperationException($"{character} did not keep its slot's window region.");
            }
        }
        if (RecoverableFramePolicy(h) is not { } prior)
            throw new InvalidOperationException($"{character} did not keep its slot's window region.");
        entries[index] = entry with { SlotClip = clip with { PreviousFramePolicy = prior } };
        Save(); // Own the policy and the region before either changes.
        if (prior != disableFrameRendering) SetFramePolicy(h, disableFrameRendering);
        if (Bounds(h) != frame || ClientBounds(h, entry.Pid) != client || !HasDefaultRegion(h, entry.Pid, ReadRegion(h)))
            throw new InvalidOperationException($"{character}'s window changed while fitting its native frame.");
        SetRectangularSlotRegion(h, clip.Bounds, redraw: false);
        if (!ReadRegion(h).Matches(clip.Bounds) || Bounds(h) != frame || ClientBounds(h, entry.Pid) != client)
            throw new InvalidOperationException($"{character} did not keep its slot's window region.");
    }

    private static MaximizedFootprint? NativeMaximizedFrame(Request request, Rectangle expectedClient)
    {
        var window = request.Handle;
        var screen = Screen.FromRectangle(request.Target);
        if (!IsZoomed(window) || Screen.FromHandle(window).DeviceName != screen.DeviceName) return null;
        var outer = Bounds(window);
        var visible = VisibleFrame(window, outer);
        var client = ClientBounds(window, request.Pid);
        return client == expectedClient && outer.Contains(screen.WorkingArea) && request.Target.Contains(visible) &&
            visible.Contains(client) && HasDefaultRegion(window, request.Pid, ReadRegion(window))
            ? new(visible, client, CoverCaption: true) : null;
    }

    private void KeepNativeMaximizedFrame(Request request, int index)
    {
        request.VerifySession();
        ReleaseSlotClip(request.Handle, index);
        GameViewWindows.FlushComposition();
        var frame = NativeMaximizedFrame(request, nativeMaxima[request].Client) ??
            throw new InvalidOperationException($"{request.Character}'s maximized game frame moved outside its master slot.");
        PresentCaptionCover(request.Handle, frame);
        nativeMaxima[request] = frame;
    }

    private void ReleaseSlotClip(nint h, int index)
    {
        var entry = entries[index];
        if (entry.SlotClip is not { } clip) return;
        // The player, the game, or Windows may have replaced our region. Only
        // a matching rectangle is ours to undo, including during crash recovery.
        if (ReadRegion(h).Matches(clip.Bounds))
            SetSlotRegion(h, clip.PreviousRegion is null ? 0 : RestoreRegion(clip.PreviousRegion), redraw: true);
        if (clip.PreviousFramePolicy is { } prior && prior != disableFrameRendering)
            SetFramePolicy(h, prior);
        entries[index] = entry with { SlotClip = null };
        Save();
    }

    private static byte[] CaptureRegion(nint h)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new InvalidOperationException("Windows could not capture the original window region.");
        try
        {
            if (GetWindowRgn(h, region) == 0)
                throw new InvalidOperationException("The original window region changed during setup.");
            uint length = GetRegionData(region, 0, 0);
            var data = new byte[length];
            if (length == 0 || CopyRegionData(region, length, data) != length)
                throw new InvalidOperationException("Windows could not save the original window region.");
            return data;
        }
        finally { DeleteObject(region); }
    }

    private static nint RestoreRegion(byte[] data)
    {
        // RGNDATAHEADER followed by RECTs. Check the length before passing journal bytes to GDI.
        const int headerSize = 32, rectangleSize = 16;
        if (data.Length < headerSize || BitConverter.ToUInt32(data, 0) != headerSize ||
            BitConverter.ToUInt32(data, 4) != 1 || (ulong)BitConverter.ToUInt32(data, 8) * rectangleSize != (ulong)(data.Length - headerSize))
            throw new InvalidDataException("The saved window region is invalid.");
        var region = ExtCreateRegion(0, (uint)data.Length, data);
        return region != 0 ? region : throw new InvalidOperationException("Windows could not recreate the original window region.");
    }

    private static void SetRectangularSlotRegion(nint window, Rectangle clip, bool redraw)
    {
        var region = CreateRectRgn(clip.Left, clip.Top, clip.Right, clip.Bottom);
        if (region == 0) throw new InvalidOperationException("Windows could not create the slot's window region.");
        SetSlotRegion(window, region, redraw);
    }

    private static void SetSlotRegion(nint window, nint region, bool redraw)
    {
        if (SetWindowRgn(window, region, redraw) == 0)
        {
            if (region != 0) DeleteObject(region);
            throw new InvalidOperationException("Windows could not update the game's window region.");
        }
        // The system now owns the region. Wait for DWM to expose its new painted bounds.
        GameViewWindows.FlushComposition();
    }

    [DllImport("user32.dll")] private static extern int GetWindowRgnBox(nint window, out Rect rect);
    [DllImport("gdi32.dll", EntryPoint = "GetRegionData")] private static extern uint CopyRegionData(nint region, uint size, [Out] byte[] data);
    [DllImport("gdi32.dll")] private static extern nint ExtCreateRegion(nint transform, uint size, byte[] data);
}
