using System.Diagnostics;
using TeamBar;

internal sealed partial class FloatingWorkspace
{
    internal static void TestSpellClicks(Action<bool, string> check, string output)
    {
        using var self = Process.GetCurrentProcess();
        var client = new DetectedClient(self.Id, self.StartTime.ToUniversalTime().Ticks, 1, 0, new("Fellwort", 10, 130));
        var team = TeamEditing.MergeDetected(new(1, [], [], []), [client]);
        team = team with { Buttons = [new("plain", "Plain tap", [new(new(CharacterId: team.Characters[0].Id), "1")])] };
        var (definition, capture) = SpellOverlayTests.Fixture();
        definition = definition with { Character = client.Character.Name, Spells = [definition.Spells[0] with { ClickKey = "5" }] };
        capture = capture with { CharacterName = client.Character.Name, ProcessId = client.ProcessId, ProcessStartedUtcTicks = client.StartedUtcTicks };
        var library = new SpellOverlayLibrary(1, [definition]);
        using var journal = new StreamWriter(new FileStream(output + ".spell-session.jsonl", FileMode.CreateNew)) { AutoFlush = true };
        using var workspace = new FloatingWorkspace(new(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), team,
            TeamEditing.Bind(team, [client]), [client]), [], output + ".spell-controls", journal, new MemoryUserSettingsStore());
        workspace.ReadIdentity = c => c; workspace.ReadSpellLibrary = () => library; workspace.ReadSpellTarget = (_, _) => capture;
        long now = Environment.TickCount64; workspace.TapClock = () => now;
        workspace.SpellPipeName = "TeamBar-SpellTest-" + Guid.NewGuid().ToString("N");
        workspace.Start(); EditionWait(() => workspace.Ready);
        T Complete<T>(Task<T> task) { EditionWait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
        var status = Complete(SpellOverlayInputPipe.RequestNamed(new("status"), workspace.SpellPipeName));
        check(status is { Ok: true, Paused: false, Session: not null }, "private spell endpoint starts ready and reports a session identity");
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("close-spell-host", ProcessId: self.Id), workspace.SpellPipeName)) is { Ok: false }
            && workspace.Ready && !workspace.IsDisposed, "a spell-only close request cannot shut down a host shared with hotkey buttons");
        long window = workspace.Handle.ToInt64();
        var registration = Complete(SpellOverlayInputPipe.RequestNamed(new("register", window, definition.Id), workspace.SpellPipeName));
        check(registration is { Ok: true, Paused: false } && workspace.spellWindows.ContainsKey(window),
            "spell window registration waits for the shared controller acknowledgement and leaves input ready");
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("status", window, definition.Id), workspace.SpellPipeName)) is { Ok: true, ReconnectRequired: false },
            "scoped status verifies the exact spell window registration");
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("status", window, "other-overlay"), workspace.SpellPipeName)) is { Ok: false, ReconnectRequired: true }
            && Complete(workspace.HandleSpellRequest(new("status", window, definition.Id), self.Id + 1)) is { Ok: false, ReconnectRequired: true },
            "scoped status rejects a different overlay or window owner while the host remains healthy");
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("unregister", window, "old-overlay"), workspace.SpellPipeName)) is { Ok: true }
            && workspace.spellWindows[window] == (self.Id, definition.Id),
            "stale unregister cannot remove another overlay that owns the same window handle");
        var request = new SpellClickRequest("tap", window, definition.Id, definition.Spells[0].SpellId, Foreground: Native.GetForegroundWindow().ToInt64(),
            ProcessId: client.ProcessId, Started: client.StartedUtcTicks, TargetId: capture.Target!.SpawnId,
            TargetName: capture.Target.Name, Class: client.Character.Class, ZoneId: capture.Zone.Id, Key: "5");
        SpellClickReply? Control(string kind) => Complete(SpellOverlayInputPipe.RequestNamed(new(kind), workspace.SpellPipeName));
        // Each simulated gesture captures its own foreground, as the live overlay
        // does. Desktop focus may legitimately change between separate test clicks.
        SpellClickRequest FreshClick() => request with { Foreground = Native.GetForegroundWindow().ToInt64() };
        check(Complete(workspace.HandleSpellRequest(request, self.Id)).Ok && workspace.sequence == 1,
            "registered spell click delivers immediately without a resume command");
        check(!Complete(workspace.HandleSpellRequest(request, self.Id)).Ok && workspace.sequence == 1, "busy shared session cannot queue a second spell tap");
        EditionWait(() => !workspace.busy);
        for (int i = 0; i < 100; i++) { now += 60000; workspace.CheckDelivery(); }
        check(workspace.sequence == 1, "spell click sends exactly one request");
        foreach (string key in new[] { "Alt+6", "Alt+7" })
        {
            library = new(1, [definition with { Spells = [definition.Spells[0] with { ClickKey = key }] }]);
            check(Complete(workspace.HandleSpellRequest(FreshClick() with { Key = key }, self.Id)).Ok, "shared backend accepts configured chord: " + key);
            EditionWait(() => !workspace.busy);
            check(Control("status") is { Ok: true, Paused: false }, "configured chord leaves input ready: " + key);
        }
        library = new(1, [definition]);
        foreach (string retired in new[] { "pause-spells", "resume-spells", "pause", "resume" })
        {
            check(Control(retired) is { Ok: false }, "retired spell endpoint command is rejected: " + retired);
            check(Control("status") is { Ok: true, Paused: false } && workspace.sequence == 3 && !workspace.busy
                && !workspace.cancelInFlight && workspace.spellWindows.TryGetValue(window, out var registered)
                && registered == (self.Id, definition.Id), "retired command leaves the ready session and registration unchanged: " + retired);
        }
        check(workspace.surfaces.Count == 1 && workspace.surfaces[0].Heading == "TEAM BAR",
            "shared spell input needs no pause or resume control strip");
        workspace.SendTap(workspace.surfaces[0], 0); EditionWait(() => !workspace.busy);
        check(workspace.sequence == 4 && Control("status") is { Ok: true, Paused: false },
            "hotkey and spell input share the always-ready controller");
        check(Complete(workspace.HandleSpellRequest(FreshClick(), self.Id)).Ok && workspace.sequence == 5,
            "spell click still delivers after a hotkey tap");
        workspace.CancelPendingInput("Spell click cancellation fixture");
        check(workspace.cancelInFlight && !Complete(workspace.HandleSpellRequest(request, self.Id)).Ok && workspace.sequence == 5,
            "pending cancellation rejects another spell click without queuing input");
        EditionWait(() => !workspace.cancelInFlight && !workspace.busy);
        check(!workspace.Failed && Control("status") is { Ok: true, Paused: false } && workspace.sequence == 5,
            "cancelling a pending spell tap keeps the session ready without sending a key");
        check(Complete(workspace.HandleSpellRequest(FreshClick(), self.Id)).Ok && workspace.sequence == 6,
            "a new spell click works immediately after cancellation settles");
        EditionWait(() => !workspace.busy);
        check(!Complete(workspace.HandleSpellRequest(request with { Foreground = -1 }, self.Id)).Ok && workspace.sequence == 6,
            "focus change cancels shared spell delivery");
        check(!Complete(workspace.HandleSpellRequest(request, self.Id + 1)).Ok && workspace.sequence == 6,
            "unregistered process cannot use another overlay's mapping");
        foreach (var changed in new[] { request with { TargetId = request.TargetId + 1 }, request with { TargetName = "Changed target" },
            request with { ZoneId = request.ZoneId + 1 }, request with { Started = request.Started + 1 } })
            check(Complete(SpellOverlayInputPipe.RequestNamed(changed, workspace.SpellPipeName)) is { Ok: false }
                && workspace.sequence == 6 && !workspace.busy, "stale target or client observation cannot send a spell tap");
        workspace.SpellIdentityRecoverySelfTest(check, FreshClick, self.Id);
        PersonaRoutingSelfTest(check, workspace.live, output);
        workspace.PersonaSpellSelfTest(check, FreshClick(), self.Id, definition, capture);
        var dockButton = SpellDockTests.Buttons()[0];
        var sequenceButton = dockButton.Value with { Rules = [new(new(CharacterId: "fellwort"), "Alt+6"),
            new(new(CharacterId: "fellwort"), "Ctrl+Shift+Alt+Y"), new(new(CharacterId: "fellwort"), "Alt+6")] };
        dockButton = dockButton with { Value = sequenceButton, Bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sequenceButton, Program.Json) };
        workspace.ReadDockButtons = () => [dockButton];
        library = new(1, [definition with { Spells = [], DockGroups = [SpellDockTests.Groups()[0]] }]);
        var dockEntry = SpellDockRules.Describe(dockButton.Value.Id, [dockButton], [client]);
        var dockClick = FreshClick() with { Kind = "dock-tap", GroupId = "support", ButtonId = dockButton.Value.Id,
            ButtonRevision = dockEntry.Revision, Recipients = dockEntry.Recipients };
        long beforeDock = workspace.sequence;
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("register", window, definition.Id), workspace.SpellPipeName)) is { Ok: true },
            "a dock-only overlay registers even without spell click mappings");
        check(Complete(workspace.HandleSpellRequest(dockClick, self.Id)).Ok && workspace.sequence == beforeDock + 1,
            "registered dock click reaches the existing one-tap backend with its saved standalone mapping");
        EditionWait(() => !workspace.busy);
        check(workspace.sequence == beforeDock + 3 && !workspace.Failed,
            "one docked button click completes its ordered modifier sequence without duplicate-target native commands");
        check(Complete(SpellOverlayInputPipe.RequestNamed(dockClick with { ButtonRevision = "stale" }, workspace.SpellPipeName)) is { Ok: false, RefreshCharactersRequired: false }
            && workspace.sequence == beforeDock + 3, "stale dock mapping cannot dispatch through the real private pipe");
        long beforeRosterRejection = workspace.sequence;
        foreach (var stale in new[] { dockClick with { Recipients = "stale-roster" }, dockClick with { Started = dockClick.Started + 1 } })
        {
            var refused = Complete(SpellOverlayInputPipe.RequestNamed(stale with { Foreground = Native.GetForegroundWindow().ToInt64() }, workspace.SpellPipeName));
            check(refused is { Ok: false, RefreshCharactersRequired: true, ReconnectRequired: false }
                && refused.Session == status!.Session && workspace.sequence == beforeRosterRejection && !workspace.busy,
                "stale dock roster returns a typed refresh request before sending any input through the private pipe");
        }
        for (int i = 0; i < 5; i++) check(Control("status") is { Ok: true, RefreshCharactersRequired: false }
            && workspace.sequence == beforeRosterRejection, "status after stale-roster rejection never replays the rejected click");
        check(Complete(workspace.HandleSpellRequest(dockClick with { Foreground = Native.GetForegroundWindow().ToInt64() }, self.Id)).Ok,
            "only a fresh explicit dock click can send input after a roster rejection");
        EditionWait(() => !workspace.busy);
        check(workspace.sequence == beforeRosterRejection + 3, "fresh dock gesture runs its ordered sequence exactly once after stale-roster rejection");
        library = new(1, [definition]);
        long completedClicks = workspace.sequence;
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("unregister", window, definition.Id), workspace.SpellPipeName))?.Ok == true,
            "spell overlay registration can be removed without stopping the bar");
        check(!Complete(workspace.HandleSpellRequest(request, self.Id)).Ok && workspace.sequence == completedClicks, "unregistered overlay cannot send a tap");
        check(Complete(SpellOverlayInputPipe.RequestNamed(new("status", window, definition.Id), workspace.SpellPipeName)) is { Ok: false, ReconnectRequired: true }
            && Complete(workspace.HandleSpellRequest(request, self.Id)) is { Ok: false, ReconnectRequired: true }
            && workspace.sequence == completedClicks,
            "lost registration is a recoverable status and tap rejection without sending input");
        check(Control("status") is { Ok: true, Paused: false }, "unregistering the overlay leaves the shared input session ready");
        workspace.Close();
        IdentityMismatchSelfTest(check, workspace.live, output);
    }
}
