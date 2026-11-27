using System.Diagnostics;
using System.Text.Json;

internal static class StandaloneTests
{
    internal static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    internal static void Run(string output)
    {
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (Directory.Exists(directory) || File.Exists(output)) throw new IOException("Test output must be new.");
        Directory.CreateDirectory(directory); string profile = Path.Combine(directory, "team.json");
        var team = new TeamDefinition(1, [new("runa", "Runa"), new("fellwort", "Fellwort")],
            [new("runa-cleric", "runa", "Cleric"), new("runa-sk", "runa", "Shadowknight"), new("fellwort-shaman", "fellwort", "Shaman")], []);
        TeamEditing.Save(profile, team, []); byte[] original = File.ReadAllBytes(profile);
        DetectedClient[] clients = [new(11, 1, 111, 0, new("Runa", 5, 130)), new(22, 2, 222, 1000, new("Fellwort", 10, 130))];
        var button = new StandaloneButton(1, "solo", "Independent", team.Characters, team.Personas,
            [new(new(CharacterId: "runa"), "1"), new(new(CharacterId: "fellwort"), "2")], []);
        Check(StandaloneButtons.Visible(button, []), "Always visibility must work without clients.");
        var conditional = button with { ShowFor = [new(Class: "Cleric"), new(Class: "Shadowknight")] };
        Check(StandaloneButtons.Visible(conditional, clients), "OR visibility did not match the second class.");
        Check(!StandaloneButtons.Visible(conditional, [clients[1]]), "Unmatched class was visible.");
        Check(!StandaloneButtons.Visible(conditional, []), "Unknown/loading personas must not show conditional buttons.");
        var named = button with { ShowFor = [new(PersonaId: "runa-cleric")] };
        Check(!StandaloneButtons.Visible(named, [clients[1] with { Character = new("Fellwort", 2, 130) }]), "A named persona matched another character of the same class.");
        Check(StandaloneButtons.Visible(named, [clients[0] with { Character = new("Runa", 2, 130) }]), "A named active persona did not match.");
        var taps = StandaloneButtons.Resolve(button, clients);
        Check(taps.Length == 2 && taps[0].Key == "1" && taps[1].Key == "2", "Independent routing lost per-recipient keys.");
        string path = StandaloneButtons.PathFor(profile, button.Id); byte[] first = StandaloneButtons.Save(path, conditional, null);
        Check(StandaloneButtons.Load(profile).Single().Value.ShowFor.Length == 2, "Standalone round-trip lost visibility.");
        Check(original.SequenceEqual(File.ReadAllBytes(profile)), "Saving a standalone modified bar configuration.");
        bool refused = false;
        try { StandaloneButtons.Save(path, button with { Rules = [new(new(Class: "Shaman"), "F13")] }, first); } catch (InvalidDataException) { refused = true; }
        Check(refused && first.SequenceEqual(File.ReadAllBytes(path)), "Invalid save replaced a standalone file.");
        File.AppendAllText(path, " "); refused = false;
        try { StandaloneButtons.Save(path, button, first); } catch (IOException) { refused = true; }
        Check(refused, "Concurrent standalone edit was overwritten.");
        File.WriteAllBytes(path, first);
        WindowLayouts.SaveBar(path, new(-1200, 200));
        Check(StandaloneButtons.Load(profile).Length == 1 && WindowLayouts.LoadBar(path) == new Point(-1200, 200), "Per-button position was mistaken for a definition.");
        StandaloneButtons.Remove(path, first);
        Check(StandaloneButtons.Load(profile).Length == 0 && Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "removed")).Length == 1, "Remove must retain a recoverable copy.");
        new TeamBar.SetupWindow(profile, "test-controller.exe", () => new(clients, []), userSettings: new TeamBar.MemoryUserSettingsStore()).StandaloneSelfTest();
        Check(original.SequenceEqual(File.ReadAllBytes(profile)), "Standalone editor changed the bar profile.");
        TeamBar.MappingConflictEditorTests.Run(Path.Combine(directory, "mapping-conflicts"));

        // Controller stand-in only. It never opens a game or generates keyboard input.
        using var process = Process.GetCurrentProcess();
        var fixtureClient = new DetectedClient(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Runa", 5, 0), true);
        team = TeamEditing.MergeDetected(team, [fixtureClient]);
        var live = new LiveSetup(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), team, TeamEditing.Bind(team, [fixtureClient]), [fixtureClient]);
        string runtimeProfile = Path.Combine(directory, "runtime", "team.json");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeProfile)!);
        var conditionalRuntime = button with { Id = "conditional", ShowFor = [new(Class: "Shadowknight")] };
        var alwaysRuntime = button with { Id = "always" };
        foreach (var doc in new[] { conditionalRuntime, alwaysRuntime }) StandaloneButtons.Save(StandaloneButtons.PathFor(runtimeProfile, doc.Id), doc, null);
        using var journal = new StreamWriter(new FileStream(output + ".runtime.jsonl", FileMode.CreateNew, FileAccess.Write)) { AutoFlush = true };
        using var workspace = new FloatingWorkspace(live, StandaloneButtons.Load(runtimeProfile), runtimeProfile, journal);
        workspace.ReadIdentity = expected => ClientSessions.Verify(expected,
            _ => new(expected.ProcessId, expected.StartedUtcTicks, expected.Window, expected.Left, "Assigned fixture"),
            _ => throw new InvalidOperationException("Assigned standalone runtime must not call the detector."));
        workspace.SelfTest(output + ".floating.png");
        var groupedTeam = team with { Buttons = [new("first", "First", [new(new(CharacterId: team.Characters.Single(c => c.Name == "Runa").Id), "1")]), new("second", "Second", [new(new(CharacterId: team.Characters.Single(c => c.Name == "Runa").Id), "2")])] };
        using (var grouped = new FloatingWorkspace(live with { Team = groupedTeam }, [], runtimeProfile + ".grouped", journal))
        { grouped.ReadIdentity = expected => expected; grouped.GroupedThemeSelfTest(); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, tests = "independent files and bar preservation; always/class OR/named persona/loading visibility; per-recipient routing; atomic invalid/concurrent-save protection; recoverable removal; WPF standalone create/edit/save/reload/remove-last; empty library apply and reload; empty condition validation; theme/navigation draft preservation; native disjoint region and WindowFromPoint; direct-click startup; one click/one tap; cancellation; independent position; automatic visibility and fail-closed persona change; controller disconnect", gameplayInput = false }));
    }
}
