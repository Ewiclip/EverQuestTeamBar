using TeamBar;

internal static partial class AppInputSessionTests
{
    private static async Task KnownRosterTests(string directory, LiveSetup live, SavedButton[] buttons, IUserSettingsStore preferences,
        Action<FloatingWorkspace> configure, Action<bool, string> check)
    {
        var primary = live.Identities!.Single();
        var candidate = primary with { ProcessId = 456, StartedUtcTicks = 789, Window = 456, Character = new("Known", 10, 130) };
        var knownTeam = TeamEditing.MergeDetected(live.Team, [candidate]);
        var known = live with { Team = knownTeam };
        DetectedClient[] Eligible(LiveSetup setup, DetectedClient[] scan, DetectedClient[] previous, SavedButton[]? saved = null)
            => AppInputSession.KnownRosterCandidates(setup, saved ?? [], [primary], [primary], scan, previous);
        check(Eligible(known, [candidate], []).Length == 0 && Eligible(known, [candidate], [candidate]).Single() == candidate,
            "Cold-start discovery requires two stable observations before enrolling an already known character.");
        check(Eligible(live, [candidate], [candidate]).Length == 0,
            "An unrelated newly discovered character is never added to the input roster.");
        check(Eligible(live with { AutoConnectCharacterNames = ["Known"] }, [candidate], [candidate]).Length == 1,
            "Configured spell/dock character names can identify an eligible known recipient.");
        check(Eligible(known with { ExcludedCharacterNames = ["kNoWn"] }, [candidate], [candidate]).Length == 0
            && Eligible(known with { ExcludedCharacterNames = ["Known"] }, [candidate with { ProcessId = 457 }], [candidate with { ProcessId = 457 }]).Length == 0,
            "Explicit character exclusions remain effective across process restarts and casing differences.");
        foreach (var invalid in new[] { candidate with { UserAssigned = true }, candidate with { BuildSha256 = "other" },
            candidate with { ClientChannel = "Beta" }, candidate with { Character = new("Known", 0, 130) },
            candidate with { Character = new("Known", 10, 0) }, candidate with { Window = 0 } })
            check(Eligible(known, [invalid], [invalid]).Length == 0,
                "Automatic roster policy rejects manual, foreign-build/channel, and incomplete identities.");
        check(Eligible(known, [candidate, candidate with { ProcessId = 457 }], [candidate]).Length == 0
            && Eligible(known, [candidate], [candidate, candidate with { ProcessId = 457 }]).Length == 0,
            "Duplicate character names in either observation defer automatic enrollment.");
        check(AppInputSession.KnownRosterCandidates(known, [], [primary with { UserAssigned = true }], [], [candidate], [candidate]).Length == 0,
            "A manually assigned roster cannot grant automatic identity discovery trust.");

        using var child = FixtureProcess(live.Controller); child.Start();
        try
        {
            candidate = candidate with { ProcessId = child.Id, StartedUtcTicks = child.StartTime.ToUniversalTime().Ticks };
            knownTeam = TeamEditing.MergeDetected(live.Team, [candidate]);
            var hidden = buttons[0].Value with { Id = "known-dock", Floating = false, Characters = knownTeam.Characters,
                Personas = knownTeam.Personas, Rules = [new(new(CharacterId: "known"), "Ctrl+Shift+Alt+Y")] };
            SavedButton[] hiddenButtons = [new(Path.Combine(directory, "known-dock.json"), hidden, [])];
            check(Eligible(live, [candidate], [candidate], hiddenButtons).Length == 1,
                "A saved dock-only button supplies its known recipient without requiring a floating surface.");
            var cold = live with { Team = live.Team with { Buttons = [] } };
            string path = Path.Combine(directory, "known-roster-team.json");
            TeamEditing.Save(path, cold.Team, []);
            FloatingWorkspace? active = null;
            void Configure(FloatingWorkspace workspace) { configure(workspace); active = workspace; }
            async Task Until(Func<bool> condition)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!condition()) await Task.Delay(20, timeout.Token);
            }
            int scans = 0;
            DetectedClient[] discovered = [candidate];
            void Inject(AppInputSession session)
            {
                session.VerifyRecoveryClient = c => c;
                session.DiscoverRecoveryClients = () => { Interlocked.Increment(ref scans); return new(discovered, []); };
                session.AcquireRecoveryTransition = () => new MemoryStream();
                session.RecoveryPollInterval = TimeSpan.FromMilliseconds(50);
            }
            var original = await AppInputSession.Start(cold, hiddenButtons, path, Path.Combine(directory, "known-before.jsonl"), preferences, Configure);
            Inject(original); active!.SetRecoveryBusy(true); original.RequestRosterRefresh();
            await Until(() => scans >= 2);
            check(ReferenceEquals(AppInputSession.Current, original) && original.ConnectedIdentities.Length == 1
                && !AppInputSession.HasPendingReconnect && !AppInputSession.IsRecovering,
                "Healthy roster monitoring does not interrupt a busy click or mark the host as reconnecting.");
            active.SetRecoveryBusy(false); original.RequestRosterRefresh();
            await Until(() => AppInputSession.Current is { Ready: true } current && current != original
                && current.ConnectedIdentities.Length == 2 && !AppInputSession.IsRecovering);
            var successor = AppInputSession.Current!;
            check(successor.Ready && active!.RecoverySnapshot().Buttons.Length == 1 && active.RecoverySequenceCount == 0
                && !AppInputSession.HasPendingReconnect,
                "A dock-only host automatically enrolls its newly loaded known recipient without replaying a key.");
            original.CloseIfOwned();
            check(successor.IsClosed && AppInputSession.Current is null && !AppInputSession.HasPendingReconnect,
                "An overlay manager's original owner closes its automatic roster successor.");

