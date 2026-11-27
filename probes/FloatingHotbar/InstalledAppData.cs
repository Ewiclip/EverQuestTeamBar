using System.Text.Json;

namespace TeamBar;

// Installed identity is a build decision. Files, environment variables and
// command-line arguments cannot redirect an installed build into developer data.
internal static class InstalledAppData
{
#if TEAMBAR_PUBLIC_RELEASE
    internal static bool IsInstalledBuild => true;
#else
    internal static bool IsInstalledBuild => false;
#endif
    internal const string ProductDirectoryName = "TeamBar";
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectoryName, BuildEdition.Id);
    internal static string ProfilesDirectory => Path.Combine(DataDirectory, "profiles");
    internal static string BackupsDirectory => Path.Combine(Path.GetDirectoryName(DataDirectory)!, "update-backups");

    internal static string DefaultProfile() => EnsureDefaultProfile(ProfilesDirectory);

    // The explicit path overload is for isolated fixtures; normal startup never
    // searches the checkout, parent folders or a previous installation.
    internal static string EnsureDefaultProfile(string profilesDirectory)
    {
        string destination = ProfileMigration.SafeFullPath(profilesDirectory);
        string parent = Path.GetDirectoryName(destination)!;
        ProfileMigration.EnsureSafeDirectory(parent);
        using var guard = new FileStream(ProfileMigration.SafeFullPath(Path.Combine(parent, ".profiles-migration.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string profile = Path.Combine(destination, "team.json");
        if (Directory.Exists(destination))
        {
            if (!File.Exists(profile)) throw new InvalidDataException("The saved profiles folder is incomplete. Its files were preserved. With Team Bar closed, restore the profiles folder from a backup or move the incomplete folder aside before reopening.");
            ProfileMigration.ValidateLibrary(destination, requireComplete: false);
            return profile;
        }
        if (Directory.EnumerateDirectories(parent, Path.GetFileName(destination) + ".*").Any(folder =>
            folder.Contains(".previous-", StringComparison.Ordinal) || folder.Contains(".importing-", StringComparison.Ordinal) || folder.Contains(".initializing-", StringComparison.Ordinal)))
            throw new InvalidDataException("An interrupted profile import or initialization was found. Its files were preserved. With Team Bar closed, restore a validated profiles folder from your backup before reopening.");
        string staging = destination + ".initializing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            TeamEditing.Save(Path.Combine(staging, "team.json"), new(1, [], [], []), []);
            ProfileMigration.ValidateLibrary(staging);
            Directory.Move(staging, destination);
        }
        finally { ProfileMigration.DeleteOwnedStaging(staging, parent); }
        return profile;
    }

    internal static string CreatePreUpdateBackup(string version) => CreatePreUpdateBackup(DataDirectory, BackupsDirectory, version);

    internal static string CreatePreUpdateBackup(string dataDirectory, string backupsDirectory, string version)
    {
        string source = ProfileMigration.SafeFullPath(dataDirectory);
        string backups = ProfileMigration.SafeFullPath(backupsDirectory);
        ProfileMigration.EnsureSafeDirectory(backups);
        string destination = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        string staging = destination + ".initializing";
        Directory.CreateDirectory(staging);
        try
        {
            string profiles = Path.Combine(source, "profiles");
            if (Directory.Exists(profiles)) ProfileMigration.CopyValidatedLibrary(profiles, Path.Combine(staging, "profiles"), normalize: false, requireComplete: false);
            foreach (string name in new[] { "user-settings.json", "spell-overlays.json" })
            {
                string file = Path.Combine(source, name);
                if (!File.Exists(file)) continue;
                ProfileMigration.ValidatePreferences(file, name);
                File.Copy(file, Path.Combine(staging, name));
                ProfileMigration.ValidatePreferences(Path.Combine(staging, name), name);
            }
            File.WriteAllText(Path.Combine(staging, "backup.json"), JsonSerializer.Serialize(new { version = 1, targetVersion = version, createdUtc = DateTime.UtcNow, credentialsIncluded = false }));
            File.WriteAllText(Path.Combine(staging, "RESTORE.txt"), "This backup contains saved profiles and preferences only.\r\nTo restore, open Team Bar Settings and choose Import existing profiles, then select this backup's profiles folder. Confirm replacing the current library only after reviewing your later edits. Import preferences separately by choosing user-settings.json.\r\nIf the app cannot open, or import rejects deliberately deleted button placeholders: exit Team Bar fully, rename the current profiles folder to preserve your later edits, then copy this backup's profiles folder into the per-user data directory shown below. Do not merge with or overwrite your renamed folder.\r\nSpell overlay data is preserved in spell-overlays.json; with Team Bar fully exited, copy that file to the same data directory after keeping a copy of the current file.\r\nData directory: " + source + "\r\nNever copy files into the application installation folder. Contribution credentials are excluded and stay in their existing per-user scope. No update automatically restores or overwrites later edits.\r\n");
            Directory.Move(staging, destination);
            return destination;
        }
        finally { ProfileMigration.DeleteOwnedStaging(staging, backups); }
    }
}
