using System.Diagnostics;

namespace TeamBar;

internal sealed class LayoutRollbackException(Exception transition, Exception rollback)
    : InvalidOperationException($"{transition.Message} The previous layout could not be fully restored: {rollback.Message}",
        new AggregateException(transition, rollback))
{
}

internal sealed partial class GameViewPlacements
{
    private sealed record Request(nint Handle, int Pid, long Started, string Character, Rectangle Target, bool Maximized,
        bool HideTitleBars, bool PreserveMaximizedHud)
    {
        internal bool Owns(Entry entry) => entry.Window == Handle.ToInt64() && entry.Pid == Pid && entry.Started == Started;

        internal void VerifySession()
        {
            using var owner = Process.GetProcessById(Pid);
            if (!GameViewWindows.SameOwner(Handle, Pid) || owner.StartTime.ToUniversalTime().Ticks != Started)
                throw new InvalidOperationException($"{Character}'s game session changed before its window could be placed.");
        }
    }

    private readonly record struct FittedWindow(Request Request, int Index, bool Maximized, bool OwnsFittedMaximum,
        bool NeedsRendererRefresh);

    // The journal records the original desktop. These requests describe the active layout for rollback.
    private readonly Dictionary<nint, Request> current = [];
    // Restored and maximized frames have different insets. Remember the
    // measured restored frame so an actual Restore command opens at slot size.
    private readonly Dictionary<(nint Window, string Display, bool ClientOnly), Padding> normalInsets = [];
    private Rectangle[] currentSurfaces = [];
    private readonly HashSet<Request> restoredFallbacks = [];
    internal string[] RestoredProfileFallbacks => current.Values.Where(restoredFallbacks.Contains).Select(r => r.Character).ToArray();
    private sealed record MaximizedFootprint(Rectangle Painted, Rectangle Client, bool CoverCaption = false);
    private readonly Dictionary<Request, MaximizedFootprint> nativeMaxima = [];
    internal string? NativeMaximumNotice
    {
        get
        {
            foreach (var request in current.Values)
                if (nativeMaxima.TryGetValue(request, out var maximum))
                    return $"{request.Character} kept its maximized EQ UI. The game uses {maximum.Client.Width} × {maximum.Client.Height} " +
                        $"of the {request.Target.Width} × {request.Target.Height} master slot." +
                        (maximum.CoverCaption ? " A strip hides the Windows title bar above the game." : "");
            return null;
        }
    }

    internal bool MatchesActiveSlot(nint window, Rectangle target)
    {
        if (!current.TryGetValue(window, out var request) || request.Target != target) return false;
        if (!nativeMaxima.TryGetValue(request, out var maximum)) return OccupiesSlot(window, target) &&
            (!request.HideTitleBars || ClientBounds(window, request.Pid) == target);
        return IsZoomed(window) && OccupiesSlot(window, maximum.Painted) && ClientBounds(window, request.Pid) == maximum.Client;
    }

    internal Rectangle[] PrepareSources((nint Handle, int Pid)[] sources, string[] characters, Rectangle[] targets,
        nint maximizedSource = 0, Rectangle[]? screenSurfaces = null, bool hideTitleBars = false,
        bool overlapRendererRefreshes = false, bool preserveWholeMasterHud = false)
    {
        if (sources.Length != targets.Length || sources.Length != characters.Length ||
            sources.Select(s => s.Handle).Distinct().Count() != sources.Length || targets.Any(r => r.Width <= 0 || r.Height <= 0))
            throw new InvalidDataException("Each game window needs a different nonempty layout slot.");
        var unsupported = characters.Where((_, i) => GameViewWindows.SameOwner(sources[i].Handle, sources[i].Pid) &&
            RequiresWindowedMode(sources[i].Handle)).ToArray();
        if (unsupported.Length > 0) throw new GameViewWindowedModeException(unsupported);
        var next = new Request[sources.Length];
        for (int i = 0; i < next.Length; i++)
        {
            var (h, pid) = sources[i];
            GameViewWindows.SourceSize(h, pid);
            using var process = Process.GetProcessById(pid);
            var screen = Screen.FromRectangle(targets[i]);
            bool maximize = h == maximizedSource && ClientSessions.IsEverQuestWindow(h);
            var request = new Request(h, pid, process.StartTime.ToUniversalTime().Ticks, characters[i], targets[i], maximize, hideTitleBars,
                maximize && preserveWholeMasterHud && (targets[i] == screen.Bounds || targets[i] == screen.WorkingArea));
            if (!entries.Any(request.Owns) && !HasDefaultRegion(h, pid, ReadRegion(h)))
                throw new InvalidOperationException($"{request.Character} already has a custom window shape. Restore its normal window before applying a layout.");
            next[i] = request;
        }
        var previous = current.Values.ToArray();
        var surfaces = screenSurfaces ?? targets;
        try
        {
            var result = Reconcile(next, surfaces, overlapRendererRefreshes);
            currentSurfaces = surfaces;
            return result;
        }
        catch (Exception error)
        {
            try { Reconcile(previous.Where(r => GameViewWindows.SameOwner(r.Handle, r.Pid) &&
                !GameViewWindows.Minimized(r.Handle) && !RequiresWindowedMode(r.Handle)).ToArray(), currentSurfaces); }
            catch (Exception rollback) { throw new LayoutRollbackException(error, rollback); }
            throw;
        }
    }

