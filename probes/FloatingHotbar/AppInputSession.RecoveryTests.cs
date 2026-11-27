using System.Diagnostics;
using System.Text.Json;
using TeamBar;

internal static partial class AppInputSessionTests
{
    private static async Task RecoveryTests(string directory, LiveSetup live, SavedButton[] buttons, IUserSettingsStore preferences,
        Action<FloatingWorkspace> configure, Action<bool, string> check)
    {
        var primary = live.Identities!.Single();
        var absent = primary with { ProcessId = 987654, StartedUtcTicks = 123, Window = 123, Character = new("Gone", 5, 130) };
        var team = TeamEditing.MergeDetected(live.Team, [absent]);
        var previous = live with { Team = team, Clients = TeamEditing.Bind(team, [primary, absent]), Identities = [primary, absent] };
        var surviving = AppInputSession.RecoverySetup(previous, c => c.ProcessId == primary.ProcessId ? c : throw new ArgumentException("Exited"));
        check(surviving.Clients.Length == 1 && surviving.Clients[0].ProcessId == primary.ProcessId
            && surviving.Team.Characters.Length == previous.Team.Characters.Length,
            "Disconnect recovery excludes exited clients without deleting saved character definitions.");
        foreach (var changed in new[] { absent with { StartedUtcTicks = 456 }, absent with { Window = 456 },
            absent with { Character = new("Different", 5, 130) }, absent with { UserAssigned = true } })
        {
            var safe = AppInputSession.RecoverySetup(previous, c => c.ProcessId == primary.ProcessId ? c : changed);
            check(safe.Clients.Length == 1, "Recovery refuses reused PIDs, different windows/characters, and changed identity sources.");
        }
        bool allLost = false;
        try { AppInputSession.RecoverySetup(previous, _ => throw new InvalidDataException("Unavailable")); }
        catch (IOException) { allLost = true; }
        check(allLost, "No verified survivors stops recovery without inventing a binding.");

        using var dummy = new Process { StartInfo = new(live.Controller) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true } };
        dummy.Start();
        try
        {
            var second = absent with { ProcessId = dummy.Id, StartedUtcTicks = dummy.StartTime.ToUniversalTime().Ticks };
            var both = previous with { Clients = TeamEditing.Bind(team, [primary, second]), Identities = [primary, second] };
            string path = Path.Combine(directory, "recovery-team.json");
            TeamEditing.Save(path, team, []);
            FloatingWorkspace? active = null;
            void ConfigureRecovery(FloatingWorkspace workspace) { configure(workspace); active = workspace; }
            async Task Until(Func<bool> condition)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!condition()) await Task.Delay(20, timeout.Token);
            }
            var original = await AppInputSession.Start(both, buttons, path, Path.Combine(directory, "before-recovery.jsonl"), preferences, ConfigureRecovery);
            original.VerifyRecoveryClient = c => c.ProcessId == primary.ProcessId ? c : throw new ArgumentException("Disconnected");
            original.DiscoverRecoveryClients = () => new([], []);
            original.AcquireRecoveryTransition = () => new MemoryStream();
            var oldWindow = active!;
            oldWindow.DisconnectRecoveryFixture();
            await Until(() => !AppInputSession.IsRecovering);
            var restored = AppInputSession.Current;
            check(restored is { Ready: true } && !ReferenceEquals(original, restored) && oldWindow.IsDisposed
                && active is { Visible: true } && active.RecoverySnapshot().Setup.Clients.Single().ProcessId == primary.ProcessId,
                "A client disconnect automatically restores the shared runtime and floating controls for the verified survivor.");
            check(active!.RecoverySnapshot().Buttons.Length == buttons.Length && active.RecoverySequenceCount == 0,
                "Recovery retains standalone buttons and does not replay the interrupted sequence.");
            original.CloseIfOwned();
            check(AppInputSession.Current is null && restored!.IsClosed && !AppInputSession.HasPendingReconnect,
                "The logical input owner can close its automatic recovery successor and cancel monitoring.");
            AppInputSession.Stop();

            // Closing all controls before a later failure must not revive them.
            var cleared = await AppInputSession.Start(live, buttons, path, Path.Combine(directory, "cleared-recovery.jsonl"), preferences, ConfigureRecovery);
            cleared.VerifyRecoveryClient = c => c;
            cleared.DiscoverRecoveryClients = () => new([], []);
            cleared.AcquireRecoveryTransition = () => new MemoryStream();
            await AppInputSession.Clear(path);
            check(active!.RecoverySnapshot().Buttons.Length == 0 && active.RecoverySnapshot().Setup.Team.Buttons.Length == 0,
                "Recovery snapshots omit deliberately cleared hotkeys.");
            AppInputSession.Stop();

            using var gate = new ManualResetEventSlim();
            var cancellable = await AppInputSession.Start(live, buttons, path, Path.Combine(directory, "cancel-recovery.jsonl"), preferences, ConfigureRecovery);
            cancellable.VerifyRecoveryClient = c => { gate.Wait(TimeSpan.FromSeconds(3)); return c; };
            cancellable.DiscoverRecoveryClients = () => new([], []);
            cancellable.AcquireRecoveryTransition = () => new MemoryStream();
            active!.DisconnectRecoveryFixture();
            check(AppInputSession.IsRecovering, "Recovery holds application lifetime while rebuilding input.");
            AppInputSession.Stop(); gate.Set();
            await Until(() => !AppInputSession.IsRecovering);
            check(AppInputSession.Current is null, "An explicit stop during recovery prevents controls from reopening.");
        }
        finally
        {
            AppInputSession.Stop(); dummy.StandardInput.Close();
            if (!dummy.WaitForExit(3000)) dummy.Kill();
        }
    }
}

internal sealed partial class FloatingWorkspace
{
    internal long RecoverySequenceCount => sequence;
    internal void DisconnectRecoveryFixture()
    {
        busy = true; sequence = 20;
        remainingTaps = TapSequence.Create([new(Environment.ProcessId, "fixture", "Fixture", "1", 2),
            new(Environment.ProcessId, "fixture", "Fixture", "2", 3)]);
        Receive(JsonSerializer.SerializeToElement(new { type = "error", message = "A client disconnected; reconnect input from Setup" }));
    }
}
