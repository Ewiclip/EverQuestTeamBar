using System.Text.Json;
internal static class ClientSessionTests
{
    internal static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        static void Reject(Action action) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Invalid session accepted."); }
        var window = new GameSession(11, 100, 111, 0, "Test window");
        var client = ClientSessions.Assign(window, "Runa", "Shadowknight"); int reads = 0;
        DetectedClient Detector(int pid) { ++reads; throw new InvalidDataException("Detector needs a game update."); }
        DetectedClient Verify(DetectedClient expected) => ClientSessions.Verify(expected, _ => window, Detector);
        Check(Identity.Same(client, Verify(client)) && reads == 0, "Manual verification read game data.");
        window = window with { Left = 3440 }; Check(Verify(client).Left == 3440 && reads == 0, "Manual window movement used detection.");
        Reject(() => Verify(client with { UserAssigned = false })); Check(reads == 1, "Failed automatic detection silently became manual.");
        window = window with { StartedUtcTicks = 101 }; Reject(() => Verify(client)); window = window with { StartedUtcTicks = 100, Window = 222 }; Reject(() => Verify(client));
        window = window with { Window = 111 }; Reject(() => ClientSessions.ValidateAssignments([client, client]));
        Reject(() => ClientSessions.ValidateAssignments([client, client with { ProcessId = 22 }]));
        var team = new TeamDefinition(1, [new("runa", "Runa")], [new("runa-sk", "runa", "Shadowknight")], [new("tap", "Tap", [new(new(CharacterId: "runa"), "5")])]);
        var file = new ClientFile(2, [new("runa-sk", 11, 100)], [client]);
        var restored = ClientSessions.Restore(team, JsonSerializer.Deserialize<ClientFile>(JsonSerializer.Serialize(file, Program.Json), Program.Json)!, Verify);
        Check(restored.Single().UserAssigned && reads == 1 && Routing.Resolve(team, "tap", file.Clients).Single().Key == "5", "Bindings lost assignment or input routing required detection.");
        Reject(() => ClientSessions.Restore(team, file with { Identities = [] }, Verify));
        Reject(() => ClientSessions.Restore(team, file with { Identities = [client with { Character = new("Runa", 2, 0) }] }, Verify));
        Reject(() => ClientSessions.Restore(team, file with { Identities = [client with { Character = new("Other", 5, 0) }] }, Verify));
        Reject(() => ClientSessions.Restore(team, file with { Identities = [client with { StartedUtcTicks = 999 }] }, Verify));
        Reject(() => ClientSessions.Restore(team, file with { Identities = [client with { UserAssigned = false }] }, Verify));
    }
}
