namespace TeamBar;

internal sealed class FixedSlotResolutionException(string message) : InvalidOperationException(message);
internal sealed class GameViewWindowedModeException(IEnumerable<string> names) : InvalidOperationException(Describe(names))
{
    internal static string Describe(IEnumerable<string> names) =>
        $"Switch to Windowed mode for {string.Join(", ", names)}. In EQ, choose Options → Display → Switch to Windowed.";
}

internal sealed partial class GameViewSession : IDisposable
{
    private sealed record Client(nint Handle, int Pid, long Started, string Name, bool SupportsPreviewInput = true);
    private sealed record Plan(GameViewLayout Layout, Rectangle[] Slots, Client?[] Homes, int[] Order, nint PrimaryUi)
    {
        internal RelativePosition?[] ButtonPositions { get; set; } = [];
        internal Func<GameViewLayout, int, RelativePosition?>? PositionReader { get; set; }
        internal Client? At(int i) => Homes[Order[i]];
        internal int Master => Layout.MasterIndex;
        internal bool MasterOccupied => Master >= 0 && At(Master) is not null;
        internal bool UseMasterResolution => Layout.MatchMasterResolution && MasterOccupied;
        internal bool FixedMatching => Master < 0 && Layout.MatchMasterResolution;
        internal Size ReferenceSize => Slots[Layout.MasterSlot ?? 0].Size;
        // Park scaled sources behind an occupied native game of exactly the reference size.
        // An offline reference character leaves its desktop slot available.
        internal int Cover
        {
            get
            {
                if (!FixedMatching) return UseMasterResolution ? Master : -1;
                int reference = Layout.MasterSlot ?? 0;
                if (At(reference) is not null) return reference;
                for (int i = 0; i < Slots.Length; i++)
                    if (At(i) is not null && Slots[i].Size == ReferenceSize) return i;
                return -1;
            }
        }
        internal bool HasPreview(int i) => At(i) is not null && (Master >= 0 ? i != Master :
            FixedMatching && Cover >= 0 && Slots[i].Size != ReferenceSize);
        internal Rectangle Target(int i) => UseMasterResolution ? Slots[Master] :
            FixedMatching && HasPreview(i) ? Slots[Cover] : Slots[i];
    }

    private Plan? plan;
    private GameViewTile?[] previews = [];
    private Rectangle[] nativeClients = [];
    private readonly System.Windows.Forms.Timer monitor = new() { Interval = 400 };
    private readonly GameViewPlacements placements;
    private readonly GameViewStacking stacking;
    private readonly Func<GameViewLayout, Rectangle[]>? resolveDisplays;
    private bool ended;
    private bool updating;

    private GameViewSession(bool live, Func<GameViewLayout, Rectangle[]>? resolveDisplays,
        Func<nint, bool>? controlFilter = null, Func<nint[]>? taskbars = null)
    {
        this.resolveDisplays = resolveDisplays;
        placements = new(live);
        stacking = new(placements, controlFilter, taskbars);
        monitor.Tick += (_, _) => CheckWindows();
        cursorReturnTimer.Tick += (_, _) => CheckCursorReturn();
        foregroundCallback = OnForegroundChanged;
        foregroundHook = Native.SetWinEventHook(ForegroundEvent, ForegroundEvent, 0, foregroundCallback, 0, 0, 0);
    }

    internal bool IsActive => !ended;
    internal bool WaitingForGameDialog => plan?.Homes.Any(c => c is not null &&
        GameViewWindows.HasOpenDialog(c.Handle, c.Pid)) == true;
    internal int OccupiedCount => plan?.Homes.Count(c => c is not null) ?? 0;
    internal string[] RestoredProfileFallbacks => placements.RestoredProfileFallbacks;
    internal string? NativeMaximumNotice => placements.NativeMaximumNotice;
    internal string? PreviewNotice { get; private set; }
    internal string? EndReason { get; private set; }
    internal bool EndedForWindowMode { get; private set; }
    internal event EventHandler? Ended;
    internal event EventHandler? StatusChanged;
    internal static void RecoverPendingPlacements() => GameViewPlacements.RecoverPending();

