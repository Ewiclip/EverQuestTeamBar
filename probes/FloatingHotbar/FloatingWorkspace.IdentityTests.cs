using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private void SpellIdentityRecoverySelfTest(Action<bool, string> check, Func<SpellClickRequest> freshClick, int requester)
    {
        T Complete<T>(Task<T> task) { EditionWait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
        SpellClickReply? Status() => Complete(SpellOverlayInputPipe.RequestNamed(new("status"), SpellPipeName));
        var originalReader = ReadIdentity;
        var originalStatus = Status();
        long originalSequence = sequence, originalWindow = Handle.ToInt64();
        var registrations = spellWindows.ToDictionary(pair => pair.Key, pair => pair.Value);
        var surface = surfaces.Single();
        var point = PointToClient(new(surface.Bounds.Left + (int)(40 * scale), surface.Bounds.Top + (int)(60 * scale)));
        nint packed = (nint)((point.Y << 16) | (point.X & 0xffff));
        void Edge(uint message) => Native.SendMessageW(Handle, message, message == 0x201 ? 1 : 0, packed);

        // The fake controller cannot send game input. Hold a real workspace
        // gesture across synthetic zoning, so recovery cannot replay its release.
        Edge(0x201);
        check(pressed == surface && Capture && sequence == originalSequence, "zoning fixture captured a held gesture without sending input");
        ReadIdentity = _ => throw new InvalidDataException("Finish entering the game, then refresh.");
        check(!CheckIdentities() && identityUnavailable && !identityBlocked && !CloseAfterClear && cancelInFlight,
            "temporary zoning waits and cancels the gesture without permanently invalidating its session");
        check(pressed is null && dragged is null && pressedAction == -1 && !Capture,
            "temporary identity loss clears the held mouse gesture");
        var waiting = Status();
        check(waiting is { Ok: false } && waiting.Session == originalStatus?.Session
            && waiting.Message.Contains("waiting", StringComparison.OrdinalIgnoreCase),
            "spell status reports temporary loading through the existing pipe session");
        EditionWait(() => !cancelInFlight);
        for (int i = 0; i < 5; i++)
        {
            check(!CheckIdentities() && !cancelInFlight && sequence == originalSequence,
                "continued identity unavailability does not repeatedly cancel or send input");
            check(Status() is { Ok: false } && !Failed && Ready,
                "continued zoning retains a live input host while spell delivery waits");
        }

        // Death may lose a level. Level is observation data, not a different
        // character or persona; a fresh stable identity must restore availability.
        ReadIdentity = expected => expected with { Character = expected.Character with { Level = expected.Character.Level - 1 } };
        check(CheckIdentities() && !identityUnavailable && !identityBlocked && !CloseAfterClear,
            "same character, class and session recover automatically after zoning or a lost level");
        var restored = Status();
        check(restored is { Ok: true, Paused: false } && restored.Session == originalStatus?.Session
            && Handle.ToInt64() == originalWindow && spellWindows.Count == registrations.Count
            && registrations.All(pair => spellWindows.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "identity recovery keeps the pipe session, protected window and registered spell overlays");
        Edge(0x202);
        for (int i = 0; i < 20; i++) { CheckIdentities(); CheckDelivery(); Application.DoEvents(); }
        check(sequence == originalSequence && !busy, "recovery and release of the pre-zone gesture never generate an automatic tap");
        check(Complete(HandleSpellRequest(freshClick(), requester)).Ok && sequence == originalSequence + 1,
            "a fresh explicit spell click works after automatic identity recovery");
        EditionWait(() => !busy);

        // Simulate death while a request is awaiting the fake controller. Do not
        // pump UI replies until the cancellation and new-click gates are checked.
        check(Complete(HandleSpellRequest(freshClick(), requester)).Ok && busy && sequence == originalSequence + 2,
            "death fixture begins with one in-flight explicit spell tap");
        ReadIdentity = _ => throw new InvalidDataException("Local character identity is not ready.");
        check(!CheckIdentities() && cancelInFlight && userCancelledTap == sequence && !identityBlocked,
            "temporary death-state identity loss cancels the in-flight tap");
        long cancellationStarted = controlPendingSince;
        for (int i = 0; i < 5; i++) CheckIdentities();
        check(controlPendingSince == cancellationStarted && sequence == originalSequence + 2,
            "repeated death-state checks preserve the original cancellation deadline and do not queue taps");
        ReadIdentity = originalReader;
        check(CheckIdentities() && !identityUnavailable && cancelInFlight,
            "stable identity recovery does not bypass an outstanding cancellation acknowledgement");
        check(!Complete(HandleSpellRequest(freshClick(), requester)).Ok && sequence == originalSequence + 2,
            "a spell click during cancellation cannot be queued for later recovery");
        EditionWait(() => !cancelInFlight && !busy);
        for (int i = 0; i < 20; i++) { CheckIdentities(); CheckDelivery(); Application.DoEvents(); }
        check(sequence == originalSequence + 2 && !Failed && Status() is { Ok: true, Paused: false },
            "settled death cancellation leaves the original host ready without replaying input");
        check(Complete(HandleSpellRequest(freshClick(), requester)).Ok && sequence == originalSequence + 3,
            "only a new spell click sends input after death cancellation settles");
        EditionWait(() => !busy);

        // A mismatch used to latch the host forever. Verify the complete native-
        // free click path, including a held mouse button and delayed cancel ack.
        long identityTime = 0;
        IdentityClock = () => identityTime;
        long beforeMismatch = sequence;
        Edge(0x201);
        ReadIdentity = client => client with { Window = client.Window + 1 };
        check(!CheckIdentities() && identityBlocked && cancelInFlight && pressed is null && !Capture,
            "a mismatched window cancels a held click immediately");
        ReadIdentity = originalReader;
        check(!CheckIdentities() && identityBlocked, "one matching observation cannot resume spell input");
        identityTime = 1000;
        check(!CheckIdentities() && !identityBlocked && cancelInFlight,
            "stable original identity recovers but discards the recovery gesture and still waits for cancellation");
        check(!Complete(HandleSpellRequest(freshClick(), requester)).Ok && sequence == beforeMismatch,
            "recovered identity cannot bypass a pending controller cancellation");
        EditionWait(() => !cancelInFlight);
        Edge(0x202);
        check(sequence == beforeMismatch && Status() is { Ok: true } && Handle.ToInt64() == originalWindow
            && registrations.All(pair => spellWindows.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "mismatch recovery keeps original registrations and never replays a held gesture");
        check(Complete(HandleSpellRequest(freshClick(), requester)).Ok && sequence == beforeMismatch + 1,
            "a fresh spell click works without refresh or apply after a temporary mismatch");
        EditionWait(() => !busy);
        IdentityClock = () => Environment.TickCount64;
        Write("spell_identity_recovery_pass", new { gameInput = false, sameSession = true, levelChangeAllowed = true,
            repeatedFailuresCancelOnce = true, heldGestureDiscarded = true, inFlightCancelled = true, automaticTaps = 0 });
    }

    private static void IdentityMismatchSelfTest(Action<bool, string> check, LiveSetup setup, string output)
    {
        var expected = setup.Identities!.Single();
        var changed = new (string Name, DetectedClient Client)[]
        {
            ("invalid class", expected with { Character = expected.Character with { ClassId = 0 } }),
            ("name", expected with { Character = expected.Character with { Name = "Different" } }),
            ("process", expected with { ProcessId = expected.ProcessId + 1 }),
            ("start time", expected with { StartedUtcTicks = expected.StartedUtcTicks + 1 }),
            ("window", expected with { Window = expected.Window + 1 }),
            ("channel", expected with { ClientChannel = "Beta" }),
            ("identity source", expected with { UserAssigned = !expected.UserAssigned }),
            ("build", expected with { BuildSha256 = new string('a', 64) })
        };
        int fixture = 0;
        foreach (var change in changed)
        {
            using var buffer = new MemoryStream();
            using var log = new StreamWriter(buffer);
            using var workspace = new FloatingWorkspace(setup with { Team = setup.Team with { Buttons = [] } }, [],
                output + ".identity-guard-" + fixture++, log, new MemoryUserSettingsStore());
            long now = 0; workspace.IdentityClock = () => now;
            workspace.ReadIdentity = _ => change.Client;
            check(!workspace.CheckIdentities() && workspace.identityBlocked && !workspace.identityUnavailable
                && workspace.CloseAfterClear && workspace.SpellStatus() is { Ok: false, RefreshCharactersRequired: true },
                "a " + change.Name + " mismatch immediately blocks input and requests roster refresh");
            for (int i = 0; i < 3; i++) { now += 2000; workspace.CheckIdentities(); }
            check(workspace.identityBlocked && !workspace.SpellStatus().Ok && workspace.sequence == 0,
                "a different " + change.Name + " cannot be adopted by waiting");
            workspace.ReadIdentity = client => client;
            check(!workspace.CheckIdentities() && workspace.identityBlocked && !workspace.identityUnavailable
                && !workspace.SpellStatus().Ok && workspace.sequence == 0,
                "one original identity reading cannot clear a " + change.Name + " mismatch");
            now += 999;
            check(!workspace.CheckIdentities() && workspace.identityBlocked,
                "rapid checks cannot shorten the " + change.Name + " recovery interval");
            now++;
            check(!workspace.CheckIdentities() && !workspace.identityBlocked && !workspace.CloseAfterClear
                && workspace.SpellStatus().Ok && workspace.sequence == 0,
                "a stable exact original session recovers from " + change.Name + " mismatch without replaying input");
            check(workspace.CheckIdentities(), "a fresh check works after " + change.Name + " recovery");
            log.Flush();
            var lines = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
            try
            {
                var mismatch = lines.Where(line => line.RootElement.GetProperty("kind").GetString() == "identity_mismatch").ToArray();
                check(mismatch.Length == 1 && mismatch[0].RootElement.GetProperty("data").GetProperty("fields").GetArrayLength() == 1,
                    "diagnostics record the exact " + change.Name + " difference once, without polling spam");
            }
            finally { foreach (var line in lines) line.Dispose(); }
        }

        var other = expected with { ProcessId = expected.ProcessId + 10, Window = expected.Window + 10,
            Character = expected.Character with { Name = "Other" } };
        foreach (bool unavailableFirst in new[] { false, true })
        {
            var clients = unavailableFirst ? new[] { other, expected } : new[] { expected, other };
            using var buffer = new MemoryStream();
            using var log = new StreamWriter(buffer);
            using var workspace = new FloatingWorkspace(setup with { Team = setup.Team with { Buttons = [] }, Identities = clients }, [],
                output + ".identity-mixed-" + unavailableFirst, log, new MemoryUserSettingsStore());
            long now = 0; workspace.IdentityClock = () => now;
            workspace.ReadIdentity = client => client.ProcessId == other.ProcessId
                ? throw new InvalidDataException("Fixture zoning")
                : client with { Character = client.Character with { Name = "Different" } };
            check(!workspace.CheckIdentities() && workspace.identityBlocked && workspace.identityUnavailable,
                "one unavailable client cannot hide another client's character change in either read order");
            workspace.ReadIdentity = client => client;
            check(!workspace.CheckIdentities() && workspace.identityBlocked && !workspace.identityUnavailable
                && !workspace.SpellStatus().Ok && workspace.sequence == 0,
                "multi-client recovery needs a stable observation of the whole roster");
            now = 1000;
            workspace.ReadIdentity = client => client.ProcessId == other.ProcessId
                ? throw new InvalidDataException("Fixture loading again") : client;
            check(!workspace.CheckIdentities() && workspace.identityBlocked,
                "another client's loading resets the pending recovery interval");
            workspace.ReadIdentity = client => client;
            now = 2000; workspace.CheckIdentities();
            now = 2999;
            check(!workspace.CheckIdentities() && workspace.identityBlocked, "whole-roster stability restarts after loading");
            now = 3000;
            check(!workspace.CheckIdentities() && !workspace.identityBlocked && workspace.SpellStatus().Ok && workspace.sequence == 0,
                "the complete original roster recovers without dispatching any tap");

            workspace.ReadIdentity = client => client with { Window = client.Window + 1 };
            workspace.CheckIdentities();
            workspace.ReadIdentity = client => client;
            now = 4000; workspace.CheckIdentities();
            workspace.ReadIdentity = client => client with { Character = client.Character with { ClassId = 2 } };
            now = 5000;
            check(!workspace.CheckIdentities() && workspace.identityBlocked,
                "a persona still changing during recovery resets the stability interval");
            now = 6000;
            check(!workspace.CheckIdentities() && !workspace.identityBlocked && workspace.live.Identities!.All(c => c.Character.ClassId == 2)
                && workspace.sequence == 0, "stable returned personas rebind before input is restored");

            workspace.identityBlocked = true; workspace.identityMismatchWaiting = false;
            workspace.identityProblem = "Persona mappings need attention · check Setup";
            workspace.ReadIdentity = client => client;
            for (int i = 0; i < 3; i++) { now += 2000; workspace.CheckIdentities(); }
            check(workspace.identityBlocked && workspace.SpellStatus() is { Ok: false, RefreshCharactersRequired: false },
                "a persona-mapping configuration error stays blocked without requesting automatic roster replacement");
        }
    }
}