            var replacement = await AppInputSession.Start(cold, hiddenButtons, path, Path.Combine(directory, "known-manual-replacement.jsonl"), preferences, Configure);
            Inject(replacement); original.CloseIfOwned();
            check(ReferenceEquals(AppInputSession.Current, replacement) && replacement.Ready,
                "An old automatic owner cannot close a separately applied replacement session.");
            await AppInputSession.Clear(path); int afterClear = scans;
            replacement.RequestRosterRefresh(); await Task.Delay(650);
            check(ReferenceEquals(AppInputSession.Current, replacement) && scans == afterClear && !AppInputSession.HasPendingReconnect,
                "Deliberately clearing controls cancels roster monitoring and cannot reopen hidden hotkeys.");
            AppInputSession.Stop();

            discovered = [];
            var shared = await AppInputSession.Start(cold, hiddenButtons, path,
                Path.Combine(directory, "known-shared-clear.jsonl"), preferences, Configure);
            Inject(shared); active!.SetRosterSpellRegistration(true);
            await AppInputSession.Clear(path);
            // The reloaded saved library also contains normal floating controls.
            // A dock edit on a cleared host should update only its known recipients.
            var updatedLibrary = hiddenButtons.Concat(buttons).ToArray();
            AppInputSession.UpdateSavedConfiguration(path, live.Team, updatedLibrary, preserveClearedHotkeys: true);
            check(!active.Visible && active.RecoverySnapshot().Setup.FloatingHotkeysCleared && active.RecoverySnapshot().Buttons.Length == 0,
                "Editing a dock after Close floating buttons leaves the shared spell host's floating buttons closed.");
            discovered = [candidate]; shared.RequestRosterRefresh();
            await Until(() => AppInputSession.Current is { Ready: true } current && current != shared
                && current.ConnectedIdentities.Length == 2 && !AppInputSession.IsRecovering);
            check(active!.RecoverySnapshot().Buttons.Length == 0 && active.RecoverySnapshot().Setup.Team.Buttons.Length == 0
                && (active.RecoverySnapshot().Setup.Team.Groups?.Length ?? 0) == 0 && !active.Visible && active.RecoverySequenceCount == 0,
                "Clearing hotkeys retains shared spell roster refresh without resurrecting floating controls or replaying input.");
            AppInputSession.UpdateSavedConfiguration(path, live.Team, updatedLibrary, preserveClearedHotkeys: true);
            check(!active.Visible && active.RecoverySnapshot().Setup.FloatingHotkeysCleared && active.RecoverySnapshot().Buttons.Length == 0,
                "A dock edit on a reconnected shared host retains the earlier close decision.");
            var previousHost = AppInputSession.Current!;
            using (var resume = new ManualResetEventSlim())
            {
                active.SetRosterSpellRegistration(true);
                previousHost.VerifyRecoveryClient = c => { resume.Wait(TimeSpan.FromSeconds(5)); return c; };
                active.DisconnectRecoveryFixture();
                await Until(() => AppInputSession.Current is null && AppInputSession.HasPendingReconnect);
                AppInputSession.UpdateSavedConfiguration(path, live.Team, updatedLibrary, preserveClearedHotkeys: true);
                resume.Set();
                await Until(() => AppInputSession.Current is { Ready: true } current && current != previousHost
                    && !AppInputSession.IsRecovering);
                check(active.RecoverySnapshot().Buttons.Length == 0 && !active.Visible && active.RecoverySnapshot().Setup.FloatingHotkeysCleared,
                    "An overlay edit during a disconnected spell host's pending recovery cannot repopulate cleared buttons.");
                AppInputSession.Current!.VerifyRecoveryClient = c => c;
            }
            shared.CloseIfOwned();

