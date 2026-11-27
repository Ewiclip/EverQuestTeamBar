using System.Text.Json;
using System.Text.Json.Nodes;

internal static class EditionProfiles
{
    // Publish one canonical library. The former Looping library is the user's
    // current configuration; keep all original libraries intact for recovery.
    internal static string Prepare(string source)
    {
        source = Path.GetFullPath(source);
        string parent = Path.GetDirectoryName(source)!;
        if (new[] { "looping", "manual", BuildEdition.Id }.Contains(Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase))
            parent = Path.GetDirectoryName(parent)!;
        string destination = Path.Combine(parent, BuildEdition.Id);
        string profile = Path.Combine(destination, "team.json");
        using var guard = new FileStream(Path.Combine(parent, ".team-bar-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(destination))
        {
            if (!File.Exists(profile)) throw new IOException("The Team Bar folder exists without team.json. Its files were preserved; restore the profile before opening Team Bar.");
            return profile;
        }
        source = new[] { Path.Combine(parent, "looping", "team.json"), Path.Combine(parent, "manual", "team.json"), source }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No saved Team Bar profile was found.", source);
        string staging = destination + ".initializing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var changes = new List<string>();
        var team = ReadObject(source);
        OneTapProfileConverter.Normalize(team, typeof(TeamDefinition), changes);
        string stagedProfile = Path.Combine(staging, "team.json");
        File.WriteAllText(stagedProfile, team.ToJsonString(Pretty));
        Routing.Validate(TeamEditing.Load(stagedProfile), [], requireClients: false);
        CopySidecars(source, stagedProfile);
        CopySidecars(source + ".controls", stagedProfile + ".controls");
        string sourceButtons = StandaloneButtons.DirectoryFor(source);
        if (Directory.Exists(sourceButtons))
        {
            foreach (string file in Directory.GetFiles(sourceButtons, "*.json"))
            {
                if (IsSidecar(file)) continue;
                var button = ReadObject(file);
                if (!OneTapProfileConverter.Normalize(button, typeof(StandaloneButton), changes)) continue;
                string target = Path.Combine(staging, "buttons", Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, button.ToJsonString(Pretty));
                CopySidecars(file, target);
            }
            StandaloneButtons.Load(stagedProfile);
        }
        File.WriteAllText(Path.Combine(staging, "profile-migration.json"), JsonSerializer.Serialize(new {
            product = BuildEdition.Label, source, changes, copiedOnce = true, originalPreserved = true
        }, Pretty));
        PublishDirectory(staging, destination);
        return profile;
    }
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Empty JSON file: " + Path.GetFileName(path));
    private static bool IsSidecar(string file) => new[] { ".bar-position.json", ".windows.json", ".screen-positions.json" }
        .Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    private static void CopySidecars(string source, string target)
    {
        foreach (string suffix in new[] { ".bar-position.json", ".windows.json", ".screen-positions.json" })
            if (File.Exists(source + suffix)) File.Copy(source + suffix, target + suffix);
    }
    private static void PublishDirectory(string staging, string destination)
    {
        // Windows scanners can briefly hold files without delete sharing.
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(staging, destination); return; }
            catch (IOException) when (attempt < 10 && !Directory.Exists(destination)) { Thread.Sleep(100); }
        }
    }
    internal static void PrepareSettings(string settingsRoot)
    {
        // Legacy fixture/conversion entry point requires an explicit scope. Never
        // infer or search the user's per-user data during application startup.
        string root = Path.GetFullPath(settingsRoot);
        string destination = Path.Combine(root, BuildEdition.Id);
        string[] sources = [Path.Combine(root, "looping"), Path.Combine(root, "manual"), root];
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".team-bar-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string path = Path.Combine(destination, "user-settings.json");
        if (!File.Exists(path) && sources.Select(folder => Path.Combine(folder, "user-settings.json")).FirstOrDefault(File.Exists) is { } legacy)
        {
            new TeamBar.LocalUserSettingsStore(legacy).Load(); // Validate before copying; do not overwrite unsupported data.
            var settings = ReadObject(legacy);
            foreach (string name in new[] { "stopLoopsOnEscape", "pauseLoopsOnEscape" })
                foreach (string key in OneTapProfileConverter.Keys(settings, name)) settings.Remove(key);
            WriteNewFile(path, System.Text.Encoding.UTF8.GetBytes(settings.ToJsonString(Pretty)));
        }
        string overlays = Path.Combine(destination, "spell-overlays.json");
        if (!File.Exists(overlays) && sources.Select(folder => Path.Combine(folder, "spell-overlays.json")).FirstOrDefault(File.Exists) is { } legacyOverlays)
        {
            TeamBar.SpellOverlayStore.Load(legacyOverlays); // Preserve original JSON, including future fields and click keys.
            WriteNewFile(overlays, File.ReadAllBytes(legacyOverlays));
        }
        string contributions = Path.Combine(destination, "Contributions");
        if (!Directory.Exists(contributions) && sources.Select(folder => Path.Combine(folder, "Contributions")).FirstOrDefault(Directory.Exists) is { } legacyContributions)
        {
            Directory.CreateDirectory(destination);
            string staging = contributions + ".initializing-" + Guid.NewGuid().ToString("N");
            CopyDirectory(legacyContributions, staging);
            PublishDirectory(staging, contributions);
        }
    }
    private static void WriteNewFile(string destination, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void CopyDirectory(string source, string destination)
    {
        // Keep encrypted credentials and pending contribution queues byte for byte.
        // Never follow links outside the personal data directory.
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("A linked contribution directory cannot be migrated automatically.");
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("A linked contribution file cannot be migrated automatically.");
            if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