    internal bool TryGetVisibleClientRect(nint source, out Rectangle visible)
        => TryGetVisibleTile(source, out _, out visible);

    internal bool TryGetVisibleTile(nint source, out nint owner, out Rectangle visible)
    {
        owner = 0;
        visible = Rectangle.Empty;
        if (ended || updating || plan is null || source == 0) return false;
        for (int i = 0; i < plan.Homes.Length; i++)
        {
            if (plan.At(i) is not { } c || c.Handle != source || !GameViewWindows.Available(source, c.Pid)) continue;
            if (previews[i] is { } tile)
            {
                if (!tile.TryGetDisplayedClient(out visible)) return false;
                owner = tile.Handle;
            }
            else
            {
                visible = GameViewPlacements.ClientBounds(source, c.Pid);
                owner = source;
            }
            return true;
        }
        return false;
    }

    internal static Rectangle[] DisplaySlots(GameViewLayout layout)
        => GameViewGeometry.Slots(layout, GameViewMonitor.Connected());

    internal static GameViewSession Start(GameViewLayout layout, Rectangle[] slots, IReadOnlyList<GameSession?> clients,
        Func<GameViewLayout, int, RelativePosition?>? readSwapButtonPosition = null,
        Action<GameViewLayout, int, RelativePosition>? saveSwapButtonPosition = null,
        AppearanceTheme theme = AppearanceTheme.Dark)
    {
        RecoverPendingPlacements();
        var session = new GameViewSession(true, DisplaySlots)
        {
            ReadSwapButtonPosition = readSwapButtonPosition,
            SaveSwapButtonPosition = saveSwapButtonPosition,
            swapButtonTheme = FloatingThemes.ForAppTheme(theme)
        };
        try { session.Apply(layout, slots, clients); return session; }
        catch { session.Dispose(); throw; }
    }

    internal void Apply(GameViewLayout layout, Rectangle[] slots, IReadOnlyList<GameSession?> clients, bool background = false)
    {
        if (!DisplaySlots(layout).SequenceEqual(slots))
            throw new InvalidOperationException("A selected screen changed. Reapply the layout using the current screens.");
        if (clients.Count != layout.Characters.Length) throw new InvalidDataException("Each layout slot needs an assignment row.");
        var homes = new Client?[clients.Count];
        for (int i = 0; i < homes.Length; i++)
        {
            if (clients[i] is not { } c) continue;
            var current = ClientSessions.ReadScreenWindow(c.ProcessId);
            if (!WindowIdentification.SameSession(c, current))
                throw new InvalidDataException($"The game window for slot {i + 1} changed. Refresh its assignment.");
            if (GameViewWindows.Available((nint)c.Window, c.ProcessId))
                homes[i] = new((nint)c.Window, c.ProcessId, c.StartedUtcTicks,
                    c.IdentifiedCharacterName ?? (string.IsNullOrWhiteSpace(layout.Characters[i]) ? $"EQ window {c.ProcessId}" : layout.Characters[i]),
                    ScreenInstallation.Same(ScreenInstallation.Current, ScreenInstallation.Original));
        }
        Configure(layout, slots, homes, background);
    }

#if !TEAMBAR_PUBLIC_RELEASE
    internal nint[] SurfaceHandles => plan is null ? [] : plan.Order.Select((_, i) =>
        previews[i]?.Handle ?? plan.At(i)?.Handle ?? 0).ToArray();
    internal nint[] SourceHandles => plan is null ? [] : plan.Order.Select((_, i) => plan.At(i)?.Handle ?? 0).ToArray();
    internal static GameViewSession StartForTest(GameViewLayout layout, Rectangle bounds, IReadOnlyList<nint> handles)
        => StartForTest(layout, GameViewGeometry.Slots(layout, _ => bounds), handles);

