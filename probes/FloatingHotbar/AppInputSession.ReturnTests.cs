using System.Diagnostics;
using TeamBar;

internal static partial class AppInputSessionTests
{
    private static async Task ReturnRecoveryTests(string directory, LiveSetup live, SavedButton[] buttons, IUserSettingsStore preferences,
        Action<FloatingWorkspace> configure, Action<bool, string> check)
    {
        var primary = live.Identities!.Single();
        var missing = primary with { ProcessId = 987654, StartedUtcTicks = 123, Window = 123, Character = new("Returning", 5, 130) };
        var candidate = missing with { ProcessId = 456, StartedUtcTicks = 789, Window = 456, Character = new("Returning", 2, 130) };
        check(AppInputSession.ReturningClients([missing], [], [candidate], []).Length == 0,
            "One discovery observation cannot reconnect a new process.");
        check(AppInputSession.ReturningClients([missing], [], [candidate], [candidate]).Single() == candidate,
            "Two matching observations allow the selected character's new process and changed persona.");
        check(AppInputSession.ReturningClients([missing], [], [candidate, missing], [candidate]).Length == 0
            && AppInputSession.ReturningClients([missing], [], [candidate], [candidate, missing]).Length == 0,
            "Ambiguous names in either observation defer reconnection.");
        foreach (var wrong in new[] { candidate with { Character = new("Unselected", 2, 130) }, candidate with { UserAssigned = true },
            candidate with { ClientChannel = "Beta" }, candidate with { BuildSha256 = "different" }, candidate with { Character = new("Returning", 2, 0) } })
            check(AppInputSession.ReturningClients([missing], [], [wrong], [wrong]).Length == 0,
                "Reconnection excludes unselected names, manual identity substitutions, other builds/channels, and incomplete identities.");
        check(AppInputSession.ReturningClients([missing with { UserAssigned = true }], [], [candidate], [candidate]).Length == 0,
            "A manually assigned character cannot silently become an automatically identified process.");
        check(AppInputSession.ReturningClients([missing], [candidate], [candidate], [candidate]).Length == 0,
            "A connected character is not added twice.");

        using var oldProcess = FixtureProcess(live.Controller);
        using var newProcess = FixtureProcess(live.Controller);
        oldProcess.Start(); newProcess.Start();
        try
        {
            var oldClient = missing with { ProcessId = oldProcess.Id, StartedUtcTicks = oldProcess.StartTime.ToUniversalTime().Ticks };
            var newClient = candidate with { ProcessId = newProcess.Id, StartedUtcTicks = newProcess.StartTime.ToUniversalTime().Ticks };
            var team = TeamEditing.MergeDetected(live.Team, [oldClient]);
            var both = live with { Team = team, Clients = TeamEditing.Bind(team, [primary, oldClient]), Identities = [primary, oldClient] };
            string path = Path.Combine(directory, "returning-team.json");
            TeamEditing.Save(path, team, []);
            FloatingWorkspace? active = null;
            void ConfigureReturn(FloatingWorkspace workspace) { configure(workspace); active = workspace; }
            async Task Until(Func<bool> condition)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!condition()) await Task.Delay(20, timeout.Token);
            }
            bool primaryAvailable = true;
            DetectedClient[] discovery = [];
            int scans = 0;
            void ConfigureSession(AppInputSession session)
            {
                session.VerifyRecoveryClient = c => c.ProcessId == primary.ProcessId && primaryAvailable
                    || discovery.Any(d => Identity.Same(d, c)) ? c : throw new IOException("Disconnected");
                session.DiscoverRecoveryClients = () => { Interlocked.Increment(ref scans); return new(discovery, []); };
                session.AcquireRecoveryTransition = () => new MemoryStream();
                session.RecoveryPollInterval = TimeSpan.FromMilliseconds(50);
            }
            var original = await AppInputSession.Start(both, buttons, path, Path.Combine(directory, "return-before.jsonl"), preferences, ConfigureReturn);
            ConfigureSession(original);
            active!.DisconnectRecoveryFixture();
            await Until(() => AppInputSession.Current is { Ready: true } && !AppInputSession.IsRecovering);
            var survivor = AppInputSession.Current!;
            check(AppInputSession.HasPendingReconnect && active!.RecoverySnapshot().Setup.Clients.Length == 1,
                "Healthy survivors remain connected while the app watches for missing selected characters.");
            active!.SetRecoveryBusy(true);
            int previousScans = scans; discovery = [newClient];
            await Until(() => scans >= previousScans + 3);
            check(ReferenceEquals(AppInputSession.Current, survivor), "A returning character cannot interrupt an active click.");
            active.SetRecoveryBusy(false);
            await Until(() => !AppInputSession.HasPendingReconnect);
            var returned = active.RecoverySnapshot();
            check(AppInputSession.Current is { Ready: true } && returned.Setup.Identities!.Any(c => Identity.Same(c, newClient))
                && returned.Setup.Clients.Length == 2 && returned.Buttons.Length == buttons.Length && active.RecoverySequenceCount == 0,
                "A restarted character reconnects automatically with its current persona, saved buttons, and no replayed keys.");
            original.CloseIfOwned(); survivor.CloseIfOwned();
            check(AppInputSession.Current is null && !AppInputSession.HasPendingReconnect,
                "The original manager owner closes its automatically reconnected runtime without leaving a watch.");
            AppInputSession.Stop();

