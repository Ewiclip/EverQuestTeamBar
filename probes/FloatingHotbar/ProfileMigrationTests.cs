using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamBar;

internal static class ProfileMigrationTests
{
    private static void Check(bool condition, string message) => StandaloneTests.Check(condition, message);
    private static void Refused(Action action, string message)
    {
        bool refused = false;
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException) { refused = true; }
        Check(refused, message);
    }

    internal static void Run(string output)
    {
        string root = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(root)) throw new IOException("Profile migration test output must be new.");
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "installed"), profiles = Path.Combine(data, "profiles"), backups = Path.Combine(root, "backups");
        string fresh = InstalledAppData.EnsureDefaultProfile(profiles);
        var empty = TeamEditing.Load(fresh);
        Check(empty.Characters.Length == 0 && empty.Personas.Length == 0 && empty.Buttons.Length == 0 && StandaloneButtons.Load(fresh).Length == 0,
            "A new install must create a valid empty library without developer characters or sample buttons.");
        Routing.Validate(empty, [], requireClients: false);
        Refused(() => Routing.Validate(empty, []), "An empty library must not become an active input session.");
        byte[] initial = File.ReadAllBytes(fresh);
        Check(InstalledAppData.EnsureDefaultProfile(profiles) == fresh && initial.SequenceEqual(File.ReadAllBytes(fresh)), "Opening again must preserve the exact saved profile.");

        string incomplete = Path.Combine(root, "incomplete"); Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "preserve.txt"), "original");
        Refused(() => InstalledAppData.EnsureDefaultProfile(incomplete), "An incomplete existing profile folder must not be overwritten.");
        Check(File.ReadAllText(Path.Combine(incomplete, "preserve.txt")) == "original", "Partial data must remain available for recovery.");
        string interrupted = Path.Combine(root, "interrupted", "profiles"); Directory.CreateDirectory(interrupted + ".previous-fixture");
        Refused(() => InstalledAppData.EnsureDefaultProfile(interrupted), "An interrupted publish must not silently create a new empty library.");

        string source = Path.Combine(root, "developer-source"); WriteLibrary(source);
        string sourceProfile = Path.Combine(source, "team.json"), sourceButton = Path.Combine(source, "buttons", "assist.json");
        File.WriteAllText(Path.Combine(source, "team.json.active-buttons.json"), "{}");
        byte[] originalTeam = File.ReadAllBytes(sourceProfile), originalButton = File.ReadAllBytes(sourceButton);
        var imported = ProfileMigration.ImportLibrary(source, profiles, backups, ProfileMigration.Fingerprint(profiles));
        Check(!File.Exists(Path.Combine(profiles, "team.json.active-buttons.json")) && StandaloneButtons.Load(imported.ProfilePath).Length > 0,
            "Import ignores a legacy active-button selection and retains the full library.");
        Check(originalTeam.SequenceEqual(File.ReadAllBytes(sourceProfile)) && originalButton.SequenceEqual(File.ReadAllBytes(sourceButton)), "Import must preserve every source file byte for byte.");
        Check(originalTeam.SequenceEqual(File.ReadAllBytes(Path.Combine(imported.BackupDirectory, "source", "team.json")))
            && initial.SequenceEqual(File.ReadAllBytes(Path.Combine(imported.BackupDirectory, "previous", "team.json"))), "Import must back up source bytes and the current library before replacement.");
        Check(TeamEditing.Load(imported.ProfilePath).Extra?.ContainsKey("futureSetting") == true, "Import must preserve unrelated future profile settings.");
        Check(WindowLayouts.LoadBar(imported.ProfilePath) == new System.Drawing.Point(120, 180)
            && WindowLayouts.LoadBar(Path.Combine(profiles, "buttons", "assist.json")) == new System.Drawing.Point(150, 210), "Saved placements must accompany the library.");
        Check(!File.ReadAllText(Path.Combine(profiles, "buttons", "assist.json")).Contains("repeat", StringComparison.OrdinalIgnoreCase), "Legacy repeat settings must not survive one-tap conversion.");
        Check(!Directory.Exists(Path.Combine(profiles, "sessions")), "Runtime logs must not be imported.");
        Check(InstalledAppData.EnsureDefaultProfile(profiles) == fresh, "Installed startup must open the saved library directly.");

        string revision = ProfileMigration.Fingerprint(profiles);
        File.AppendAllText(fresh, " "); byte[] laterEdit = File.ReadAllBytes(fresh);
        Refused(() => ProfileMigration.ImportLibrary(source, profiles, backups, revision), "A stale confirmation must not overwrite later edits.");
        Check(laterEdit.SequenceEqual(File.ReadAllBytes(fresh)), "Rejected import changed the current library.");
        RejectInvalidSources(root, profiles, backups);
        TestRetainedPlacements(root, backups);
        TestSpellOverlayImport(root, backups);

        string preferences = Path.Combine(data, "user-settings.json");
        File.WriteAllText(preferences, "{\"version\":1,\"theme\":\"dark\"}");
        string selectedPreferences = Path.Combine(root, "selected-preferences.json");
        File.WriteAllText(selectedPreferences, "{\"version\":1,\"theme\":\"light\",\"futureSetting\":{\"keep\":true}}");
        byte[] oldPreferences = File.ReadAllBytes(preferences);
        string preferenceBackup = ProfileMigration.ImportPreferences(selectedPreferences, preferences, backups, oldPreferences);
        Check(new LocalUserSettingsStore(preferences).Load().Theme == AppearanceTheme.Light
            && oldPreferences.SequenceEqual(File.ReadAllBytes(Path.Combine(preferenceBackup, "user-settings.json"))), "Preference import must validate, back up and replace atomically.");
        File.AppendAllText(preferences, " "); byte[] newPreferences = File.ReadAllBytes(preferences);
        Refused(() => ProfileMigration.ImportPreferences(selectedPreferences, preferences, backups, oldPreferences), "Stale preference import must refuse a later edit.");
        Check(newPreferences.SequenceEqual(File.ReadAllBytes(preferences)), "Rejected preference import modified a later edit.");
        File.WriteAllText(selectedPreferences, """{"version":2,"enableScreenDetection":false,"enableSpellBarData":true,"enableDataCollection":false}""");
        ProfileMigration.ImportPreferences(selectedPreferences, preferences, backups, newPreferences);
        var importedSettings = new LocalUserSettingsStore(preferences).Load();
        Check(!importedSettings.EnableScreenDetection && importedSettings.EnableSpellBarData && !importedSettings.EnableDataCollection,
            "Importing version 2 preferences keeps spell bar data enabled independently of detection and collection.");
        File.WriteAllText(selectedPreferences, "{}");
        Refused(() => ProfileMigration.ValidatePreferences(selectedPreferences, "user-settings.json"), "Partial preference JSON must be rejected.");
        File.WriteAllText(selectedPreferences, "{\"version\":1,\"apiKey\":\"fixture-secret\"}");
        Refused(() => ProfileMigration.ValidatePreferences(selectedPreferences, "user-settings.json"), "Credentials must not be imported with preferences.");

        string contributions = Path.Combine(data, "Contributions"); Directory.CreateDirectory(contributions);
        File.WriteAllText(Path.Combine(contributions, "credentials.bin"), "fixture-only-credential");
        string updateBackup = InstalledAppData.CreatePreUpdateBackup(data, backups, "1.2.3");
        Check(File.Exists(Path.Combine(updateBackup, "profiles", "team.json")) && File.Exists(Path.Combine(updateBackup, "user-settings.json"))
            && !Directory.Exists(Path.Combine(updateBackup, "Contributions")), "Update backups must preserve profiles/preferences while leaving credentials in their existing scope.");
        Check(File.ReadAllText(Path.Combine(contributions, "credentials.bin")) == "fixture-only-credential", "Backup must not modify contribution credentials.");
        string afterBackup = ProfileMigration.Fingerprint(profiles);
        Check(InstalledAppData.EnsureDefaultProfile(profiles) == fresh && afterBackup == ProfileMigration.Fingerprint(profiles), "Opening after a backup must not restore or overwrite current edits.");

        // Deletion deliberately leaves group placeholders and old placements in
        // the app's current schema. Existing-data checks must preserve that state.
        string existingData = Path.Combine(root, "existing-data"), existing = Path.Combine(existingData, "profiles"); WriteLibrary(existing);
        string removedButton = Path.Combine(existing, "buttons", "assist.json");
        StandaloneButtons.Remove(removedButton, File.ReadAllBytes(removedButton));
        File.WriteAllText(Path.Combine(existing, "private-notes.txt"), "fixture-only unrelated data");
        var filesBeforeRead = Directory.GetFiles(existing, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        ProfileMigration.ValidateLibrary(existing, requireComplete: false);
        Check(filesBeforeRead.Count == Directory.GetFiles(existing, "*", SearchOption.AllDirectories).Length
            && filesBeforeRead.All(file => file.Value.SequenceEqual(File.ReadAllBytes(file.Key))), "Healthcheck validation must not write, migrate or delete existing user data.");
        Refused(() => ProfileMigration.ValidateLibrary(existing), "Explicit imports must still reject incomplete sources and unknown files.");
        Check(InstalledAppData.EnsureDefaultProfile(existing) == Path.Combine(existing, "team.json"), "A deliberately removed button must not break installed startup.");
        string existingBackup = InstalledAppData.CreatePreUpdateBackup(existingData, backups, "1.2.4");
        Check(File.Exists(Path.Combine(existingBackup, "profiles", "team.json"))
            && File.Exists(Path.Combine(existingBackup, "profiles", "buttons", "assist.json.bar-position.json"))
            && !File.Exists(Path.Combine(existingBackup, "profiles", "private-notes.txt"))
            && File.ReadAllText(Path.Combine(existing, "private-notes.txt")) == "fixture-only unrelated data",
            "Existing-schema backups must retain placeholders and placements without exporting unrelated files.");

        bool checkedLinks = CheckReparsePoints(root, profiles, backups);
        Check(LocalUserSettingsStore.DefaultPath.StartsWith(InstalledAppData.DataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && ContributionStorage.DefaultDirectory.StartsWith(InstalledAppData.DataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && SpellOverlayStore.DefaultPath.StartsWith(InstalledAppData.DataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "All installed state must share the same compiled per-user scope.");
        Check(InstalledAppData.ProductDirectoryName == "TeamBar", "All installations share the Team Bar data scope.");
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, reparsePointsChecked = checkedLinks, liveUserDataAccessed = false,
            tests = "empty install; no automatic restore; source/prior backups; one-tap conversion; retained deleted-button placements; malformed, incomplete, credential and unknown files; stale edits; explicit preference and spell-overlay imports; live-overlay lock exclusion; credential-free pre-update backup; isolated storage scope" }));
    }

    private static void TestRetainedPlacements(string root, string backups)
    {
        string source = Path.Combine(root, "retained-placements"); WriteLibrary(source);
        string profile = Path.Combine(source, "team.json");
        var active = StandaloneButtons.Load(profile).Single().Value;
        string retiredPath = StandaloneButtons.PathFor(profile, "retired");
        byte[] retired = StandaloneButtons.Save(retiredPath, active with { Id = "retired" }, null);
        WindowLayouts.SaveBar(retiredPath, new(321, 234));
        StandaloneButtons.Remove(retiredPath, retired);
        byte[] position = File.ReadAllBytes(retiredPath + ".bar-position.json");
        Check(Directory.GetFiles(Path.Combine(source, "buttons", "removed")).Length == 1, "The fixture must represent a deliberately removed button with retained placement.");
        ProfileMigration.ValidateLibrary(source);
        string destination = Path.Combine(root, "retained-import", "profiles");
        var imported = ProfileMigration.ImportLibrary(source, destination, backups, "missing");
        Check(StandaloneButtons.Load(imported.ProfilePath).Select(button => button.Value.Id).SequenceEqual(["assist"])
            && !File.Exists(Path.Combine(destination, "buttons", "retired.json"))
            && position.SequenceEqual(File.ReadAllBytes(Path.Combine(destination, "buttons", "retired.json.bar-position.json"))),
            "Import must preserve orphan placement bytes without reviving a removed action.");
        Check(!Directory.Exists(Path.Combine(destination, "buttons", "removed")), "Import must not need or export removed-button archives.");
        ProfileMigration.ValidateLibrary(destination);
        string roundTrip = Path.Combine(root, "retained-round-trip", "profiles");
        ProfileMigration.ImportLibrary(destination, roundTrip, backups, "missing");
        Check(position.SequenceEqual(File.ReadAllBytes(Path.Combine(roundTrip, "buttons", "retired.json.bar-position.json"))),
            "A retained placement must remain importable after its original deletion history is excluded from a backup.");
    }

    private static void TestSpellOverlayImport(string root, string backups)
    {
        string source = Path.Combine(root, "selected-spell-overlays.json"), destination = Path.Combine(root, "overlay-import", "spell-overlays.json");
        var original = new SpellOverlayDefinition("original", "Original fixture", "Fixture", [], PersonaClass: "Warrior");
        byte[] originalBytes = SpellOverlayStore.Save(destination, new(3, [original]), null);
        var selected = new SpellOverlayDefinition("selected", "Selected fixture", "Fixture",
            [new(1, "Fixture spell", ClickKey: "Alt+1", Alternatives: [new(2, "Fixture alternative")])],
            PersonaClass: "Warrior", DockGroups: [new("dock", "Fixture dock", SpellDockSide.Left, ["assist"])],
            FollowCharacterWindow: false, FreePosition: new(150, 250));
        byte[] sourceBytes = SpellOverlayStore.Save(source, new(3, [selected]), null);
        string preferences = Path.Combine(Path.GetDirectoryName(destination)!, "user-settings.json");
        File.WriteAllText(preferences, "{\"version\":1,\"spellOverlaysLoaded\":false,\"allowGameMemoryReading\":false}");
        byte[] preferencesBefore = File.ReadAllBytes(preferences);
        using (var liveManager = new FileStream(destination + ".runtime.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Refused(() => ProfileMigration.ImportSpellOverlays(source, destination, backups), "A live overlay manager must block replacement of its saved library.");
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(destination)), "A rejected live-manager import changed saved overlays.");
        }
        string backup = ProfileMigration.ImportSpellOverlays(source, destination, backups);
        var imported = SpellOverlayStore.Load(destination).Library.Overlays.Single();
        Check(sourceBytes.SequenceEqual(File.ReadAllBytes(source)) && sourceBytes.SequenceEqual(File.ReadAllBytes(destination))
            && originalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(backup, "spell-overlays.json"))),
            "Overlay import must preserve the original source and back up the previous library before atomic replacement.");
        Check(imported.Spells.Single().ClickKey == "Alt+1" && imported.Spells.Single().Alternatives?.Single().SpellId == 2
            && imported.DockGroups?.Single().ButtonIds.Single() == "assist" && imported.FreePosition == new OverlayDesktopPosition(150, 250),
            "Overlay import must retain click mappings, alternatives, docked buttons and free placements.");
        Check(preferencesBefore.SequenceEqual(File.ReadAllBytes(preferences)), "Importing overlays must not opt into memory reading or change the loaded-state preference.");
        File.WriteAllText(source, "{\"version\":3,\"overlays\":[null]}");
        Refused(() => ProfileMigration.ImportSpellOverlays(source, destination, backups), "Incomplete overlay definitions must be rejected before replacement.");
        Check(sourceBytes.SequenceEqual(File.ReadAllBytes(destination)), "A malformed overlay source changed the saved library.");
    }

    private static void WriteLibrary(string folder)
    {
        var team = new TeamDefinition(1, [new("fixture", "Fixture")], [new("fixture-warrior", "fixture", "Warrior")], [],
            Groups: [new("group", "Fixture group", [new("assist", 0)])]);
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.SerializeToNode(team)!.AsObject(); json["futureSetting"] = new JsonObject { ["keep"] = true };
        File.WriteAllText(Path.Combine(folder, "team.json"), json.ToJsonString());
        var button = new StandaloneButton(1, "assist", "Assist", team.Characters, team.Personas, [new(new(CharacterId: "fixture"), "1")], []);
        string file = Path.Combine(folder, "buttons", "assist.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var buttonJson = JsonSerializer.SerializeToNode(button)!.AsObject(); buttonJson["repeat"] = new JsonObject { ["intervalMs"] = 1000 };
        File.WriteAllText(file, buttonJson.ToJsonString());
        WindowLayouts.SaveBar(Path.Combine(folder, "team.json"), new(120, 180)); WindowLayouts.SaveBar(file, new(150, 210));
        File.WriteAllBytes(file + ".lock", []);
        Directory.CreateDirectory(Path.Combine(folder, "sessions")); File.WriteAllText(Path.Combine(folder, "sessions", "fixture.jsonl"), "{}\n");
    }

    private static void RejectInvalidSources(string root, string profiles, string backups)
    {
        var mutations = new Action<string>[]
        {
            path => File.WriteAllText(Path.Combine(path, "secrets.json"), "{}"),
            path => File.WriteAllText(Path.Combine(path, "team.json.pending.tmp"), "incomplete"),
            path => File.WriteAllText(Path.Combine(path, "buttons", "assist.json"), "{broken"),
            path => File.Delete(Path.Combine(path, "buttons", "assist.json")),
            path => File.WriteAllText(Path.Combine(path, "team.json"), "{\"version\":1,\"characters\":null}"),
            path => File.WriteAllText(Path.Combine(path, "team.json"), "{\"version\":1,\"Version\":1}"),
            path => File.WriteAllText(Path.Combine(path, "team.json"), "{\"version\":1,\"apiKey\":\"fixture\"}"),
            path => Directory.CreateDirectory(Path.Combine(path, "Contributions")),
            path => { var node = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "buttons", "assist.json")))!.AsObject(); node["Id"] = "../outside"; File.WriteAllText(Path.Combine(path, "buttons", "assist.json"), node.ToJsonString()); }
        };
        for (int i = 0; i < mutations.Length; i++)
        {
            string source = Path.Combine(root, "invalid-" + i); WriteLibrary(source); mutations[i](source);
            string before = ProfileMigration.Fingerprint(profiles);
            Refused(() => ProfileMigration.ImportLibrary(source, profiles, backups, before), "Invalid source " + i + " must be rejected before publication.");
            Check(before == ProfileMigration.Fingerprint(profiles), "Invalid source " + i + " changed the current library.");
        }
    }

    private static bool CheckReparsePoints(string root, string profiles, string backups)
    {
        string target = Path.Combine(root, "link-target"); WriteLibrary(target);
        string link = Path.Combine(root, "linked-source");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return false; }
        Refused(() => ProfileMigration.ImportLibrary(link, profiles, backups, ProfileMigration.Fingerprint(profiles)), "A linked source directory must be rejected.");
        Refused(() => InstalledAppData.EnsureDefaultProfile(Path.Combine(link, "child")), "A linked ancestor must be rejected for destination data.");
        return true;
    }
}
