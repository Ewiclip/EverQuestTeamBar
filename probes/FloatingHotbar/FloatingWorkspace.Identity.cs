using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private bool identityMismatchWaiting;
    private DetectedClient[]? identityRecoveryCandidate;
    private long identityRecoveryStarted;
    private readonly Dictionary<int, DetectedClient> lastIdentityMismatches = [];
    internal Func<long> IdentityClock = () => Environment.TickCount64;

    private bool CheckIdentities()
    {
        var detected = new List<DetectedClient>(); bool changed = false, personaChanged = false, unavailable = false;
        bool wasUnavailable = identityUnavailable, recovered = false;
        foreach (var expected in live.Identities!)
        {
            try
            {
                var actual = ReadIdentity(expected);
                // Personas may change class, but never reassign native input to
                // another character, process, window, identity source or build.
                bool sameCharacter = Identity.SameCharacterSession(expected, actual);
                if (sameCharacter) detected.Add(actual);
                else
                {
                    changed = true;
                    // Ignore position/level changes when deduplicating diagnostics.
                    var signature = actual with { Left = 0, Character = actual.Character with { Level = 0 } };
                    if (!lastIdentityMismatches.TryGetValue(expected.ProcessId, out var previous) || previous != signature)
                    {
                        Write("identity_mismatch", new { expected = IdentityLog(expected), actual = IdentityLog(actual), fields = IdentityMismatchFields(expected, actual) });
                        lastIdentityMismatches[expected.ProcessId] = signature;
                    }
                }
                if (sameCharacter && expected.Character.ClassId != actual.Character.ClassId) personaChanged = true;
            }
            catch (Exception error) { unavailable = true; Write("identity_unavailable", new { expected.ProcessId, error.Message }); }
        }
        current = detected.ToArray();
        identityUnavailable = unavailable;
        if (changed && !identityBlocked)
        {
            identityBlocked = identityMismatchWaiting = true;
            CancelPendingInput("Character identity changed");
        }
        else if (unavailable && !wasUnavailable && !identityBlocked)
            CancelPendingInput("Character identity temporarily unavailable");

        if (identityMismatchWaiting)
        {
            if (changed || unavailable) identityRecoveryCandidate = null;
            else if (identityRecoveryCandidate is null || identityRecoveryCandidate.Length != current.Length
                || !identityRecoveryCandidate.Zip(current).All(pair => Identity.Same(pair.First, pair.Second)))
            {
                identityRecoveryCandidate = current;
                identityRecoveryStarted = IdentityClock();
            }
            else if (IdentityClock() - identityRecoveryStarted >= 1000)
            {
                // Two matching observations at least one second apart, covering
                // the entire original roster. Never replay the cancelled gesture.
                identityBlocked = identityMismatchWaiting = false;
                identityRecoveryCandidate = null; lastIdentityMismatches.Clear();
                recovered = true;
                Write("identity_session_restored", new { clients = current, inputRequiresNewClick = true });
            }
        }
        if (!identityBlocked && !unavailable && personaChanged)
        {
            CancelPendingInput("Persona changed · updating mappings");
            try
            {
                RebindPersonas(current);
                status = BuildEdition.ReadyHint;
                Write("personas_updated", new { clients = current.Select(c => new { c.ProcessId, c.Character.Name, c.Character.Class }), inputRequiresNewClick = true });
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                identityBlocked = true; identityMismatchWaiting = false;
                identityProblem = "Persona mappings need attention · check Setup";
                Write("persona_mapping_failed", new { error.Message });
            }
        }
        if (identityBlocked) status = identityProblem;
        else if (unavailable) status = IdentityWaitingMessage;
        else if (wasUnavailable || recovered)
        {
            status = BuildEdition.ReadyHint;
            if (!recovered) Write("identity_restored", new { inputRequiresNewClick = true });
        }
        RefreshPlacement();
        // Even the gesture discovering recovery or a new persona is discarded.
        Invalidate(); return !identityBlocked && !identityUnavailable && !personaChanged && !recovered;
    }

    private static object IdentityLog(DetectedClient client) => new { client.ProcessId, client.StartedUtcTicks, client.Window,
        client.Character, client.UserAssigned, client.ClientChannel, client.BuildSha256 };

    private static string[] IdentityMismatchFields(DetectedClient expected, DetectedClient actual)
    {
        var fields = new List<string>();
        if (expected.ProcessId != actual.ProcessId) fields.Add("ProcessId");
        if (expected.StartedUtcTicks != actual.StartedUtcTicks) fields.Add("StartedUtcTicks");
        if (expected.Window != actual.Window) fields.Add("Window");
        if (expected.Character.Name != actual.Character.Name) fields.Add("Character.Name");
        if (actual.Character.ClassId is < 1 or > 16) fields.Add("Character.ClassId");
        if (expected.UserAssigned != actual.UserAssigned) fields.Add("UserAssigned");
        if (expected.ClientChannel != actual.ClientChannel) fields.Add("ClientChannel");
        if (expected.BuildSha256 != actual.BuildSha256) fields.Add("BuildSha256");
        return fields.ToArray();
    }
}