            // Both clients can be lost, and then return separately while the same watch is active.
            discovery = []; primaryAvailable = false;
            var allGone = await AppInputSession.Start(both, buttons, path, Path.Combine(directory, "return-all-gone.jsonl"), preferences, ConfigureReturn);
            ConfigureSession(allGone); active!.DisconnectRecoveryFixture();
            await Until(() => AppInputSession.Current is null && AppInputSession.HasPendingReconnect && !AppInputSession.IsRecovering);
            check(AppInputSession.ActiveProfile == path, "Losing every client retains the applied profile and a cancellable reconnect watch.");
            primaryAvailable = true;
            await Until(() => AppInputSession.Current is { Ready: true } && !AppInputSession.IsRecovering);
            check(active!.RecoverySnapshot().Setup.Clients.Length == 1 && AppInputSession.HasPendingReconnect,
                "The first returning character restores controls while the rest are still absent.");
            primaryAvailable = false; active!.DisconnectRecoveryFixture();
            await Until(() => AppInputSession.Current is null && !AppInputSession.IsRecovering);
            primaryAvailable = true; discovery = [newClient];
            await Until(() => AppInputSession.Current is { Ready: true } && !AppInputSession.HasPendingReconnect);
            check(active!.RecoverySnapshot().Setup.Clients.Length == 2 && active.RecoverySequenceCount == 0,
                "A second disconnect during recovery preserves the original roster until all selected characters return.");
            AppInputSession.Stop();

            discovery = []; primaryAvailable = false;
            var hidden = await AppInputSession.Start(both, buttons, path, Path.Combine(directory, "return-clear.jsonl"), preferences, ConfigureReturn);
            ConfigureSession(hidden); active!.DisconnectRecoveryFixture();
            await Until(() => AppInputSession.Current is null && !AppInputSession.IsRecovering);
            check(await AppInputSession.Clear(path) && !AppInputSession.HasPendingReconnect,
                "Close floating buttons cancels reconnection even when no game clients are connected.");
            discovery = [newClient]; primaryAvailable = true;
            await Task.Delay(150);
            check(AppInputSession.Current is null, "Closed controls do not resurrect when characters return.");
        }
        finally
        {
            AppInputSession.Stop();
            foreach (var process in new[] { oldProcess, newProcess })
            { process.StandardInput.Close(); if (!process.WaitForExit(3000)) process.Kill(); }
        }
    }
    private static Process FixtureProcess(string executable) => new()
    {
        StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true }
    };
}

internal sealed partial class FloatingWorkspace
{
    internal void SetRecoveryBusy(bool value) { busy = value; pendingSince = TapClock(); }
}
