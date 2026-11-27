namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private async Task TestInputStartupRecovery(Action<bool, string> check)
    {
        var originalStart = StartSpellInputHost; var originalTransition = AcquireClickTransition;
        var originalRequest = RequestSpellInput; var originalClock = ClickClock; var originalCapture = CaptureTarget;
        var requests = new List<SpellClickRequest>();
        var now = DateTime.UtcNow; int starts = 0;
        AppInputSession? restoredFixture = null;
        const string timeout = "A client did not begin reading input; finish loading into the game";
        pollTimer.Stop();
        try
        {
            ClickClock = () => now;
            AcquireClickTransition = () => new MemoryStream();
            RequestSpellInput = request => { requests.Add(request); return Task.FromResult<SpellClickReply?>(null); };
            StartSpellInputHost = () => { starts++; throw new IOException(timeout); };
            var configured = applied.Where(d => d.HasClickMappings).ToArray();
            ResetHostRetry(); nextClickCheck = DateTime.MinValue;
            await RefreshClickSession();
            check(starts == 1 && hostRetryFailures == 1 && nextHostRetry == now.AddSeconds(5)
                && ClickSessionStatus.Text.Contains("retrying automatically"),
                "a game-input startup timeout schedules recovery and reports automatic retry");
            foreach (int delay in new[] { 5, 10, 20, 30, 30 })
            {
                int before = starts;
                await ConnectSavedClicks(configured, generation);
                check(starts == before, "startup recovery does not busy-loop during its backoff: " + delay);
                now = nextHostRetry;
                await ConnectSavedClicks(configured, generation);
                check(starts == before + 1 && nextHostRetry == now.AddSeconds(Math.Min(30, delay * 2)),
                    "unchanged clients retry automatically with bounded backoff: " + delay);
            }

            ResetHostRetry(); nextClickCheck = DateTime.MinValue;
            const string unrestoredWindow = "Expected one restored EQ window";
            string unchangedClients = HostAttemptKey(configured);
            int unchangedGeneration = generation;
            var unchangedOverlays = floating.ToDictionary(pair => pair.Key, pair => pair.Value);
            int beforeWindowFailure = starts;
            StartSpellInputHost = () => { starts++; throw new IOException(unrestoredWindow); };
            await RefreshClickSession();
            check(starts == beforeWindowFailure + 1 && hostRetryFailures == 1 && nextHostRetry == now.AddSeconds(5)
                && ClickSessionStatus.Text.Contains("retrying automatically") && clickRegistrations.Count == 0,
                "a minimized game window schedules automatic recovery and leaves spell buttons disconnected");
            now = now.AddSeconds(4);
            await RefreshClickSession();
            check(starts == beforeWindowFailure + 1 && hostRetryFailures == 1,
                "a minimized game window does not trigger another connection before the retry deadline");
            now = now.AddSeconds(1);
            await RefreshClickSession();
            check(starts == beforeWindowFailure + 2 && hostRetryFailures == 2 && nextHostRetry == now.AddSeconds(10),
                "the same minimized client retries automatically with increasing backoff");
            now = now.AddSeconds(10);
            int restoreRequestStart = requests.Count;
            bool restoredHostReady = false;
            RequestSpellInput = request =>
            {
                requests.Add(request);
                return restoredHostReady ? originalRequest(request) : Task.FromResult<SpellClickReply?>(null);
            };
            StartSpellInputHost = async () =>
            {
                starts++;
                restoredFixture = await StartRestoredWindowFixture();
                restoredHostReady = true;
                return restoredFixture;
            };
            await RefreshClickSession();
            check(starts == beforeWindowFailure + 3 && restoredFixture is { Ready: true }
                && ReferenceEquals(ownedClickHost, restoredFixture)
                && lastHostAttempt is null && hostRetryFailures == 0 && clickSession == "fixture"
                && clickRegistrations.Keys.Order().SequenceEqual(configured.Where(d => floating[d.Id].IsVisible).Select(d => d.Id).Order())
                && requests.Skip(restoreRequestStart).Any(r => r.Kind == "register")
                && HostAttemptKey(configured) == unchangedClients && generation == unchangedGeneration
                && unchangedOverlays.All(pair => ReferenceEquals(floating[pair.Key], pair.Value)),
                "restoring the same game session repairs spell registrations without toggling overlays or changing persona");
            check(requests.Skip(restoreRequestStart).All(r => r.Kind != "tap"),
                "restoring a minimized window never sends or replays a spell keypress");
            await CloseOwnedClickHost(); restoredFixture = null;
            RequestSpellInput = request => { requests.Add(request); return Task.FromResult<SpellClickReply?>(null); };

            var previousClients = detected; var previousButtons = savedDockButtons;
            try
            {
                detected = SpellDockTests.Clients(); savedDockButtons = SpellDockTests.Buttons();
                var dockDefinition = configured[0] with { Character = "Fellwort", DockGroups = SpellDockTests.Groups() };
                string before = HostAttemptKey([dockDefinition]);
                detected = detected.Select(c => c.Character.Name == "Runa" ? c with { StartedUtcTicks = c.StartedUtcTicks + 1 } : c).ToArray();
                check(before != HostAttemptKey([dockDefinition]),
                    "a dock-only recipient's changed process session invalidates the previous connection attempt");
            }
            finally { detected = previousClients; savedDockButtons = previousButtons; }

            ResetHostRetry();
            StartSpellInputHost = () => { starts++; throw new InvalidDataException("Input API already redirected by another component"); };
            bool rejected = false;
            try { await ConnectSavedClicks(configured, generation); } catch (InvalidDataException) { rejected = true; }
            int afterFailure = starts;
            await ConnectSavedClicks(configured, generation);
            check(rejected && starts == afterFailure && nextHostRetry == DateTime.MaxValue,
                "an incompatible input hook is not retried as a transient loading timeout");

            ResetHostRetry(); nextClickCheck = DateTime.MinValue;
            var pending = new TaskCompletionSource<AppInputSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartSpellInputHost = () => { starts++; return pending.Task; };
            int captures = 0;
            CaptureTarget = (pid, started) => { captures++; return originalCapture(pid, started); };
            var connecting = RefreshClickSession();
            try
            {
                check(connectingClicks && !connecting.IsCompleted, "the startup fixture waits for game input without generating keys");
                await Poll().WaitAsync(TimeSpan.FromSeconds(5));
                check(captures > 0 && !polling && !connecting.IsCompleted,
                    "target readings continue while an input connection is still waiting");
            }
            finally { pending.TrySetException(new IOException(timeout)); await connecting; }
            RequestSpellInput = request => { requests.Add(request); return originalRequest(request); };
            nextClickCheck = DateTime.MinValue; await RefreshClickSession();
            check(lastHostAttempt is null && hostRetryFailures == 0 && clickRegistrations.Count > 0,
                "a healthy input host clears failed startup state and restores spell registrations");
            check(requests.All(r => r.Kind != "tap"), "startup recovery never sends or replays a spell keypress");
        }
        finally
        {
            if (restoredFixture is not null)
            {
                restoredFixture.CloseIfOwned();
                if (ReferenceEquals(ownedClickHost, restoredFixture)) await CloseOwnedClickHost();
            }
            StartSpellInputHost = originalStart; AcquireClickTransition = originalTransition;
            RequestSpellInput = originalRequest; ClickClock = originalClock; CaptureTarget = originalCapture;
            ResetHostRetry(); nextClickCheck = DateTime.MinValue;
            if (!closed) pollTimer.Start();
        }
    }

    private static async Task<AppInputSession> StartRestoredWindowFixture()
    {
        // Use the existing input-free controller and this test process, never an EQ client.
        string controller = Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe");
        if (!File.Exists(controller)) throw new FileNotFoundException("The input-free UI fixture controller is required.", controller);
        string folder = Path.Combine(Path.GetTempPath(), "team-bar-restored-window-" + Guid.NewGuid().ToString("N"));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var client = new DetectedClient(process.Id, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Fixture", 2, 130));
        var team = new TeamDefinition(1, [new("fixture", "Fixture")], [new("fixture-cleric", "fixture", "Cleric")], []);
        var live = new LiveSetup(controller, team, TeamEditing.Bind(team, [client]), [client]);
        return await AppInputSession.Start(live, [], Path.Combine(folder, "team.json"), Path.Combine(folder, "session.jsonl"),
            new MemoryUserSettingsStore(), workspace =>
            {
                workspace.ReadIdentity = expected => expected;
                workspace.ReadFrame = _ => null;
                workspace.ReadSpellLibrary = () => new(1, []);
                workspace.ReadSpellTarget = (_, _) => throw new InvalidOperationException("This fixture must never read a spell target.");
                workspace.SpellPipeName = "TeamBar-RestoredWindowTest-" + Guid.NewGuid().ToString("N");
            });
    }
}