    private Rectangle[] Reconcile(Request[] requests, Rectangle[] surfaces, bool overlapRendererRefreshes = false)
    {
        foreach (var dropped in entries.Where(e => !requests.Any(r => r.Owns(e))).ToArray())
        {
            RestoreOne(dropped);
            current.Remove((nint)dropped.Window);
            foreach (var key in normalInsets.Keys.Where(key => key.Window == (nint)dropped.Window).ToArray())
                normalInsets.Remove(key);
            restoredFallbacks.RemoveWhere(request => request.Owns(dropped));
            foreach (var key in nativeMaxima.Keys.Where(request => request.Owns(dropped)).ToArray()) nativeMaxima.Remove(key);
            entries.RemoveAll(e => e.Window == dropped.Window && e.Pid == dropped.Pid && e.Started == dropped.Started);
            Save();
        }
        // Each EQ process has its own window thread and renderer. Waiting for a
        // worker that sends messages to our own UI thread would deadlock it.
        bool independent = overlapRendererRefreshes && requests.Length > 1 &&
            requests.All(r => r.Pid != Environment.ProcessId) && requests.Select(r => r.Pid).Distinct().Count() == requests.Length;
        var clients = independent ? PlaceSwap(requests, surfaces) : requests.Select(r => Place(r, surfaces)).ToArray();
        for (int i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            if (RequiresWindowedMode(request.Handle)) throw new GameViewWindowedModeException([request.Character]);
            if (!MatchesActiveSlot(request.Handle, request.Target) || ClientBounds(request.Handle, request.Pid) != clients[i])
                throw new InvalidOperationException($"{request.Character} moved while the layout was being applied.");
        }
        return clients;
    }

    private Rectangle Place(Request request, Rectangle[] surfaces)
    {
        var fitted = Fit(request);
        if (fitted.NeedsRendererRefresh)
            RefreshNormalResolution(request.Handle, request.Pid, request.HideTitleBars ? request.Character : null);
        return FinishPlacement(fitted, surfaces);
    }

    private Rectangle[] PlaceSwap(Request[] requests, Rectangle[] surfaces)
    {
        var fitted = new FittedWindow[requests.Length];
        var refreshes = new List<Task>(requests.Length);
        var activating = new HashSet<nint>();
        PreserveForeground(activating.Contains, () =>
        {
            try
            {
                // A primary that can use its native maximized profile needs no
                // normal refresh. Let its fit overlap the demoted game's refresh.
                foreach (int i in Enumerable.Range(0, requests.Length)
                    .OrderBy(i => requests[i].Maximized && !restoredFallbacks.Contains(requests[i])))
                {
                    var request = requests[i];
                    fitted[i] = Fit(request, activating);
                    if (fitted[i].NeedsRendererRefresh)
                    {
                        // Only the native renderer cycle is independent. The
                        // coordinator owns all journal, region and layout state.
                        activating.Add(request.Handle);
                        refreshes.Add(Task.Run(() =>
                        {
                            request.VerifySession();
                            if (RequiresWindowedMode(request.Handle))
                                throw new GameViewWindowedModeException([request.Character]);
                            RefreshNormalRenderer(request.Handle, request.Pid, request.HideTitleBars ? request.Character : null);
                        }));
                    }
                }
            }
            catch (Exception error)
            {
                // A later fit can fail with an earlier refresh still running.
                // Join it before propagating the failure and rolling back.
                refreshes.Insert(0, Task.FromException(error));
            }
            Task.WhenAll(refreshes).GetAwaiter().GetResult();
        });
        return fitted.Select(window =>
        {
            window.Request.VerifySession();
            return FinishPlacement(window, surfaces);
        }).ToArray();
    }

