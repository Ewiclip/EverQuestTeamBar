using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TeamBar;

// Runs in a separate process after installation, without starting WPF, reading
// game memory, loading native input DLLs, or creating/migrating user data.
internal static class InstallationHealthCheck
{
    internal static int Run(string receiptPath)
    {
        if (!InstalledAppData.IsInstalledBuild)
            throw new InvalidOperationException("Installation checks require an installed release build.");
        if (!Path.IsPathFullyQualified(receiptPath))
            throw new ArgumentException("An absolute installation receipt path is required.");
        string receipt = ProfileMigration.SafeFullPath(receiptPath);
        if (!Directory.Exists(Path.GetDirectoryName(receipt)))
            throw new DirectoryNotFoundException("The installation receipt directory does not exist.");
        string version = ReleaseUpdateConfiguration.InstalledVersion;
        _ = ReleaseUpdateVersion.Parse(version);
        var metadata = typeof(InstallationHealthCheck).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? "", StringComparer.Ordinal);
        foreach (string name in new[] { "TeamBar.exe", "TeamBar.dll", "TeamBar.deps.json", "TeamBar.runtimeconfig.json", "coreclr.dll", "PresentationFramework.dll", "System.Security.Cryptography.Pkcs.dll" })
        {
            string file = ProfileMigration.SafeFullPath(Path.Combine(AppContext.BaseDirectory, name));
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                throw new InvalidDataException("The installed application is incomplete: " + name);
        }
        foreach (var entry in new[] { ("TeamBridge.dll", "NativeBridgeHash"), ("TeamControllerHost.dll", "NativeCollectorHash") })
        {
            string expected = metadata.GetValueOrDefault(entry.Item2, "");
            string file = ProfileMigration.SafeFullPath(Path.Combine(AppContext.BaseDirectory, entry.Item1));
            if (!ReleaseUpdateManifest.IsHash(expected)) throw new InvalidDataException("The installed native inventory is missing.");
            using var input = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed native library does not match this release: " + entry.Item1);
        }
        // Validate existing data read-only. A new install does not need profiles
        // until normal startup creates its empty library.
        if (Directory.Exists(InstalledAppData.ProfilesDirectory))
            ProfileMigration.ValidateLibrary(InstalledAppData.ProfilesDirectory, requireComplete: false);
        foreach (string name in new[] { "user-settings.json", "spell-overlays.json" })
        {
            string file = Path.Combine(InstalledAppData.DataDirectory, name);
            if (File.Exists(file)) ProfileMigration.ValidatePreferences(file, name);
        }
        using var output = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new { healthy = true, version, dataSchemaCompatible = true });
        output.Flush(flushToDisk: true);
        return 0;
    }
}
