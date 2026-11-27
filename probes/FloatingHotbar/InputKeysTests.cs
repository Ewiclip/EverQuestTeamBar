using System.Text.Json;
using TeamBar;

internal static class InputKeysTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }
    internal static void Run()
    {
        Check(InputKeys.Names.Take(12).SequenceEqual("1234567890-=".Select(c => c.ToString())), "Original key choice order changed.");
        string[] extendedNames = ["Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown", "Insert", "Delete", "NumpadEnter", "NumpadDivide"];
        Check(InputKeys.Names.Length == 90 && InputKeys.Names.Skip(78).SequenceEqual(extendedNames),
            "Extended keys must follow all existing choices without shifting their indices.");
        Check(extendedNames.Select(InputKeys.Scan).SequenceEqual(new[] { 0xc8, 0xd0, 0xcb, 0xcd, 0xc7, 0xcf, 0xc9, 0xd1, 0xd2, 0xd3, 0x9c, 0xb5 }),
            "Extended keys must retain their DirectInput high bit.");
        Check(InputKeys.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == InputKeys.Names.Length
            && InputKeys.Names.Select(InputKeys.Scan).Distinct().Count() == InputKeys.Names.Length, "Key names or physical scan codes overlap.");
        Check(InputKeys.Scan("[") == 0x1a && InputKeys.Scan("]") == 0x1b && InputKeys.Scan("\\") == 0x2b,
            "Bracket and backslash keys must use their physical scan codes.");
        Check(InputKeys.Scan("a") == 0x1e && InputKeys.Scan("f12") == 0x58 && InputKeys.Scan("numpadadd") == 0x4e,
            "Letter, function and named numpad keys must accept case-insensitive names.");
        Check(InputKeys.Scan("Enter") != InputKeys.Scan("NumpadEnter") && InputKeys.Scan("/") != InputKeys.Scan("NumpadDivide")
            && InputKeys.Scan("Up") != InputKeys.Scan("Numpad8") && InputKeys.Scan("1") != InputKeys.Scan("Numpad1"),
            "Extended navigation, main keyboard and numpad keys must stay distinct.");
        foreach (string name in InputKeys.Names)
        {
            Check(InputKeys.Normalize(name.ToLowerInvariant()) == name && Routing.Scan(name) == InputKeys.Scan(name), "Key canonicalization or routing disagrees: " + name);
            Program.Validate(new(1, [new("key", "Key", [new("Fixture", name)])]));
            foreach (int modifiers in Enumerable.Range(0, 8))
            {
                var parsed = SpellOverlayKey.Parse(SpellOverlayKey.Format(name.ToLowerInvariant(), modifiers));
                Check(parsed.Key == name && parsed.Scan == InputKeys.Scan(name) && parsed.Modifiers == modifiers,
                    "Spell key/modifier round-trip changed: " + name + "/" + modifiers);
            }
        }
        var spaced = SpellOverlayKey.Parse("alt + ctrl + shift + f12");
        Check(spaced.Key == "F12" && spaced.Modifiers == 7 && SpellOverlayKey.Format(spaced.Key, spaced.Modifiers) == "Ctrl+Shift+Alt+F12",
            "Named keys must preserve modifier grammar and canonical formatting.");
        foreach (string key in new[] { "", " ", "55", "F13", "Ctrl", "Shift", "Alt", "CapsLock", "NumLock", "ScrollLock",
            "Menu", "PrintScreen", "Pause", "VolumeUp", "Win", "+", "{", "Ctrl+[" })
        {
            Reject(() => InputKeys.Scan(key), "Unsupported standalone key accepted: " + key);
            if (key != "Ctrl+[") Reject(() => Program.Validate(new(1, [new("key", "Key", [new("Fixture", key)])])), "Legacy preview validator accepted unsupported key: " + key);
        }
        foreach (string key in new[] { "Ctrl+Ctrl+[", "Ctrl+", "+[", "Ctrl++", "Win+F1", "Ctrl+Alt+Shift+Alt+]", "Alt+CapsLock" })
            Reject(() => SpellOverlayKey.Parse(key), "Invalid modifier chord accepted: " + key);
        Reject(() => SpellOverlayKey.Format("[", -1), "Negative modifier mask accepted.");
        Reject(() => SpellOverlayKey.Format("]", 8), "Unknown modifier mask accepted.");

        var team = Fixture(["A"]);
        team = team with { Buttons = [team.Buttons[0] with { Rules = [new(new(CharacterId: "fixture"), "A"), new(new(CharacterId: "fixture"), "a")] }] };
        ClientBinding[] clients = [new("fixture-persona", 11, 1)];
        Routing.Validate(team, clients);
        var taps = Routing.Resolve(team, "key-0", clients);
        Check(taps.Length == 2 && taps.All(t => t.Key == "A" && t.Scan == 0x1e), "Repeated rows must retain both taps, with canonical key names.");
        var conflict = team with { Buttons = [team.Buttons[0] with { Rules = [new(new(CharacterId: "fixture"), "A"), new(new(CharacterId: "fixture"), "B")] }] };
        Routing.Validate(conflict, clients);
        Check(Routing.Resolve(conflict, "key-0", clients).Select(t => t.Key).SequenceEqual(["A", "B"]), "Same-character keys lost their configured order.");
        foreach (int modifiers in Enumerable.Range(0, 8))
        {
            string key = SpellOverlayKey.Format("NumpadEnter", modifiers);
            var chordTeam = Fixture([key]); Routing.Validate(chordTeam, clients);
            var tap = Routing.Resolve(chordTeam, "key-0", clients).Single();
            Check(tap.Key == key && tap.Modifiers == modifiers && tap.Scan == 0x9c, "Team routing lost a modifier chord.");
        }
        var a = new RoutedTap(11, "a", "A", "Alt+6", 7, 4);
        var b = new RoutedTap(22, "b", "B", "1", 2);
        var batches = TapSequence.Create([a, a with { Key = "Ctrl+7", Scan = 8, Modifiers = 2 }, a, b, b with { Key = "2", Scan = 3 }]).ToArray();
        Check(batches.Length == 3 && batches.Select(x => x.Length).SequenceEqual([2, 2, 1])
            && batches[0][0] == a && batches[0][1] == b && batches[1][0].Modifiers == 2 && batches[2][0] == a,
            "Batching must preserve per-character order, repeats, modifiers, and broadcasting to different characters.");
    }
    private static TeamDefinition Fixture(string[] keys) => new(1, [new("fixture", "Fixture")], [new("fixture-persona", "fixture", "Cleric")],
        keys.Select((key, i) => new TeamButton("key-" + i, "Key " + i, [new(new(CharacterId: "fixture"), key)])).ToArray());

    internal static void Run(string output)
    {
        string fullOutput = Path.GetFullPath(output), folder = fullOutput + ".files";
        if (File.Exists(fullOutput) || Directory.Exists(folder)) throw new IOException("Key test output must be new.");
        Run();
        Directory.CreateDirectory(folder);
        string[] samples = ["Alt+[", "Ctrl+Shift+]", "\\", "A", "F12", "NumpadAdd"];
        var team = Fixture(samples); ClientBinding[] clients = [new("fixture-persona", 11, 1)];
        string teamPath = Path.Combine(folder, "team.json");
        byte[] bytes = TeamEditing.SaveChecked(teamPath, team, clients, null);
        var reloaded = TeamEditing.Load(teamPath); Routing.Validate(reloaded, clients);
        Check(reloaded.Buttons.Select(b => b.Rules.Single().Key).SequenceEqual(samples), "Expanded hotbar keys changed after save/reload.");
        foreach (var button in reloaded.Buttons)
            Check(Routing.Resolve(reloaded, button.Id, clients).Single().Scan == SpellOverlayKey.Parse(button.Rules.Single().Key).Scan, "Saved hotbar key routes the wrong scan code.");
        Reject(() => TeamEditing.SaveChecked(teamPath, Fixture(["F13"]), clients, bytes), "Invalid key overwrote a saved profile.");
        Check(File.ReadAllBytes(teamPath).SequenceEqual(bytes), "Rejected key save changed the working profile.");

        string[] extendedSamples = ["Up", "Down", "Home", "PageDown", "NumpadEnter", "NumpadDivide"];
        string extendedPath = Path.Combine(folder, "extended-team.json");
        TeamEditing.SaveChecked(extendedPath, Fixture(extendedSamples), clients, null);
        var extendedTeam = TeamEditing.Load(extendedPath); Routing.Validate(extendedTeam, clients);
        Check(extendedTeam.Buttons.Select(b => b.Rules.Single().Key).SequenceEqual(extendedSamples), "Extended hotbar keys changed after save/reload.");
        foreach (var button in extendedTeam.Buttons)
            Check(Routing.Resolve(extendedTeam, button.Id, clients).Single().Scan == InputKeys.Scan(button.Rules.Single().Key),
                "Saved extended hotbar key lost its DirectInput scan offset.");

        var standalone = new StandaloneButton(1, "brackets", "Brackets", team.Characters, team.Personas, [new(new(CharacterId: "fixture"), "]")], []);
        StandaloneButtons.Save(StandaloneButtons.PathFor(teamPath, standalone.Id), standalone, null);
        var savedButton = StandaloneButtons.Load(teamPath).Single().Value;
        Check(savedButton.Rules.Single().Key == "]", "Standalone bracket key changed after save/reload.");
        var live = new DetectedClient(11, 1, 111, 0, new("Fixture", 2, 130));
        Check(StandaloneButtons.Resolve(savedButton, [live]).Single().Scan == 0x1b, "Saved standalone bracket does not route to its scan code.");
        var orderedButton = standalone with { Id = "ordered", Rules = [new(new(CharacterId: "fixture"), "Alt+6"),
            new(new(CharacterId: "fixture"), "Ctrl+Shift+7"), new(new(CharacterId: "fixture"), "Alt+6")] };
        StandaloneButtons.Save(StandaloneButtons.PathFor(teamPath, orderedButton.Id), orderedButton, null);
        var restoredSequence = StandaloneButtons.Load(teamPath).Single(b => b.Value.Id == "ordered").Value;
        var restoredTaps = StandaloneButtons.Resolve(restoredSequence, [live]);
        Check(restoredTaps.Select(t => t.Key).SequenceEqual(["Alt+6", "Ctrl+Shift+7", "Alt+6"])
            && restoredTaps.Select(t => t.Modifiers).SequenceEqual([4, 3, 4]), "Standalone save/reload changed the sequence or modifiers.");
        foreach (string key in new[] { "Up", "NumpadEnter", "NumpadDivide" })
        {
            var extendedButton = standalone with { Id = key.ToLowerInvariant(), Label = key, Rules = [new(new(CharacterId: "fixture"), key)] };
            StandaloneButtons.Save(StandaloneButtons.PathFor(teamPath, extendedButton.Id), extendedButton, null);
            var restoredButton = StandaloneButtons.Load(teamPath).Single(b => b.Value.Id == extendedButton.Id).Value;
            var tap = StandaloneButtons.Resolve(restoredButton, [live]).Single();
            Check(tap.Key == key && tap.Scan == InputKeys.Scan(key), "Saved standalone extended key changed: " + key);
        }

        string[] spellKeys = ["[", "]", "Ctrl+[", "Alt+]", "Shift+A", "Ctrl+F12", "Alt+NumpadAdd", "Space", "Tab", "Enter", "Backspace", "Escape",
            "Up", "Ctrl+Down", "Shift+Left", "Alt+Right", "Ctrl+Home", "End", "PageUp", "Ctrl+Shift+PageDown", "Insert", "Delete", "Alt+NumpadEnter", "Ctrl+NumpadDivide"];
        var overlay = new SpellOverlayDefinition("keys", "Keys", "Fixture", spellKeys.Select((key, i) => new SpellWatch(100 + i, "Spell " + i, ClickKey: key)).ToArray());
        string overlayPath = Path.Combine(folder, "spell-overlays.json");
        SpellOverlayStore.Save(overlayPath, new(1, [overlay]), null);
        Check(SpellOverlayStore.Load(overlayPath).Library.Overlays.Single().Spells.Select(s => s.ClickKey).SequenceEqual(spellKeys),
            "Expanded spell keys/modifiers changed after save/reload.");
        using var report = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, new { passed = true, gameInput = false, keyCount = InputKeys.Names.Length,
            keys = InputKeys.Names.Select(name => new { name, scan = InputKeys.Scan(name) }).ToArray(),
            tests = "shared scan catalog, stable existing key order, extended scan distinction, case aliases, all modifier combinations, unsupported-key rejection, routing conflicts, legacy validator, hotbar/standalone/spell save and reload including extended keys, invalid-save preservation" }, new JsonSerializerOptions { WriteIndented = true });
    }
}
