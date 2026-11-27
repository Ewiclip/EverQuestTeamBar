using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal sealed record CharacterIdentity(string Name, int ClassId, int Level)
{
    internal string Class => Identity.Classes[ClassId - 1];
}
internal sealed record DetectedClient(int ProcessId, long StartedUtcTicks, long Window, int Left, CharacterIdentity Character, bool UserAssigned = false,
    string ClientChannel = "Live", string BuildSha256 = Identity.BuildHash)
{
    public override string ToString() => $"{Character.Name} — {Character.Class}" + (ClientChannel == "Live" ? "" : $" · {ClientChannel}");
    public string CollectorLabel => $"{Character.Name} — {Character.Class} · {ClientChannel}";
}
internal sealed record Discovery(DetectedClient[] Clients, string[] Errors);
internal sealed record IdentityLayout(ulong PlayerRoot, ulong PcRoot, ulong GameRoot,
    ulong GameState, ulong PcPlayer, ulong PlayerType, ulong PlayerName, ulong PcName,
    ulong PcProfileType, ulong PcProfileList, ulong PlayerClass, ulong ProfileClass, ulong ProfileLevel,
    ulong ListType = 0, ulong ListProfile = 8, ulong ListNext = 0x18, ulong ProfileType = 0x18, int ReadyState = 5);

