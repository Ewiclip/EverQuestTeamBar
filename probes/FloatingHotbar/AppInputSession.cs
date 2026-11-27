using TeamBar;

// A floating-input runtime belongs to the application, not a child process.
// All window operations run on the application's STA dispatcher.
internal sealed partial class AppInputSession : IDisposable
{
    internal static AppInputSession? Current { get; private set; }
    internal string Profile { get; }
    private readonly MemoryFeature identityFeature;
    internal bool IsClosed => disposed || workspace.IsDisposed;
    internal bool Ready => !IsClosed && workspace.Ready;
    private readonly FloatingWorkspace workspace;
    private readonly StreamWriter journal;
    private readonly BarSession session;
    private Guid ownership = Guid.NewGuid();
    private bool disposed, reportedReady;

    private AppInputSession(LiveSetup live, SavedButton[] buttons, string profile, string log,
        IUserSettingsStore? preferences, Action<FloatingWorkspace>? configure)
    {
        identityFeature = live.IdentityFeature;
        VerifyRecoveryClient = expected => ClientSessions.Verify(expected, identityFeature);
        DiscoverRecoveryClients = () => Identity.Discover(feature: identityFeature);
        Profile = Path.GetFullPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log))!);
        journal = new(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        try
        {
            workspace = new(live, buttons, Profile, journal, preferences ?? new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath));
            configure?.Invoke(workspace);
            if (Path.GetFileName(live.Controller).Equals("BarUiTestController.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Fixture observers cannot accidentally discover or read real clients.
                VerifyRecoveryClient = expected => workspace.ReadIdentity(expected);
                DiscoverRecoveryClients = () => new([], []);
            }
            workspace.HotkeysClearing = () =>
            {
                if (workspace.HasSpellRegistrations && recovery is { } watch && ReferenceEquals(watch.Owner, this))
                {
                    watch.RequireSpellRegistrations = true;
                    watch.Capture(this);
                }
                else CancelRecovery(this);
            };
            session = new(Profile, () => workspace.Ready,
                () => { if (!workspace.IsDisposed) workspace.BeginInvoke(() => Dispose()); },
                workspace.ClearHotkeys, () => workspace.CloseAfterClear);
            workspace.FormClosed += (_, _) =>
            {
                if (reportedReady && workspace.Failed && TryRecoverDisconnectedClients(preferences, configure)) return;
                if (reportedReady && workspace.Failed)
                    TeamBarApplication.Current?.NotifyInputFailure(workspace.LastError ?? "The input connection closed. Reopen Team Bar to reconnect.");
                Dispose();
            };
        }
        catch { workspace?.Dispose(); journal.Dispose(); throw; }
    }
    internal static async Task<AppInputSession> Start(LiveSetup live, SavedButton[] buttons, string profile, string log,
        IUserSettingsStore? preferences = null, Action<FloatingWorkspace>? configure = null, bool continuingRecovery = false, Guid? continuingOwnership = null)
    {
        if (live.Identities?.Any(c => !c.UserAssigned) == true && !GameDataAccess.IsEnabled(live.IdentityFeature))
            throw new InvalidOperationException(GameDataAccess.DisabledReason(live.IdentityFeature));
        if (Current is not null) throw new InvalidOperationException("Close the current input session before replacing it.");
        if (!continuingRecovery) CancelRecovery();
        await Backend.WaitForNativeShutdown();
        if (live.Identities?.Any(c => !c.UserAssigned) == true && !GameDataAccess.IsEnabled(live.IdentityFeature))
            throw new InvalidOperationException(GameDataAccess.DisabledReason(live.IdentityFeature));
        if (Current is not null) throw new InvalidOperationException("Another input session has already connected.");
        var next = new AppInputSession(live, buttons, profile, log, preferences, configure);
        if (continuingRecovery && continuingOwnership is { } ownership) next.ownership = ownership;
        Current = next;
        try
        {
            next.workspace.Start();
            long deadline = Environment.TickCount64 + 15000;
            while (!next.Ready)
            {
                if (next.IsClosed || next.workspace.Failed)
                    throw new IOException(next.workspace.LastError ?? "The input session could not start.");
                if (Environment.TickCount64 >= deadline) throw new IOException("The input session has not finished connecting.");
                await Task.Delay(50);
            }
            next.reportedReady = true;
            if (!continuingRecovery) next.WatchKnownCharacters(live, buttons, preferences, configure);
            return next;
        }
        catch { next.Dispose(); throw; }
    }
    internal static Task<bool> Clear(string profile)
    {
        bool waiting = recovery is { } watch && string.Equals(watch.Profile, Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase);
        if (Current is not { } active || !string.Equals(active.Profile, Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase))
        {
            if (waiting) CancelRecovery();
            return waiting ? Task.FromResult(true) : BarSession.ClearHotkeys(profile);
        }
        return ClearCurrent(active);
    }
    private static async Task<bool> ClearCurrent(AppInputSession active)
    {
        await active.workspace.ClearHotkeys();
        if (active.workspace.CloseAfterClear) active.Dispose();
        return true;
    }
    internal static void Stop() { CancelRecovery(); Current?.Dispose(); }
    internal static void RevokeGameData(MemoryFeature feature)
    {
        if (recovery?.IdentityFeature == feature) CancelRecovery();
        if (Current is { } active && active.identityFeature == feature && active.workspace.RecoverySnapshot().Setup.Identities?.Any(c => !c.UserAssigned) == true) active.Dispose();
    }
    internal void CloseIfOwned()
    {
        // Automatic roster rebuilds remain part of the manager's logical host;
        // a manually applied replacement has a fresh owner and cannot be closed.
        if (recovery is { } watch && watch.Ownership == ownership) CancelRecovery();
        if (Current is { } active && active.ownership == ownership) active.Dispose();
    }
    public void Dispose() => Dispose(preserveRecovery: false);
    private void Dispose(bool preserveRecovery)
    {
        if (disposed) return;
        if (!preserveRecovery) CancelRecovery(this);
        disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        session.Dispose();
        workspace.Close(); workspace.Dispose(); journal.Dispose();
        TeamBarApplication.Current?.CheckIdle();
    }
}
