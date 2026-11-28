using System.Text.Json;
using TeamBar;

internal static class RoutingConflictTests
{
    internal static TeamDefinition Fixture() => new(1, [new("aster", "Aster"), new("bran", "Bran")],
        [new("aster-shaman", "aster", "Shaman"), new("aster-cleric", "aster", "Cleric"), new("bran-cleric", "bran", "Cleric")],
        [new("conflict", "Conflicting action", [new(new(CharacterId: "aster"), "1"), new(new(PersonaId: "aster-shaman"), "2"),
            new(new(CharacterId: "aster", Class: "Shaman"), "3"), new(new(Class: "Cleric"), "4")]),
         new("healthy", "Healthy action", [new(new(CharacterId: "aster"), "5"), new(new(Class: "Cleric"), "6")])]);
    internal static DetectedClient[] Clients() => [new(101, 1001, 11, 0, new("Aster", 10, 130)), new(202, 2002, 22, 0, new("Bran", 2, 130))];
    internal static SavedButton Saved(TeamDefinition team, string id, string path = "fixture")
    {
        var button = team.Buttons.Single(b => b.Id == id);
        var value = new StandaloneButton(1, id, button.Label, team.Characters, team.Personas, button.Rules, []);
        return new(path, value, JsonSerializer.SerializeToUtf8Bytes(value, Program.Json));
    }
    internal static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); checks.Add(why); }
        Routing.SelfTest();
        Check(true, "Existing recipient precedence and repeated-key sequence regressions pass.");
        var team = Fixture(); var clients = Clients(); var bindings = TeamEditing.Bind(team, clients);
        var original = JsonSerializer.Serialize(team);
        Routing.Validate(team, bindings);
        Check(true, "Structurally valid competing selectors load without preventing startup.");
        var conflicts = Routing.Conflicts(team);
        Check(conflicts.Length == 1 && conflicts[0].ButtonId == "conflict" && conflicts[0].PersonaId == "aster-shaman"
            && conflicts[0].RuleIndices.SequenceEqual([1, 2]), "Diagnostics identify only competing winning rows and their recipient.");
        Check(Routing.Resolve(team, "conflict", bindings).Length == 0,
            "An active conflict disables the entire button, including healthy fan-out and fallback rows.");
        Check(Routing.Resolve(team, "healthy", bindings).Select(t => t.Key).SequenceEqual(["5", "6"]),
            "Unrelated buttons retain their complete routes.");
        Check(Routing.Conflicts(team, []).Length == 0 && Routing.Conflicts(team).Length == 1,
            "All-persona editor diagnostics remain distinct from the active-client conflict set.");
        var inactive = clients.Select(c => c.Character.Name == "Aster" ? c with { Character = c.Character with { ClassId = 2 } } : c).ToArray();
        Check(Routing.Conflicts(team, TeamEditing.Bind(team, inactive)).Length == 0
            && Routing.Resolve(team, "conflict", TeamEditing.Bind(team, inactive)).Select(t => t.Key).SequenceEqual(["1", "4"]),
            "An inactive persona conflict does not disable the current character and class routes.");
        var ordered = team with { Buttons = [new("ordered", "Ordered sequence", [new(new(CharacterId: "ASTER", Class: "Sha man"), "Alt+1"),
            new(new(CharacterId: "aster", Class: "Shaman"), "2"), new(new(CharacterId: "aster", Class: "SHAMAN"), "2")])] };
        Routing.Validate(ordered, bindings);
        Check(Routing.Conflicts(ordered).Length == 0 && Routing.Resolve(ordered, "ordered", bindings).Select(t => t.Key).SequenceEqual(["Alt+1", "2", "2"]),
            "Equivalent selectors normalize ID case and class whitespace while preserving ordered repeated keys.");
        var sameKey = team with { Buttons = [team.Buttons[0] with { Rules = [new(new(PersonaId: "aster-shaman"), "2"), new(new(CharacterId: "aster", Class: "Shaman"), "2")] }] };
        Check(Routing.Resolve(sameKey, "conflict", bindings).Length == 0,
            "Different winning selector domains remain ambiguous even when they name the same key.");
        var lower = team with { Buttons = [new("lower", "Ranked sequence", [new(new(Class: "Shaman"), "1"), new(new(CharacterId: "aster"), "2"),
            new(new(PersonaId: "aster-shaman"), "3"), new(new(PersonaId: "ASTER-SHAMAN"), "4")])] };
        Check(Routing.Conflicts(lower).Length == 0 && Routing.Resolve(lower, "lower", bindings).Select(t => t.Key).SequenceEqual(["3", "4"]),
            "Different lower-priority selectors do not conflict with the winning persona sequence.");
        var mapped = Routing.DisplayButton(team, "conflict", bindings);
        Check(mapped.DisabledReason is not null && FloatingPainter.ActionHint(mapped).Contains("Conflicting selectors")
            && Routing.DisplayButton(team, "healthy", bindings).DisabledReason is null,
            "Runtime display metadata explains disabled conflicts without marking healthy buttons.");
        Check(!JsonSerializer.Serialize(mapped).Contains("DisabledReason") && JsonSerializer.Serialize(team) == original,
            "Conflict evaluation preserves all saved mappings and keeps runtime status out of configuration JSON.");
        var saved = Saved(team, "conflict");
        Check(StandaloneButtons.Conflicts(saved.Value, clients).Length == 1 && StandaloneButtons.Resolve(saved.Value, clients).Length == 0
            && StandaloneButtons.Display(saved.Value, clients).DisabledReason is not null,
            "Standalone resolution and display apply the same complete-button conflict protection.");
        string directory = Path.GetFullPath(output) + ".fixtures";
        Directory.CreateDirectory(directory); string profile = Path.Combine(directory, "team.json");
        TeamEditing.Save(profile, team, bindings);
        string buttonPath = StandaloneButtons.PathFor(profile, "conflict");
        StandaloneButtons.Save(buttonPath, saved.Value, null);
        var loaded = StandaloneButtons.Load(profile).Single();
        Check(JsonSerializer.Serialize(loaded.Value.Rules) == JsonSerializer.Serialize(saved.Value.Rules)
            && Routing.Conflicts(TeamEditing.Load(profile)).Length == 1,
            "Saving and reopening a diagnosed conflict preserves every original row for repair.");
        foreach (var invalid in new[] { new ButtonRule(new(PersonaId: "missing"), "1"), new ButtonRule(new(PersonaId: "aster-shaman"), "F13") })
        {
            bool rejected = false;
            try { Routing.Validate(team with { Buttons = [new("bad", "Invalid mapping", [invalid])] }, bindings); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Structural validation still rejects " + (invalid.Target.PersonaId == "missing" ? "unknown personas." : "unsupported keys."));
        }
        foreach (string theme in new[] { "light", "dark" })
        {
            var appearance = new FloatingAppearance { ThemeId = theme };
            using var bitmap = new Bitmap(448, 80); using var graphics = Graphics.FromImage(bitmap);
            FloatingPainter.Draw(graphics, "FICTIONAL CONFLICT FIXTURE", [mapped, Routing.DisplayButton(team, "healthy", bindings)], appearance, pressed: 0, hovered: 0);
            var firstButton = FloatingPainter.ActionBounds(0, true);
            var border = bitmap.GetPixel((int)firstButton.Left + 17, (int)firstButton.Top); var expected = FloatingPainter.ConflictColor(appearance.Selected);
            Check(border.R == expected.R && border.G == expected.G && border.B == expected.B,
                "A conflicted floating button keeps its red border under hover and press in the " + theme + " theme.");
            bitmap.Save(output + "." + theme + ".png");
        }
        FloatingWorkspace.TestRoutingConflicts(team, clients, profile, Check);
        SpellDockTests.Run(Check, output + ".dock");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
    }
    internal static void RunUi(string output)
    {
        var checks = new List<string>(); Exception? failure = null;
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); checks.Add(why); }
        app.Startup += async (_, _) =>
        {
            try
            {
                MappingConflictEditorTests.Run(Path.GetFullPath(output) + ".fixtures");
                checks.Add("Saved conflicts load in the editor, use red labels and borders, retain inactive diagnostics, and save an ordered sequence after repair.");
                await SpellOverlayWindow.TestDockedControls(Check);
            }
            catch (Exception error) { failure = error; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("Mapping conflict UI fixture failed.", failure);
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
    }
}

