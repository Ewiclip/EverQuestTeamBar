using System.Text.Json;
using TeamBar;

// Disconnected window regions let independently placed buttons use the exact HWND
// already recognized by both mouse guards. Gaps are outside the window's region.
internal sealed partial class FloatingWorkspace : Form
{
    private sealed class Surface(string path, string heading, ButtonMapping[] actions, RoutedTap[][] taps, StandaloneButton? condition = null, string? anchorId = null, string? anchorName = null)
    {
        internal readonly string Path = path, Heading = heading;
        internal ButtonMapping[] Actions = actions;
        internal RoutedTap[][] Taps = taps;
        internal readonly StandaloneButton? Condition = condition;
        internal readonly string? AnchorId = anchorId, AnchorName = anchorName;
        internal CharacterPositions? Positions;
        internal RelativePosition InitialPosition = new(.5, .85);
        internal GameFrame? Frame;
        internal bool PositionFileUnavailable, HasSavedPosition;
        internal Rectangle Bounds;
        internal bool Visible = true;
        internal ButtonGroup? Group;
        internal ButtonPlacement[] Members = [];
        internal string? MembersKey;
    }
    private readonly List<Surface> surfaces = [];
    private SavedButton[] appliedButtons;
    private LiveSetup live;
    private readonly StreamWriter log;
    private Backend? backend;
    private MouseShield? shield;
    private NotifyIcon? tray;
    private ContextMenuStrip? trayMenu;
    private ToolStripMenuItem? trayStatus;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer placementTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer deliveryTimer = new() { Interval = 50 };

    private long pendingSince;
    private long userCancelledTap;

    private readonly IUserSettingsStore preferences;
    private FloatingAppearance appearance = FloatingThemes.ForApplication(new());
    private readonly ToolTip actionTooltip = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer tooltipTimer = new() { Interval = 500 };
    private bool preferenceError;
    private Surface? hovered;
    private int hoveredAction = -1;
    private bool busy, identityBlocked, identityUnavailable, connected, shown;
    private const string IdentityWaitingMessage = "Character data unavailable · waiting for the character to finish loading";
    private string identityProblem = "Character or game session changed · waiting for the original clients";
    private string status = "Connecting…";
    private long sequence;
    private nint downForeground;
    private Surface? pressed, dragged;
    private int pressedAction = -1;
    private Point dragOffset;
    private Rectangle downBounds;
    private float scale = 1;
    private DetectedClient[] current;
    internal Func<DetectedClient, DetectedClient> ReadIdentity;
    internal Func<DetectedClient, GameFrame?> ReadFrame = AnchorPositions.ReadFrame;
    internal Func<long> TapClock = () => Environment.TickCount64;
    internal bool Failed;
    internal bool Ready => Volatile.Read(ref connected) && Volatile.Read(ref shown) && !Volatile.Read(ref Failed);
    internal bool CloseAfterClear => Volatile.Read(ref identityBlocked);
    internal string? LastError;

