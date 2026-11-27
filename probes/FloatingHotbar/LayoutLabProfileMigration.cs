using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamBar;

internal static class LayoutLabProfileMigration
{
    internal static void ImportForBase(string defaultProfile)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(InstalledAppData.ProductDirectoryName, "TeamBar")) return;
        string lab = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeamBar-LayoutLab", BuildEdition.Id, "profiles");
        Import(lab, Path.GetDirectoryName(Path.GetFullPath(defaultProfile))!, Path.Combine(InstalledAppData.DataDirectory, "layout-imports"));
    }

    internal static void Import(string labProfiles, string baseProfiles, string receipts)
    {
        if (!Directory.Exists(labProfiles) || !Directory.Exists(baseProfiles)) return;
        labProfiles = ProfileMigration.SafeFullPath(labProfiles);
        baseProfiles = ProfileMigration.SafeFullPath(baseProfiles);
        receipts = ProfileMigration.SafeFullPath(receipts);
        if (StringComparer.OrdinalIgnoreCase.Equals(labProfiles, baseProfiles)) return;

        var errors = new List<string>();
        const string suffix = ".game-views.json";
        var search = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string sidecar in Directory.EnumerateFiles(labProfiles, "*.json" + suffix, search))
        {
            string relative = Path.GetRelativePath(labProfiles, sidecar)[..^suffix.Length];
            var folders = relative.Split(Path.DirectorySeparatorChar).SkipLast(1);
            if (folders.Any(f => f.Equals("buttons", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("sessions", StringComparison.OrdinalIgnoreCase) || f.Equals("library-backups", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                string sourceProfile = ProfileMigration.SafeFullPath(Path.Combine(labProfiles, relative));
                string destProfile = ProfileMigration.SafeFullPath(Path.Combine(baseProfiles, relative));
                if (File.Exists(sourceProfile) && File.Exists(destProfile)) ImportProfile(sourceProfile, destProfile, receipts);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                errors.Add(relative + " (" + ex.Message + ")");
            }
        }
        if (errors.Count != 0) throw new InvalidDataException("Could not import Layout Lab layouts for " + string.Join(", ", errors));
    }

    private static void ImportProfile(string sourceProfile, string destProfile, string receipts)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destProfile.ToUpperInvariant())));
        string receipt = ProfileMigration.SafeFullPath(Path.Combine(receipts, key + ".done"));
        if (File.Exists(receipt)) return;
        ProfileMigration.SafeFullPath(GameViewLayouts.PathFor(sourceProfile));
        var source = GameViewLayouts.Load(sourceProfile);
        if (source.Length == 0) return;

        string dest = ProfileMigration.SafeFullPath(GameViewLayouts.PathFor(destProfile));
        using var guard = File.Open(ProfileMigration.SafeFullPath(dest + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(receipt)) return;
        var existing = GameViewLayouts.Load(destProfile);
        var names = existing.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = source.Where(l => names.Add(l.Name)).ToArray();
        ProfileMigration.EnsureSafeDirectory(receipts);
        if (missing.Length != 0)
        {
            // Keep the original records, including fields this version may not use.
            var doc = File.Exists(dest) ? JsonNode.Parse(File.ReadAllText(dest), new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject()
                : new JsonObject { ["Layouts"] = new JsonArray() };
            doc["Version"] = GameViewLayouts.FileVersion(existing.Concat(missing));
            var layouts = doc["Layouts"]!.AsArray();
            foreach (var layout in missing)
                layouts.Add(JsonSerializer.SerializeToNode(layout with { Windows = null }, Program.Json));
            WindowLayouts.SaveJson(dest, doc);
        }
        // Publish last so an interrupted import can safely retry. Keep the receipt
        // outside the library so deleting an imported layout is respected.
        using var done = File.Open(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
}