            using var discoveryGate = new ManualResetEventSlim();
            bool entered = false;
            var stopped = await AppInputSession.Start(cold, hiddenButtons, path, Path.Combine(directory, "known-stop-pending.jsonl"), preferences, Configure);
            Inject(stopped);
            stopped.DiscoverRecoveryClients = () => { entered = true; discoveryGate.Wait(TimeSpan.FromSeconds(3)); return new([candidate], []); };
            stopped.RequestRosterRefresh(); await Until(() => entered);
            AppInputSession.Stop(); discoveryGate.Set(); await Task.Delay(650);
            check(AppInputSession.Current is null && !AppInputSession.HasPendingReconnect,
                "A discovery finishing after Stop cannot resurrect the input host.");

            var revoked = await AppInputSession.Start(cold, hiddenButtons, path, Path.Combine(directory, "known-memory-revoked.jsonl"), preferences, Configure);
            Inject(revoked); GameDataAccess.SetEnabled(MemoryFeature.ScreenDetection, false); AppInputSession.RevokeGameData(MemoryFeature.ScreenDetection);
            revoked.RequestRosterRefresh(); await Task.Delay(650);
            check(AppInputSession.Current is null && !AppInputSession.HasPendingReconnect,
                "Memory revocation cancels ongoing roster reconciliation.");
            var manualClient = primary with { UserAssigned = true };
            var manual = await AppInputSession.Start(cold with { Identities = [manualClient] }, hiddenButtons, path,
                Path.Combine(directory, "known-manual.jsonl"), preferences, Configure);
            Inject(manual); int beforeManual = scans; manual.RequestRosterRefresh(); await Task.Delay(650);
            check(manual.Ready && scans == beforeManual && manual.ConnectedIdentities.Length == 1,
                "Memory-off manual sessions never auto-discover or enroll other characters.");
            AppInputSession.Stop(); GameDataAccess.SetEnabled(MemoryFeature.ScreenDetection, true);
        }
        finally
        {
            AppInputSession.Stop(); GameDataAccess.SetEnabled(MemoryFeature.ScreenDetection, true);
            child.StandardInput.Close(); if (!child.WaitForExit(3000)) child.Kill();
        }
    }
}

internal sealed partial class FloatingWorkspace
{
    internal void SetRosterSpellRegistration(bool registered)
    {
        if (registered) spellWindows[Handle.ToInt64()] = (Environment.ProcessId, "roster-fixture");
        else spellWindows.Clear();
    }
}
