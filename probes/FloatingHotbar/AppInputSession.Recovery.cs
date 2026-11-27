using System.Text.Json;
using TeamBar;

internal sealed partial class AppInputSession
{
    private static RecoveryWatch? recovery;
    internal static bool IsRecovering => recovery?.Rebuilding == true;
    internal static bool HasPendingReconnect => recovery?.PendingReconnect == true;
    internal static string? ActiveProfile => Current?.Profile ?? recovery?.Profile;
    internal DetectedClient[] ConnectedIdentities => IsClosed ? [] : workspace.RecoverySnapshot().Setup.Identities?.ToArray() ?? [];
    internal Func<DetectedClient, DetectedClient> VerifyRecoveryClient;
    internal Func<Discovery> DiscoverRecoveryClients;
    internal Func<IDisposable?> AcquireRecoveryTransition = () => SpellOverlayInputHost.TryBeginTransition();
    internal TimeSpan RecoveryPollInterval = TimeSpan.FromSeconds(2);

    internal void RequestRosterRefresh()
    {
        if (ReferenceEquals(Current, this) && recovery is { } watch && ReferenceEquals(watch.Owner, this)) watch.RequestPoll();
    }
    private void WatchKnownCharacters(LiveSetup initial, SavedButton[] saved, IUserSettingsStore? preferences, Action<FloatingWorkspace>? configure)
    {
        if (!GameDataAccess.IsEnabled(identityFeature) || initial.Identities?.Any(c => !c.UserAssigned) != true) return;
        var watch = new RecoveryWatch(this, initial, saved, preferences, configure) { Owner = this, Rebuilding = false };
        recovery = watch;
        _ = watch.Run(initialDelay: true);
    }

    private static void CancelRecovery(AppInputSession? owner = null)
    {
        if (recovery is not { } watch || owner is not null && !ReferenceEquals(watch.Owner, owner)) return;
        recovery = null; watch.Stop.Cancel();
        TeamBarApplication.Current?.CheckIdle();
    }
    private bool TryRecoverDisconnectedClients(IUserSettingsStore? preferences, Action<FloatingWorkspace>? configure)
    {
        if (disposed || !GameDataAccess.IsEnabled(identityFeature) || workspace.LastError != "A client disconnected; reconnect input from Setup") return false;
        if (recovery is { } ongoing)
        {
            if (!ReferenceEquals(ongoing.Owner, this)) return false;
            ongoing.Capture(this);
            ongoing.Owner = null; ongoing.Rebuilding = true;
            Dispose(preserveRecovery: true);
            ongoing.RequestPoll();
            return true;
        }
        var snapshot = workspace.RecoverySnapshot();
        // Spell-only hosts retain their existing overlay-manager lifecycle.
        if (snapshot.Setup.Team.Buttons.Length == 0 && (snapshot.Setup.Team.Groups?.Length ?? 0) == 0 && snapshot.Buttons.Length == 0) return false;
        var watch = new RecoveryWatch(this, snapshot.Setup, snapshot.Buttons, preferences, configure);
        recovery = watch;
        Dispose(preserveRecovery: true); // Drops every interrupted gesture and sequence.
        _ = watch.Run();
        return true;
    }