    private FittedWindow Fit(Request request, HashSet<nint>? activating = null)
    {
        var (window, pid, started, character, target, _, hideTitleBars, _) = request;
        request.VerifySession();
        // The game can change its native mode after preflight while an earlier source is moving.
        if (RequiresWindowedMode(window)) throw new GameViewWindowedModeException([character]);
        int index = entries.FindIndex(request.Owns);
        var entry = index >= 0 ? entries[index] : Capture(window, pid, character, target);
        if (!nativeMaxima.TryGetValue(request, out var previousMaximum) || !previousMaximum.CoverCaption)
            ReleaseCaptionCover(window);
        bool interruptedFit = index >= 0 && entry.Positioning;
        if (index < 0) { index = entries.Count; entries.Add(entry); }
        var prior = ClientBounds(window, pid);
        var saved = ReadPlacement(window);
        if (entry.SlotClip is { } clip && clip.ClientOnly != hideTitleBars)
        {
            // The previous cutout can still look exactly like the target while
            // the native caption underneath it is outside the new visible slot.
            ReleaseSlotClip(window, index);
            entry = entries[index];
        }
        bool Fits() => nativeMaxima.TryGetValue(request, out var maximum)
            ? IsZoomed(window) && ClientBounds(window, pid) == maximum.Client && (hideTitleBars || OccupiesSlot(window, maximum.Painted))
            : FitsSlot(window, pid, target, hideTitleBars);
        bool fittedMaximum = entry.OwnsFittedMaximum;
        bool wantMaximized = request.Maximized && !restoredFallbacks.Contains(request);
        bool refresh = false;
        var destination = Screen.FromRectangle(target);
        void RememberNormalInsets()
        {
            var frame = Bounds(window);
            var visible = hideTitleBars ? ClientBounds(window, pid) : VisibleFrame(window, frame);
            normalInsets[(window, Screen.FromHandle(window).DeviceName, hideTitleBars)] =
                new Padding(visible.Left - frame.Left, visible.Top - frame.Top, frame.Right - visible.Right, frame.Bottom - visible.Bottom);
        }
        if (!Fits() || (saved.Show == 3) != wantMaximized)
        {
            entries[index] = entry with { Positioning = true };
            Save();
            ReleaseSlotClip(window, index);
            if (!IsZoomed(window) && !entry.OwnsFittedMaximum) RememberNormalInsets();
            if (entry.OwnsFittedMaximum)
            {
                ClearFittedMaximum(window);
                // Retain journal ownership until Windows has received the
                // real restored size and placement has committed.
                fittedMaximum = false;
                var frame = Bounds(window);
                if (!wantMaximized && Fits() && frame.Width > 1)
                    PositionOuter(window, character, frame with { Width = frame.Width - 1 });
            }
            var offset = WorkOffset(window, destination);
            bool demotingProfile = saved.Show == 3 &&
                (interruptedFit || (current.TryGetValue(window, out var active) && active.Maximized));
            Size? restoredRenderSize = null;
            void FitNormal(bool exactSize)
            {
                var savedPlacement = ReadPlacement(window);
                if (savedPlacement.Show == 3 && demotingProfile && !wantMaximized && restoredRenderSize is null)
                {
                    var restoreFrame = normalInsets.TryGetValue((window, destination.DeviceName, hideTitleBars), out var insets)
                        ? Rectangle.FromLTRB(target.Left - insets.Left, target.Top - insets.Top,
                            target.Right + insets.Right, target.Bottom + insets.Bottom)
                        : OuterForSlot(window, pid, target, hideTitleBars);
                    if (activating is null)
                        PreserveForeground(h => h == window, () => restoredRenderSize = RestoreRendererToSlot(window, pid, savedPlacement, restoreFrame, offset));
                    else
                    {
                        activating.Add(window);
                        restoredRenderSize = RestoreRendererToSlot(window, pid, savedPlacement, restoreFrame, offset);
                    }
                    savedPlacement = ReadPlacement(window);
                }
                if (savedPlacement.Show == 3)
                {
                    RestoreForLayout(window, character, OuterForSlot(window, pid, target, hideTitleBars), savedPlacement, offset);
                    GameViewWindows.FlushComposition();
                }
                var outer = OuterForSlot(window, pid, target, hideTitleBars);
                if (hideTitleBars && persist && ClientSessions.IsEverQuestWindow(window))
                {
                    // EQ can save the parked position at logout. Claim only this
                    // exact normal placement when its caption is outside all screens.
                    var reachable = ReachableRestoreBounds(outer, outer, target, target.Top,
                        Screen.AllScreens.Select(s => s.Bounds));
                    if (reachable.Frame != outer)
                        GameViewLaunchGuard.Shared.Record(ClientSessions.LaunchSettings, pid, started,
                            new GameViewLaunchPosition(outer.X, outer.Y, target.Width, target.Height, reachable.Frame.Y));
                }
                if (Bounds(window) != outer) PositionOuter(window, character, outer, exactSize);
            }
            if (wantMaximized)
            {
                // Maximizing a window is a distinct desktop transition. An
                // interactive sizing loop is only appropriate for restored EQ.
                activating?.Add(window);
                wantMaximized = FitMaximized(request, activating is not null);
                GameViewWindows.FlushComposition();
                if (!wantMaximized || !IsZoomed(window) || !Fits())
                {
                    if (request.PreserveMaximizedHud)
                        throw new InvalidOperationException($"{character} could not keep its maximized EQ UI in the master slot.");
                    // Keep a refusal for this exact window and slot until it
                    // leaves the layout. Refocusing it should not flash again.
                    restoredFallbacks.Add(request);
                    wantMaximized = false;
                }
            }
            if (!wantMaximized)
            {
                WithResizeNotifications(window, pid, true, restoring: false, () => FitNormal(false));
                GameViewWindows.FlushComposition();
                if (!Fits())
                {
                    // A monitor-sized client plus its hidden caption can exceed
                    // Windows' usual maximum tracking height for a restored window.
                    var outer = OuterForSlot(window, pid, target, hideTitleBars);
                    var monitor = Screen.FromRectangle(target).Bounds;
                    bool oversizedFrame = hideTitleBars && (outer.Width > monitor.Width || outer.Height > monitor.Height);
                    FitNormal(exactSize: oversizedFrame);
                    GameViewWindows.FlushComposition();
                }
            }
            var finalSize = ClientBounds(window, pid).Size;
            refresh = !wantMaximized && (saved.Show != 3 || demotingProfile) && finalSize != restoredRenderSize && finalSize != prior.Size &&
                (ReadStyle(window, styleIndex) & caption) == caption;
        }
        if (!wantMaximized && entries[index].SlotClip is null) RememberNormalInsets();
        return new(request, index, wantMaximized, fittedMaximum, refresh);
    }

