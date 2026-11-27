using System.Text.Json;

// A button belongs to its physical slot. Its offset stays there when games
// swap, another monitor is added, or the Windows display number changes.
internal sealed class GameViewSwapButtonPositions(string profile)
{
    private sealed record Entry(string Layout, string Monitor, int Slot, RelativePosition Position)
    {
        internal bool Matches(string layout, string monitor, int slot) => Slot == slot &&
            StringComparer.OrdinalIgnoreCase.Equals(Layout, layout) && StringComparer.OrdinalIgnoreCase.Equals(Monitor, monitor);
    }
    private sealed record PositionFile(int Version, Entry[] Slots);
    private readonly string path = Path.GetFullPath(profile) + ".swap-button-positions.json";
    private Entry[]? entries;

    private static (string Monitor, int Slot) Locate(GameViewLayout layout, int slot)
    {
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        foreach (var screen in layout.ScreenPlans)
        {
            if (slot < screen.Count) return (screen.MonitorId ?? screen.Monitor, slot);
            slot -= screen.Count;
        }
        throw new ArgumentOutOfRangeException(nameof(slot));
    }

    private static bool Valid(RelativePosition? p) => p is { X: >= 0 and <= 1, Y: >= 0 and <= 1 };

    private Entry[] Load()
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var data = JsonSerializer.Deserialize<PositionFile>(stream, Program.Json);
            if (data is not { Version: 1, Slots: not null })
                throw new InvalidDataException("Unsupported swap button positions.");
            var keys = new HashSet<(string, string, int)>();
            foreach (var e in data.Slots)
                if (e is null || string.IsNullOrWhiteSpace(e.Layout) || string.IsNullOrWhiteSpace(e.Monitor) ||
                    e.Slot < 0 || !Valid(e.Position) || !keys.Add((e.Layout.ToUpperInvariant(), e.Monitor.ToUpperInvariant(), e.Slot)))
                    throw new InvalidDataException("Invalid saved swap button position.");
            return data.Slots;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException ex) { throw new InvalidDataException("The saved swap button positions could not be read.", ex); }
    }

    internal void Validate() => Load();

    internal RelativePosition? Read(GameViewLayout layout, int slot)
    {
        var (monitor, local) = Locate(layout, slot);
        entries ??= Load();
        return entries.FirstOrDefault(e => e.Matches(layout.Name, monitor, local))?.Position;
    }

    internal void Save(GameViewLayout layout, int slot, RelativePosition position)
    {
        if (!Valid(position)) throw new ArgumentOutOfRangeException(nameof(position));
        var (monitor, local) = Locate(layout, slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var guard = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var updated = Load().ToList();
        int index = updated.FindIndex(e => e.Matches(layout.Name, monitor, local));
        var entry = new Entry(layout.Name, monitor, local, position);
        if (index < 0) updated.Add(entry);
        else updated[index] = entry;
        var saved = updated.ToArray();
        WindowLayouts.SaveJson(path, new PositionFile(1, saved));
        entries = saved;
    }
}
