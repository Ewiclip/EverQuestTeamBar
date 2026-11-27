using System.Text;
using System.Text.Json;

internal static class EditorTests
{
    internal static void Run(string log)
    {
        using var access = TeamBar.GameDataAccess.FixtureScope(true);
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        const ulong image = 0x140000000, game = 0x100000, pc = 0x200000, player = 0x300000, list = 0x400000, profile = 0x500000;
        var memory = new Dictionary<ulong, byte>();
        void Put(ulong a, byte[] data) { for (int i = 0; i < data.Length; i++) memory[a + (ulong)i] = data[i]; }
        void U64(ulong a, ulong v) => Put(a, BitConverter.GetBytes(v));
        void I32(ulong a, int v) => Put(a, BitConverter.GetBytes(v));
        byte[] Read(ulong a, int n) => Enumerable.Range(0, n).Select(i => memory.GetValueOrDefault(a + (ulong)i)).ToArray();
        U64(image + 0xf35250, game); I32(game + 0x5e4, 5); U64(image + 0xeb6440, player); U64(image + 0xeb5fb0, pc); U64(pc + 0x2848, player);
        Put(player + 0xb4, Encoding.ASCII.GetBytes("Runa\0")); Put(pc + 0x2fa0, Encoding.ASCII.GetBytes("Runa\0"));
        U64(pc + 0x2f08, list); U64(list + 8, profile); I32(player + 0xfe4, 5); I32(profile + 0x17cc, 5); I32(profile + 0x17e8, 130);
        var identity = Identity.Decode(Read, image); Check(identity.Character.Name == "Runa" && identity.Character.Class == "Shadowknight", "Identity decode failed.");
        void Reject(Action change, Action reset, string message)
        {
            change(); bool failed = false; try { Identity.Decode(Read, image); } catch (InvalidDataException) { failed = true; } reset(); Check(failed, message);
        }
        Reject(() => I32(profile + 0x17cc, 2), () => I32(profile + 0x17cc, 5), "Class disagreement accepted.");
        Reject(() => I32(game + 0x5e4, 1), () => I32(game + 0x5e4, 5), "Character-select state accepted.");
        Reject(() => U64(pc + 0x2848, 0), () => U64(pc + 0x2848, player), "Invalid local-player link accepted.");
        Reject(() => { I32(list, 1); U64(list + 0x18, list); }, () => { I32(list, 0); U64(list + 0x18, 0); }, "Cyclic profile chain accepted.");
        Reject(() => Put(pc + 0x2fa0, Encoding.ASCII.GetBytes("Else\0")), () => Put(pc + 0x2fa0, Encoding.ASCII.GetBytes("Runa\0")), "Name disagreement accepted.");
        DetectedClient[] clients = [new(11, 1, 111, 0, new("Runa", 5, 130)), new(22, 2, 222, 3440, new("Fellwort", 10, 130))];
        Check(!Identity.Same(clients[0], clients[0] with { Character = new("Runa", 2, 130) }), "Persona change was not detected.");
        Check(!Identity.Same(clients[0], clients[0] with { StartedUtcTicks = 99 }), "PID reuse was not detected.");
        var original = new TeamDefinition(1, [new("runa", "Runa"), new("fellwort", "Fellwort")],
            [new("runa-cleric", "runa", "Cleric"), new("fellwort-current", "fellwort", null)],
            [new("split", "Split action", [new(new(CharacterId: "runa"), "1"), new(new(CharacterId: "fellwort"), "2")]),
             new("invisibility", "Invisibility", [new(new(CharacterId: "runa"), "5"), new(new(CharacterId: "fellwort"), "5")])]);
        var team = TeamEditing.MergeDetected(original, clients); var bindings = TeamEditing.Bind(team, clients);
        Routing.Validate(team, bindings);
        Check(team.Personas.Any(p => p.Id == "runa-cleric") && bindings[0].PersonaId != "runa-cleric", "Merge lost an inactive persona or bound the wrong class.");
        Check(TeamEditing.MergeDetected(team, clients).Personas.Length == team.Personas.Length, "Refresh duplicated personas.");
        string profilePath = Path.GetFullPath(log) + ".profile.json";
        if (File.Exists(log) || File.Exists(profilePath)) throw new IOException("Test output must be new.");
        TeamEditing.Save(profilePath, team, bindings); byte[] good = File.ReadAllBytes(profilePath);
        bool bad = false; try { TeamEditing.Save(profilePath, team with { Buttons = [team.Buttons[0] with { Rules = [new(new(CharacterId: "runa"), "F13")] }] }, bindings); } catch (InvalidDataException) { bad = true; }
        Check(bad && good.SequenceEqual(File.ReadAllBytes(profilePath)), "Invalid save replaced a working profile.");
        ProfileSaveTests.Run(profilePath + ".concurrent.json", team, bindings);
        TeamBar.UserSettingsTests.Run(log + ".settings.json");
        ClientSessionTests.Run();
        new TeamBar.SetupWindow(profilePath, "test-controller.exe", () => new(clients, []), userSettings: new TeamBar.LocalUserSettingsStore(profilePath + ".preferences.json")).SelfTest(scan => clients = scan);
        var persisted = TeamEditing.Load(profilePath); Routing.Validate(persisted, TeamEditing.Bind(persisted, clients));
        Check(persisted.Buttons.Length == 0 && (persisted.Groups?.Length ?? 0) == 0 && StandaloneButtons.Load(profilePath).Length == 0,
            "The editor's deliberate empty library did not persist.");
        Routing.SelfTest();
        using var output = new FileStream(log, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new { passed = true, tests = "identity cross-checks, loading state, invalid pointers, cyclic profiles, name/class mismatch, persona/PID change, discovery merge, atomic invalid-save preservation, WPF editor selection/edit/reorder/add/remove, unified class-recipient editing and rule preservation, empty recipient validation, delete-last-button offline apply and empty profile reload, deliberate deselection preserved across refresh, concurrent profile edit rejection, profile round-trip, routing precedence", limitation = "Synthetic memory and editor fixtures; no gameplay input generated." });
    }
}
