using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamBar;

// File resources and ordinary Windows layout only. Input and memory readers keep
// their own installation and build checks. Pin the selection for a layout's lifetime.
internal static class ScreenInstallation
{
    internal const string Original = @"C:\EverQuest-Live";
    private static readonly Lazy<string> Selected = new(() =>
    {
        try
        {
            var saved = new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath).Load().ScreenGameDirectory;
            if (EqIconInstallation.IsLocalPath(saved)) return Normalize(saved!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { }
        return Original;
    });

    internal static string Current => Selected.Value;
    internal static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static string Validate(string path)
    {
        if (!EqIconInstallation.IsLocalPath(path))
            throw new InvalidDataException("Choose an EverQuest Live installation on a local drive.");
        string folder = Normalize(path);
        if (new DriveInfo(Path.GetPathRoot(folder)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Choose an EverQuest Live installation on a local drive.");
        if (!File.Exists(Path.Combine(folder, "eqgame.exe")))
            throw new InvalidDataException("Choose the EverQuest Live folder containing eqgame.exe.");
        // Preserve the previously supported local path. Other folders must
        // identify the Live channel before their windows can be managed.
        if (Same(folder, Original)) return folder;
        var launcher = new FileInfo(Path.Combine(folder, "LaunchPad.ini"));
        if (!launcher.Exists || launcher.Length > 65536)
            throw new InvalidDataException("This folder needs an EverQuest Live LaunchPad.ini.");
        bool settings = false;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadLines(launcher.FullName))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { settings = line.Equals("[Settings]", StringComparison.OrdinalIgnoreCase); continue; }
            int equals = line.IndexOf('=');
            if (!settings || equals < 1) continue;
            string key = line[..equals].Trim();
            if (!fields.TryAdd(key, line[(equals + 1)..].Trim()) &&
                (key.Equals("id", StringComparison.OrdinalIgnoreCase) || key.Equals("appName", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("environment", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("LaunchPad.ini contains conflicting game or channel settings.");
        }
        if (!string.Equals(fields.GetValueOrDefault("id"), "eq", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fields.GetValueOrDefault("appName"), "EverQuest", StringComparison.OrdinalIgnoreCase) ||
            (fields.TryGetValue("environment", out string? channel) && !channel.Equals("live", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose a Live installation. This launcher's game or channel is unsupported.");
        return folder;
    }

    internal static string RecoveryName(string name, string folder)
    {
        if (Same(folder, Original)) return name + ".json";
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(folder).ToUpperInvariant()));
        return name + "-" + Convert.ToHexString(key.AsSpan(0, 16)) + ".json";
    }
}