    private static DetectedClient[] VerifiedSurvivors(LiveSetup previous, Func<DetectedClient, DetectedClient> verify)
    {
        var verified = new List<DetectedClient>();
        foreach (var expected in previous.Identities ?? [])
        {
            try
            {
                var actual = verify(expected);
                if (Identity.SameCharacterSession(expected, actual)) verified.Add(actual);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException
                or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        }
        return verified.ToArray();
    }
    internal static LiveSetup RecoverySetup(LiveSetup previous, Func<DetectedClient, DetectedClient> verify)
    {
        var identities = VerifiedSurvivors(previous, verify);
        if (identities.Length == 0) throw new IOException("Waiting for your selected characters to return.");
        return BindRecovery(previous, identities);
    }
    private static LiveSetup BindRecovery(LiveSetup previous, DetectedClient[] identities)
    {
        ClientSessions.ValidateAssignments(identities);
        var team = TeamEditing.MergeDetected(previous.Team, identities);
        var bindings = TeamEditing.Bind(team, identities); Routing.Validate(team, bindings);
        return previous with { Team = team, Clients = bindings, Identities = identities };
    }

    // A new process needs two consecutive unambiguous, fully loaded observations,
    // followed by verification immediately before connection.
    internal static DetectedClient[] ReturningClients(DetectedClient[] wanted, DetectedClient[] connected,
        DetectedClient[] scan, DetectedClient[] previousScan)
    {
        var added = new List<DetectedClient>();
        foreach (var expected in wanted)
        {
            if (connected.Any(c => c.Character.Name.Equals(expected.Character.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var matches = scan.Where(c => c.Character.Name.Equals(expected.Character.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) continue;
            var candidate = matches[0];
            if (expected.UserAssigned || candidate.UserAssigned || candidate.ClientChannel != "Live"
                || candidate.BuildSha256 != expected.BuildSha256 || candidate.Character.ClassId is < 1 or > 16
                || candidate.Character.Level is < 1 or > 255 || candidate.ProcessId <= 0 || candidate.StartedUtcTicks <= 0 || candidate.Window == 0
                || connected.Any(c => c.ProcessId == candidate.ProcessId)) continue;
            var prior = previousScan.Where(c => c.Character.Name.Equals(expected.Character.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (prior.Length == 1 && !prior[0].UserAssigned && Identity.Same(prior[0], candidate)) added.Add(candidate);
        }
        return added.ToArray();
    }

    internal static DetectedClient[] KnownRosterCandidates(LiveSetup setup, SavedButton[] buttons, DetectedClient[] wanted,
        DetectedClient[] connected, DetectedClient[] scan, DetectedClient[] previousScan)
    {
        var excluded = setup.ExcludedCharacterNames.Concat(wanted.Where(c => c.UserAssigned).Select(c => c.Character.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = setup.Team.Characters.Select(c => c.Name).Concat(buttons.SelectMany(b => b.Value.Characters).Select(c => c.Name))
            .Concat(setup.AutoConnectCharacterNames).Concat(wanted.Select(c => c.Character.Name)).Where(name => !excluded.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trustedBuilds = wanted.Where(c => !c.UserAssigned && c.ClientChannel == "Live").Select(c => c.BuildSha256)
            .ToHashSet(StringComparer.Ordinal);
        var eligible = scan.Where(c => known.Contains(c.Character.Name) && !c.UserAssigned && c.ClientChannel == "Live"
            && trustedBuilds.Contains(c.BuildSha256)).ToArray();
        // ReturningClients requires unique, complete observations in both scans,
        // and never adopts a process already enrolled for another character.
        return ReturningClients(eligible, connected, scan, previousScan);
    }

    private sealed class RecoveryWatch
    {
        internal readonly string Profile;
        internal readonly Guid Ownership;
        internal MemoryFeature IdentityFeature => setup.IdentityFeature;
        internal readonly CancellationTokenSource Stop = new();
        internal AppInputSession? Owner;
        internal bool Rebuilding = true;
        internal bool RequireSpellRegistrations;
        private LiveSetup setup;
        private SavedButton[] buttons;
        private DetectedClient[] wanted;
        private readonly IUserSettingsStore? preferences;
        private readonly Action<FloatingWorkspace>? configure;
        private Func<DetectedClient, DetectedClient> verify;
        private Func<Discovery> discover;
        private Func<IDisposable?> transition;
        private TimeSpan interval;
        private readonly SemaphoreSlim pollRequested = new(0, 1);
        private DetectedClient[] previousScan = [];
        private string? lastMessage;
        private int failures;
        internal bool PendingReconnect => Owner is null || wanted.Any(w => !setup.Identities!.Any(c =>
            c.Character.Name.Equals(w.Character.Name, StringComparison.OrdinalIgnoreCase)));

        internal RecoveryWatch(AppInputSession source, LiveSetup initial, SavedButton[] saved,
            IUserSettingsStore? settings, Action<FloatingWorkspace>? configureWorkspace)
        {
            Profile = source.Profile; Ownership = source.ownership; setup = initial; buttons = saved; wanted = initial.Identities!;
            preferences = settings; configure = configureWorkspace; verify = source.VerifyRecoveryClient;
            discover = source.DiscoverRecoveryClients; transition = source.AcquireRecoveryTransition;
            interval = source.RecoveryPollInterval;
        }
        internal void Capture(AppInputSession source)
        {
            (setup, buttons) = source.workspace.RecoverySnapshot();
            verify = source.VerifyRecoveryClient; discover = source.DiscoverRecoveryClients;
            transition = source.AcquireRecoveryTransition; interval = source.RecoveryPollInterval;
        }
        internal void RequestPoll()
        {
            try { if (pollRequested.CurrentCount == 0) pollRequested.Release(); }
            catch (Exception error) when (error is ObjectDisposedException or SemaphoreFullException) { }
        }
        internal void UpdateConfiguration(TeamDefinition team, SavedButton[] savedButtons, bool preserveClearedHotkeys)
        {
            if (preserveClearedHotkeys && setup.FloatingHotkeysCleared)
            {
                setup = setup.RememberOverlayRecipients(savedButtons);
                return;
            }
            RequireSpellRegistrations = false;
            setup = setup with { Team = team, FloatingHotkeysCleared = false };
            buttons = savedButtons;
        }
        private void Report(string message)
        {
            if (lastMessage == message) return;
            lastMessage = message;
            TeamBarApplication.Current?.NotifyInputRecovery(message, Profile, Owner?.workspace.RecoverySnapshot().Setup.Identities);
        }
        private bool CurrentOwner => ReferenceEquals(Current, Owner);
        internal async Task Run(bool initialDelay = false)
        {
            try
            {
                bool delay = initialDelay;
                long notBefore = 0;
                while (!Stop.IsCancellationRequested && ReferenceEquals(recovery, this))
                {
                    if (delay)
                        await pollRequested.WaitAsync(failures == 0 ? interval : TimeSpan.FromSeconds(Math.Min(30, 2 << failures)), Stop.Token);
                    delay = true;
                    long remaining = notBefore - Environment.TickCount64;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), Stop.Token);
                    if (!GameDataAccess.IsEnabled(IdentityFeature)) break;
                    try { await Poll(); failures = 0; }
                    catch (OperationCanceledException) when (Stop.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        failures = Math.Min(failures + 1, 4);
                        previousScan = [];
                        Report("Waiting to reconnect your selected characters: " + error.Message);
                    }
                    finally
                    {
                        Rebuilding = false;
                        // Repeated stale-status nudges cannot defeat startup-error backoff.
                        notBefore = Environment.TickCount64 + (failures == 0 ? 500 : Math.Min(30000, 2000 << failures));
                    }
                    if (!CurrentOwner) break;
                }
            }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(recovery, this)) recovery = null;
                Stop.Dispose(); pollRequested.Dispose(); TeamBarApplication.Current?.CheckIdle();
            }
        }
        private async Task Poll()
        {
            if (!CurrentOwner) return;
            if (RequireSpellRegistrations && Owner is not null && !Owner.workspace.HasSpellRegistrations) return;
            if (Owner is { } owner) Capture(owner);
            var before = Owner;
            var capturedSetup = setup;
            // Discovery runs off the UI thread at a bounded cadence. A newly loaded
            // known recipient must not require a manual Settings refresh.
            var observed = await Task.Run(() =>
            {
                var survivors = VerifiedSurvivors(capturedSetup, verify);
                return (Survivors: survivors, Scan: discover().Clients);
            }, Stop.Token);
            Stop.Token.ThrowIfCancellationRequested();
            if (!CurrentOwner || !ReferenceEquals(Owner, before) || !GameDataAccess.IsEnabled(IdentityFeature)) return;
            var additions = KnownRosterCandidates(capturedSetup, buttons, wanted, observed.Survivors, observed.Scan, previousScan);
            previousScan = observed.Scan;
            var nextClients = observed.Survivors.Concat(additions).ToArray();
            // Keep a loading selected client enrolled until it is verified again
            // or a stable replacement of that same character is available.
            if (Owner is not null && setup.Identities!.Any(c => !nextClients.Any(n =>
                n.Character.Name.Equals(c.Character.Name, StringComparison.OrdinalIgnoreCase)))) return;
            if (Owner is not null && additions.Length == 0) return;
            if (nextClients.Length > 6)
            { Report("More than six known characters are available. Choose the characters to connect in Settings."); return; }
            if (nextClients.Length == 0)
            {
                Report("Waiting for your selected characters to return. Buttons will reconnect automatically.");
                return;
            }
            if (Owner is not null && !Owner.workspace.CanReconnect) return;
            using var acquired = transition();
            if (acquired is null) return;
            Rebuilding = true;
            var confirmed = await Task.Run(() => nextClients.Select(c =>
            {
                var actual = verify(c);
                if (!Identity.SameCharacterSession(c, actual)) throw new IOException("A returning character is still changing.");
                return actual;
            }).ToArray(), Stop.Token);
            Stop.Token.ThrowIfCancellationRequested();
            if (!CurrentOwner || !ReferenceEquals(Owner, before) || !GameDataAccess.IsEnabled(IdentityFeature)
                || Owner is not null && !Owner.workspace.CanReconnect) return;
            if (Owner is { } active) Capture(active);
            var next = BindRecovery(setup, confirmed);
            var old = Owner; Owner = null;
            old?.Dispose(preserveRecovery: true);
            await Backend.WaitForNativeShutdown();
            Stop.Token.ThrowIfCancellationRequested();
            string log = Path.Combine(Path.GetDirectoryName(Profile)!, "sessions", "recovery-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var restored = await Start(next, buttons, Profile, log, preferences, configure, continuingRecovery: true, continuingOwnership: Ownership);
            if (Stop.IsCancellationRequested) { restored.CloseIfOwned(); return; }
            Owner = restored; setup = next;
            wanted = wanted.Concat(additions).DistinctBy(c => c.Character.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            restored.VerifyRecoveryClient = verify; restored.DiscoverRecoveryClients = discover;
            restored.AcquireRecoveryTransition = transition; restored.RecoveryPollInterval = interval;
            var missing = wanted.Where(w => !confirmed.Any(c => c.Character.Name.Equals(w.Character.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Character.Name).ToArray();
            restored.journal.WriteLine(JsonSerializer.Serialize(new { kind = "clients_recovered", connected = confirmed.Select(c => c.ProcessId),
                returned = additions.Select(c => c.Character.Name), waiting = missing, inputReplayed = false }));
            Report("Buttons connected for " + string.Join(", ", confirmed.Select(c => c.Character.Name)) + "."
                + (missing.Length == 0 ? "" : " Waiting to reconnect: " + string.Join(", ", missing) + ".")
                + " Interrupted clicks were cancelled.");
        }
    }
}

internal sealed partial class FloatingWorkspace
{
    internal bool HasSpellRegistrations => spellWindows.Count > 0;
    internal bool CanReconnect => Ready && !busy && !cancelInFlight && pressed is null && dragged is null && !Capture;
    internal Action? HotkeysClearing;
    internal (LiveSetup Setup, SavedButton[] Buttons) RecoverySnapshot()
    {
        if (pendingConfiguration is { } pending) return (live with { Team = pending.Team }, pending.Buttons);
        var team = live.Team with { Buttons = surfaces.Any(s => s.Condition is null && s.Group is null) ? live.Team.Buttons : [],
            Groups = surfaces.Where(s => s.Group is not null).Select(s => s.Group!).ToArray() };
        // Floating=false definitions still identify configured dock recipients.
        // ClearHotkeys explicitly empties appliedButtons before a roster-only rebuild.
        return (live with { Team = team }, appliedButtons);
    }
}
