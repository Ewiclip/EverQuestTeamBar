using F = System.Windows.Forms;
namespace TeamBar;

internal sealed partial class TeamBarApplication
{
    private sealed class RunningView(GameViewLayout layout, Rectangle[] slots, GameViewSession session,
        GameSession?[] clients, Func<GameSession?[], GameSession?[]>? refresh)
    {
        internal GameViewLayout Layout { get; } = layout;
        internal Rectangle[] Slots { get; } = slots;
        internal GameViewSession Session { get; } = session;
        internal GameSession?[] Clients = clients;
        internal Func<GameSession?[], GameSession?[]>? Refresh { get; } = refresh;
        internal GameViewWindow?[]? FailedAssignment;
        internal int FailedAttempts;
        internal long RetryAt;
    }

    private RunningView? activeGameView;
    private AppearanceTheme gameViewTheme = AppearanceTheme.Dark;
    private readonly F.ToolStripItem endGameViewMenu;
    private readonly System.Windows.Threading.DispatcherTimer layoutRosterTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly System.Windows.Threading.DispatcherTimer launchSettingsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? launchRepairError;

    private void MonitorLaunchSettings()
    {
        CheckLaunchSettings(force: true);
        launchSettingsTimer.Tick += (_, _) => CheckLaunchSettings(force: false);
        launchSettingsTimer.Start();
    }

    private void CheckLaunchSettings(bool force)
    {
        GameViewLaunchGuard.Shared.Poll(force);
        var error = GameViewLaunchGuard.Shared.LastError;
        if (force && error is not null && error != launchRepairError)
            tray.ShowBalloonTip(5000, "Team Bar window layout", $"EQ launch position recovery will retry. {error}", F.ToolTipIcon.Warning);
        if (force || error is null) launchRepairError = error;
    }
    private bool checkingLayoutRoster;
    private int screenDetectionRevision;
    internal string? ActiveGameViewName => activeGameView?.Layout.Name;
    internal GameViewSession? ActiveGameViewSession => activeGameView?.Session;
    internal string GameViewStatus { get; private set; } = "";
    internal event EventHandler? GameViewChanged;

    internal void SetGameViewTheme(AppearanceTheme theme)
    {
        if (gameViewTheme == theme) return;
        gameViewTheme = theme;
        activeGameView?.Session.SetSwapButtonTheme(FloatingThemes.ForAppTheme(theme));
    }

    internal GameSession?[] PreviousLayoutSeats(GameViewLayout layout) =>
        activeGameView is { } prior && prior.Layout.Characters.Length == layout.Characters.Length
            ? prior.Clients.ToArray() : new GameSession?[layout.Characters.Length];

    private GameSession?[] WithSwapButtonNames(string? file, GameViewLayout layout, GameSession?[] clients)
    {
        if (!layout.AddSwapButton || layout.Windows is null || file is null || OpenSetupForProfile(file) is not { } setup) return clients;
        return clients.Select((game, slot) => game is not null && layout.Windows[slot] is { } pin &&
            GameViewWindow.From(game) == pin && setup.InlineWindowCharacter(pin) is { } name
                ? game with { IdentifiedCharacterName = name } : game).ToArray();
    }

    private void ApplyGameView(string file, GameViewLayout layout, Rectangle[] slots, GameSession?[] clients,
        Func<GameSession?[], GameSession?[]>? refresh = null, bool keepFocus = false)
    {
        var session = activeGameView?.Session;
        var namedClients = WithSwapButtonNames(file, layout, clients);
        if (session is null)
        {
            var positions = new GameViewSwapButtonPositions(file);
            session = GameViewSession.Start(layout, slots, namedClients, positions.Read, positions.Save, gameViewTheme);
            session.Ended += GameViewEnded;
            session.StatusChanged += LayoutStatusChanged;
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(file, activeLayoutProfile))
            session.Apply(layout, slots, namedClients, background: keepFocus);
        else
        {
            var previousRead = session.ReadSwapButtonPosition;
            var previousSave = session.SaveSwapButtonPosition;
            var positions = new GameViewSwapButtonPositions(file);
            session.ReadSwapButtonPosition = positions.Read;
            session.SaveSwapButtonPosition = positions.Save;
            try { session.Apply(layout, slots, namedClients, background: keepFocus); }
            catch
            {
                session.ReadSwapButtonPosition = previousRead;
                session.SaveSwapButtonPosition = previousSave;
                throw;
            }
        }

        activeLayoutProfile = file;
        activeGameView = new(layout, slots, session, clients, refresh);
        endGameViewMenu.Visible = true;
        layoutRosterTimer.Stop();
        layoutRosterTimer.Tick -= RefreshLayoutRoster;
        if (refresh is not null && (layout.Characters.Any(name => !string.IsNullOrWhiteSpace(name)) || layout.Windows?.Any(w => w is not null) == true))
        {
            layoutRosterTimer.Tick += RefreshLayoutRoster;
            layoutRosterTimer.Start();
        }
        ShowLayoutStatus();
    }

