using System.Diagnostics;
using System.Text.Json;
using TeamBar;

internal static class ButtonGroupTests
{
    internal static void Run(string output)
    {
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (Directory.Exists(directory) || File.Exists(output)) throw new IOException("Use a new group-test report.");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        string profile = Path.Combine(directory, "team.json");
        var team = new TeamDefinition(1, [new("fixture", "Fixture")], [new("cleric", "fixture", "Cleric")],
            [new("heal", "Quick heal", [new(new(CharacterId: "fixture"), "Ctrl+Shift+H")]),
             new("assist", "Assist", [new(new(CharacterId: "fixture"), "Alt+1"), new(new(CharacterId: "fixture"), "2")])], "fixture");
        TeamEditing.Save(profile, team, []);
        var independent = new StandaloneButton(1, "utility", "Utility", team.Characters, team.Personas,
            [new(new(CharacterId: "fixture"), "6")], []);
        var independentPath = StandaloneButtons.PathFor(profile, independent.Id);
        var independentBytes = StandaloneButtons.Save(independentPath, independent, null);
        WindowLayouts.SaveBar(profile, new(240, 170));
        byte[] original = File.ReadAllBytes(profile);
        ButtonGroups.Migrate(profile);
        var migrated = TeamEditing.Load(profile); var library = StandaloneButtons.Load(profile); var group = migrated.Groups!.Single();
        Check(migrated.Buttons.Length == 0 && library.Length == 3 && group.Buttons.Length == 2
            && group.FollowCharacterId == "fixture" && group.Columns == 2, "Migration produces shared saved buttons and a group with the original character anchor.");
        Check(library.Where(b => b.Value.Id != "utility").All(b => !b.Value.Floating)
            && independentBytes.SequenceEqual(File.ReadAllBytes(independentPath)), "Migration avoids duplicate floating copies and preserves existing library bytes.");
        Check(library.Single(b => b.Value.Label == "Quick heal").Value.Rules[0].Key == "Ctrl+Shift+H"
            && library.Single(b => b.Value.Label == "Assist").Value.Rules.Select(r => r.Key).SequenceEqual(["Alt+1", "2"]),
            "Migration preserves modifier chords and ordered repeated-recipient mappings.");
        Check(WindowLayouts.LoadBar(ButtonGroups.PositionPath(profile, group.Id)) == new Point(240, 170),
            "The converted group retains the original bar's desktop position.");
        string backup = Directory.GetDirectories(Path.Combine(directory, "library-backups")).Single();
        Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(backup, "team.json"))), "Migration keeps an exact original-profile backup.");
        var migratedBytes = File.ReadAllBytes(profile); ButtonGroups.Migrate(profile);
        Check(migratedBytes.SequenceEqual(File.ReadAllBytes(profile)) && StandaloneButtons.Load(profile).Length == 3, "Migration is idempotent.");
        Check(group.ColumnNames is null && ButtonColumns.Names(null, group.Columns).SequenceEqual(["Column 1", "Column 2"]), "Legacy groups retain their layout and receive default column labels without migration.");
        var named = group with { ColumnNames = ["Utility", "Combat"] };
        var namedJson = JsonSerializer.Serialize(named, Program.Json);
        Check(JsonSerializer.Deserialize<ButtonGroup>(namedJson, Program.Json)!.ColumnNames!.SequenceEqual(named.ColumnNames), "Custom column names round-trip with floating group definitions.");
        bool rejectedName = false;
        try { ButtonGroups.Validate([named with { ColumnNames = ["", "Combat"] }], migrated.Characters); } catch (InvalidDataException) { rejectedName = true; }
        Check(rejectedName, "Incomplete column names cannot replace valid saved layouts.");
        var shifted = ButtonColumns.RemoveEmpty([new("utility", 2)], 1);
        Check(shifted.Single().Column == 1 && ButtonColumns.Names(["Utility", "Other", "Combat"], 3).Where((_, i) => i != 1).SequenceEqual(["Utility", "Combat"]), "Removing an empty column retains neighboring names and references.");
        var modified = library.Single(b => b.Value.Label == "Quick heal");
        StandaloneButtons.Save(modified.Path, modified.Value with { Label = "Shared healing" }, modified.Bytes);
        library = StandaloneButtons.Load(profile);
        Check(SpellDockRules.Describe(modified.Value.Id, library, []).Label == "Shared healing"
            && group.Buttons.Any(b => b.ButtonId == modified.Value.Id), "Editing a library button updates group references without copying mappings.");
        using var process = Process.GetCurrentProcess();
        var client = new DetectedClient(process.Id, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Fixture", 2, 130));
        var live = new LiveSetup(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), migrated, TeamEditing.Bind(migrated, [client]), [client]);
        using (var log = new StreamWriter(new FileStream(Path.Combine(directory, "runtime.jsonl"), FileMode.CreateNew, FileAccess.Write)))
        using (var workspace = new FloatingWorkspace(live, library, profile, log))
        {
            workspace.ReadIdentity = c => c; workspace.ReadFrame = _ => null;
            workspace.GroupReferenceSelfTest(profile, Check);
        }
        var visual = migrated with { Groups = [group with { Name = "Support", Columns = 2,
            Buttons = [.. group.Buttons, new("utility", 0)] }] };
        TeamEditing.Save(profile, visual, []);
        new SetupWindow(profile, "input-free-review", () => new([client], []), true, new MemoryUserSettingsStore()).ReviewUnifiedGroups(directory);
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
    }
}

