using System.ComponentModel;
using W = System.Windows;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private static async Task TestManagerLifetime(SpellOverlayDefinition definition, TargetCapture capture, SpellOverlayCatalog catalog,
        Action<bool, string> check)
    {
        string fixturePath = Path.Combine(Path.GetTempPath(), "team-bar-overlay-lifetime-" + Guid.NewGuid().ToString("N"), "spell-overlays.json");
        var library = new SpellOverlayLibrary(1, [definition]);
        byte[] originalBytes = SpellOverlayStore.Save(fixturePath, library, null);
        var manager = new SpellOverlayManagerWindow(fixturePath, reviewOnly: true);
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1]);
        bool displayClosed = false; display.Closed += (_, _) => displayClosed = true;
        try
        {
            string transitionFolder = Path.Combine(Path.GetDirectoryName(fixturePath)!, "input-transition");
            using (var first = SpellOverlayInputHost.TryBeginTransition(transitionFolder))
            {
                using var second = SpellOverlayInputHost.TryBeginTransition(transitionFolder);
                check(first is not null && second is null, "input handoff lock excludes a simultaneous automatic host launch");
            }
            using (var next = SpellOverlayInputHost.TryBeginTransition(transitionFolder))
                check(next is not null, "input handoff lock can be acquired after the transition completes");
            // Exercise real window/tray/event lifetime without starting discovery,
            // reading a game, or connecting to the live input pipe.
            manager.runtimeLock = new(fixturePath + ".runtime.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            manager.InitializeLifetime(); manager.Show();
            manager.applied = [definition]; manager.floating.Add(definition.Id, display);
            manager.RememberLoaded(true);
            display.Render(definition, SpellOverlayRules.Evaluate(definition, capture, catalog: catalog));
            display.Place(definition, new("lifetime-fixture", "Fixture", System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            bool duplicateRejected = false;
            try { using var duplicate = new FileStream(fixturePath + ".runtime.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { duplicateRejected = true; }
            check(duplicateRejected, "a live overlay manager retains its single-runtime file lock");
            manager.OverlayName.Text = "Unsaved lifetime change";
            var cancelled = new CancelEventArgs(); manager.HandleSettingsClosing(cancelled, true, () => false);
            check(cancelled.Cancel && manager.IsVisible && manager.dirty, "canceling close preserves the visible unsaved editor");
            var hide = new CancelEventArgs(); manager.HandleSettingsClosing(hide, true, () => true);
            check(hide.Cancel && !manager.IsVisible && !manager.closed && display.IsVisible && !displayClosed && manager.applied.Length == 1,
                "closing overlay settings leaves the same floating runtime alive");
            check(manager.preferences.Load().SpellOverlaysLoaded, "closing settings preserves the overlay restart preference");
            check(!manager.dirty && manager.selected!.Name == definition.Name && File.ReadAllBytes(fixturePath).SequenceEqual(originalBytes),
                "discarding on close restores saved drafts without writing the profile");
            check(manager.runtimeLock is not null && manager.tray?.Visible == true,
                "hidden settings retain the runtime lock and a tray reopening route");
            check(TryActivateExisting(fixturePath), "another launch can signal the existing hidden manager");
            for (int i = 0; i < 20 && !manager.IsVisible; i++) await Task.Delay(25);
            check(manager.IsVisible && !displayClosed && manager.floating[definition.Id] == display,
                "activation restores settings without replacing the floating windows");
            manager.Close();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            check(!manager.IsVisible && !manager.closed && !displayClosed,
                "the actual window Close event hides settings without destroying their runtime");
            manager.ReopenSettings();
            check(manager.IsVisible, "same-process Open can show hidden settings again");
            await manager.ExitOverlays();
            check(manager.closed && displayClosed && manager.applied.Length == 0 && manager.floating.Count == 0,
                "explicit Exit closes floating overlays and ends the runtime");
            check(!manager.preferences.Load().SpellOverlaysLoaded, "explicit Exit overlays disables their next automatic startup");
            check(manager.tray is null && manager.activateSettings is null && !TryActivateExisting(fixturePath),
                "explicit Exit disposes tray and activation resources");
            using var released = new FileStream(fixturePath + ".runtime.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            check(released.CanWrite, "explicit Exit releases the runtime lock for a fresh launch");
        }
        finally
        {
            manager.exiting = true; manager.Close(); display.Close();
        }
        await TestOverlayRestart(definition, capture, fixturePath + ".restart", check);
    }

    private static async Task TestOverlayRestart(SpellOverlayDefinition definition, TargetCapture capture, string folder, Action<bool, string> check)
    {
        string profile = Path.Combine(folder, "overlays.json"), settings = Path.Combine(folder, "preferences.json");
        definition = definition with { PersonaClass = capture.CharacterClass,
            Spells = definition.Spells.Select((s, i) => s with { ClickKey = i == 0 ? "5" : null, PersonaClasses = null }).ToArray() };
        var otherPersona = definition with { Id = "berserker-restart", Name = "Berserker Bar", PersonaClass = "Berserker" };
        var activeCapture = capture;
        var requests = new List<SpellClickRequest>();
        var registrations = new Dictionary<long, string>();
        SpellOverlayStore.Save(profile, new(2, [definition, otherPersona]), null);
        var store = new LocalUserSettingsStore(settings);
        string buttonProfile = Path.Combine(folder, "team.json");
        var team = new TeamDefinition(1, [new("bard", capture.CharacterName)], [new("bard-current", "bard", capture.CharacterClass)], []);
        TeamEditing.Save(buttonProfile, team, []);
        var sample = new StandaloneButton(1, "assist", "Assist fixture", team.Characters, team.Personas,
            [new(new(CharacterId: "bard"), "Ctrl+Shift+Alt+Y")], [], Floating: false);
        StandaloneButtons.Save(StandaloneButtons.PathFor(buttonProfile, sample.Id), sample, null);
        SpellOverlayManagerWindow Create()
        {
            var next = new SpellOverlayManagerWindow(profile, userSettings: new LocalUserSettingsStore(settings));
            next.ButtonProfile = () => buttonProfile;
            next.ReadDockButtons = () => StandaloneButtons.Load(buttonProfile);
            var client = new DetectedClient(123, 456, 789, 0, new(capture.CharacterName, 10, 130));
            next.DiscoverCharacters = () => new([client], []);
            next.CaptureTarget = (_, _) => activeCapture;
            next.ReadClientFrame = _ => new("restart-fixture", "Fixture", System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea);
            next.RequestSpellInput = request =>
            {
                requests.Add(request);
                if (request.Kind == "register") registrations[request.Window] = request.OverlayId!;
                if (request.Kind == "unregister") registrations.Remove(request.Window);
                if (request.Kind == "status" && request.Window != 0
                    && (!registrations.TryGetValue(request.Window, out var id) || id != request.OverlayId))
                    return Task.FromResult<SpellClickReply?>(new(false, "Reconnecting spell buttons", Session: "fixture", ReconnectRequired: true));
                return Task.FromResult<SpellClickReply?>(new(true, "Fixture ready", false, "fixture"));
            };
            return next;
        }
        async Task Loaded(SpellOverlayManagerWindow next)
        {
            await next.InitializeRuntime();
            for (int i = 0; i < 100 && (next.polling || !next.floating.Values.Any(w => w.IsVisible)); i++) await Task.Delay(25);
            check(next.applied.Length == 2 && next.floating[definition.Id].IsVisible && !next.floating[otherPersona.Id].IsVisible && !next.IsVisible,
                "restoration loads saved floating overlays without opening the settings window");
        }
        var first = Create();
        try
        {
            await Loaded(first);
            first.nextClickCheck = DateTime.MinValue; await first.RefreshClickSession();
            check(first.clickRegistrations.Keys.SequenceEqual([definition.Id]), "only the visible persona bar registers for spell clicks");
            requests.Clear(); registrations.Clear();
            first.nextClickCheck = DateTime.MinValue; await first.RefreshClickSession();
            check(registrations.Values.Contains(definition.Id) && requests.Any(r => r.Kind == "register" && r.OverlayId == definition.Id)
                && requests.All(r => r.Kind != "tap"), "lost overlay registration repairs automatically without replaying a click");
            requests.Clear();
            first.nextClickCheck = DateTime.MinValue; await first.RefreshClickSession();
            check(requests.Any(r => r.Kind == "status" && r.Window != 0 && r.OverlayId == definition.Id)
                && requests.All(r => r.Kind != "register"), "healthy window registration is verified without repeatedly registering it");
            long handle = first.clickRegistrations[definition.Id];
            first.clickRegistrations[definition.Id] = -1; requests.Clear();
            first.nextClickCheck = DateTime.MinValue; await first.RefreshClickSession();
            check(first.clickRegistrations[definition.Id] == handle && requests.Any(r => r.Kind == "register" && r.Window == handle),
                "a cached old window handle cannot authorize its replacement");
            var ordinarySender = first.RequestSpellInput;
            first.RequestSpellInput = request =>
            {
                if (request.Kind != "tap") return ordinarySender(request);
                requests.Add(request);
                return Task.FromResult<SpellClickReply?>(new(false, "Reconnect fixture", Session: "fixture", ReconnectRequired: true));
            };
            requests.Clear();
            var rejected = await first.SendSpellClick(new("tap", handle, definition.Id));
            check(rejected is { ReconnectRequired: true } && first.clickRegistrations.Count == 0 && requests.Count(r => r.Kind == "tap") == 1,
                "a rejected spell gesture invalidates its registration and sends no retry");
            await first.RefreshClickSession();
            check(first.clickRegistrations.ContainsKey(definition.Id) && requests.Count(r => r.Kind == "tap") == 1,
                "registration recovers after a rejected tap without replaying that tap");
            first.RequestSpellInput = _ => throw new IOException("Fixture transport interrupted");
            bool interrupted = false;
            try { await first.SendSpellClick(new("tap", handle, definition.Id)); } catch (IOException) { interrupted = true; }
            check(interrupted && first.clickRegistrations.Count == 0 && first.clickSession is null,
                "uncertain click delivery invalidates the connection instead of retrying input");
            first.RequestSpellInput = ordinarySender;
            first.nextClickCheck = DateTime.MinValue; await first.RefreshClickSession();
            requests.Clear();
            activeCapture = capture with { CharacterClass = "Berserker", Buffs = capture.Buffs! with { CharacterClass = "Berserker" } };
            await first.Poll();
            first.nextClickCheck = DateTime.MinValue; await first.RefreshClickSession();
            check(requests.Where(r => r.Kind is "register" or "unregister").Select(r => (r.Kind, r.OverlayId))
                .SequenceEqual(new (string, string?)[] { ("unregister", definition.Id), ("register", otherPersona.Id) })
                && first.clickRegistrations.Keys.SequenceEqual([otherPersona.Id]),
                "persona switch releases old input registration before claiming the replacement slot");
            check(!first.floating[definition.Id].IsVisible && first.floating[otherPersona.Id].IsVisible,
                "fresh persona capture switches whole bars before cached character discovery changes");
            check(first.selected?.PersonaClass == capture.CharacterClass && !first.dirty,
                "persona switching leaves the selected editor and saved configuration unchanged");
            activeCapture = capture; await first.Poll();
            check(first.floating[definition.Id].IsVisible && !first.floating[otherPersona.Id].IsVisible,
                "returning to the original persona restores its bar without Refresh");
            check(requests.All(r => r.Kind != "tap"), "switching personas never requests a game keypress");
            check(store.Load().SpellOverlaysLoaded, "showing saved overlays immediately persists their loaded state");
            await first.TestInputStartupRecovery(check);
            await first.TestInputRosterRefresh(check);
            await first.TestOverlayButtonWorkflow(definition.Id, buttonProfile, check);
            await first.ExitOverlays(preserveLoadedState: true);
            check(first.closed && store.Load().SpellOverlaysLoaded, "application shutdown retains loaded overlays for next launch");
            var second = Create();
            try
            {
                await Loaded(second);
                var reopened = second.floating[definition.Id];
                check(second.savedLibrary.Overlays.Single(d => d.Id == definition.Id).DockGroups!.Any(g => g.Name == RestartedDockTitle) &&
                    OverlayTestControls(reopened).OfType<System.Windows.Controls.TextBlock>().Any(t => t.IsVisible && t.Text == RestartedDockTitle),
                    "Restarting the overlay manager restores the title entered directly on the floating dock group.");
                var lower = OverlayTestControls(reopened).OfType<System.Windows.Controls.Border>()
                    .Single(b => W.Automation.AutomationProperties.GetName(b) == "Lower fixture");
                var middle = OverlayTestControls(reopened).OfType<System.Windows.Controls.Border>()
                    .Single(b => W.Automation.AutomationProperties.GetName(b) == "Middle fixture");
                check(lower.IsVisible && middle.IsVisible && System.Windows.Controls.Grid.GetColumn(lower) == 1 &&
                    System.Windows.Controls.Grid.GetColumn(middle) == 1 && System.Windows.Controls.Grid.GetRow(lower) == 2 &&
                    System.Windows.Controls.Grid.GetRow(middle) == 1,
                    "The restarted overlay displays the middle and lower buttons in their saved rows with the top cell vacant.");
                second.Hide_Click(second, new());
                check(second.floating.Count == 0 && !store.Load().SpellOverlaysLoaded, "explicit Hide stops overlays and disables automatic restoration");
                second.ApplySaved();
                check(store.Load().SpellOverlaysLoaded, "showing saved overlays re-enables automatic restoration");
                await second.ExitOverlays();
                check(second.closed && !store.Load().SpellOverlaysLoaded, "explicit Exit overlays persists unloaded state across stores");
            }
            finally { if (!second.closed) await second.ExitOverlays(); }
        }
        finally { if (!first.closed) await first.ExitOverlays(); }
    }
}