    internal FloatingWorkspace(LiveSetup setup, SavedButton[] buttons, string profile, StreamWriter journal, IUserSettingsStore? userSettings = null)
    {
        preferences = userSettings ?? new MemoryUserSettingsStore();
        try { appearance = FloatingThemes.ForApplication(preferences.Load()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { preferenceError = true; journal.WriteLine(JsonSerializer.Serialize(new { kind = "preferences_unavailable", error.Message })); }
        ReadIdentity = expected => ClientSessions.Verify(expected, setup.IdentityFeature);
        live = setup; appliedButtons = buttons; current = setup.Identities ?? throw new InvalidDataException("Floating buttons need detected identities."); log = journal;
        Write("input_identities_bound", new { clients = current });
        Text = "Team Bar · Floating buttons"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        Icon = AppBranding.ApplicationIcon;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual; TopMost = true; DoubleBuffered = true;
        Disposed += (_, _) => { tooltipTimer.Dispose(); actionTooltip.Dispose(); };
        tooltipTimer.Tick += (_, _) =>
        {
            tooltipTimer.Stop();
            if (!Visible || hovered is not { } surface || hoveredAction < 0 || hoveredAction >= surface.Actions.Length || pressed is not null || dragged is not null) return;
            var point = PointToClient(Cursor.Position);
            // Routed game clicks do not generate ordinary WinForms hover messages.
            actionTooltip.Show(FloatingPainter.ActionHint(surface.Actions[hoveredAction]), this, point.X + 12, point.Y + 20, 5000);
        };
        VisibleChanged += (_, _) => { if (!Visible) ClearActionTooltip(); };
        SetStyle(ControlStyles.Selectable, false); BackColor = Color.FromArgb(24, 31, 38);
        surfaces.AddRange(BuildSurfaces(setup, buttons, profile, current));
        timer.Tick += (_, _) => { CheckIdentities(); RefreshAppearance(); };
        placementTimer.Tick += (_, _) => { ApplyPendingConfiguration(); RefreshPlacement(); };
        // This timer only enforces acknowledgement deadlines; it never dispatches taps.
        deliveryTimer.Tick += (_, _) => CheckDelivery();
    }
    private List<Surface> BuildSurfaces(LiveSetup setup, SavedButton[] buttons, string profile, DetectedClient[] identities)
    {
        var result = new List<Surface>();
        if (setup.Team.Buttons.Length > 0)
            result.Add(new(profile, "TEAM BAR", Routing.Display(setup).Buttons, setup.Team.Buttons.Select(b => Routing.Resolve(setup.Team, b.Id, setup.Clients)).ToArray(),
                anchorId: setup.Team.FollowCharacterId, anchorName: setup.Team.Characters.SingleOrDefault(c => string.Equals(c.Id, setup.Team.FollowCharacterId, StringComparison.OrdinalIgnoreCase))?.Name));
        foreach (var group in setup.Team.Groups ?? [])
        {
            if (group.Buttons.Length == 0) continue;
            var surface = new Surface(ButtonGroups.PositionPath(profile, group.Id), group.Name.ToUpperInvariant(), [], [],
                anchorId: group.FollowCharacterId, anchorName: setup.Team.Characters.SingleOrDefault(c => c.Id == group.FollowCharacterId)?.Name) { Group = group };
            RefreshGroup(surface, identities, buttons); result.Add(surface);
        }
        foreach (var saved in buttons.Where(b => b.Value.Floating))
        {
            var taps = StandaloneButtons.Resolve(saved.Value, identities);
            string heading = string.IsNullOrWhiteSpace(saved.Value.Label) ? "" : "STANDALONE";
            result.Add(new(saved.Path, heading, [StandaloneButtons.Display(saved.Value, identities)], [taps], saved.Value,
                saved.Value.FollowCharacterId, saved.Value.Characters.SingleOrDefault(c => string.Equals(c.Id, saved.Value.FollowCharacterId, StringComparison.OrdinalIgnoreCase))?.Name));
        }
        return result;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; p.ExStyle &= ~0x40000; return p; } }
    internal void Start()
    {
        backend = new(live, Handle);
        if (backend.SelectedBridgePath is { } bridgePath)
            Write("input_bridge_selected", new { bridgePath });
        backend.Message += data =>
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(() => Receive(data)); } catch (InvalidOperationException) { }
        };
        backend.Start();
    }
    private void Receive(JsonElement data)
    {
        if (IsDisposed || Disposing) return;
        Write("backend", data);
        if (data.GetProperty("type").GetString() == "overlays") { ReceiveSpellRegistration(data); return; }
        switch (data.GetProperty("type").GetString())
        {
            case "ready":
                spellMouseSupported = data.TryGetProperty("spellOverlays", out var feature) && feature.GetBoolean();
                if (!data.TryGetProperty("alwaysEnabled", out var enabled) || !enabled.GetBoolean() ||
                    !data.TryGetProperty("paused", out var pausedState) || pausedState.GetBoolean())
                { FailSession("The input controller is outdated. Rebuild and reconnect Team Bar."); return; }
                connected = true; status = BuildEdition.ReadyHint; PrepareSurfaces();
                if (surfaces.Count > 0) Show(); else InitializeInputHost();
                break;
            case "cancelled": ReceiveCancelled(); break;
            case "accepted": status = "Sending tap…"; break;
            case "complete": CompleteTap(data, data.GetProperty("ok").GetBoolean(), "Tap incomplete · reconnect input controls; check the session log"); break;
            case "rejected": RejectTap(data); break;
            case "error":
                string message = data.GetProperty("message").GetString() ?? "Input controller disconnected.";
                FailSession(message == "A different TeamBridge build is loaded; restart this client"
                    ? "An EQ client is using a different Team Bar bridge. Camp out, fully exit that EQ client, and reopen it to reconnect."
                    : message);
                return;
        }
        if (identityBlocked) status = identityProblem;
        else if (identityUnavailable) status = IdentityWaitingMessage;
        Invalidate();
    }
    private void PrepareSurfaces()
    {
        scale = DeviceDpi / 96f * (float)appearance.Scale; Opacity = appearance.Opacity;
        var desktop = SystemInformation.VirtualScreen;
        if (!Native.SetWindowPos(Handle, -1, desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x10)) throw new IOException("Cannot position floating controls.");
        var areas = Screen.AllScreens.Select(s => s.WorkingArea).ToArray(); var area = Screen.PrimaryScreen!.WorkingArea;
        int singleIndex = 0;
        var anchoredCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var surface in surfaces)
        {
            var size = SurfaceSize(surface);
            Point fallback = surface.Condition is null ? new(area.Left + 45, area.Top + (int)(110 * scale))
                : new(area.Left + 45 + (singleIndex % 4) * (int)(240 * scale), area.Top + (int)((270 + singleIndex / 4 * 138) * scale));
            if (surface.Condition is not null) singleIndex++;
            Point point = fallback;
            try { var saved = WindowLayouts.LoadBar(surface.Path); point = saved ?? fallback; surface.HasSavedPosition = saved is not null; }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { Write("position_restore_failed", new { surface.Path, error.Message }); }
            surface.Bounds = new(WindowLayouts.Clamp(point, size, areas), size);
            if (surface.AnchorId is not null)
            {
                int number = anchoredCounts.GetValueOrDefault(surface.AnchorName!, 0); anchoredCounts[surface.AnchorName!] = number + 1;
                surface.InitialPosition = new(.12 + .25 * (number % 4), Math.Max(.05, .8 - .13 * (number / 4)));
                try { surface.Positions = AnchorPositions.Find(AnchorPositions.Load(surface.Path), surface.AnchorId); }
                catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                { surface.PositionFileUnavailable = true; Write("screen_positions_unavailable", new { surface.Path, error.Message }); }
            }
        }
        RefreshPlacement(); RebuildRegion();
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); InitializeInputHost();
    }
    private void InitializeInputHost()
    {
        if (shown) return;
        // A spell-only session needs an HWND/message pump, never a visible control strip.
        shield = new(Handle); shown = true; timer.Start(); placementTimer.Start(); deliveryTimer.Start();
        if (surfaces.Count > 0 && TeamBarApplication.Current is null)
        {
            trayMenu = new(); trayStatus = new() { Enabled = false }; trayMenu.Items.Add(trayStatus);
            trayMenu.Items.Add("Close floating buttons", null, (_, _) => Close());
            tray = new() { Icon = AppBranding.TrayIcon, ContextMenuStrip = trayMenu, Visible = true };
            UpdateTray();
        }
        Write("input_host_ready", new { visible = Visible, standalone = surfaces.Count(s => s.Condition is not null), foreground = Native.GetForegroundWindow().ToInt64() });
        StartSpellInputServer();
    }
    private void UpdateTray()
    {
        if (tray is null) return;
        string detail = preferenceError ? "Settings unreadable · check Setup" : status;
        string tooltip = "Team Bar · " + detail;
        tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..60] + "…";
        if (trayStatus is not null) trayStatus.Text = detail;
    }
    private void DisposeTray()
    {
        tray?.Dispose(); tray = null; trayMenu?.Dispose(); trayMenu = null; trayStatus = null;
    }
    protected override void OnInvalidated(InvalidateEventArgs e)
    { base.OnInvalidated(e); UpdateTray(); }
    private GameFrame? FrameFor(Surface surface)
    {
        var client = current.SingleOrDefault(c => string.Equals(c.Character.Name, surface.AnchorName, StringComparison.OrdinalIgnoreCase));
        return client is null ? null : ReadFrame(client);
    }
    private void RefreshPlacement()
    {
        if (dragged is not null || pressed is not null || IsDisposed) return;
        bool changed = false;
        var desktop = SystemInformation.VirtualScreen;
        if (Bounds != desktop)
        {
            if (!Native.SetWindowPos(Handle, -1, desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x10)) return;
            changed = true;
        }
        var frames = new Dictionary<string, GameFrame?>(StringComparer.OrdinalIgnoreCase);
        foreach (var surface in surfaces)
        {
            if (surface.Group is not null && RefreshGroup(surface, current))
            { surface.Bounds = new(surface.Bounds.Location, SurfaceSize(surface)); changed = true; }
            bool visible = surface.Actions.Length > 0 && (surface.Condition is null || StandaloneButtons.Visible(surface.Condition, current));
            if (surface.AnchorId is not null)
            {
                string name = surface.AnchorName!;
                if (!frames.TryGetValue(name, out var frame)) { frame = FrameFor(surface); frames[name] = frame; }
                surface.Frame = frame; visible &= frame is not null;
                if (frame is not null)
                {
                    if (surface.Positions is null)
                    {
                        var fallback = surface.HasSavedPosition && frame.Area.Contains(surface.Bounds.Location)
                            ? AnchorPositions.Normalize(surface.Bounds.Location, surface.Bounds.Size, frame.Area) : surface.InitialPosition;
                        surface.Positions = new(surface.AnchorId, fallback, []);
                        if (!surface.PositionFileUnavailable)
                            try { surface.Positions = AnchorPositions.Seed(surface.Path, surface.AnchorId, fallback); }
                            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                            { surface.PositionFileUnavailable = true; status = "Could not save screen placement · check the session log"; Write("screen_position_save_failed", new { surface.Path, error.Message }); }
                    }
                    var point = AnchorPositions.Resolve(surface.Positions, surface.Bounds.Size, frame);
                    if (point != surface.Bounds.Location) { surface.Bounds = new(point, surface.Bounds.Size); changed = true; }
                }
            }
            if (surface.Visible != visible) { surface.Visible = visible; changed = true; }

        }
        if (changed) RebuildRegion();
    }
    private void RebuildRegion()
    {
        ClearActionTooltip();
        var region = new Region(); region.MakeEmpty();
        foreach (var surface in surfaces.Where(s => s.Visible))
        {
            var rect = surface.Bounds; rect.Offset(-Left, -Top);
            using var outline = FloatingPainter.SurfaceOutline(rect, SurfaceScale(surface)); region.Union(outline);
        }
        var old = Region; Region = region; old?.Dispose(); Invalidate();
    }
    private Surface? At(Point screen) => surfaces.LastOrDefault(s => s.Visible && s.Bounds.Contains(screen));
    private float SurfaceScale(Surface surface) => scale * (float)(surface.Group?.Scale ?? 1);
    private PointF Local(Surface surface, Point screen) => new((screen.X - surface.Bounds.X) / SurfaceScale(surface), (screen.Y - surface.Bounds.Y) / SurfaceScale(surface));
    private int ActionAt(Surface surface, PointF point)
    {
        if (surface.Group is null) return FloatingPainter.ActionAt(surface.Actions.Length, point, FloatingPainter.ShowsHeader(surface.Heading, appearance));
        var cells = GroupCells(surface);
        for (int i = 0; i < cells.Length; i++) if (cells[i].Contains(point)) return i;
        return -1;
    }
    private Size SurfaceSize(Surface surface)
    {
        var size = surface.Group is null ? FloatingPainter.LogicalSize(surface.Actions.Length, FloatingPainter.ShowsHeader(surface.Heading, appearance)) : GroupCanvas(surface);
        return new((int)Math.Ceiling(size.Width * SurfaceScale(surface)), (int)Math.Ceiling(size.Height * SurfaceScale(surface)));
    }
    private void RefreshAppearance()
    {
        // Do not move a button underneath an active click or drag.
        if (pressed is not null || dragged is not null || IsDisposed) return;
        try
        {
            var saved = preferences.Load();
            var next = FloatingThemes.ForApplication(saved);
            bool recovered = preferenceError; preferenceError = false;
            if (next.ApplicationTheme == appearance.ApplicationTheme && LocalUserSettingsStore.SameFloating(next, appearance)) { if (recovered) Invalidate(); return; }
            appearance = next; preferenceError = false;
            scale = DeviceDpi / 96f * (float)appearance.Scale; Opacity = appearance.Opacity;
            foreach (var surface in surfaces)
            {
                var size = SurfaceSize(surface);
                Rectangle[] areas = surface.Frame is { } frame ? [frame.Area] : Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
                surface.Bounds = new(WindowLayouts.Clamp(surface.Bounds.Location, size, areas), size);
            }
            hovered = null; hoveredAction = -1;
            RefreshPlacement(); RebuildRegion();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            if (!preferenceError) Write("preferences_reload_failed", new { error.Message });
            preferenceError = true;
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        foreach (var surface in surfaces.Where(s => s.Visible))
        {
            var state = g.Save(); g.TranslateTransform(surface.Bounds.X - Left, surface.Bounds.Y - Top); g.ScaleTransform(SurfaceScale(surface), SurfaceScale(surface));
            FloatingPainter.Draw(g, surface.Heading, surface.Actions, appearance,
                busy, pressed == surface ? pressedAction : -1, hovered == surface ? hoveredAction : -1,
                surface.Group is null ? null : GroupCells(surface), surface.Group is null ? null : GroupCanvas(surface));
            g.Restore(state);
        }
    }
    protected override void WndProc(ref Message m)
    {

        if (m.Msg == 0x21) { m.Result = 3; return; }
        if (m.Msg == MouseShield.RoutedMouse)
        {
            long packed = m.LParam.ToInt64(); var point = PointToClient(new(unchecked((short)packed), unchecked((short)(packed >> 16))));
            var args = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
            if ((int)m.WParam == 0x201) OnMouseDown(args); else if ((int)m.WParam == 0x202) OnMouseUp(args); else if ((int)m.WParam == 0x200) OnMouseMove(args);
            m.Result = 0; return;
        }
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        ClearActionTooltip();
        var screen = PointToScreen(e.Location); var surface = At(screen); if (surface is null) return;
        downForeground = Native.GetForegroundWindow(); downBounds = surface.Bounds; var point = Local(surface, screen);
        // An untitled surface keeps its top padding as a drag handle above the first action.
        if (!appearance.Locked && !appearance.HideHeaders && point.Y < (FloatingPainter.ShowsHeader(surface.Heading, appearance) ? 27 : 9))
        { dragged = surface; dragOffset = new(screen.X - surface.Bounds.X, screen.Y - surface.Bounds.Y); }
        else { pressed = surface; pressedAction = ActionAt(surface, point); }
        Capture = true; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = At(PointToScreen(e.Location));
        int overAction = over is null ? -1 : ActionAt(over, Local(over, PointToScreen(e.Location)));
        if (over != hovered || overAction != hoveredAction)
        {
            hovered = over; hoveredAction = overAction;
            ClearActionTooltip();
            if (over is not null && overAction >= 0 && pressed is null && dragged is null) tooltipTimer.Start();
            Invalidate();
        }
        if (dragged is not null)
        {
            var screen = PointToScreen(e.Location);
            Rectangle[] areas = dragged.Frame is { } frame ? [frame.Area] : Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
            dragged.Bounds = new(WindowLayouts.Clamp(new(screen.X - dragOffset.X, screen.Y - dragOffset.Y), dragged.Bounds.Size, areas), dragged.Bounds.Size);
            RebuildRegion();
        }
    }
    protected override void OnMouseLeave(EventArgs e)
    { base.OnMouseLeave(e); hovered = null; hoveredAction = -1; ClearActionTooltip(); Invalidate(); }
    private void ClearActionTooltip() { tooltipTimer.Stop(); actionTooltip.Hide(this); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
        var released = pressed; int action = pressedAction; var moved = dragged;
        pressed = dragged = null; pressedAction = -1; Capture = false;
        if (moved is not null) { SavePosition(moved, explicitPlacement: true); RefreshPlacement(); return; }
        var screen = PointToScreen(e.Location);
        if (released is null || At(screen) != released) { Invalidate(); return; }
        var point = Local(released, screen);
        if (action >= 0 && action == ActionAt(released, point))
        {
            if (!connected || cancelInFlight || busy || !CheckIdentities()) return;
            var foreground = Native.GetForegroundWindow();
            if (foreground == downForeground && foreground != Handle && connected && released.Visible && released.Bounds == downBounds)
            {
                var taps = released.Taps[action];
                if (released.Actions[action].DisabledReason is { } reason) status = reason;
                else if (taps.Length == 0) status = "No active recipients for this button";
                else SendTap(released, action);
            }
            else status = "Tap cancelled · focus or connection changed";
        }
        Invalidate();
    }
    private void SendTap(Surface surface, int action)
    {
        if (surface.Actions[action].DisabledReason is { } reason) { status = reason; return; }
        if (surface.Taps[action].Length == 0) { status = "No active recipients for this button"; return; }
        busy = true; pendingSince = TapClock(); pendingSpellTap = false;
        try
        {
            BeginTapSequence(surface.Taps[action]);
            Write("tap_requested", new { id = sequence, button = surface.Actions[action].Id,
                targets = surface.Taps[action] });
        }
        catch (InvalidOperationException error) { busy = false; FailSession("Controller unavailable · " + error.Message); }
    }
    private void CompleteTap(JsonElement data, bool ok, string failure)
    {
        if (!busy || !data.TryGetProperty("id", out var id) || id.GetInt64() != sequence) return;
        if (ok && userCancelledTap != sequence && remainingTaps.Count > 0)
        {
            // Revalidate on the UI thread after the previous keys were released.
            // A persona/loading transition clears the rest and requires a new click.
            if (CheckIdentities() && !cancelInFlight && connected && remainingTaps.Count > 0)
            {
                try { SendNextTap(); return; }
                catch (InvalidOperationException error) { FailSession("Controller unavailable · " + error.Message); return; }
            }
        }
        remainingTaps.Clear(); busy = false; pendingSpellTap = false;
        bool cancelledByUser = userCancelledTap == sequence && (
            data.GetProperty("type").GetString() == "rejected" && data.TryGetProperty("reason", out var reason) && reason.GetString() == "Cancelled" ||
            data.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array && targets.GetArrayLength() > 0 &&
            targets.EnumerateArray().All(t => t.TryGetProperty("result", out var result) && result.GetInt32() is 1 or 4) &&
            targets.EnumerateArray().Any(t => t.GetProperty("result").GetInt32() == 4));
        userCancelledTap = 0;
        if (!ok && !cancelledByUser) { FailSession(failure); return; }

        status = "Tap delivered · foreground kept";
    }
    private void RejectTap(JsonElement data)
    {
        if (!busy || !data.TryGetProperty("id", out var id) || id.GetInt64() != sequence) return;
        string reason = data.TryGetProperty("reason", out var detail) ? detail.GetString() ?? "Tap rejected" : "Tap rejected";
        // These preflight rejections published no key-down. Cancel the finite
        // sequence, retain the controls, and require a fresh deliberate click.
        if (reason is "Release the mapped key first" or "Release modifier keys first" or "A client is not reading input" or "Cancelled")
        {
            remainingTaps.Clear(); busy = pendingSpellTap = false; userCancelledTap = 0; ClearCapturedClick();
            status = reason == "Cancelled" ? "Click cancelled · ready for a new click" : reason + " · click again when ready";
            Write("tap_rejected", new { id = sequence, reason, retryAutomatically = false });
            return;
        }
        CompleteTap(data, false, reason);
    }
    private void FailSession(string reason)
    {
        remainingTaps.Clear();
        ClearCapturedClick(); Failed = true; LastError = reason; status = reason;
        Write("session_failed", new { reason });
        if (connected) try { backend!.Send("cancel"); } catch (InvalidOperationException) { }
        connected = false; cancelInFlight = false; Hide(); Close();
    }
    private void CheckDelivery()
    {
        if (CheckControlTimeout()) return;
        if (busy)
        {
            if (TapClock() - pendingSince >= 3000)
            {
                FailSession("The controller did not acknowledge the tap. Reconnect input controls.");
            }
        }
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { base.OnMouseCaptureChanged(e); if (!Capture) { pressed = dragged = null; pressedAction = -1; Invalidate(); } }
    private void SavePosition(Surface surface, bool explicitPlacement = false)
    {
        try
        {
            if (explicitPlacement && surface.AnchorId is not null)
            {
                var frame = FrameFor(surface);
                if (frame is null || frame != surface.Frame) { status = "Game moved during the drag · place the control again"; return; }
                if (surface.PositionFileUnavailable) throw new IOException("The saved screen-position file could not be read; it was preserved.");
                surface.Positions = AnchorPositions.Remember(surface.Path, surface.AnchorId, frame, surface.Bounds.Location, surface.Bounds.Size);
                status = "Position saved for " + surface.AnchorName + " on this monitor";
            }
            WindowLayouts.SaveBar(surface.Path, surface.Bounds.Location);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { status = "Could not save a button position"; Write("position_save_failed", new { surface.Path, error.Message }); }
    }
    private void Write(string kind, object data) => log.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, kind, data }));
    internal Task ClearHotkeys()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { BeginInvoke(() =>
        {
            try
            {
                CheckIdentities();
                ClearCapturedClick();
                if (busy && !pendingSpellTap) CancelPendingInput("Hotkey library cleared");
                pendingConfiguration = null; appliedButtons = []; surfaces.Clear(); hovered = null; hoveredAction = -1; RebuildRegion(); Hide();
                live = live with { FloatingHotkeysCleared = true };
                HotkeysClearing?.Invoke();
                Write("hotkeys_cleared", new { spellInputRetained = spellInputPipe is not null });
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }); }
        catch (InvalidOperationException error) { completion.TrySetException(error); }
        return completion.Task;
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        remainingTaps.Clear();
        StopSpellInputServer();
        DisposeTray();
        deliveryTimer.Stop(); timer.Stop(); placementTimer.Stop(); if (shown) foreach (var surface in surfaces) SavePosition(surface);
        shield?.Dispose(); shield = null; backend?.Dispose(); backend = null; base.OnFormClosed(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            StopSpellInputServer();
            DisposeTray();
            deliveryTimer.Dispose(); timer.Dispose(); placementTimer.Dispose(); shield?.Dispose(); backend?.Dispose();
        }
        base.Dispose(disposing);
    }
    internal static int Run(LiveSetup live, SavedButton[] buttons, string profile, StreamWriter journal, bool showErrors = true)
    {
        using var workspace = new FloatingWorkspace(live, buttons, profile, journal, new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath));
        using var session = new BarSession(profile, () => workspace.Ready,
            () => { try { workspace.BeginInvoke(() => workspace.Close()); } catch (InvalidOperationException) { } }, workspace.ClearHotkeys,
            () => workspace.CloseAfterClear);
        using var context = new ApplicationContext(); workspace.FormClosed += (_, _) => context.ExitThread(); workspace.Start(); Application.Run(context);
        if (workspace.Failed)
        {
            string error = workspace.LastError ?? "Floating controls disconnected.";
            if (showErrors) MessageBox.Show(error, "Team Bar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else Console.Error.WriteLine(error);
        }
        return workspace.Failed ? 1 : 0;
    }
