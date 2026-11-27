using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed record BarPosition(int Version, int X, int Y);
internal sealed record GamePosition(string Character, bool Maximized, int Left, int Top, int Right, int Bottom);
internal sealed record GameLayout(int Version, GamePosition[] Windows);

internal static class WindowLayouts
{
    internal static string BarPath(string profile) => Path.GetFullPath(profile) + ".bar-position.json";
    internal static string GamesPath(string profile) => Path.GetFullPath(profile) + ".windows.json";
    internal static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static Point Clamp(Point point, Size size, Rectangle[] areas)
    {
        if (areas.Length == 0) return point;
        var bounds = new Rectangle(point, size);
        var area = areas.OrderByDescending(a => { var r = Rectangle.Intersect(a, bounds); return (long)r.Width * r.Height; })
            .ThenBy(a => Math.Pow((double)point.X - Math.Clamp(point.X, a.Left, a.Right), 2) + Math.Pow((double)point.Y - Math.Clamp(point.Y, a.Top, a.Bottom), 2)).First();
        return new(Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
    }
    internal static Point? LoadBar(string profile)
    {
        string path = BarPath(profile); if (!File.Exists(path)) return null;
        var p = JsonSerializer.Deserialize<BarPosition>(File.ReadAllText(path), Program.Json);
        if (p?.Version != 1 || Math.Abs((long)p.X) > 100000 || Math.Abs((long)p.Y) > 100000) throw new InvalidDataException("Invalid saved bar position.");
        return new(p.X, p.Y);
    }
    internal static void SaveBar(string profile, Point point) => SaveJson(BarPath(profile), new BarPosition(1, point.X, point.Y));
    internal static void CheckClient(DetectedClient client)
    {
        if (!Identity.Same(client, ClientSessions.Verify(client))) throw new InvalidDataException("A character changed. Refresh clients before using the layout.");
        GetWindowThreadProcessId((nint)client.Window, out uint owner);
        if (owner != client.ProcessId) throw new InvalidDataException("The selected game window was replaced. Refresh clients.");
    }
    internal static GamePosition Capture(string name, nint window)
    {
        var p = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(window, ref p)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (IsIconic(window)) throw new InvalidOperationException("Restore minimized game windows before saving a layout.");
        return new(name, p.Show == 3, p.Normal.Left, p.Normal.Top, p.Normal.Right, p.Normal.Bottom);
    }
    internal static void Restore(nint window, GamePosition saved)
    {
        Validate(new(1, [saved]));
        // WINDOWPLACEMENT uses workspace coordinates. Keep its normal rectangle with the paired API.
        var p = new Placement { Length = (uint)Marshal.SizeOf<Placement>(), Show = saved.Maximized ? 3u : 4u,
            Min = new() { X = -1, Y = -1 }, Max = new() { X = -1, Y = -1 },
            Normal = new() { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom } };
        if (!SetWindowPlacement(window, ref p)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void Validate(GameLayout layout)
    {
        if (layout.Version != 1 || layout.Windows is null || layout.Windows.Length is < 1 or > 6) throw new InvalidDataException("Invalid game window layout.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in layout.Windows)
            if (p is null || string.IsNullOrWhiteSpace(p.Character) || !names.Add(p.Character) || p.Right <= p.Left || p.Bottom <= p.Top
                || new[] { p.Left, p.Top, p.Right, p.Bottom }.Any(v => Math.Abs((long)v) > 100000)) throw new InvalidDataException("Invalid or duplicate game window placement.");
    }
    internal static void SaveGames(string profile, DetectedClient[] clients)
    {
        foreach (var c in clients) CheckClient(c);
        var layout = new GameLayout(1, clients.Select(c => Capture(c.Character.Name, (nint)c.Window)).ToArray()); Validate(layout);
        foreach (var c in clients) CheckClient(c);
        SaveJson(GamesPath(profile), layout);
    }
    internal static void RestoreGames(string profile, DetectedClient[] clients)
    {
        var layout = JsonSerializer.Deserialize<GameLayout>(File.ReadAllText(GamesPath(profile)), Program.Json) ?? throw new InvalidDataException("Empty layout."); Validate(layout);
        if (clients.Length == 0) throw new InvalidOperationException("Select characters first.");
        var plans = clients.Select(c => (Client: c, Position: layout.Windows.SingleOrDefault(p => string.Equals(p.Character, c.Character.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No saved window position for {c.Character.Name}. Save a layout first."))).ToArray();
        foreach (var plan in plans) CheckClient(plan.Client);
        foreach (var plan in plans) { CheckClient(plan.Client); Restore((nint)plan.Client.Window, plan.Position); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement { internal uint Length, Flags, Show; internal Native.Point Min, Max; internal Rect Normal; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