internal static class Identity
{
    internal static readonly string[] Classes = ["Warrior", "Cleric", "Paladin", "Ranger", "Shadowknight", "Druid", "Monk", "Bard", "Rogue", "Shaman", "Necromancer", "Wizard", "Magician", "Enchanter", "Beastlord", "Berserker"];
    internal const string BuildHash = "3d60b6357e8337f63264c179f6936aa640dcc589174593ccc8d5451e54770b14";
    internal static readonly IdentityLayout LiveLayout = new(0xeb6440, 0xeb5fb0, 0xf35250,
        0x5e4, 0x2848, 0x135, 0xb4, 0x2fa0, 0x2f10, 0x2f08, 0xfe4, 0x17cc, 0x17e8);
    private static readonly Dictionary<(string Path, string ExpectedHash), (long Length, DateTime WriteTime)> verifiedFiles = [];
    private static readonly object FileLock = new();
    internal static void VerifyFile(string path, string expectedHash)
    {
        lock (FileLock)
        {
            string fullPath = Path.GetFullPath(path);
            var key = (fullPath.ToUpperInvariant(), expectedHash.ToLowerInvariant());
            var info = new FileInfo(fullPath); var stamp = (info.Length, info.LastWriteTimeUtc);
            if (verifiedFiles.TryGetValue(key, out var verified) && verified == stamp) return;
            using var file = File.OpenRead(fullPath);
            if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(key.Item2, StringComparison.Ordinal))
                throw new InvalidDataException("This EQ build needs a detector update.");
            info.Refresh();
            if ((info.Length, info.LastWriteTimeUtc) != stamp) throw new InvalidDataException("The EQ executable changed during verification.");
            if (verifiedFiles.Count >= 16) verifiedFiles.Clear();
            verifiedFiles[key] = stamp;
        }
    }
    internal static DetectedClient Read(int pid) => Read(pid, MemoryFeature.ScreenDetection);
    internal static DetectedClient Read(int pid, MemoryFeature feature)
    {
        using var memory = new MemoryReader(pid, feature);
        return IdentityResolver.Read(memory);
    }
    internal static DetectedClient Read(MemoryReader memory, IdentityLayout layout, string channel, string buildHash)
    {
        var first = Decode(memory.Read, memory.Base, layout); var second = Decode(memory.Read, memory.Base, layout);
        if (first != second) throw new InvalidDataException("Character data is changing. Finish loading and refresh.");
        using var process = Process.GetProcessById(memory.ProcessId); nint window = process.MainWindowHandle;
        if (process.StartTime.ToUniversalTime().Ticks != memory.StartedUtcTicks || window == 0 || !GetWindowRect(window, out var rect))
            throw new InvalidDataException("The game window changed. Refresh the clients.");
        return new(memory.ProcessId, memory.StartedUtcTicks, window.ToInt64(), rect.Left, first.Character,
            ClientChannel: channel, BuildSha256: buildHash);
    }
    // Passive decoders keep their independently verified build/layout. The default
    // Decode overload is historical fixture/collector support, not input discovery.
    internal sealed record Snapshot(ulong Player, ulong Pc, ulong Profile, CharacterIdentity Character);
    internal static Snapshot Decode(Func<ulong, int, byte[]> read, ulong image) => Decode(read, image, LiveLayout);
    internal static Snapshot Decode(Func<ulong, int, byte[]> read, ulong image, IdentityLayout layout)
    {
        ulong U64(ulong a) => BitConverter.ToUInt64(read(a, 8));
        int I32(ulong a) => BitConverter.ToInt32(read(a, 4));
        ulong Ptr(ulong a) { ulong p = U64(a); if (!MemoryReader.Pointer(p) || (p & 7) != 0) throw new InvalidDataException("Character data is unavailable."); return p; }
        string Name(ulong a)
        {
            byte[] bytes = read(a, 64); int end = Array.IndexOf(bytes, (byte)0);
            if (end is < 2 or > 30 || !bytes.Take(end).All(b => b is >= 65 and <= 90 or >= 97 and <= 122))
                throw new InvalidDataException("Character name validation failed.");
            return Encoding.ASCII.GetString(bytes, 0, end);
        }
        ulong game = Ptr(image + layout.GameRoot);
        if (I32(game + layout.GameState) != layout.ReadyState) throw new InvalidDataException("Finish entering the game, then refresh.");
        ulong player = Ptr(image + layout.PlayerRoot), pc = Ptr(image + layout.PcRoot);
        if (Ptr(pc + layout.PcPlayer) != player || read(player + layout.PlayerType, 1)[0] != 0)
            throw new InvalidDataException("Local character identity is not ready.");
        string name = Name(player + layout.PlayerName);
        if (Name(pc + layout.PcName) != name) throw new InvalidDataException("Character identity sources disagree.");
        int profileType = I32(pc + layout.PcProfileType);
        if (profileType != 0) throw new InvalidDataException("This alternate character mode is not supported yet.");
        ulong list = Ptr(pc + layout.PcProfileList), profile = 0;
        var visited = new HashSet<ulong>();
        for (int i = 0; i < 16 && visited.Add(list); i++)
        {
            if (I32(list + layout.ListType) == profileType) { profile = Ptr(list + layout.ListProfile); break; }
            ulong next = U64(list + layout.ListNext); if (!MemoryReader.Pointer(next)) break; list = next;
        }
        if (profile == 0 || I32(profile + layout.ProfileType) != profileType) throw new InvalidDataException("Active persona is unavailable.");
        int cls = I32(player + layout.PlayerClass), profileClass = I32(profile + layout.ProfileClass), level = I32(profile + layout.ProfileLevel);
        if (cls is < 1 or > 16 || cls != profileClass || level is < 1 or > 255)
            throw new InvalidDataException("Class identity sources disagree or are changing.");
        if (U64(image + layout.PlayerRoot) != player || U64(image + layout.PcRoot) != pc || U64(image + layout.GameRoot) != game
            || I32(game + layout.GameState) != layout.ReadyState)
            throw new InvalidDataException("Character data changed during detection.");
        return new(player, pc, profile, new(name, cls, level));
    }
    internal static bool Same(DetectedClient expected, DetectedClient actual) => expected.ProcessId == actual.ProcessId
        && expected.StartedUtcTicks == actual.StartedUtcTicks && expected.Window == actual.Window
        && expected.Character.Name == actual.Character.Name && expected.Character.ClassId == actual.Character.ClassId
        && expected.ClientChannel == actual.ClientChannel && expected.BuildSha256 == actual.BuildSha256;
    internal static bool SameCharacterSession(DetectedClient expected, DetectedClient actual) =>
        actual.Character.ClassId is >= 1 and <= 16 && expected.UserAssigned == actual.UserAssigned
        && Same(expected, actual with { Character = actual.Character with { ClassId = expected.Character.ClassId } });
    internal static Discovery Discover(IEnumerable<int>? assignedPids = null, MemoryFeature feature = MemoryFeature.ScreenDetection)
    {
#if TEAMBAR_APPLICATION
        if (!TeamBar.GameDataAccess.IsEnabled(feature)) return new([], []);
#endif
        return DiscoverInput(ProcessIds(), pid => Read(pid, feature), assignedPids);
    }
    internal static int[] ProcessIds()
    {
        var processes = Process.GetProcessesByName("eqgame");
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    internal static Discovery DiscoverInput(IEnumerable<int> processIds, Func<int, DetectedClient> read, IEnumerable<int>? assignedPids = null)
    {
        var found = new List<DetectedClient>(); var errors = new List<string>(); var assigned = assignedPids?.ToHashSet() ?? [];
        foreach (int pid in processIds)
        {
                if (!assigned.Contains(pid))
                try
                {
                    var client = read(pid);
                    if (client.ClientChannel != "Live")
                        throw new InvalidDataException("This client is available only for passive collection, not Team Bar input.");
                    found.Add(client);
                }
                catch (Exception e) when (e is InvalidDataException or Win32Exception or InvalidOperationException or ArgumentException or IOException)
                { errors.Add($"Client PID {pid} could not be identified: {e.Message}"); }
        }
        return new(found.OrderBy(c => c.Left).ToArray(), errors.ToArray());
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
}