internal sealed partial class FloatingWorkspace
{
    internal void GroupReferenceSelfTest(string profile, Action<bool, string> check)
    {
        var group = surfaces.Single(s => s.Group is not null);
        check(surfaces.Count == 2 && group.Actions.Length == 2 && group.Actions.Any(a => a.Label == "Shared healing"),
            "Runtime displays one shared group plus only the separately enabled floating button.");
        check(group.Taps.SelectMany(t => t).Any(t => t.Key == "Ctrl+Shift+H" && t.Modifiers == 3),
            "Grouped references retain two held modifiers and a tapped key.");
        var cells = GroupCells(group);
        check(cells[0].Left == 3 && GroupCanvas(group).Width - cells[1].Right == 3 &&
            cells[0].Right + 18 == cells[1].Left && ActionAt(group, new(cells[1].X + 10, cells[1].Y + 1)) == 1,
            "The outermost columns sit three pixels from the shell, and the first pixels of the button still receive clicks.");
        var stacked = new Surface(group.Path, group.Heading, [], []) { Group = group.Group, Members = [.. group.Members, new("utility", 0)] };
        var bottomCell = GroupCells(stacked)[^1];
        check(GroupCanvas(stacked).Height - bottomCell.Bottom == 3 && ActionAt(stacked, new(bottomCell.X + 10, bottomCell.Bottom + 1)) == -1,
            "A multirow hotkey group ends three pixels below its last button and its bottom padding does not receive clicks.");
        var snapshot = RecoverySnapshot();
        var emptyGroup = group.Group! with { Buttons = [] };
        var emptySetup = snapshot.Setup with { Team = snapshot.Setup.Team with { Groups = [emptyGroup] } };
        var visible = BuildSurfaces(emptySetup, snapshot.Buttons, profile, current);
        check(visible.All(s => s.Group is null) && visible.Any(s => s.Condition is not null),
            "An empty floating group has no display or mouse region while standalone buttons stay available.");
        check(snapshot.Setup.Team.Groups!.Length == 1 && snapshot.Buttons.Length == 3,
            "Disconnect recovery retains group membership and group-only button definitions.");
    }
}

namespace TeamBar
{
    public partial class SetupWindow
    {
        internal void ReviewUnifiedGroups(string directory)
        {
            try
            {
                Show();
                long until = Environment.TickCount64 + 10000;
                while (refreshing || clients.Count == 0)
                { if (Environment.TickCount64 > until) throw new TimeoutException("Group review did not load."); Settle(); Thread.Sleep(5); }
                DarkThemeChoice.IsChecked = true;
                ButtonList.SelectedItem = standaloneDrafts.Single(b => b.Label == "Shared healing");
                Settle(); Export(Path.Combine(directory, "buttons-dark.png"));
                BarLibraryMode.IsChecked = true; Settle(); Export(Path.Combine(directory, "groups-dark.png"));
                LightThemeChoice.IsChecked = true; Settle(); Export(Path.Combine(directory, "groups-light.png"));
                Width = 980; Height = 720; Settle(); Export(Path.Combine(directory, "groups-compact.png"));
            }
            finally { Close(); }
        }
    }
}