internal sealed partial class FloatingWorkspace
{
    internal static void TestRoutingConflicts(TeamDefinition team, DetectedClient[] identities, string profile, Action<bool, string> check)
    {
        var buttons = team.Buttons.Select(b => RoutingConflictTests.Saved(team, b.Id, profile + "." + b.Id)).ToArray();
        team = team with { Groups = [new("fixture-group", "Fixture group", [new("conflict", 0), new("healthy", 0)])] };
        using var journal = new StreamWriter(new MemoryStream());
        using var workspace = new FloatingWorkspace(new("unused-controller", team, TeamEditing.Bind(team, identities), identities), buttons, profile, journal);
        check(workspace.surfaces.Count == 4 && workspace.surfaces.Count(s => s.Actions.Any(a => a.DisabledReason is not null)) == 3,
            "Inline, grouped and independent floating surfaces all retain a visible disabled conflict.");
        foreach (var surface in workspace.surfaces)
        {
            int index = Array.FindIndex(surface.Actions, a => a.DisabledReason is not null);
            if (index >= 0) workspace.SendTap(surface, index);
        }
        check(!workspace.busy && workspace.sequence == 0 && !workspace.Failed && workspace.status.Contains("Conflicting selectors"),
            "Attempting every disabled floating control never starts a backend sequence or fails the session.");
        var inactive = identities.Select(c => c.Character.Name == "Aster" ? c with { Character = c.Character with { ClassId = 2 } } : c).ToArray();
        workspace.RebindPersonas(inactive);
        check(workspace.surfaces.All(s => s.Actions.All(a => a.DisabledReason is null))
            && workspace.surfaces.SelectMany(s => s.Taps).All(t => t.Length > 0),
            "Switching away from the conflicted persona restores usable routes across all floating surfaces.");
        workspace.RebindPersonas(identities);
        check(workspace.surfaces.Count(s => s.Actions.Any(a => a.DisabledReason is not null)) == 3
            && workspace.surfaces.SelectMany(s => s.Actions.Zip(s.Taps)).Where(p => p.First.DisabledReason is not null).All(p => p.Second.Length == 0),
            "Switching back automatically restores red disabled state and removes every affected route.");
    }
}