    internal static GameViewSession StartForTest(GameViewLayout layout, Rectangle[] slots, IReadOnlyList<nint> handles,
        Func<GameViewLayout, Rectangle[]>? resolveDisplays = null, Func<nint, bool>? controlFilter = null,
        Func<nint[]>? taskbars = null)
    {
        var session = new GameViewSession(false, resolveDisplays, controlFilter ?? GameViewRuntimeTests.IsTeamBarControl, taskbars);
        try { session.ApplyForTest(layout, slots, handles); return session; }
        catch { session.Dispose(); throw; }
    }

    internal void ApplyForTest(GameViewLayout layout, Rectangle bounds, IReadOnlyList<nint> handles, bool background = false)
        => ApplyForTest(layout, GameViewGeometry.Slots(layout, _ => bounds), handles, background);

    internal void ApplyForTest(GameViewLayout layout, Rectangle[] slots, IReadOnlyList<nint> handles, bool background = false)
    {
        if (handles.Any(h => h != 0 && !GameViewWindows.SameOwner(h, Environment.ProcessId)))
            throw new ArgumentException("Layout fixtures must belong to this process.");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long started = process.StartTime.ToUniversalTime().Ticks;
        Configure(layout, slots, handles.Select((h, i) => h == 0 ? null : new Client(h, process.Id, started, layout.Characters[i])).ToArray(), background);
    }
#endif

