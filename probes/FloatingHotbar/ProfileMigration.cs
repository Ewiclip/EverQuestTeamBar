using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record ProfileImportResult(string ProfilePath, string BackupDirectory);

internal static class ProfileMigration
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const long MaxLibraryBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly string[] Sidecars = [".bar-position.json", ".screen-positions.json", ".windows.json"];

    internal static string SafeFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Choose a saved profiles folder.");
        string full = Path.GetFullPath(path);
        for (string? item = full; item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked files or folders cannot be imported or used for saved Team Bar data.");
        return full;
    }

    internal static void EnsureSafeDirectory(string path) { SafeFullPath(path); Directory.CreateDirectory(path); SafeFullPath(path); }

    private static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(part => part is "" or "." or ".." || part.Contains(':')))
            throw new InvalidDataException("Invalid profile file path.");
        string path = SafeFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A profile file is outside the selected folder.");
        return path;
    }

    // Enumerate without following links. Known transient logs, locks and historical
    // backups stay in the untouched source; arbitrary files and folders are refused.
    private static string[] Inventory(string directory, bool rejectUnknown = true)
    {
        string root = SafeFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected profiles folder does not exist.");
        var files = new List<string>(); long total = 0; int visited = 0;
        void Walk(string folder, string relative)
        {
            foreach (string item in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++visited > 10000) throw new InvalidDataException("The selected folder contains too many files.");
                SafeFullPath(item);
                string name = Path.GetFileName(item), path = relative.Length == 0 ? name : relative + "/" + name;
                bool directoryEntry = Directory.Exists(item);
                if (directoryEntry)
                {
                    if (path is not ("buttons" or "buttons/removed" or "sessions" or "library-backups")
                        && !path.StartsWith("library-backups/", StringComparison.Ordinal))
                    {
                        if (rejectUnknown) throw new InvalidDataException("Unrecognized profile folder: " + path + ". Credentials and unrelated files cannot be imported.");
                        continue; // Existing unrelated files stay in their original scope.
                    }
                    Walk(item, path); continue;
                }
                if (path.StartsWith("sessions/", StringComparison.Ordinal) || path.StartsWith("library-backups/", StringComparison.Ordinal)
                    || path.StartsWith("buttons/removed/", StringComparison.Ordinal))
                {
                    if (rejectUnknown && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unrecognized history file: " + path);
                    continue;
                }
                if (IsKnownLock(path))
                {
                    if (new FileInfo(item).Length != 0) throw new InvalidDataException("A profile lock file contains unexpected data: " + path);
                    continue;
                }
                // Older builds saved a display subset here. All saved buttons now belong to the library.
                if (path == "team.json.active-buttons.json") continue;
                if (path == "profile-migration.json") { ReadJson(item); continue; }
                if (!IsDataFile(path))
                {
                    if (rejectUnknown) throw new InvalidDataException("Unrecognized or incomplete profile file: " + path + ". Credentials and unrelated files cannot be imported.");
                    continue;
                }
                long length = new FileInfo(item).Length;
                if (length <= 0 || length > MaxFileBytes || (total += length) > MaxLibraryBytes) throw new InvalidDataException("The profiles folder is empty, incomplete or too large.");
                files.Add(path);
            }
        }
        Walk(root, "");
        if (!files.Contains("team.json", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Choose the folder that directly contains team.json and its buttons folder.");
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsKnownLock(string path)
    {
        if (path is "team.json.lock" or "team.json.apply.lock" or "team.json.library-migration.lock" or "team.json.active-buttons.json.lock" or "team.json.game-views.json.lock" or "team.json.swap-button-positions.json.lock" or "buttons/.library-edit.lock") return true;
        return path.StartsWith("buttons/", StringComparison.Ordinal) && path.EndsWith(".json.lock", StringComparison.Ordinal)
            && IsButtonName(path[8..^10]);
    }
    private static bool IsButtonName(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static bool IsDataFile(string relative)
    {
        if (relative is "team.json" or "team.json.assignments.json" or "team.json.game-views.json" or "team.json.swap-button-positions.json") return true;
        if (relative.StartsWith("buttons/", StringComparison.Ordinal))
        {
            string name = relative[8..];
            if (name.EndsWith(".json", StringComparison.Ordinal) && IsButtonName(name[..^5])) return true;
            return Sidecars.Any(s => name.EndsWith(".json" + s, StringComparison.Ordinal) && IsButtonName(name[..^(5 + s.Length)]));
        }
        foreach (string suffix in Sidecars)
        {
            if (relative is var name && (name == "team.json" + suffix || name == "team.json.controls" + suffix)) return true;
            if (relative.StartsWith("team.json.group-", StringComparison.Ordinal) && relative.EndsWith(suffix, StringComparison.Ordinal)
                && IsButtonName(relative[16..^suffix.Length])) return true;
        }
        return false;
    }

    private static JsonObject ReadJson(string file, long maxBytes = MaxFileBytes)
    {
        SafeFullPath(file);
        if (new FileInfo(file).Length <= 0 || new FileInfo(file).Length > maxBytes) throw new InvalidDataException("Invalid profile file size: " + Path.GetFileName(file));
        byte[] bytes = File.ReadAllBytes(file);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        void Inspect(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate JSON fields are not supported.");
                    string key = Regex.Replace(property.Name, "[^a-zA-Z]", "").ToLowerInvariant();
                    if (key.Contains("password", StringComparison.Ordinal) || key.Contains("secret", StringComparison.Ordinal)
                        || key.Contains("credential", StringComparison.Ordinal) || key is "token" or "accesstoken" or "refreshtoken" or "apikey" or "authorization" or "privatekey" or "cookie")
                        throw new InvalidDataException("Credentials cannot be copied with a profile or preference import.");
                    Inspect(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Inspect(item);
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object: " + Path.GetFileName(file));
        Inspect(document.RootElement);
        return (JsonObject)JsonNode.Parse(bytes)!;
    }

    internal static void ValidateLibrary(string directory, bool requireComplete = true)
    {
        string[] files = Inventory(directory, rejectUnknown: requireComplete);
        foreach (string relative in files) ReadJson(Child(directory, relative));
        string profile = Path.Combine(directory, "team.json");
        try
        {
            var team = TeamEditing.Load(profile); Routing.Validate(team, [], requireClients: false);
            var buttons = StandaloneButtons.Load(profile);
            var characters = team.Characters.ToDictionary(character => character.Id, StringComparer.OrdinalIgnoreCase);
            var personas = team.Personas.ToDictionary(persona => persona.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var button in buttons)
            {
                foreach (var character in button.Value.Characters)
                {
                    if (characters.TryGetValue(character.Id, out var saved) && saved != character) throw new InvalidDataException("Saved buttons have conflicting character identities.");
                    characters[character.Id] = character;
                }
                foreach (var persona in button.Value.Personas)
                {
                    if (personas.TryGetValue(persona.Id, out var saved) && saved != persona) throw new InvalidDataException("Saved buttons have conflicting persona identities.");
                    personas[persona.Id] = persona;
                }
            }
            Routing.Validate(team with { Characters = characters.Values.ToArray(), Personas = personas.Values.ToArray() }, [], requireClients: false);
            var ids = buttons.Select(button => button.Value.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (requireComplete && (team.Groups ?? []).SelectMany(group => group.Buttons).Any(button => !ids.Contains(button.ButtonId)))
                throw new InvalidDataException("A group refers to a missing button. Import the complete profiles folder.");
            foreach (string relative in files)
            {
                string file = Child(directory, relative);
                if (relative.EndsWith(".bar-position.json", StringComparison.Ordinal)) WindowLayouts.LoadBar(file[..^18]);
                else if (relative.EndsWith(".screen-positions.json", StringComparison.Ordinal)) AnchorPositions.Load(file[..^22]);
                else if (relative.EndsWith(".windows.json", StringComparison.Ordinal))
                    WindowLayouts.Validate(JsonSerializer.Deserialize<GameLayout>(File.ReadAllBytes(file), Program.Json) ?? throw new InvalidDataException("Empty window layout."));
                else if (relative == "team.json.assignments.json") ManualAssignments.Load(profile, client => client);
                else if (relative == "team.json.game-views.json") GameViewLayouts.Load(profile);
                else if (relative == "team.json.swap-button-positions.json") new GameViewSwapButtonPositions(profile).Validate();
                // Deleting a button intentionally retains its saved placements.
                // Valid geometry is inert without the button document, and must
                // survive imports/backups without reviving archived actions.
                // Do not require a removal archive: histories are not exported.
            }
        }
        catch (Exception error) when (error is NullReferenceException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("The profile is incomplete or malformed. The original files were preserved.", error); }
    }

    internal static string Fingerprint(string directory, bool rejectUnknown = true)
    {
        if (!Directory.Exists(directory)) return "missing";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string relative in Inventory(directory, rejectUnknown))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant() + "\0"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Child(directory, relative))));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void CopyValidatedLibrary(string source, string destination, bool normalize, bool requireComplete = true)
    {
        ValidateLibrary(source, requireComplete); string before = Fingerprint(source, rejectUnknown: requireComplete);
        EnsureSafeDirectory(destination);
        foreach (string relative in Inventory(source, rejectUnknown: requireComplete))
        {
            string target = Child(destination, relative); EnsureSafeDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Child(source, relative), target);
        }
        if (Fingerprint(source, rejectUnknown: requireComplete) != before) throw new IOException("The source profile changed during import. Close its editor and try again.");
        if (normalize)
        {
            string profile = Path.Combine(destination, "team.json");
            var changes = new List<string>();
            foreach (string file in Inventory(destination).Where(file => file == "team.json" || file.StartsWith("buttons/", StringComparison.Ordinal) && !Sidecars.Any(s => file.EndsWith(s, StringComparison.Ordinal))))
            {
                string path = Child(destination, file); var json = ReadJson(path);
                if (!OneTapProfileConverter.Normalize(json, file == "team.json" ? typeof(TeamDefinition) : typeof(StandaloneButton), changes))
                { File.Delete(path); foreach (string suffix in Sidecars) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
                else File.WriteAllText(path, json.ToJsonString(Pretty));
            }
            ButtonGroups.Migrate(profile);
        }
        ValidateLibrary(destination, requireComplete);
    }

    internal static ProfileImportResult ImportLibrary(string sourceDirectory, string profilesDirectory, string backupsDirectory, string expectedFingerprint)
    {
        string source = SafeFullPath(sourceDirectory), destination = SafeFullPath(profilesDirectory), backups = SafeFullPath(backupsDirectory);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a separate source profiles folder.");
        string parent = Path.GetDirectoryName(destination)!; EnsureSafeDirectory(parent); EnsureSafeDirectory(backups);
        using var guard = new FileStream(SafeFullPath(Path.Combine(parent, ".profiles-migration.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        string staging = destination + ".importing-" + id, backup = Path.Combine(backups, "import-" + id), rollback = destination + ".previous-" + id;
        try
        {
            string sourceFingerprint = Fingerprint(source);
            CopyValidatedLibrary(source, staging, normalize: true);
            // Keep a validated exact copy before normalization and a copy of the
            // library being replaced. A failed import never alters its source.
            CopyValidatedLibrary(source, Path.Combine(backup, "source"), normalize: false);
            if (Fingerprint(source) != sourceFingerprint || Fingerprint(Path.Combine(backup, "source")) != sourceFingerprint)
                throw new IOException("The selected source changed during import. Nothing was replaced.");
            if (Fingerprint(destination, rejectUnknown: false) != expectedFingerprint) throw new IOException("Your current library changed. Reopen Settings before importing it again.");
            if (Directory.Exists(destination)) CopyValidatedLibrary(destination, Path.Combine(backup, "previous"), normalize: false, requireComplete: false);
            if (Fingerprint(destination, rejectUnknown: false) != expectedFingerprint) throw new IOException("Your current library changed while its backup was made. Nothing was replaced.");
            File.WriteAllText(Path.Combine(backup, "RESTORE.txt"), "To restore, choose Import existing profiles in Settings and select the previous folder. Review the replacement confirmation. Later edits are never overwritten automatically. The source folder contains the original imported data before conversion. Credentials and transient sessions are excluded.\r\nIf the app cannot open, or strict import rejects intentionally deleted button placeholders: exit Team Bar, rename the current profiles folder to preserve later edits, and copy the previous folder to " + destination + ". Do not merge it with or overwrite the renamed folder. Restore user data only; never copy these files into the application installation folder.\r\n");
            if (Directory.Exists(destination)) Directory.Move(destination, rollback);
            try { Directory.Move(staging, destination); }
            catch { if (Directory.Exists(rollback) && !Directory.Exists(destination)) Directory.Move(rollback, destination); throw; }
            // The renamed original remains beside the active folder as additional
            // crash recovery evidence; never delete a user's old data tree.
            return new(Path.Combine(destination, "team.json"), backup);
        }
        finally { DeleteOwnedStaging(staging, parent); }
    }

    internal static void ValidatePreferences(string file, string name)
    {
        var json = ReadJson(file, name == "spell-overlays.json" ? 16 * 1024 * 1024 : MaxFileBytes);
        string? versionKey = OneTapProfileConverter.Keys(json, "version").SingleOrDefault();
        if (versionKey is null || json[versionKey] is not JsonValue version || !version.TryGetValue<int>(out _))
            throw new InvalidDataException("The preferences file is incomplete: a supported version is required.");
        if (name == "user-settings.json") new LocalUserSettingsStore(file).Load();
        else if (name == "spell-overlays.json") SpellOverlayStore.Load(file);
        else throw new InvalidDataException("Choose a user-settings.json or spell-overlays.json file.");
    }

    internal static string ImportPreferences(string sourceFile, string destinationFile, string backupsDirectory, byte[]? expected)
    {
        string source = SafeFullPath(sourceFile), destination = SafeFullPath(destinationFile);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a separate source preferences file.");
        string name = Path.GetFileName(destination); ValidatePreferences(source, name); byte[] bytes = File.ReadAllBytes(source);
        string parent = Path.GetDirectoryName(destination)!; EnsureSafeDirectory(parent); EnsureSafeDirectory(backupsDirectory);
        string backup = Path.Combine(backupsDirectory, "preferences-" + Guid.NewGuid().ToString("N")); EnsureSafeDirectory(backup);
        using var guard = new FileStream(SafeFullPath(destination + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[]? actual = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
        if (expected is null ? actual is not null : actual is null || !expected.SequenceEqual(actual)) throw new IOException("Preferences changed while the import was open. Try again.");
        File.WriteAllBytes(Path.Combine(backup, "source-" + name), bytes);
        if (actual is not null) File.WriteAllBytes(Path.Combine(backup, name), actual);
        string staging = destination + ".importing-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(staging, bytes); ValidatePreferences(staging, name);
            File.Move(staging, destination, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return backup;
    }

    internal static string ImportSpellOverlays(string sourceFile, string destinationFile, string backupsDirectory)
    {
        string destination = SafeFullPath(destinationFile);
        if (!Path.GetFileName(destination).Equals("spell-overlays.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the installed spell-overlay library destination.");
        ValidatePreferences(sourceFile, "spell-overlays.json");
        EnsureSafeDirectory(Path.GetDirectoryName(destination)!);
        // The caller flushes and closes its manager first. This lock also prevents an
        // independent manager from retaining old drafts while the file is replaced.
        using var runtimeGuard = new FileStream(SafeFullPath(destination + ".runtime.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        byte[]? expected = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
        return ImportPreferences(sourceFile, destination, backupsDirectory, expected);
    }

    internal static void DeleteOwnedStaging(string staging, string parent)
    {
        string full = SafeFullPath(staging), root = SafeFullPath(parent);
        if (!Path.GetDirectoryName(full)!.Equals(root, StringComparison.OrdinalIgnoreCase)
            || !(Path.GetFileName(full).Contains(".initializing", StringComparison.Ordinal) || Path.GetFileName(full).Contains(".importing-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Refused to remove an unrecognized staging folder.");
        if (Directory.Exists(full)) { foreach (string item in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)) SafeFullPath(item); Directory.Delete(full, recursive: true); }
    }
}