    private void ShowLayoutStatus(string? detail = null)
    {
        if (activeGameView is not { } view) return;
        string status = $"{view.Layout.Name} is active · {view.Session.OccupiedCount} of {view.Layout.Characters.Length} slots occupied.";
        if (view.Session.StationaryPreviewClicks && !view.Layout.SwapOnClick && view.Layout.MatchMasterResolution)
            status += " Testing stationary clicks with game focus.";
        if (view.Session.RestoredProfileFallbacks is { Length: > 0 } restored)
            status += $" EQ kept the restored UI layout for {string.Join(", ", restored)} when focused.";
        if (view.Session.NativeMaximumNotice is { } maximum) status += " " + maximum;
        if (view.Session.PreviewNotice is { } notice) status += " " + notice;
        if (detail is not null) status += " " + detail;
        if (status == GameViewStatus) return;
        GameViewStatus = status;
        GameViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LayoutStatusChanged(object? sender, EventArgs e) => ShowLayoutStatus();

    private async void RefreshLayoutRoster(object? sender, EventArgs e)
    {
        if (checkingLayoutRoster || exiting || disposed || activeGameView is not { Refresh: { } scan } view) return;
        checkingLayoutRoster = true;
        int revision = screenDetectionRevision;
        try
        {
            var next = await Task.Run(() => scan(view.Clients));
            // An older lookup must never alter a newer wizard selection or a closed session.
            if (!ReferenceEquals(activeGameView, view) || revision != screenDetectionRevision || exiting || disposed) return;
            if (view.Session.WaitingForGameDialog) return;
            var keys = next.Select(c => c is not null && GameViewWindows.Available((nint)c.Window, c.ProcessId)
                ? GameViewWindow.From(c) : null).ToArray();
            if (view.FailedAssignment?.SequenceEqual(keys) == true)
            {
                if (Environment.TickCount64 < view.RetryAt) return;
            }
            else view.FailedAttempts = 0;
            try { view.Session.Apply(view.Layout, view.Slots, WithSwapButtonNames(activeLayoutProfile, view.Layout, next), background: true); }
            catch (GameViewWindowedModeException)
            {
                // EQ keeps the same HWND when switching modes. Recheck on the next roster scan.
                view.FailedAssignment = null;
                view.FailedAttempts = 0;
                throw;
            }
            catch
            {
                // A newly logged-in renderer can be briefly unavailable. Allow two
                // delayed retries, then wait for a changed roster or an explicit Apply.
                view.FailedAssignment = keys;
                view.RetryAt = ++view.FailedAttempts switch
                {
                    1 => Environment.TickCount64 + 10_000,
                    2 => Environment.TickCount64 + 45_000,
                    _ => long.MaxValue
                };
                throw;
            }
            view.Clients = next;
            view.FailedAssignment = null;
            ShowLayoutStatus();
        }
        catch (GameViewWindowedModeException error)
        {
            if (ReferenceEquals(activeGameView, view)) ShowLayoutStatus($"{error.Message} Team Bar will retry automatically.");
        }
        catch (Exception error)
        {
            if (ReferenceEquals(activeGameView, view)) ShowLayoutStatus(error.Message +
                (view.FailedAttempts >= 3 ? " Use Apply layout to retry." : ""));
        }
        finally { checkingLayoutRoster = false; }
    }

    internal void EndGameView() => activeGameView?.Session.Dispose();

    private void GameViewEnded(object? sender, EventArgs e)
    {
        if (activeGameView is not { } running || !ReferenceEquals(running.Session, sender)) return;
        layoutRosterTimer.Stop();
        layoutRosterTimer.Tick -= RefreshLayoutRoster;
        running.Session.Ended -= GameViewEnded;
        running.Session.StatusChanged -= LayoutStatusChanged;
        activeGameView = null;
        endGameViewMenu.Visible = false;
        GameViewStatus = running.Session.EndedForWindowMode
            ? $"{running.Layout.Name} ended when EQ switched to fullscreen. Apply the layout to restart it."
            : running.Session.EndReason ?? $"{running.Layout.Name} ended.";
        if (running.Session.EndReason is { Length: > 0 } reason && !exiting && !disposed)
            tray.ShowBalloonTip(4500, "Team Bar window layout", reason, F.ToolTipIcon.Info);
        GameViewChanged?.Invoke(this, EventArgs.Empty);
        CheckIdle();
    }
}