    private void Configure(GameViewLayout layout, Rectangle[] slots, Client?[] homes, bool background, int[]? promotedOrder = null)
    {
        ObjectDisposedException.ThrowIf(ended, this);
        if (fixedClick != null)
        {
            if (background && plan is { } active && active.Layout == layout &&
                active.Slots.SequenceEqual(slots) && active.Homes.SequenceEqual(homes)) return;
            CancelFixedClick();
            ObjectDisposedException.ThrowIf(ended, this);
        }
        if (WaitingForGameDialog)
        {
            if (background) return;
            throw new InvalidOperationException("Close the open EQ dialog before changing the layout.");
        }
        if (homes.Length != slots.Length || homes.Length != layout.Characters.Length || homes.OfType<Client>().Select(c => c.Handle).Distinct().Count() != homes.Count(c => c is not null))
            throw new InvalidDataException("Assign a different EQ window to each occupied slot.");
        var old = plan;
        bool sameGeometry = old is not null && slots.SequenceEqual(old.Slots) && old.Master == layout.MasterIndex;
        // Swapping controls what the next click does. Turning it off freezes
        // the games in their current seats, including a previously promoted game.
        bool sameLayout = old is not null && (old.Master == layout.MasterIndex || old.Layout.SwapOnClick != layout.SwapOnClick) &&
            string.Equals(old.Layout.Name, layout.Name, StringComparison.OrdinalIgnoreCase) &&
            old.Layout.ScreenPlans.SequenceEqual(layout.ScreenPlans);
        var order = promotedOrder ?? (sameLayout ? OrderAfterSeatEdits(old!, layout) : Enumerable.Range(0, homes.Length).ToArray());
        var foreground = Native.GetForegroundWindow();
        nint primaryUi = 0;
        if (!layout.MatchMasterResolution)
        {
            if (layout.MasterIndex >= 0) primaryUi = homes[order[layout.MasterIndex]]?.Handle ?? 0;
            else primaryUi = FixedPrimaryUi(layout, homes, order);
        }
        var next = new Plan(layout, slots, homes, order, primaryUi);
        RequireWindowedClients(next);
        LoadSwapButtonPositions(next, old);
        // A button can fill an empty reference slot. Until it does, leave each
        // game playable at its own size instead of parking it in the empty slot.
        if (next.FixedMatching && next.Cover < 0 && !layout.AddSwapButton && Enumerable.Range(0, homes.Length)
            .Any(i => next.At(i) is not null && next.Slots[i].Size != next.ReferenceSize))
            throw new FixedSlotResolutionException("Matching fixed slots needs an occupied slot at the master resolution. " +
                "Assign a game to a reference-size slot, or use the fixed slots at their own resolutions.");
        bool samePreviewSizing = old?.Layout.MatchMasterResolution == layout.MatchMasterResolution &&
            old.Layout.HideTitleBars == layout.HideTitleBars &&
            old.FixedMatching == next.FixedMatching && (!next.FixedMatching || old.Layout.MasterSlot == layout.MasterSlot);
        bool samePresentation = sameGeometry && samePreviewSizing && old!.PrimaryUi == primaryUi && old.Order.SequenceEqual(order) &&
            old.Homes.SequenceEqual(homes) && (background || NativePositionsMatch(old));
        if (samePresentation)
        {
            try
            {
                UpdateStacking(next, previews);
            }
            catch (Exception error)
            {
                try { UpdateStacking(old!, previews); }
                catch (Exception rollback) { Stop($"Could not restore the previous window order: {rollback.Message}"); }
                throw new InvalidOperationException(error.Message, error);
            }
            plan = next;
            return;
        }

        var previousFallbacks = placements.RestoredProfileFallbacks;
        var previousMaximum = placements.NativeMaximumNotice;
        updating = true;
        try
        {
            Rectangle[] measured;
            try
            {
                // The former master is becoming a selector. Release its layout-owned
                // topmost flag before EQ maximizes it during the resize, so it can't
                // jump in front of the still pictures on either monitor.
                bool exchanging = promotedOrder is not null && old is not null && old.Layout.SwapTargetIndex >= 0 &&
                    old.At(old.Layout.SwapTargetIndex) is not null;
                if (exchanging && old!.At(old.Layout.SwapTargetIndex) is { } demoted)
                    placements.SetPinned(demoted.Handle, demoted.Pid, false);
                measured = Position(next, overlapRendererRefreshes: exchanging && !next.Layout.MatchMasterResolution);
            }
            catch (Exception error) when (old is not null && error is not LayoutRollbackException)
            {
                // Geometry rollback can re-admit an old occupant after it was
                // temporarily removed. Reapply its previous stacking policy too.
                try { UpdateStacking(old, previews); }
                catch (Exception rollback) { Stop($"Could not restore the previous window order: {rollback.Message}"); }
                throw;
            }
            var nextPreviews = new GameViewTile?[homes.Length];
            try
            {
                for (int i = 0; i < homes.Length; i++)
                {
                    if (!next.HasPreview(i) || next.At(i) is not { } client) continue;
                    var existing = i < previews.Length ? previews[i] : null;
                    if (existing is not null && samePreviewSizing && old!.At(i) == client &&
                        old.Slots[i] == slots[i] && old.Target(i) == next.Target(i) &&
                        existing.MatchesSourceGeometry())
                        nextPreviews[i] = existing;
                    else nextPreviews[i] = OpenPreview(client, next, i, measured[i].Size);
                }
                // Joining a running team must not take the keyboard away from another app.
                // The main game needs to stay above the native windows parked behind it.
                if (!background && promotedOrder is not null) Raise(next.At(layout.SwapTargetIndex)!);
                else if (!background && next.MasterOccupied) Raise(next.At(next.Master)!);
                else if (!background)
                    for (int i = homes.Length - 1; i >= 0; i--)
                        if (next.At(i) is { } c) Raise(c);
                if (!background && homes.OfType<Client>().Any())
                {
                    var preferred = promotedOrder is not null ? next.At(layout.SwapTargetIndex) :
                        next.MasterOccupied ? next.At(next.Master) : homes.FirstOrDefault(c => c?.Handle == foreground) ??
                        (next.FixedMatching && next.Cover >= 0 ? next.At(next.Cover) : null) ?? homes.FirstOrDefault(c => c?.Handle == primaryUi) ??
                        homes.First(c => c is not null);
                    if (!GameViewWindows.TryFocus(preferred!.Handle) && promotedOrder is not null)
                        throw new InvalidOperationException($"Windows declined to focus {preferred.Name} in its slot.");
                }
                UpdateStacking(next, nextPreviews);
            }
            catch (Exception error)
            {
                foreach (var tile in nextPreviews.OfType<GameViewTile>().Except(previews.OfType<GameViewTile>())) tile.Dispose();
                if (old is not null)
                {
                    try
                    {
                        nativeClients = Position(old);
                        UpdateStacking(old, previews);
                    }
                    catch (Exception rollback) { Stop($"Could not restore the previous layout: {rollback.Message}"); }
                }
                throw new InvalidOperationException(error.Message, error);
            }
            foreach (var tile in previews.OfType<GameViewTile>().Except(nextPreviews.OfType<GameViewTile>())) tile.Dispose();
            previews = nextPreviews;
            nativeClients = measured;
            plan = next;
            monitor.Start();
        }
        catch (LayoutRollbackException error)
        {
            Stop(error.Message);
            throw;
        }
        finally { updating = false; }
        if (!next.FixedMatching || old is null || !old.Homes.SequenceEqual(homes) || promotedOrder is not null)
            SetPreviewNotice(null);
        if (old is null || !old.Homes.SequenceEqual(homes) || !previousFallbacks.SequenceEqual(placements.RestoredProfileFallbacks) ||
            previousMaximum != placements.NativeMaximumNotice)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int[] OrderAfterSeatEdits(Plan previous, GameViewLayout layout)
    {
        var order = previous.Order;
        if (ReferenceEquals(previous.Layout, layout)) return order;
        // A saved assignment goes into its designated slot immediately. Keep
        // unrelated live swaps, including when a character logs in or out.
        for (int slot = 0; slot < order.Length; slot++)
        {
            if (order[slot] == slot ||
                (StringComparer.OrdinalIgnoreCase.Equals(previous.Layout.Characters[slot], layout.Characters[slot]) &&
                 previous.Layout.Windows?[slot] == layout.Windows?[slot])) continue;
            if (ReferenceEquals(order, previous.Order)) order = order.ToArray();
            int current = Array.IndexOf(order, slot);
            (order[slot], order[current]) = (order[current], order[slot]);
        }
        return order;
    }

    private static nint FixedPrimaryUi(GameViewLayout layout, Client?[] homes, int[] order)
    {
        // EQ's maximized UI belongs to a whole-monitor seat. An explicit swap
        // can give that seat a new occupant. Ordinary focus changes keep both UIs.
        int slot = 0;
        nint firstOccupied = 0;
        foreach (var screen in layout.ScreenPlans)
        {
            if (screen.Count == 1 && homes[order[slot]] is { } game)
            {
                if (slot == layout.MasterSlot) return game.Handle;
                if (firstOccupied == 0) firstOccupied = game.Handle;
            }
            slot += screen.Count;
        }
        return firstOccupied;
    }

    private static void RequireWindowedClients(Plan current)
    {
        var affected = current.Homes.OfType<Client>().Where(c => GameViewPlacements.RequiresWindowedMode(c.Handle))
            .Select(c => c.Name).ToArray();
        if (affected.Length > 0) throw new GameViewWindowedModeException(affected);
    }

    private Rectangle[] Position(Plan next, bool overlapRendererRefreshes = false)
    {
        var occupied = Enumerable.Range(0, next.Homes.Length).Where(i => next.At(i) is not null)
            .OrderByDescending(i => i == next.Master).ToArray();
        var clients = occupied.Select(i => next.At(i)!).ToArray();
        var surfaces = next.Slots;
        if (next.Layout.AvoidTaskbar)
            surfaces = surfaces.Concat(Screen.AllScreens.SelectMany(screen => ReservedTaskbarAreas(screen.Bounds, screen.WorkingArea))).ToArray();
        int swapSlot = next.Layout.SwapTargetIndex;
        var measured = placements.PrepareSources(clients.Select(c => (c.Handle, c.Pid)).ToArray(),
            clients.Select(c => c.Name).ToArray(), occupied.Select(next.Target).ToArray(), next.PrimaryUi, surfaces,
            next.Layout.HideTitleBars, overlapRendererRefreshes,
            preserveWholeMasterHud: !next.Layout.MatchMasterResolution && swapSlot >= 0 &&
                next.At(swapSlot)?.Handle == next.PrimaryUi);
        var actual = new Rectangle[next.Homes.Length];
        for (int j = 0; j < occupied.Length; j++) actual[occupied[j]] = measured[j];
        return actual;
    }

    private static IEnumerable<Rectangle> ReservedTaskbarAreas(Rectangle monitor, Rectangle work)
    {
        if (work.Top > monitor.Top) yield return Rectangle.FromLTRB(monitor.Left, monitor.Top, monitor.Right, work.Top);
        if (work.Bottom < monitor.Bottom) yield return Rectangle.FromLTRB(monitor.Left, work.Bottom, monitor.Right, monitor.Bottom);
        if (work.Left > monitor.Left) yield return Rectangle.FromLTRB(monitor.Left, work.Top, work.Left, work.Bottom);
        if (work.Right < monitor.Right) yield return Rectangle.FromLTRB(work.Right, work.Top, monitor.Right, work.Bottom);
    }

    private bool NativePositionsMatch(Plan current)
    {
        for (int i = 0; i < current.Homes.Length; i++)
            if (current.At(i) is { } c && (!placements.MatchesActiveSlot(c.Handle, current.Target(i)) ||
                GameViewPlacements.ClientBounds(c.Handle, c.Pid) != nativeClients[i])) return false;
        return true;
    }

    private void UpdateStacking(Plan current, GameViewTile?[] tiles)
    {
        SyncSwapButtons(current, tiles);
        var seats = new List<GameViewStacking.Seat>();
        for (int i = 0; i < current.Homes.Length; i++)
            if (current.At(i) is { } c)
                seats.Add(new(c.Handle, c.Pid, tiles[i]?.Handle ?? c.Handle, current.Slots[i],
                    swapButtons.GetValueOrDefault(i)?.Widget.Handle ?? 0));
        stacking.Apply(current.Layout, seats.ToArray(), current.Cover >= 0 ? current.At(current.Cover)!.Handle : 0);
    }

    private GameViewTile OpenPreview(Client client, Plan view, int slot, Size reference)
    {
        string? hint = view.FixedMatching ? $"Click to interact with {client.Name} in this slot." : null;
        bool nativeSlot = !view.Layout.MatchMasterResolution;
        var tile = new GameViewTile(this, client.Handle, client.Pid, client.Name, nativeSlot, hint);
        try { tile.Open(view.Slots[slot], reference); return tile; }
        catch { tile.Dispose(); throw; }
    }

    private static void Raise(Client client)
    {
        const uint noMoveSizeOrActivation = 0x0001 | 0x0002 | 0x0010 | 0x0200;
        if (!GameViewWindows.SameOwner(client.Handle, client.Pid) ||
            !Native.SetWindowPos(client.Handle, 0, 0, 0, 0, 0, noMoveSizeOrActivation))
            throw new InvalidOperationException($"Windows could not bring {client.Name} forward.");
    }

    internal bool IsFixedPreview(GameViewTile tile)
    {
        if (ended || updating || plan is not { FixedMatching: true } current || tile.IsDisposed ||
            !tile.Visible || WaitingForGameDialog) return false;
        int slot = Array.IndexOf(previews, tile);
        if (slot < 0 || current.At(slot) is not { } player || player.Handle != tile.Source ||
            !GameViewWindows.Available(player.Handle, player.Pid) || tile.Bounds != current.Slots[slot]) return false;
        try
        {
            return current.HasPreview(slot) && GameViewPlacements.OccupiesSlot(player.Handle, current.Target(slot)) &&
                tile.TryGetDisplayedClient(out _) && tile.MatchesSourceGeometry();
        }
        catch (InvalidOperationException) { return false; } // Source disappeared or changed during measurement.
    }

    internal bool PromoteOnClick(GameViewTile tile)
    {
        int index = Array.IndexOf(previews, tile);
        if (ended || updating || index < 0) return false;
        // Check that the source is still in its slot before consuming the selection.
        CheckWindows();
        if (ended || plan is not { } current) return false;
        if (current.Master < 0) return false;
        if (current.At(current.Master)?.Handle == tile.Source) return true;
        return previews[index]?.Source == tile.Source && Promote(index);
    }

    private void SetPreviewNotice(string? message)
    {
        if (PreviewNotice == message) return;
        PreviewNotice = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Promote(int index)
    {
        CancelFixedClick();
        CancelCursorReturn();
        var current = plan!;
        var next = current.At(index)!;
        var oldPreview = previews[index];
        oldPreview?.RefreshSource();
        int master = current.Layout.SwapTargetIndex;
        if (master < 0 || master == index) return false;
        RelockSwapButtons();
        bool resizing = !current.Layout.MatchMasterResolution && current.At(master) is not null;
        if (resizing)
        {
            // The held pictures show the incoming occupants before EQ is ready.
            // Hide only the outgoing labels so their old pixels and hitboxes
            // cannot punch holes through the transition.
            swapButtons.GetValueOrDefault(master)?.Widget.Hide();
            swapButtons.GetValueOrDefault(index)?.Widget.Hide();
        }
        try
        {
            using var cover = resizing ? CoverSwapScreens(current, master, index) : null;
            if (!GameViewWindows.TryFocus(next.Handle)) return false;
            var order = current.Order.ToArray();
            (order[master], order[index]) = (order[index], order[master]);
            if (!current.UseMasterResolution)
            {
                // Per-slot resolution and an empty master both need a real placement.
                Configure(current.Layout, current.Slots, current.Homes, background: false, promotedOrder: order);
                return true;
            }
            var swapped = current with { Order = order };
            var nextTiles = previews.ToArray();
            var oldMaster = current.At(master)!;
            GameViewTile? replacement = null;
            try
            {
                Raise(next);
                replacement = OpenPreview(oldMaster, current, index, nativeClients[master].Size);
                nextTiles[index] = replacement;
                UpdateStacking(swapped, nextTiles);
            }
            catch
            {
                replacement?.Dispose();
                try
                {
                    Raise(oldMaster);
                    UpdateStacking(current, previews);
                    if (Native.GetForegroundWindow() == next.Handle) GameViewWindows.TryFocus(oldMaster.Handle);
                }
                catch (Exception rollback) { Stop($"Could not restore the previous window order: {rollback.Message}"); }
                throw;
            }
            (nativeClients[master], nativeClients[index]) = (nativeClients[index], nativeClients[master]);
            plan = swapped;
            previews = nextTiles;
            oldPreview!.Dispose();
            return true;
        }
        finally
        {
            if (resizing && !ended && plan == current && swapButtons.Values.Any(b => !b.Widget.Visible))
                UpdateStacking(current, previews);
        }
    }

    private GameViewSwapCover? CoverSwapScreens(Plan current, int master, int selected)
    {
        var masterScreen = Screen.FromRectangle(current.Slots[master]).Bounds;
        var selectedScreen = Screen.FromRectangle(current.Slots[selected]).Bounds;
        var seats = new List<(nint Surface, Rectangle Slot)>();
        for (int i = 0; i < current.Slots.Length; i++)
        {
            var slot = current.Slots[i];
            if (current.At(i) is not { } client || !(slot.IntersectsWith(masterScreen) || slot.IntersectsWith(selectedScreen)))
                continue;
            seats.Add((previews[i]?.Handle ?? client.Handle, slot));
        }
        return GameViewSwapCover.TryShow(seats, current.Slots[master], current.Slots[selected]);
    }

    private void CheckWindows()
    {
        if (ended || updating || fixedClick != null || plan is not { } current) return;
        try
        {
            if (resolveDisplays is not null && !resolveDisplays(current.Layout).SequenceEqual(current.Slots))
                throw new InvalidOperationException("A layout screen changed. Reapply the layout using the current screens.");
            // A native EQ dialog disables its game owner for input. Keep the
            // current seats, picture and z-order until EQ dismisses the dialog.
            if (WaitingForGameDialog) return;
            if (current.Homes.Any(c => c is not null && (!GameViewWindows.SameOwner(c.Handle, c.Pid) || GameViewWindows.Minimized(c.Handle))))
            {
                var remaining = current.Homes.Select(c => c is not null && GameViewWindows.SameOwner(c.Handle, c.Pid) &&
                    !GameViewWindows.Minimized(c.Handle) ? c : null).ToArray();
                Configure(current.Layout, current.Slots, remaining, background: true);
                return;
            }
            RequireWindowedClients(current);
            UpdateStacking(current, previews);
            var foreground = Native.GetForegroundWindow();
            int selected = current.MasterOccupied && IsPreviewSource(foreground)
                ? Array.FindIndex(previews, tile => tile?.Source == foreground)
                : -1;
            var previousMaximum = placements.NativeMaximumNotice;
            for (int i = 0; i < current.Homes.Length; i++)
            {
                if (current.At(i) is not { } c || !GameViewWindows.Available(c.Handle, c.Pid)) continue;
                var target = current.Target(i);
                // Focusing an EQ window can discard its old crop. When the
                // native client hasn't moved, the swap will verify both new
                // placements. Repairing the outgoing crop can itself resize EQ.
                if (WillRefitAfterFocus(current.Layout.MatchMasterResolution, current.Master, selected, i) &&
                    placements.CanReplaceDroppedSlotClip(c.Handle, c.Pid, nativeClients[i])) continue;
                placements.VerifySlotClip(c.Handle, c.Pid, c.Name);
                if (!placements.MatchesActiveSlot(c.Handle, target))
                    throw new InvalidOperationException("A game window moved or resized. Reapply the layout to use its new position.");
                // EQ owns the frame. A change to its client insets updates both preview and overlay anchors.
                nativeClients[i] = GameViewPlacements.ClientBounds(c.Handle, c.Pid);
                previews[i]?.RefreshSource();
            }
            if (previousMaximum != placements.NativeMaximumNotice) StatusChanged?.Invoke(this, EventArgs.Empty);
            if (selected >= 0 && Native.GetForegroundWindow() == foreground && IsPreviewSource(foreground)) Promote(selected);
        }
        catch (GameViewWindowedModeException ex)
        {
            EndedForWindowMode = true;
            Stop($"Layout ended. {ex.Message} Then apply the layout again.");
        }
        catch (Exception ex) { Stop(ex.Message); }
    }

    internal static bool WillRefitAfterFocus(bool matching, int master, int selected, int slot) =>
        !matching && master >= 0 && selected >= 0 && (slot == master || slot == selected);

    internal void Stop(string reason)
    {
        if (ended) return;
        EndReason = reason;
        Dispose();
    }

    public void Dispose()
    {
        if (ended) return;
        CancelFixedClick();
        if (ended) return;
        ended = true;
        CancelCursorReturn();
        cursorReturnTimer.Dispose();
        StopWatchingForeground();
        monitor.Stop();
        monitor.Dispose();
        ClearSwapButtons();
        foreach (var tile in previews) tile?.Dispose();
        try { placements.Restore(); }
        catch (Exception ex) { EndReason = ex.Message; EndedForWindowMode = false; }
        try { stacking.Dispose(); }
        catch (Exception ex) { EndReason ??= ex.Message; }
        Ended?.Invoke(this, EventArgs.Empty);
    }
}