#if !TEAMBAR_PUBLIC_RELEASE
    internal void SelfTest(string previewPath)
    {
        static void Check(bool value, string why) => StandaloneTests.Check(value, why);
        static void Wait(Func<bool> condition)
        {
            long until = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
            Check(condition(), "Standalone runtime fixture timed out.");
        }
        Point Position(Surface s, int x, int y) => PointToClient(new(s.Bounds.Left + (int)(x * scale), s.Bounds.Top + (int)(y * scale)));
        void Edge(int message, Point p) => Native.SendMessageW(Handle, (uint)message, message == 0x201 ? 1 : 0, (nint)((p.Y << 16) | (p.X & 0xffff)));
        void Click(Surface s, int x, int y) { var p = Position(s, x, y); Edge(0x201, p); Edge(0x202, p); Application.DoEvents(); }
        nint foreground = Native.GetForegroundWindow(); Check(foreground != 0, "Interactive desktop required for native window test.");
        Start(); Wait(() => Ready);
        using (var bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            var bounds = surfaces.Where(s => s.Visible).Select(s => s.Bounds).Aggregate(Rectangle.Union);
            bounds.Offset(-Left, -Top); bounds.Inflate(14, 14); bounds.Intersect(new(0, 0, Width, Height));
            using var rendered = bitmap.Clone(bounds, bitmap.PixelFormat); rendered.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        Check(Ready && foreground == Native.GetForegroundWindow(), "Standalone startup must be ready and nonactivating.");
        Check(surfaces.Count == 2 && surfaces.All(s => s.Heading != "TEAM BAR"), "Standalone workspace must contain only the two saved buttons.");
        var always = surfaces.Single(s => s.Condition?.Id == "always"); var conditional = surfaces.Single(s => s.Condition?.Id == "conditional");
        var center = new Native.Point { X = conditional.Bounds.Left + 50, Y = conditional.Bounds.Top + 55 };
        Check(Region!.IsVisible(center.X - Left, center.Y - Top) && Native.WindowFromPoint(center) == Handle, "Visible standalone island was not registered to the protected HWND.");
        var gap = new Native.Point { X = always.Bounds.Right + 3, Y = always.Bounds.Top + 50 };
        Check(!Region.IsVisible(gap.X - Left, gap.Y - Top) && Native.WindowFromPoint(gap) != Handle, "Empty space between buttons blocks the underlying window.");
        Check(Native.SendMessageW(Handle, 0x21, 0, 0) == 3, "Standalone controls must refuse activation while keeping clicks.");
        var down = Position(always, 40, 60); Edge(0x201, down); Edge(0x202, Position(conditional, 40, 60)); Application.DoEvents();
        Check(sequence == 0, "Releasing over another button must cancel.");
        Click(always, 40, 60); Wait(() => sequence == 1 && !busy);
        Check(foreground == Native.GetForegroundWindow(), "Standalone tap changed foreground.");
        var untouched = conditional.Bounds; var drag = Position(always, 40, 12);
        Edge(0x201, drag); Edge(0x200, new(drag.X + 20, drag.Y + 20)); Edge(0x202, new(drag.X + 20, drag.Y + 20)); Application.DoEvents();
        Check(conditional.Bounds == untouched && WindowLayouts.LoadBar(always.Path) == always.Bounds.Location && sequence == 1, "Dragging must save only its own button and send no tap.");
        var originalAppearance = appearance; var originalSize = always.Bounds.Size; var protectedHandle = Handle;
        void ChangeLook(FloatingAppearance next) { preferences.SaveFloating(next, preferences.Load().Floating); RefreshAppearance(); Application.DoEvents(); }
        ChangeLook(appearance with { ThemeId = "everquest", Locked = true, Opacity = .55 });
        var lockedBounds = always.Bounds; drag = Position(always, 40, 12);
        Edge(0x201, drag); Edge(0x200, new(drag.X + 35, drag.Y + 25)); Edge(0x202, new(drag.X + 35, drag.Y + 25)); Application.DoEvents();
        Check(always.Bounds == lockedBounds && sequence == 1, "A locked header moved a button or sent input.");
        var protectedPoint = new Native.Point { X = always.Bounds.Left + 40, Y = always.Bounds.Top + 60 };
        Check(Handle == protectedHandle && Opacity == .55 && Native.WindowFromPoint(protectedPoint) == Handle, "Opacity changed the protected HWND or made it click-through.");
        down = Position(always, 40, 60); Edge(0x201, down);
        ChangeLook(appearance with { Scale = 1.5, HideHeaders = true });
        Check(always.Bounds.Size == originalSize && !appearance.HideHeaders, "Preferences changed geometry during an active click.");
        CancelPendingInput("Deferred appearance fixture"); Wait(() => !cancelInFlight);
        Edge(0x202, down); RefreshAppearance();
        Check(appearance.HideHeaders && always.Bounds.Size == SurfaceSize(always) && always.Bounds.Width > originalSize.Width, "Resize/header preference did not apply after release.");
        var hiddenBounds = always.Bounds; drag = Position(always, 40, 12);
        Edge(0x201, drag); Edge(0x200, new(drag.X + 400, drag.Y + 25)); Edge(0x202, new(drag.X + 400, drag.Y + 25)); Application.DoEvents();
        Check(always.Bounds == hiddenBounds && sequence == 1 && foreground == Native.GetForegroundWindow(), "Hidden headers retained a drag region or changed focus.");
        ChangeLook(originalAppearance);
        Write("floating_themes_runtime_pass", new { opacityInterceptsClicks = true, locked = true, deferredResize = true, headerRemoved = true, foregroundKept = true, gameInput = false });
        var originalPersona = live.Identities!.Single();
        ReadIdentity = expected => expected with { Character = new("Runa", 2, 130) }; CheckIdentities();
        Check(!identityBlocked && cancelInFlight && !conditional.Visible && always.Visible && Native.WindowFromPoint(center) != Handle, "Persona visibility must update while the old gesture is cancelled.");
        Wait(() => !cancelInFlight);
        ReadIdentity = _ => originalPersona; CheckIdentities(); Check(conditional.Visible, "A matching persona must restore button visibility.");
        Wait(() => !cancelInFlight);
        Click(always, 40, 60); Wait(() => !busy); Check(sequence == 2 && !identityBlocked, "Persona change must allow a fresh click without reapplying.");
        ReadIdentity = _ => throw new InvalidDataException("Fixture loading"); CheckIdentities();
        Check(!conditional.Visible && always.Visible, "Unavailable identity must hide conditional buttons and keep Always visible.");
        backend!.Send("stop-test"); Wait(() => Failed && !Visible);
        Write("standalone_runtime_pass", new { foregroundKept = foreground == Native.GetForegroundWindow(), gameInput = false });
    }
    internal void PlacementSelfTest()
    {
        static void Check(bool value, string why) => StandaloneTests.Check(value, why);
        static void Wait(Func<bool> condition)
        {
            long until = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
            Check(condition(), "Anchor runtime fixture timed out.");
        }
        var area = Screen.PrimaryScreen!.WorkingArea;
        var first = new GameFrame("fixture-monitor-A", "First fixture monitor", new(area.Left + 100, area.Top + 160, Math.Min(1100, area.Width - 200), Math.Min(700, area.Height - 220)));
        GameFrame? frame = first; ReadFrame = _ => frame;
        nint foreground = Native.GetForegroundWindow(); Start(); Wait(() => Ready);
        var bar = surfaces.Single(s => s.Group is not null); var button = surfaces.Single(s => s.Condition is not null);
        Check(bar.Visible && button.Visible && bar.Bounds != button.Bounds, "New attached controls should be visible and start apart.");
        var barOriginal = bar.Bounds; var buttonOriginal = button.Bounds;
        frame = first with { Area = new(first.Area.X + 70, first.Area.Y + 40, first.Area.Width, first.Area.Height) };
        Wait(() => button.Bounds.Location == new Point(buttonOriginal.X + 70, buttonOriginal.Y + 40));
        Check(bar.Bounds.Location == new Point(barOriginal.X + 70, barOriginal.Y + 40), "The grouped bar did not follow the same character window.");
        frame = first with { ScreenId = "fixture-monitor-B", ScreenLabel = "Second fixture monitor", Area = new(area.Left + 200, area.Top + 180, 600, 340) };
        Wait(() => button.Frame?.ScreenId == "fixture-monitor-B");
        Check(frame.Area.Contains(button.Bounds) && frame.Area.Contains(bar.Bounds), "Smaller-screen fallback went outside the game area.");
        Check(AnchorPositions.Find(AnchorPositions.Load(button.Path), "runa")!.Screens.Length == 0, "Automatic following saved an explicit override.");
        var beforeDrag = bar.Bounds;
        var down = PointToClient(new(button.Bounds.Left + 35, button.Bounds.Top + 12));
        void Edge(int message, Point p) => Native.SendMessageW(Handle, (uint)message, message == 0x201 ? 1 : 0, (nint)((p.Y << 16) | (p.X & 0xffff)));
        Edge(0x201, down); Edge(0x200, new(down.X + 45, down.Y - 40)); Edge(0x202, new(down.X + 45, down.Y - 40)); Application.DoEvents();
        var remembered = button.Bounds.Location;
        Check(bar.Bounds == beforeDrag && button.Positions!.Screens.Single().ScreenId == "fixture-monitor-B", "A standalone drag altered the bar or did not save the current monitor.");
        frame = first; Wait(() => button.Frame?.ScreenId == first.ScreenId);
        frame = new("fixture-monitor-B", "Second fixture monitor", new(area.Left + 200, area.Top + 180, 600, 340));
        Wait(() => button.Frame?.ScreenId == "fixture-monitor-B"); Check(button.Bounds.Location == remembered, "Returning to a monitor did not restore its explicit placement.");
        var history = File.ReadAllText(button.Path + ".screen-positions.json");
        preferences.SaveFloating(appearance with { Scale = 1.4, HideHeaders = true, Locked = true }, preferences.Load().Floating); RefreshAppearance();
        Check(button.Bounds.Location == AnchorPositions.Resolve(button.Positions!, button.Bounds.Size, frame) && frame.Area.Contains(button.Bounds), "Resizing did not project the saved monitor position with the new control size.");
        Check(history == File.ReadAllText(button.Path + ".screen-positions.json"), "Changing appearance replaced an explicit monitor placement.");
        var hiddenPoint = new Native.Point { X = button.Bounds.Left + 20, Y = button.Bounds.Top + 45 };
        frame = null; Wait(() => !button.Visible && !bar.Visible);
        Check(Native.WindowFromPoint(hiddenPoint) != Handle, "Unavailable anchor left an invisible click-blocking region.");
        frame = first; Wait(() => button.Visible && bar.Visible);
        Check(sequence == 0 && foreground == Native.GetForegroundWindow(), "Following, restoring or dragging sent input or changed focus.");
        var prior = button.Positions!.Screens.Length; Close();
        Check(AnchorPositions.Find(AnchorPositions.Load(button.Path), "runa")!.Screens.Length == prior, "Closing the session promoted fallback into a monitor override.");
        Write("anchor_runtime_pass", new { foregroundKept = true, gameInput = false });
    }
    internal void GroupedThemeSelfTest()
    {
        static void Check(bool value, string why) => StandaloneTests.Check(value, why);
        static void Wait(Func<bool> condition)
        {
            long until = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
            Check(condition(), "Grouped theme fixture timed out.");
        }
        nint foreground = Native.GetForegroundWindow(); Start(); Wait(() => Ready);
        Check(surfaces.Count == 1, "A bar-only session created unexpected surfaces.");
        var bar = surfaces[0]; Check(bar.Actions.Length == 2, "Expected a two-button bar fixture.");
        void Click(Surface surface, int x, int y)
        {
            var p = PointToClient(new(surface.Bounds.Left + (int)(x * scale), surface.Bounds.Top + (int)(y * scale)));
            nint packed = (nint)((p.Y << 16) | (p.X & 0xffff)); Native.SendMessageW(Handle, 0x201, 1, packed); Native.SendMessageW(Handle, 0x202, 0, packed); Application.DoEvents();
        }
        Click(bar, 40, 60); Wait(() => sequence == 1 && !busy);
        Click(bar, 250, 60); Wait(() => sequence == 2 && !busy);
        Click(bar, 220, 60); Check(sequence == 2, "Padding between grouped buttons sent a tap.");
        preferences.SaveFloating(appearance with { ThemeId = "dark", Scale = .7, HideHeaders = true, Opacity = .35 }, preferences.Load().Floating); RefreshAppearance();
        Click(bar, 250, 30); Wait(() => sequence == 3 && !busy);
        Check(foreground == Native.GetForegroundWindow(), "Themed grouped button took focus.");
        Click(bar, 40, 30); Wait(() => sequence == 4 && !busy);
        Write("grouped_themes_runtime_pass", new { routing = "first / second / second / first", scaledHitRegions = true, alwaysEnabled = true, foregroundKept = true, gameInput = false }); Close();
    }
#endif
}
