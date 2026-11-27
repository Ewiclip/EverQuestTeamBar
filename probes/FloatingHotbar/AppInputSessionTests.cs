using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using TeamBar;
using W = System.Windows;

internal static partial class AppInputSessionTests
{
    internal static void Run(string output)
    {
        using var access = GameDataAccess.FixtureScope(true);
        string result = Path.GetFullPath(output), directory = result + ".fixtures";
        if (File.Exists(result) || Directory.Exists(directory)) throw new IOException("Application runtime test output must be new.");
        if (W.Application.Current is not null || AppInputSession.Current is not null)
            throw new InvalidOperationException("Run the application runtime fixture in its own process.");
        string controller = Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe");
        if (!File.Exists(controller)) throw new FileNotFoundException("The input-free UI fixture controller is required.", controller);
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        using var process = Process.GetCurrentProcess();
        long started = process.StartTime.ToUniversalTime().Ticks;
        var client = new DetectedClient(Environment.ProcessId, started, 1, 0, new("Fixture", 2, 130));
        var team = new TeamDefinition(1, [new("fixture", "Fixture")], [new("fixture-cleric", "fixture", "Cleric")],
            [new("action", "Fixture action", [new(new(CharacterId: "fixture"), "[")])]);
        var live = new LiveSetup(controller, team, TeamEditing.Bind(team, [client]), [client]);
        string profile = Path.Combine(directory, "team.json");
        TeamEditing.Save(profile, team, live.Clients);
        var standalone = new StandaloneButton(1, "single", "Fixture single key", team.Characters, team.Personas,
            [new(new(CharacterId: "fixture"), "]")], []);
        StandaloneButtons.Save(StandaloneButtons.PathFor(profile, standalone.Id), standalone, null);
        var buttons = StandaloneButtons.Load(profile);
        string spellPipe = "TeamBar-AppRuntimeTest-" + Guid.NewGuid().ToString("N");
        string firstLog = Path.Combine(directory, "first.jsonl"), secondLog = Path.Combine(directory, "second.jsonl");
        var preferences = new MemoryUserSettingsStore();
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        SetupWindow? setup = null;
        FloatingWorkspace? firstWorkspace = null, secondWorkspace = null;
        int ticks = 0;
        var pulse = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        pulse.Tick += (_, _) => ticks++;

        void Configure(FloatingWorkspace workspace)
        {
            workspace.ReadIdentity = expected => expected;
            workspace.ReadFrame = _ => null;
            workspace.ReadSpellLibrary = () => new(1, []);
            workspace.ReadSpellTarget = (_, _) => throw new InvalidOperationException("This fixture must never read a spell target.");
            workspace.SpellPipeName = spellPipe;
        }
        async Task Responsive(string stage)
        {
            int before = ticks;
            await Task.Delay(100);
            await app.Dispatcher.InvokeAsync(() => Check(app.Dispatcher.CheckAccess(), "Runtime remains on the application dispatcher: " + stage), DispatcherPriority.ContextIdle);
            Check(ticks > before, "WPF dispatcher continues processing while input is active: " + stage);
        }
        async Task<SpellClickReply> SpellStatus()
        {
            var status = await SpellOverlayInputPipe.RequestNamed(new("status"), spellPipe).WaitAsync(TimeSpan.FromSeconds(5));
            Check(status is { Ok: true, Paused: false, Session: not null }, "Fixture spell endpoint reports a ready input session.");
            return status!;
        }
        async Task Closed(AppInputSession session, FloatingWorkspace workspace, string log)
        {
            Check(session.IsClosed && !session.Ready && workspace.IsDisposed && !workspace.IsHandleCreated,
                "Stopping input disposes its workspace and HWND.");
            Check(await BarSession.Request(profile, "status").WaitAsync(TimeSpan.FromSeconds(5)) is null,
                "Stopping input releases its profile pipe.");
            Check(await SpellOverlayInputPipe.RequestNamed(new("status"), spellPipe).WaitAsync(TimeSpan.FromSeconds(5)) is null,
                "Stopping input releases its spell pipe.");
            using var journal = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(journal.Length > 0, "Stopping input closes its journal and leaves the session record readable.");
        }
        app.Startup += async (_, _) =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                pulse.Start();
                var first = await AppInputSession.Start(live, buttons, profile, firstLog, preferences,
                    workspace => { Configure(workspace); firstWorkspace = workspace; });
                Check(ReferenceEquals(AppInputSession.Current, first) && first.Ready && firstWorkspace is not null,
                    "Input starts inside the application and becomes its active runtime.");
                var firstWindow = firstWorkspace ?? throw new InvalidOperationException("The first workspace was not configured.");
                nint firstHandle = firstWindow.Handle;
                GetWindowThreadProcessId(firstHandle, out uint owner);
                Check(owner == Environment.ProcessId, "Floating workspace HWND belongs to the application process.");
                var firstBarStatus = await BarSession.Request(profile, "status").WaitAsync(TimeSpan.FromSeconds(5));
                Check(firstBarStatus is { Ready: true } && firstBarStatus.ProcessId == Environment.ProcessId && firstBarStatus.StartedUtcTicks == started,
                    "Profile endpoint identifies the current application process.");
                string firstSpellSession = (await SpellStatus()).Session!;
                firstWindow.ConfigurationSelfTest(profile, Check);
                Check((await SpellStatus()).Session == firstSpellSession && firstWindow.Handle == firstHandle,
                    "Live configuration changes retain the same input session and mouse-guard HWND.");
                await Responsive("first start");

                using (var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await PipeDisconnectTests.Abandon(spellPipe, JsonSerializer.Serialize(new SpellClickRequest("status")), true, disconnectTimeout.Token);
                Check((await SpellStatus()).Session == firstSpellSession,
                    "A disconnected spell client preserves the active runtime session.");
                await Responsive("disconnected spell client");

                setup = new(profile, controller, () => new([client], []), reviewOnly: true, userSettings: preferences)
                    { ShowActivated = false };
                bool setupClosed = false;
                setup.Closed += (_, _) => setupClosed = true;
                setup.Show();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                setup.Close(); setup = null;
                Check(setupClosed && !app.Dispatcher.HasShutdownStarted && first.Ready && ReferenceEquals(AppInputSession.Current, first),
                    "Closing the configuration window leaves the application input runtime alive.");
                Check((await SpellStatus()).Session == firstSpellSession,
                    "Closing configuration preserves the existing spell connection.");

                Check(!await AppInputSession.Clear(Path.Combine(directory, "other-team.json")) && first.Ready,
                    "Clearing an unrelated profile cannot alter the active runtime.");
                Check(await AppInputSession.Clear(profile.ToUpperInvariant()) && first.Ready && ReferenceEquals(AppInputSession.Current, first)
                    && firstWindow.Handle == firstHandle && !firstWindow.Visible,
                    "Clearing healthy hotkeys retains the same ready hidden input host.");
                Check((await SpellStatus()).Session == firstSpellSession,
                    "Clearing hotkeys preserves the spell endpoint session.");
                await Responsive("cleared host");

                AppInputSession.Stop();
                Check(AppInputSession.Current is null, "Stop removes the active runtime reference.");
                await Closed(first, firstWindow, firstLog);
                var second = await AppInputSession.Start(live, buttons, profile, secondLog, preferences,
                    workspace => { Configure(workspace); secondWorkspace = workspace; });
                Check(second.Ready && !ReferenceEquals(first, second) && !ReferenceEquals(firstWorkspace, secondWorkspace)
                    && ReferenceEquals(AppInputSession.Current, second), "Reapply creates fresh runtime and workspace instances.");
                var secondWindow = secondWorkspace ?? throw new InvalidOperationException("The replacement workspace was not configured.");
                GetWindowThreadProcessId(secondWindow.Handle, out uint replacementOwner);
                Check(replacementOwner == Environment.ProcessId, "Replacement HWND remains in the application process.");
                string secondSpellSession = (await SpellStatus()).Session!;
                Check(secondSpellSession != firstSpellSession, "Replacement uses a fresh spell session on the released endpoint.");
                first.CloseIfOwned();
                Check(second.Ready && !secondWindow.IsDisposed && ReferenceEquals(AppInputSession.Current, second)
                    && (await SpellStatus()).Session == secondSpellSession,
                    "A stale owner cannot close the replacement input runtime.");
                await Responsive("replacement");
                GameDataAccess.SetEnabled(MemoryFeature.ScreenDetection, false); AppInputSession.RevokeGameData(MemoryFeature.ScreenDetection);
                await Closed(second, secondWindow, secondLog);
                Check(AppInputSession.Current is null, "Revoking memory access stops an active automatically identified input runtime.");
                GameDataAccess.SetEnabled(MemoryFeature.ScreenDetection, true);
                await RecoveryTests(directory, live, buttons, preferences, Configure, Check);
                await ReturnRecoveryTests(directory, live, buttons, preferences, Configure, Check);
                await KnownRosterTests(directory, live, buttons, preferences, Configure, Check);
                GameDataAccess.SetEnabled(MemoryFeature.ScreenDetection, false);
                bool blockedAuto = false;
                try { await AppInputSession.Start(live, buttons, profile, Path.Combine(directory, "blocked-auto.jsonl"), preferences, Configure); }
                catch (InvalidOperationException) { blockedAuto = true; }
                Check(blockedAuto && AppInputSession.Current is null, "Memory-off mode rejects an automatically identified input session before starting its backend.");
                var spellOnly = await AppInputSession.Start(live with { IdentityFeature = MemoryFeature.SpellBarData }, [], profile,
                    Path.Combine(directory, "spell-only.jsonl"), preferences, Configure);
                AppInputSession.RevokeGameData(MemoryFeature.ScreenDetection);
                GameDataAccess.SetEnabled(MemoryFeature.DataCollection, false); AppInputSession.RevokeGameData(MemoryFeature.DataCollection);
                Check(spellOnly.Ready && (await SpellStatus()).Ok, "Spell input connects and remains ready with screen detection and data collection off.");
                GameDataAccess.SetEnabled(MemoryFeature.SpellBarData, false); AppInputSession.RevokeGameData(MemoryFeature.SpellBarData);
                Check(spellOnly.IsClosed && AppInputSession.Current is null && !AppInputSession.HasPendingReconnect,
                    "Revoking spell data closes the spell-only input host and its reconnect watch.");
                var manual = client with { UserAssigned = true };
                FloatingWorkspace? manualWorkspace = null;
                var manualSession = await AppInputSession.Start(live with { Identities = [manual] }, buttons, profile,
                    Path.Combine(directory, "manual.jsonl"), preferences, workspace => { Configure(workspace); manualWorkspace = workspace; });
                Check(manualSession.Ready, "Manually assigned floating buttons connect with memory reading off.");
                AppInputSession.RevokeGameData(MemoryFeature.ScreenDetection);
                Check(manualSession.Ready && ReferenceEquals(AppInputSession.Current, manualSession), "Revoking game-data access preserves a manually assigned input host.");
                manualWorkspace!.SendManualFixtureTap(Check);
                foreach (string log in new[] { firstLog, secondLog })
                    foreach (string line in File.ReadLines(log))
                    {
                        using var entry = JsonDocument.Parse(line);
                        Check(entry.RootElement.GetProperty("kind").GetString() is not ("tap_requested" or "spell_click_requested"),
                            "Runtime lifecycle fixture dispatches no gameplay requests.");
                    }
                TeamBarApplication.TestSettingsLifetime(app, profile, controller, manual, preferences, manualSession, Check);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                pulse.Stop();
                try { setup?.Close(); }
                catch (Exception cleanup) { failure ??= cleanup; }
                try { AppInputSession.Stop(); }
                catch (Exception cleanup) { failure ??= cleanup; }
                app.Shutdown();
            }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("In-process application runtime fixture failed.", failure);
        using var report = new FileStream(result, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, new { passed = true, tests = checks, dispatcherTicks = ticks,
            gameInputSent = false, processMemoryRead = false, processMemoryWritten = false,
            limitation = "Uses the isolated fixture controller and synthetic identities; no native game bridge is loaded." }, new JsonSerializerOptions { WriteIndented = true });
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}

internal sealed partial class FloatingWorkspace
{
    internal void SendManualFixtureTap(Action<bool, string> check)
    {
        long before = sequence;
        SendTap(surfaces.First(), 0); EditionWait(() => !busy);
        check(sequence == before + 1 && !Failed && !identityUnavailable,
            "An explicit manual button click completes through the fake controller with memory reading disabled.");
    }
}
