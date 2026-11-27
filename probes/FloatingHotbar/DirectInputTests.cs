using System.Diagnostics;
using System.Text.Json;

internal static class DirectInputTests
{
    internal static void Run(string output)
    {
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(directory)) throw new IOException("Direct-input test output must be new.");
        Directory.CreateDirectory(directory);
        string transitionFolder = Path.Combine(directory, "input-transition");
        using (var first = TeamBar.SpellOverlayInputHost.TryBeginTransition(transitionFolder))
        {
            StandaloneTests.Check(first is not null, "The first input-host transition did not acquire its lock.");
            using var duplicate = TeamBar.SpellOverlayInputHost.TryBeginTransition(transitionFolder);
            StandaloneTests.Check(duplicate is null, "Concurrent setup and spell-manager transitions could both start an input host.");
        }
        using (var next = TeamBar.SpellOverlayInputHost.TryBeginTransition(transitionFolder))
            StandaloneTests.Check(next is not null, "A completed input-host transition did not release its lock.");
        using var process = Process.GetCurrentProcess();
        var client = new DetectedClient(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Runa", 5, 130));
        var team = new TeamDefinition(1, [new("runa", "Runa")], [new("runa-sk", "runa", "Shadowknight")],
            [new("tap", "Direct tap", [new(new(CharacterId: "runa"), "1")])]);
        string profile = Path.Combine(directory, "team.json"); TeamEditing.Save(profile, team, []);
        var solo = new StandaloneButton(1, "solo", "Standalone direct tap", team.Characters, team.Personas, team.Buttons[0].Rules, []);
        StandaloneButtons.Save(StandaloneButtons.PathFor(profile, solo.Id), solo, null);
        var live = new LiveSetup(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), team, TeamEditing.Bind(team, [client]), [client]);
        using var journal = new StreamWriter(new FileStream(output + ".runtime.jsonl", FileMode.CreateNew, FileAccess.Write)) { AutoFlush = true };
        void RunCase(string name, Action<FloatingWorkspace> test, bool standalone = false)
        {
            using var workspace = new FloatingWorkspace(live, standalone ? StandaloneButtons.Load(profile) : [], profile + "." + name, journal);
            workspace.ReadIdentity = expected => expected; test(workspace);
        }
        RunCase("bar", workspace => workspace.EditionOneTapSelfTest());
        RunCase("standalone", workspace => workspace.StandaloneDirectInputSelfTest(), true);
        RunCase("cancel-race", workspace => workspace.ControlRaceSelfTest());
        RunCase("control-timeout", workspace => workspace.ControlTimeoutSelfTest());
        RunCase("tap-timeout", workspace => workspace.TapTimeoutSelfTest());
        foreach (string scenario in new[] { "bar", "dock", "cancel", "persona", "failure", "timeout", "reject-key", "reject-modifiers", "reject-loading" })
            RunCase("sequence-" + scenario, workspace => workspace.SequenceSelfTest(scenario));
        var emptyTeam = team with { Buttons = [] };
        var hiddenLive = live with { Team = emptyTeam, Clients = TeamEditing.Bind(emptyTeam, [client]) };
        using (var workspace = new FloatingWorkspace(hiddenLive, [], profile + ".hidden-host", journal))
        {
            workspace.ReadIdentity = expected => expected;
            workspace.SpellPipeName = "TeamBar-HiddenHostTest-" + Guid.NewGuid().ToString("N");
            workspace.HiddenHostSelfTest();
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, gameplayInput = false,
            tests = "ready immediately with no control strip; one tap per bar and standalone click; no input from holds, elapsed time, background refresh or release without press; busy clicks not queued; cancellation races and captured-release safety; persona invalidation; control and tap acknowledgement deadlines; healthy hotkey clearing retains private spell input and tray exit; stale empty host closes after acknowledgment; ready zero-surface host and close handshake; serialized input-host transitions" }));
    }
}
