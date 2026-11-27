using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed record GameFrame(string ScreenId, string ScreenLabel, Rectangle Area);
internal sealed record RelativePosition(double X, double Y);
internal sealed record ScreenPosition(string ScreenId, string Label, RelativePosition Position);
internal sealed record CharacterPositions(string CharacterId, RelativePosition Fallback, ScreenPosition[] Screens);
internal sealed record AnchorLayout(int Version, CharacterPositions[] Characters);

internal static class AnchorPositions
{
    internal static string FileFor(string entity) => Path.GetFullPath(entity) + ".screen-positions.json";
    internal static RelativePosition Normalize(Point point, Size control, Rectangle area) => new(
        area.Width > control.Width ? Math.Clamp((double)(point.X - area.Left) / (area.Width - control.Width), 0, 1) : .5,
        area.Height > control.Height ? Math.Clamp((double)(point.Y - area.Top) / (area.Height - control.Height), 0, 1) : .5);
    internal static Point Project(RelativePosition position, Size control, Rectangle area) => new(
        area.Left + (int)Math.Round(Math.Max(0, area.Width - control.Width) * position.X),
        area.Top + (int)Math.Round(Math.Max(0, area.Height - control.Height) * position.Y));
    internal static Point Resolve(CharacterPositions positions, Size control, GameFrame frame)
    {
        var saved = positions.Screens.SingleOrDefault(s => string.Equals(s.ScreenId, frame.ScreenId, StringComparison.OrdinalIgnoreCase));
        return Project(saved?.Position ?? positions.Fallback, control, frame.Area);
    }
    internal static AnchorLayout Load(string entity)
    {
        string path = FileFor(entity); if (!File.Exists(path)) return new(1, []);
        var value = JsonSerializer.Deserialize<AnchorLayout>(File.ReadAllBytes(path), Program.Json) ?? throw new InvalidDataException("Empty saved screen positions.");
        Validate(value); return value;
    }
    private static void Validate(AnchorLayout layout)
    {
        static bool Position(RelativePosition? p) => p is not null && double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1;
        if (layout.Version != 1 || layout.Characters is null || layout.Characters.Length > 64) throw new InvalidDataException("Unsupported saved screen positions.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in layout.Characters)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.CharacterId) || !ids.Add(c.CharacterId) || !Position(c.Fallback) || c.Screens is null || c.Screens.Length > 64)
                throw new InvalidDataException("Invalid character screen positions.");
            var screens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in c.Screens)
                if (s is null || string.IsNullOrWhiteSpace(s.ScreenId) || s.ScreenId.Length > 512 || string.IsNullOrWhiteSpace(s.Label) || !screens.Add(s.ScreenId) || !Position(s.Position))
                    throw new InvalidDataException("Invalid monitor position.");
        }
    }
    internal static CharacterPositions? Find(AnchorLayout layout, string characterId) => layout.Characters.SingleOrDefault(c => string.Equals(c.CharacterId, characterId, StringComparison.OrdinalIgnoreCase));
    internal static CharacterPositions Seed(string entity, string characterId, RelativePosition fallback)
    {
        return Update(entity, characterId, previous => previous ?? new(characterId, fallback, []));
    }
    internal static CharacterPositions Remember(string entity, string characterId, GameFrame frame, Point point, Size control)
    {
        var position = Normalize(point, control, frame.Area);
        return Update(entity, characterId, previous => new(characterId, position,
            [.. (previous?.Screens ?? []).Where(s => !string.Equals(s.ScreenId, frame.ScreenId, StringComparison.OrdinalIgnoreCase)), new(frame.ScreenId, frame.ScreenLabel, position)]));
    }
    private static CharacterPositions Update(string entity, string characterId, Func<CharacterPositions?, CharacterPositions> update)
    {
        string path = FileFor(entity); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var layout = Load(entity); var previous = Find(layout, characterId); var next = update(previous);
        var updated = layout with { Characters = [.. layout.Characters.Where(c => !string.Equals(c.CharacterId, characterId, StringComparison.OrdinalIgnoreCase)), next] };
        Validate(updated);
        if (next != previous) WindowLayouts.SaveJson(path, updated);
        return next;
    }
    internal static void ValidateCharacter(string? characterId, CharacterProfile[] characters)
    {
        if (characterId is not null && !characters.Any(c => string.Equals(c.Id, characterId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose a known character for screen following.");
    }
    // Geometry reads never activate or move the game. Client coordinates exclude the window chrome.
    internal static GameFrame? ReadFrame(DetectedClient client) =>
        ReadFrame(client, TeamBar.TeamBarApplication.Current?.ActiveGameViewSession);

    internal static GameFrame? ReadFrame(DetectedClient client, TeamBar.GameViewSession? session)
    {
        nint window = (nint)client.Window; GetWindowThreadProcessId(window, out uint owner);
        if (owner != client.ProcessId || !IsWindowVisible(window) || IsIconic(window)) return null;
        Rectangle area;
        Screen screen;
        if (session is not null && session.TryGetVisibleClientRect(window, out var visible))
        {
            // A live tile can occupy the full monitor, including the usual taskbar area.
            screen = Screen.FromRectangle(visible);
            area = Rectangle.Intersect(visible, screen.Bounds);
        }
        else
        {
            var origin = new Native.Point();
            if (!GetClientRect(window, out var rect) || !ClientToScreen(window, ref origin)) return null;
            screen = Screen.FromHandle(window);
            area = Rectangle.Intersect(new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top), screen.WorkingArea);
        }
        if (area.Width < 1 || area.Height < 1) return null;
        var (id, description) = GameViewMonitor.IdentityFor(screen.DeviceName);
        string label = screen.DeviceName.Replace("\\\\.\\", "");
        if (!string.IsNullOrWhiteSpace(description)) label += " · " + description;
        return new(id ?? screen.DeviceName, label, area);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Native.Point point);
}
