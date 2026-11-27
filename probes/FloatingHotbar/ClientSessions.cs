using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Session liveness/window ownership is independent of the optional EQ data decoder.
internal sealed record GameSession(int ProcessId, long StartedUtcTicks, long Window, int Left, string Label)
{
    internal string? IdentifiedCharacterName { get; init; }
}
internal static class ClientSessions
{
    internal static string LaunchSettings => Path.Combine(TeamBar.ScreenInstallation.Current, "eqclient.ini");
    internal static GameSession ReadWindow(int pid) => ReadWindow(pid, TeamBar.ScreenInstallation.Original);
    internal static GameSession ReadScreenWindow(int pid) => ReadWindow(pid, TeamBar.ScreenInstallation.Validate(TeamBar.ScreenInstallation.Current));
    private static GameSession ReadWindow(int pid, string directory)
    {
        using var process = Process.GetProcessById(pid);
        using var handle = OpenProcess(0x1000, false, pid); // query metadata only; no VM_READ
        var path = new StringBuilder(32768); int capacity = path.Capacity;
        if (handle.IsInvalid || !QueryFullProcessImageNameW(handle, 0, path, ref capacity)
            || !string.Equals(path.ToString(), Path.Combine(directory, "eqgame.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected window is not the Live EverQuest client.");
        var hwnd = process.MainWindowHandle; GetWindowThreadProcessId(hwnd, out uint owner);
        if (process.HasExited || hwnd == 0 || owner != pid || !IsEverQuestWindow(hwnd) || !GetWindowRect(hwnd, out var rect))
            throw new InvalidDataException("This game window is no longer available. Assign it again.");
        string monitor = Screen.FromHandle(hwnd).DeviceName.Replace("\\\\.\\", "");
        return new(pid, process.StartTime.ToUniversalTime().Ticks, hwnd.ToInt64(), rect.Left,
            $"{monitor} · ({rect.Left}, {rect.Top}) · {rect.Right - rect.Left} × {rect.Bottom - rect.Top}");
    }
    internal static bool IsEverQuestWindow(nint window)
    {
        var name = new StringBuilder(128);
        return GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == "_EverQuestwndclass";
    }
    internal static GameSession[] ListWindows() => ListWindows(ReadWindow);
    internal static GameSession[] ListScreenWindows() => ListWindows(ReadScreenWindow);
    private static GameSession[] ListWindows(Func<int, GameSession> read)
    {
        var found = new List<GameSession>();
        foreach (var process in Process.GetProcessesByName("eqgame")) using (process)
            try { found.Add(read(process.Id)); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return found.OrderBy(w => w.Left).ThenBy(w => w.ProcessId).ToArray();
    }
    internal static DetectedClient Verify(DetectedClient expected) => Verify(expected, MemoryFeature.ScreenDetection);
    internal static DetectedClient Verify(DetectedClient expected, MemoryFeature feature) => Verify(expected, ReadWindow, pid => Identity.Read(pid, feature));
    internal static DetectedClient Verify(DetectedClient expected, Func<int, GameSession> windowReader, Func<int, DetectedClient> detector)
    {
        if (!expected.UserAssigned) return detector(expected.ProcessId); // A decoder failure never silently changes source.
        var window = windowReader(expected.ProcessId);
        if (window.StartedUtcTicks != expected.StartedUtcTicks || window.Window != expected.Window || window.ProcessId != expected.ProcessId)
            throw new InvalidDataException("The assigned game session changed. Assign its current window again.");
        return expected with { Left = window.Left };
    }
    internal static DetectedClient Assign(GameSession window, string name, string cls)
    {
        if (name.Length is < 2 or > 30 || !name.All(char.IsAsciiLetter) || !Identity.Classes.Contains(cls))
            throw new InvalidDataException("Select a saved character and its current class.");
        if (window.ProcessId <= 0 || window.StartedUtcTicks <= 0 || window.Window == 0) throw new InvalidDataException("Select a current game window.");
        return new(window.ProcessId, window.StartedUtcTicks, window.Window, window.Left, new(name, Array.IndexOf(Identity.Classes, cls) + 1, 0), true);
    }
    internal static void ValidateAssignments(DetectedClient[] clients)
    {
        if (clients.Length is < 1 or > 6 || clients.Select(c => c.ProcessId).Distinct().Count() != clients.Length
            || clients.Select(c => c.Character.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != clients.Length)
            throw new InvalidDataException("Assign one window per character, up to six characters.");
    }
    internal static DetectedClient[] Restore(TeamDefinition team, ClientFile file, Func<DetectedClient, DetectedClient>? verify = null)
    {
        Routing.Validate(team, file.Clients); verify ??= Verify;
        DetectedClient[] identities;
        if (file.Version == 1) identities = file.Clients.Select(c => Identity.Read(c.ProcessId)).ToArray();
        else if (file.Version == 2 && file.Identities is not null && file.Identities.Length == file.Clients.Length) identities = file.Identities;
        else throw new InvalidDataException("Unsupported or incomplete client bindings. Apply again in Setup.");
        ValidateAssignments(identities);
        foreach (var binding in file.Clients)
        {
            var expected = identities.SingleOrDefault(i => i.ProcessId == binding.ProcessId) ?? throw new InvalidDataException("A session identity is missing.");
            var persona = team.Personas.Single(p => string.Equals(p.Id, binding.PersonaId, StringComparison.OrdinalIgnoreCase));
            var character = team.Characters.Single(c => string.Equals(c.Id, persona.CharacterId, StringComparison.OrdinalIgnoreCase));
            if (expected.StartedUtcTicks != binding.StartedUtcTicks || expected.Window == 0 || expected.Character.ClassId is < 1 or > 16
                || !string.Equals(character.Name, expected.Character.Name, StringComparison.OrdinalIgnoreCase)
                || (persona.Class is null ? file.Version != 1 : TeamEditing.ClassKey(persona.Class) != TeamEditing.ClassKey(expected.Character.Class))
                || !Identity.Same(expected, verify(expected)))
                throw new InvalidDataException("A saved session no longer matches its assignment. Refresh or assign windows in Setup.");
        }
        return identities;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll")] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, StringBuilder path, ref int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
}
