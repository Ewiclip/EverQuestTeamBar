using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class EditionTests
{
    private static void Check(bool value, string message) => StandaloneTests.Check(value, message);
    internal static void Run(string output)
    {
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(directory)) throw new IOException("Single-version test output must be new.");
        Directory.CreateDirectory(directory);
        var team = new TeamDefinition(1, [new("runa", "Runa")], [new("runa-sk", "runa", "Shadowknight")],
            [new("tap", "Tap", [new(new(CharacterId: "runa"), "1")])]);
        TestMigration(directory, team);
        TestSettings(Path.Combine(directory, "settings"));
        TestImport(team);
        var assembly = typeof(Program).Assembly;
        foreach (string type in new[] { "ButtonRepeats`1", "RepeatOptions", "EscapeStopShortcut", "RepeatTests" })
            Check(assembly.GetType(type) is null, "Team Bar still contains " + type);
        foreach (var type in new[] { typeof(TeamButton), typeof(StandaloneButton), typeof(ButtonMapping), typeof(TeamBar.ButtonDraft), typeof(TeamBar.UserSettings) })
            foreach (string property in new[] { "Repeat", "PauseAllLoops", "ResumeAllLoops", "RepeatEnabled", "PauseLoopsOnEscape" })
                Check(type.GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null, "Model retained " + property);
        foreach (string type in new[] { "InventoryDecoder", "RecipeDecoder", "TeamBar.ItemCollectorWindow", "TeamBar.ContributionClient", "TeamBar.SpellOverlayStore" })
            Check(assembly.GetType(type) is not null, "Team Bar lost " + type);
        using var process = Process.GetCurrentProcess();
        var client = new DetectedClient(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Runa", 5, 130));
        string runtimeProfile = Path.Combine(directory, "runtime", "team.json"); TeamEditing.Save(runtimeProfile, team, []);
        var window = new TeamBar.SetupWindow(runtimeProfile, "review-only.exe", () => new([], []), true, new TeamBar.MemoryUserSettingsStore());
        Check(window.FindName("LoopMode") is null && window.FindName("ButtonAction") is null && window.FindName("EscapeStopsLoops") is null, "Compiled markup includes loop controls.");
        Check(window.FindName("ItemCollectionButton") is not null, "Data collection was removed.");
        Check(window.Title.Contains(BuildEdition.Label), "Editor does not identify Team Bar."); window.Close();
        var live = new LiveSetup(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), team, TeamEditing.Bind(team, [client]), [client]);
        using var journal = new StreamWriter(new FileStream(output + ".runtime.jsonl", FileMode.CreateNew, FileAccess.Write)) { AutoFlush = true };
        using (var workspace = new FloatingWorkspace(live, [], runtimeProfile, journal))
        { workspace.ReadIdentity = expected => expected; workspace.EditionOneTapSelfTest(); }
        using (var workspace = new FloatingWorkspace(live, [], runtimeProfile + ".timeout", journal))
        { workspace.ReadIdentity = expected => expected; workspace.EditionTimeoutSelfTest(); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, product = BuildEdition.Label, gameplayInput = false,
            tests = "single executable and model; compiled markup; safe legacy imports; one-time canonical migration; source and unknown field preservation; placements; settings, overlays and contributions; ready immediately with no control strip; one tap per click; no held/delayed/release-only taps; cancellation and captured-release safety; acknowledgement deadlines" }));
    }
    private static void TestMigration(string directory, TeamDefinition team)
    {
        string originalProfile = Path.Combine(directory, "team.json"); TeamEditing.Save(originalProfile, team, []);
        string manualProfile = Path.Combine(directory, "manual", "team.json"); TeamEditing.Save(manualProfile, team with { Buttons = [team.Buttons[0] with { Label = "Older manual" }] }, []);
        string source = Path.Combine(directory, "looping", "team.json"); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var raw = JsonSerializer.SerializeToNode(team)!.AsObject();
        raw["FutureProfile"] = JsonNode.Parse("{\"keep\":true}");
        raw["Buttons"]![0]!["Label"] = "Current button";
        raw["Buttons"]![0]!["Repeat"] = JsonNode.Parse("{\"intervalMs\":5000,\"variationMs\":250}");
        raw["Buttons"]![0]!["repeat"] = JsonNode.Parse("{\"intervalMs\":6000}");
        raw["Buttons"]![0]!["FutureButton"] = 42;
        raw["Buttons"]!.AsArray().Add(JsonNode.Parse("{\"Id\":\"pause\",\"Label\":\"Pause\",\"Rules\":[],\"stopAllLoops\":true}"));
        raw["Buttons"]!.AsArray().Add(JsonNode.Parse("{\"Id\":\"resume\",\"Label\":\"Resume\",\"Rules\":[],\"resumeAllLoops\":true}"));
        File.WriteAllText(source, raw.ToJsonString()); byte[] original = File.ReadAllBytes(source);
        var solo = new StandaloneButton(1, "solo", "Solo", team.Characters, team.Personas, team.Buttons[0].Rules, []);
        var soloJson = JsonSerializer.SerializeToNode(solo)!.AsObject();
        soloJson["Repeat"] = raw["Buttons"]![0]!["Repeat"]!.DeepClone(); soloJson["FutureButton"] = "preserved";
        string sourceSolo = StandaloneButtons.PathFor(source, "solo"); Directory.CreateDirectory(Path.GetDirectoryName(sourceSolo)!);
        File.WriteAllText(sourceSolo, soloJson.ToJsonString()); byte[] originalSolo = File.ReadAllBytes(sourceSolo);
        var pauseSolo = soloJson.DeepClone().AsObject(); pauseSolo["Id"] = "pause"; pauseSolo["Rules"] = new JsonArray(); pauseSolo["Repeat"] = null; pauseSolo["stopAllLoops"] = true;
        File.WriteAllText(StandaloneButtons.PathFor(source, "pause"), pauseSolo.ToJsonString());
        WindowLayouts.SaveBar(sourceSolo, new(71, 83)); WindowLayouts.SaveBar(source, new(100, 120)); WindowLayouts.SaveBar(source + ".controls", new(200, 240));
        File.WriteAllText(source + ".screen-positions.json", "{\"keep\":true}");
        string prepared = EditionProfiles.Prepare(originalProfile);
        var copied = TeamEditing.Load(prepared); var singles = StandaloneButtons.Load(prepared);
        Check(original.SequenceEqual(File.ReadAllBytes(source)) && originalSolo.SequenceEqual(File.ReadAllBytes(sourceSolo)), "Migration modified the source library.");
        Check(Path.GetDirectoryName(prepared) == Path.Combine(directory, BuildEdition.Id), "Wrong canonical profile directory.");
        Check(copied.Buttons.Length == 1 && copied.Buttons[0].Label == "Current button" && singles.Length == 1, "Migration failed to prefer the current library or retained loop controls.");
        Check(copied.Extra!["FutureProfile"].GetProperty("keep").GetBoolean() && copied.Buttons[0].Extra!["FutureButton"].GetInt32() == 42
            && singles[0].Value.Extra!["FutureButton"].GetString() == "preserved", "Migration lost unknown profile data.");
        Check(!File.ReadAllText(prepared).Contains("repeat", StringComparison.OrdinalIgnoreCase), "Migration retained duplicate-case repeat fields.");
        Check(WindowLayouts.LoadBar(StandaloneButtons.PathFor(prepared, "solo")) == new Point(71, 83)
            && WindowLayouts.LoadBar(prepared) == new Point(100, 120) && WindowLayouts.LoadBar(prepared + ".controls") == new Point(200, 240)
            && File.ReadAllText(prepared + ".screen-positions.json") == "{\"keep\":true}", "Migration lost saved placements.");
        TeamEditing.Save(prepared, copied, []);
        Check(TeamEditing.Load(prepared).Extra!["FutureProfile"].GetProperty("keep").GetBoolean(), "Saving the migrated profile lost unknown data.");
        File.AppendAllText(prepared, " "); byte[] saved = File.ReadAllBytes(prepared);
        foreach (string input in new[] { originalProfile, source, manualProfile, prepared })
            Check(EditionProfiles.Prepare(input) == prepared && saved.SequenceEqual(File.ReadAllBytes(prepared)), "Reopening Team Bar overwrote its library or created a nested edition.");
        Check(TeamEditing.Load(source).Buttons.Length == 1 && StandaloneButtons.Load(source).Length == 1
            && original.SequenceEqual(File.ReadAllBytes(source)), "Direct legacy import did not safely normalize or changed its source.");
        string fallback = Path.Combine(directory, "fallback", "team.json"); TeamEditing.Save(fallback, team, []);
        string fallbackManual = Path.Combine(Path.GetDirectoryName(fallback)!, "manual", "team.json"); TeamEditing.Save(fallbackManual, team with { Buttons = [team.Buttons[0] with { Label = "Manual fallback" }] }, []);
        Check(TeamEditing.Load(EditionProfiles.Prepare(fallback)).Buttons[0].Label == "Manual fallback", "Missing current library did not fall back to the existing manual library.");
        string invalid = Path.Combine(directory, "invalid", "team.json"); Directory.CreateDirectory(Path.GetDirectoryName(invalid)!); File.WriteAllText(invalid, "{}");
        bool rejected = false; try { EditionProfiles.Prepare(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(Path.Combine(Path.GetDirectoryName(invalid)!, BuildEdition.Id)), "An invalid source published a partial canonical library.");
    }
    private static void TestImport(TeamDefinition team)
    {
        foreach (string repeat in new[] { "{}", "false", "null", "{\"intervalMs\":1}" })
        {
            var node = JsonSerializer.SerializeToNode(team.Buttons[0])!.AsObject(); node["RePeAt"] = JsonNode.Parse(repeat);
            var button = node.Deserialize<TeamButton>(Program.Json)!;
            Check(button.Rules.Length == 1 && !JsonSerializer.Serialize(button, Program.Json).Contains("repeat", StringComparison.OrdinalIgnoreCase), "Imported repeats were not removed.");
        }
        foreach (string control in new[] { "stopAllLoops", "pauseAllLoops", "resumeAllLoops" })
        {
            string json = "{\"id\":\"control\",\"label\":\"Control\",\"rules\":[],\"" + control + "\":true}";
            Check(JsonSerializer.Deserialize<TeamButton>(json, Program.Json) is null, "Imported loop control was not omitted.");
        }
        var neutral = JsonSerializer.Deserialize<TeamButton>("{\"id\":\"tap\",\"label\":\"Tap\",\"rules\":[],\"repeat\":null,\"stopAllLoops\":false,\"resumeAllLoops\":false}", Program.Json)!;
        Check(neutral is not null && neutral.Extra?.Count is not > 0, "Neutral legacy fields were retained.");
    }
    private static void TestSettings(string root)
    {
        string looping = Path.Combine(root, "looping"); Directory.CreateDirectory(looping);
        File.WriteAllText(Path.Combine(root, "user-settings.json"), "{\"version\":1,\"theme\":\"dark\"}");
        const string originalSettings = "{\"version\":1,\"theme\":\"light\",\"stopLoopsOnEscape\":true,\"StopLoopsOnEscape\":true,\"futurePreference\":42}";
        string legacySettings = Path.Combine(looping, "user-settings.json"); File.WriteAllText(legacySettings, originalSettings);
        string legacyOverlays = Path.Combine(looping, "spell-overlays.json");
        const string overlays = "{\"version\":1,\"overlays\":[{\"id\":\"debuffs\",\"name\":\"Debuffs\",\"character\":\"Runa\",\"spells\":[{\"spellId\":100,\"name\":\"Test\",\"clickKey\":\"1\"}]}],\"future\":true}";
        File.WriteAllText(legacyOverlays, overlays);
        string contributionDirectory = Path.Combine(looping, "Contributions"); Directory.CreateDirectory(Path.Combine(contributionDirectory, "queues"));
        File.WriteAllBytes(Path.Combine(contributionDirectory, "credential.bin"), [0, 1, 2, 3, 255]);
        File.WriteAllText(Path.Combine(contributionDirectory, "queues", "pending.json"), "{\"fixture\":true}");
        EditionProfiles.PrepareSettings(root);
        string canonical = Path.Combine(root, BuildEdition.Id);
        string settingsPath = Path.Combine(canonical, "user-settings.json"); var store = new TeamBar.LocalUserSettingsStore(settingsPath);
        Check(store.Load().Theme == TeamBar.AppearanceTheme.Light && store.Load().Extra!["futurePreference"].GetInt32() == 42, "Personal preference migration did not preserve the current settings.");
        Check(!File.ReadAllText(settingsPath).Contains("stopLoopsOnEscape", StringComparison.OrdinalIgnoreCase), "Personal preference migration retained loop fields.");
        Check(File.ReadAllText(Path.Combine(canonical, "spell-overlays.json")) == overlays, "Spell overlays, click keys, or future fields were lost.");
        Check(File.ReadAllBytes(Path.Combine(canonical, "Contributions", "credential.bin")).SequenceEqual(new byte[] { 0, 1, 2, 3, 255 })
            && File.ReadAllText(Path.Combine(canonical, "Contributions", "queues", "pending.json")) == "{\"fixture\":true}", "Contribution settings or queued data were lost.");
        store.Save(new() { Theme = TeamBar.AppearanceTheme.Dark });
        File.WriteAllText(Path.Combine(canonical, "spell-overlays.json"), "{\"version\":1,\"overlays\":[]}");
        File.WriteAllText(Path.Combine(canonical, "Contributions", "queues", "pending.json"), "current");
        EditionProfiles.PrepareSettings(root);
        Check(store.Load().Theme == TeamBar.AppearanceTheme.Dark && File.ReadAllText(legacySettings) == originalSettings
            && File.ReadAllText(legacyOverlays) == overlays, "Reopening overwrote current preferences or originals.");
        Check(File.ReadAllText(Path.Combine(canonical, "spell-overlays.json")) == "{\"version\":1,\"overlays\":[]}"
            && File.ReadAllText(Path.Combine(canonical, "Contributions", "queues", "pending.json")) == "current", "Reopening overwrote current auxiliary data.");
    }
}
