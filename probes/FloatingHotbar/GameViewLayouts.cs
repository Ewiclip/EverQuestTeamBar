using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<GameViewKind>))]
internal enum GameViewKind { Corner, EqualSplit, Rows, Grid, MainStrip }

[JsonConverter(typeof(JsonStringEnumConverter<GameViewEdge>))]
internal enum GameViewEdge { Bottom, Top, Left, Right }

[JsonConverter(typeof(JsonStringEnumConverter<GameViewSwapButtonStyle>))]
internal enum GameViewSwapButtonStyle { CharacterName, Icon, CustomText }

internal sealed record GameViewScreen(string Monitor, [property: JsonRequired] GameViewKind Kind, int Count,
    GameViewEdge Edge = GameViewEdge.Bottom, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MonitorId = null);

internal sealed record GameViewWindow(int ProcessId, long StartedUtcTicks, long Window)
{
    internal static GameViewWindow From(GameSession session) => new(session.ProcessId, session.StartedUtcTicks, session.Window);
    internal bool Matches(DetectedClient client) => ProcessId == client.ProcessId && StartedUtcTicks == client.StartedUtcTicks && Window == client.Window;
}

// Slot numbers follow screen order, then slot order within each screen. Empty
// and offline slots keep their index in Characters, Windows and the runtime.
internal sealed record GameViewLayout(string Name, [property: JsonRequired] GameViewKind Kind, string Monitor,
    [property: JsonRequired] bool SwapOnClick, string[] Characters)
{
    public GameViewEdge Edge { get; init; } = GameViewEdge.Bottom;
    public bool AvoidTaskbar { get; init; }
    public bool AlwaysOnTop { get; init; }
    public bool MatchMasterResolution { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideTitleBars { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AddSwapButton { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GameViewSwapButtonStyle SwapButtonStyle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SwapButtonText { get; init; }
    internal const int MaxSwapButtonTextLength = 64;

    // A manual window pick is valid only for this exact live game session.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameViewWindow?[]? Windows { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameViewScreen[]? Screens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MasterSlot { get; init; }
    internal GameViewScreen[] ScreenPlans => Screens ?? [new(Monitor, Kind, Characters.Length, Edge)];
    internal int MasterIndex => SwapOnClick ? MasterSlot ?? 0 : -1;
    internal int SwapTargetIndex => SwapOnClick || AddSwapButton ? MasterSlot ?? 0 : -1;
    internal bool HasSwapButtonSettings => AddSwapButton || SwapButtonStyle != GameViewSwapButtonStyle.CharacterName ||
        !string.IsNullOrWhiteSpace(SwapButtonText);
    internal string SwapButtonLabel(string character) =>
        SwapButtonStyle == GameViewSwapButtonStyle.CustomText && !string.IsNullOrWhiteSpace(SwapButtonText)
            ? SwapButtonText.Replace("{character}", character, StringComparison.OrdinalIgnoreCase) : character;

    internal GameSession?[] Resolve(GameSession[] sessions, DetectedClient[] roster, IReadOnlyList<GameSession?>? previous = null)
    {
        var seats = new GameSession?[Characters.Length];
        var claimed = new HashSet<GameViewWindow>();
        for (int i = 0; i < seats.Length; i++)
        {
            if (Windows?[i] is not { } chosen) continue;
            var live = sessions.FirstOrDefault(w => GameViewWindow.From(w) == chosen);
            if (live is null) continue;
            if (!claimed.Add(chosen)) throw new InvalidOperationException("Choose a different EQ window for each slot.");
            seats[i] = live;
        }
        for (int i = 0; i < seats.Length; i++)
        {
            if (seats[i] is not null || string.IsNullOrWhiteSpace(Characters[i])) continue;
            var matches = sessions.Where(w =>
            {
                var key = GameViewWindow.From(w);
                return !claimed.Contains(key) && roster.Any(c => key.Matches(c) &&
                    string.Equals(c.Character.Name, Characters[i], StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            if (matches.Length == 1) seats[i] = matches[0];
            else if (previous?.Count == seats.Length && previous[i] is { } last)
                seats[i] = matches.FirstOrDefault(w => GameViewWindow.From(w) == GameViewWindow.From(last));
            if (seats[i] is { } match) claimed.Add(GameViewWindow.From(match));
        }
        return seats;
    }
}

internal static class GameViewLayouts
{
    private sealed record LayoutFile(int Version, GameViewLayout[] Layouts);
    internal static string PathFor(string profile) => Path.GetFullPath(profile) + ".game-views.json";

    private static string Label(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new InvalidDataException($"Choose a valid {field}.");
        return value.Trim();
    }

    internal static GameViewLayout Normalize(GameViewLayout? layout, int version = 4)
    {
        if (layout?.Characters is null) throw new InvalidDataException("Missing game layout or character slots.");
        if (version < 3 && (layout.Screens is not null || layout.MasterSlot.HasValue))
            throw new InvalidDataException("Screen groups and a master slot require layout file version 3.");
        if (version < 4 && layout.HasSwapButtonSettings)
            throw new InvalidDataException("Swap button settings require layout file version 4.");
        if (!Enum.IsDefined(layout.SwapButtonStyle) || layout.SwapButtonText is { } buttonText &&
            (buttonText.Length > GameViewLayout.MaxSwapButtonTextLength || buttonText.Any(char.IsControl)))
            throw new InvalidDataException($"Choose a swap button style and a single line of up to {GameViewLayout.MaxSwapButtonTextLength} characters.");
        if (version == 1 && !(layout.Kind == GameViewKind.Corner || layout.Kind == GameViewKind.EqualSplit && layout.Characters.Length == 2))
            throw new InvalidDataException("Unsupported arrangement in the original layout file.");
        var screens = layout.ScreenPlans.ToArray();
        if (screens.Length == 0) throw new InvalidDataException("Select at least one screen for the layout.");
        var namedMonitors = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var monitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i] ?? throw new InvalidDataException("Choose a monitor for each screen in the layout.");
            string monitor = Label(screen.Monitor, "monitor");
            string? id = screen.MonitorId is null ? null : Label(screen.MonitorId, "monitor identity");
            // A disconnected panel and a connected replacement can share their
            // last Windows number. Only allow that when both have distinct IDs.
            if (namedMonitors.TryGetValue(monitor, out bool identified) && (!identified || id is null)
                || id is not null && !monitorIds.Add(id))
                throw new InvalidDataException("Choose a different monitor for each screen in the layout.");
            namedMonitors[monitor] = id is not null;
            GameViewGeometry.ValidateCount(screen.Kind, screen.Count);
            if (!Enum.IsDefined(screen.Edge)) throw new InvalidDataException("Choose a valid edge for the preview strip.");
            screens[i] = screen with { Monitor = monitor, MonitorId = id };
        }
        if (screens.Sum(s => (long)s.Count) != layout.Characters.Length)
            throw new InvalidDataException("Each screen slot needs an entry in the layout's character assignments.");
        if (layout.MasterSlot is { } master && (master < 0 || master >= layout.Characters.Length)
            || screens.Length > 1 && layout.SwapTargetIndex >= 0 && layout.MasterSlot is null)
            throw new InvalidDataException("Choose one master slot for swapping, or turn swapping off.");
        var characters = layout.Characters.Select(name => version != 1 && string.IsNullOrWhiteSpace(name)
            ? "" : Label(name, "character name")).ToArray();
        var names = characters.Where(name => name.Length > 0).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Assign each character to a different slot.");
        if (layout.Windows is { } windows && (windows.Length != characters.Length ||
            windows.OfType<GameViewWindow>().Any(w => w.ProcessId <= 0 || w.StartedUtcTicks <= 0 || w.Window == 0) ||
            windows.OfType<GameViewWindow>().Distinct().Count() != windows.Count(w => w is not null)))
            throw new InvalidDataException("Choose a different valid EQ window for each slot.");
        return layout with { Name = Label(layout.Name, "layout name"), Monitor = screens[0].Monitor, Kind = screens[0].Kind,
            Edge = screens[0].Edge, Screens = layout.Screens is null ? null : screens, Characters = characters,
            SwapButtonText = string.IsNullOrWhiteSpace(layout.SwapButtonText) ? null : layout.SwapButtonText.Trim() };
    }

    internal static int FileVersion(IEnumerable<GameViewLayout> layouts) =>
        layouts.Any(layout => layout.HasSwapButtonSettings) ? 4 :
        layouts.Any(layout => layout.Screens is not null || layout.MasterSlot.HasValue) ? 3 : 2;

    internal static GameViewLayout[] Load(string profile)
    {
        LayoutFile? file;
        try
        {
            using var stream = File.Open(PathFor(profile), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            file = JsonSerializer.Deserialize<LayoutFile>(stream, Program.Json);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException ex) { throw new InvalidDataException("The saved game layouts could not be read.", ex); }
        if (file is not { Version: 1 or 2 or 3 or 4, Layouts: not null }) throw new InvalidDataException("Unsupported game layout file.");
        var layouts = file.Layouts.Select(layout => Normalize(layout, file.Version)).ToArray();
        if (layouts.Select(layout => layout.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != layouts.Length)
            throw new InvalidDataException("The saved game layouts contain duplicate names.");
        return layouts;
    }

    internal static void Save(string profile, GameViewLayout layout) =>
        Write(profile, layout.Name, _ => layout);

    internal static GameViewLayout Update(string profile, string name, Func<GameViewLayout, GameViewLayout> update) =>
        Write(profile, name, current => update(current ?? throw new InvalidDataException("That layout is no longer saved.")));

    internal static GameViewLayout SaveEdit(string profile, GameViewLayout? original, GameViewLayout edited) =>
        Write(profile, edited.Name, current =>
        {
            if (original is null || !StringComparer.OrdinalIgnoreCase.Equals(original.Name, edited.Name))
            {
                if (current is not null) throw new InvalidDataException("A layout with that name already exists. Choose another name.");
            }
            else if (current is null || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(original))
                throw new InvalidDataException("This preset changed while you were editing. Save a copy under a new name, or cancel and reopen it.");
            return edited;
        });

    private static GameViewLayout Write(string profile, string name, Func<GameViewLayout?, GameViewLayout> edit)
    {
        string path = PathFor(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var guard = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var layouts = Load(profile).ToList();
        int index = layouts.FindIndex(existing => StringComparer.OrdinalIgnoreCase.Equals(existing.Name, name?.Trim()));
        var normalized = Normalize(edit(index < 0 ? null : layouts[index]));
        if (index < 0) layouts.Add(normalized);
        else layouts[index] = normalized;
        WindowLayouts.SaveJson(path, new LayoutFile(FileVersion(layouts), layouts.ToArray()));
        return normalized;
    }
}

internal static class GameViewGeometry
{
    internal static Rectangle[] Slots(GameViewLayout layout, Func<string, Rectangle?> boundsForMonitor)
        => CalculateSlots(layout, screen => boundsForMonitor(screen.Monitor));

    internal static Rectangle[] Slots(GameViewLayout layout, IReadOnlyList<GameViewMonitor> connected)
        => CalculateSlots(layout, screen =>
        {
            var monitor = GameViewMonitor.Find(screen, connected);
            return layout.AvoidTaskbar ? monitor?.WorkArea : monitor?.Bounds;
        });

    private static Rectangle[] CalculateSlots(GameViewLayout layout, Func<GameViewScreen, Rectangle?> boundsForScreen)
    {
        var slots = new List<Rectangle>(layout.Characters.Length);
        var regions = new List<Rectangle>();
        foreach (var screen in GameViewLayouts.Normalize(layout).ScreenPlans)
        {
            var bounds = boundsForScreen(screen) ?? throw new InvalidOperationException(
                $"Monitor {screen.Monitor} is disconnected. Reconnect it or choose another screen.");
            if (regions.Any(area => area.IntersectsWith(bounds)))
                throw new InvalidOperationException("Selected screens overlap. Choose distinct extended desktop displays.");
            slots.AddRange(Slots(screen.Kind, bounds, screen.Count, screen.Edge));
            regions.Add(bounds);
        }
        return slots.ToArray();
    }

    internal static string Label(GameViewKind kind, int count) => kind switch
    {
        GameViewKind.EqualSplit => count switch { 1 => "Full monitor", 2 => "50/50 · side by side", _ => "Equal columns" },
        GameViewKind.Rows => count == 2 ? "50/50 · stacked" : "Equal rows",
        GameViewKind.Grid => "Grid",
        GameViewKind.MainStrip => "Main + preview strip",
        GameViewKind.Corner => "Large + small corner",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static bool SupportsCount(GameViewKind kind, int count) => kind switch
    {
        GameViewKind.Corner => count is 2 or 3,
        GameViewKind.EqualSplit or GameViewKind.Rows => count is >= 1 and <= 6,
        GameViewKind.Grid or GameViewKind.MainStrip => count is >= 2 and <= 6,
        _ => false
    };

    internal static void ValidateCount(GameViewKind kind, int count)
    {
        if (!SupportsCount(kind, count)) throw new InvalidDataException($"The {kind} arrangement does not support {count} slots.");
    }

    private static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Choose a nonempty display region.");
    }

    // Calculate shared boundaries from the monitor origin so adjacent cells
    // match exactly on odd-sized or negative-origin displays.
    private static int Boundary(int start, int length, int index, int parts) =>
        start + (int)((long)length * index / parts);

    private static Rectangle Cell(Rectangle bounds, int column, int row, int columns, int rows) =>
        Rectangle.FromLTRB(Boundary(bounds.Left, bounds.Width, column, columns), Boundary(bounds.Top, bounds.Height, row, rows),
            Boundary(bounds.Left, bounds.Width, column + 1, columns), Boundary(bounds.Top, bounds.Height, row + 1, rows));

    internal static Rectangle[] Slots(GameViewKind kind, Rectangle bounds, int count, GameViewEdge edge = GameViewEdge.Bottom)
    {
        ValidateCount(kind, count);
        ValidateBounds(bounds);
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        var slots = new Rectangle[count];
        if (kind == GameViewKind.MainStrip)
        {
            int divisions = Math.Max(3, count - 1);
            bool vertical = edge is GameViewEdge.Left or GameViewEdge.Right;
            var main = bounds;
            var strip = bounds;
            if (vertical)
            {
                strip.Width = bounds.Width / divisions;
                main.Width -= strip.Width;
                if (edge == GameViewEdge.Left) main.X += strip.Width;
                else strip.X += main.Width;
            }
            else
            {
                strip.Height = bounds.Height / divisions;
                main.Height -= strip.Height;
                if (edge == GameViewEdge.Top) main.Y += strip.Height;
                else strip.Y += main.Height;
            }
            slots[0] = main;
            for (int i = 1; i < count; i++)
                slots[i] = vertical ? Cell(strip, 0, i - 1, 1, divisions) : Cell(strip, i - 1, 0, divisions, 1);
        }
        else if (kind == GameViewKind.Corner)
        {
            int width = bounds.Width / 3, height = bounds.Height / 3;
            slots[0] = new(bounds.Left, bounds.Top, bounds.Width - width, bounds.Height - height);
            for (int i = 1; i < count; i++)
                slots[i] = new(bounds.Left + (i - 1) * width, slots[0].Bottom, width, height);
        }
        else
        {
            int columns = kind switch { GameViewKind.Rows => 1, GameViewKind.Grid => (count + 1) / 2, _ => count };
            int rows = (count + columns - 1) / columns;
            for (int i = 0; i < count; i++) slots[i] = Cell(bounds, i % columns, i / columns, columns, rows);
        }
        if (slots.Any(slot => slot.Width <= 0 || slot.Height <= 0))
            throw new ArgumentOutOfRangeException(nameof(bounds), "The selected display is too small for this arrangement.");
        return slots;
    }
}