    // Changing ShowCmd and the normal rectangle together can bypass EQ's
    // renderer restore handler. Give it SC_RESTORE with the next size already
    // in WINDOWPLACEMENT, so the small slot only enters the restored HUD.
    private static Size? RestoreRendererToSlot(nint window, int pid, WindowPlacement placement, Rectangle frame, Point offset)
    {
        frame.Offset(-offset.X, -offset.Y);
        if (placement.Normal.Rectangle != frame)
        {
            placement.Normal = new Rect(frame.Left, frame.Top, frame.Right, frame.Bottom);
            if (!SetWindowPlacement(window, ref placement) || !IsZoomed(window)) return null;
        }
        if (!SendSystemCommand(window, pid, 0xF120) || IsZoomed(window)) return null;
        GameViewWindows.FlushComposition();
        return ClientBounds(window, pid).Size;
    }

    private Rectangle FinishPlacement(FittedWindow fitted, Rectangle[] surfaces)
    {
        var (request, index, wantMaximized, fittedMaximum, _) = fitted;
        var (window, pid, _, character, target, _, hideTitleBars, _) = request;
        bool nativeMaximum = nativeMaxima.TryGetValue(request, out var maximum);
        if (maximum is not null) target = maximum.Painted;
        var fittedFrame = Bounds(window);
        // The HWND keeps its original styles and local mouse coordinates.
        // Crop the actual caption even when the slot has no neighbor.
        if (maximum?.CoverCaption == true)
            ReleaseSlotClip(window, index);
        else if (hideTitleBars || surfaces.Any(s => !s.IntersectsWith(target) && s.IntersectsWith(fittedFrame)))
            ApplySlotClip(window, index, target, character, hideTitleBars, nativeMaximum && hideTitleBars ? request : null);
        else
            ReleaseSlotClip(window, index);
        if (nativeMaxima.TryGetValue(request, out maximum) && maximum.CoverCaption)
        {
            PresentCaptionCover(window, maximum);
            target = maximum.Painted;
            hideTitleBars = false;
        }
        else ReleaseCaptionCover(window);
        var client = ClientBounds(window, pid);
        if (!OccupiesSlot(window, target) || (hideTitleBars ? client != target : !target.Contains(client)))
            throw new InvalidOperationException($"{character}'s game window did not fit its {target.Width} x {target.Height} slot.");
        fittedFrame = Bounds(window);
        if (entries[index].Positioning || entries[index].Parked != fittedFrame)
        {
            var appliedOffset = WorkOffset(window, Screen.FromHandle(window));
            var normal = ReadPlacement(window).Normal.Rectangle;
            normal.Offset(appliedOffset);
            entries[index] = entries[index] with { Positioning = false, Parked = fittedFrame, NormalOffset = appliedOffset,
                ParkedNormal = wantMaximized ? normal : null, OwnsFittedMaximum = fittedMaximum };
            Save();
        }
        current[window] = request;
        return client;
    }
}
