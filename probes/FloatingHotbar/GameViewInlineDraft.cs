namespace TeamBar;

// The editor addresses slots by screen and local index. Flatten them only when
// saving, so changing one screen's capacity doesn't move another screen's seats.
internal sealed class GameViewInlineDraft
{
    internal sealed class Screen(GameViewScreen plan)
    {
        internal GameViewScreen Plan { get; private set; } = plan;
        internal string Device => Plan.Monitor;
        internal int Count => Plan.Count;
        internal GameViewKind Kind => Plan.Kind;
        internal GameViewEdge Edge => Plan.Edge;
        internal GameViewMonitor? Display { get; private set; }
        public string Label => MonitorIdentification.Label(Device) + (Display is null ? " · disconnected" : "");
        public override string ToString() => Label;
        internal string[] Characters { get; private set; } = Enumerable.Repeat("", plan.Count).ToArray();
        internal GameViewWindow?[] Windows { get; private set; } = new GameViewWindow?[plan.Count];

        internal void Connect(GameViewMonitor? display)
        {
            Display = display;
            if (display is not null) Plan = Plan with { Monitor = display.Device, MonitorId = display.MonitorId };
        }

        internal void Resize(int count, GameViewKind kind, GameViewEdge edge)
        {
            if (count != Count)
            {
                var names = Enumerable.Repeat("", count).ToArray();
                var windows = new GameViewWindow?[count];
                Array.Copy(Characters, names, Math.Min(count, Count));
                Array.Copy(Windows, windows, Math.Min(count, Count));
                Characters = names;
                Windows = windows;
            }
            Plan = Plan with { Count = count, Kind = kind, Edge = edge };
        }
    }

    private readonly List<Screen> screens = [];
    internal IReadOnlyList<Screen> Screens => screens;
    private Screen? chosenMaster;
    private int chosenSeat;
    private bool swapping;
    private bool addSwapButton;
    private bool UsesSwapTarget => swapping || addSwapButton;
    internal GameViewSwapButtonStyle SwapButtonStyle { get; set; }
    internal string? SwapButtonText { get; set; }
    internal int TotalSlots => screens.Sum(s => s.Count);
    internal bool AvoidTaskbar { get; set; }
    internal bool AlwaysOnTop { get; set; }
    internal bool MatchMasterResolution { get; set; }
    internal bool HideTitleBars { get; set; }

    internal GameViewInlineDraft(GameViewLayout? layout, IReadOnlyList<GameViewMonitor> monitors)
    {
        AvoidTaskbar = layout?.AvoidTaskbar ?? false;
        AlwaysOnTop = layout?.AlwaysOnTop ?? false;
        MatchMasterResolution = layout?.MatchMasterResolution ?? true;
        HideTitleBars = layout?.HideTitleBars == true;
        if (layout is not null)
        {
            swapping = layout.SwapOnClick;
            addSwapButton = layout.AddSwapButton;
            SwapButtonStyle = layout.SwapButtonStyle;
            SwapButtonText = layout.SwapButtonText;
            int destination = layout.MasterSlot ?? layout.SwapTargetIndex;
            int offset = 0;
            foreach (var plan in layout.ScreenPlans)
            {
                var screen = new Screen(plan);
                screens.Add(screen);
                Array.Copy(layout.Characters, offset, screen.Characters, 0, plan.Count);
                if (layout.Windows is { } windows) Array.Copy(windows, offset, screen.Windows, 0, plan.Count);
                if (destination >= offset && destination < offset + plan.Count)
                {
                    chosenMaster = screen;
                    chosenSeat = destination - offset;
                }
                offset += plan.Count;
            }
        }
        RefreshDisplays(monitors);
    }

    internal void RefreshDisplays(IReadOnlyList<GameViewMonitor> monitors)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A saved physical identity wins over a legacy Windows display number.
        foreach (var screen in screens.OrderByDescending(s => s.Count > 0).ThenByDescending(s => s.Plan.MonitorId is not null))
        {
            var match = GameViewMonitor.Find(screen.Plan, monitors);
            screen.Connect(match is not null && claimed.Add(match.Device) ? match : null);
        }
        screens.RemoveAll(s => s.Count == 0 && s.Display is null);
        foreach (var display in monitors.Where(m => !claimed.Contains(m.Device)))
        {
            var screen = new Screen(new(display.Device, GameViewKind.EqualSplit, 0, MonitorId: display.MonitorId));
            screen.Connect(display);
            screens.Add(screen);
        }
    }

    internal bool SwapOnClick
    {
        get => swapping;
        set
        {
            if (swapping && !value) chosenMaster ??= MasterScreen;
            swapping = value;
        }
    }

    internal bool AddSwapButton
    {
        get => addSwapButton;
        set
        {
            if (addSwapButton && !value) chosenMaster ??= MasterScreen;
            addSwapButton = value;
        }
    }

    internal Screen? MasterScreen
    {
        get
        {
            if (chosenMaster is not null || !UsesSwapTarget) return chosenMaster;
            var automatic = GameViewMonitor.DefaultSwapScreen(screens.Where(s => s.Count > 0).Select(s => s.Display));
            return automatic is null ? null : screens.First(s => Equals(s.Display, automatic));
        }
    }

    internal int MasterSeat => MasterScreen is null ? -1 : chosenSeat;

    internal void MoveScreen(Screen offline, Screen unused)
    {
        RequireScreen(offline);
        RequireScreen(unused);
        if (offline.Display is not null || unused.Count != 0 || unused.Display is null)
            throw new InvalidOperationException("Choose a disconnected screen and an unused connected monitor.");
        screens.Remove(unused);
        offline.Connect(unused.Display);
    }

    internal void Configure(Screen screen, int count, GameViewKind kind, GameViewEdge edge)
    {
        RequireScreen(screen);
        if (count is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(count));
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(edge)) throw new ArgumentException("Choose a valid screen arrangement and edge.");
        if (!GameViewGeometry.SupportsCount(kind, count)) kind = GameViewKind.EqualSplit;
        screen.Resize(count, kind, edge);
        if (chosenMaster == screen && chosenSeat >= count)
        {
            chosenMaster = null;
            chosenSeat = 0;
            swapping = false;
            addSwapButton = false;
        }
        if (TotalSlots < 2) { SwapOnClick = false; AddSwapButton = false; }
    }

    internal void SetMaster(Screen screen, int slot)
    {
        RequireSlot(screen, slot);
        if (TotalSlots < 2) throw new InvalidOperationException("Configure at least two slots to enable swapping.");
        chosenMaster = screen;
        chosenSeat = slot;
        if (!UsesSwapTarget) swapping = true;
    }

    private void RequireScreen(Screen screen)
    {
        if (!screens.Contains(screen)) throw new InvalidOperationException("That screen is no longer in the layout.");
    }

    private void RequireSlot(Screen screen, int slot)
    {
        RequireScreen(screen);
        if (slot < 0 || slot >= screen.Count) throw new InvalidOperationException("That slot is no longer in the layout.");
    }

    internal void Clear(Screen screen, int slot)
    {
        RequireSlot(screen, slot);
        screen.Characters[slot] = "";
        screen.Windows[slot] = null;
    }

    internal void Assign(Screen screen, int slot, string? character, GameViewWindow? window = null,
        IReadOnlyDictionary<GameViewWindow, string>? liveCharacters = null)
    {
        RequireSlot(screen, slot);
        string name = character?.Trim() ?? "";
        if (name.Any(char.IsControl)) throw new InvalidDataException("Choose a valid character name.");
        if (window is not null && (window.ProcessId <= 0 || window.StartedUtcTicks <= 0 || window.Window == 0))
            throw new InvalidDataException("Choose a valid EQ window.");
        if (window is null && name.Length > 0 && liveCharacters is not null)
            ReleaseConflictingBindings(name, liveCharacters);
        foreach (var other in screens)
        {
            for (int i = 0; i < other.Count; i++)
            {
                bool sameName = name.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(other.Characters[i], name);
                bool sameWindow = window is not null && other.Windows[i] == window;
                if (!sameName && !sameWindow) continue;
                // Either half of an existing manual assignment can be dragged
                // from the palette. Carry its partner unless a new one was supplied.
                window ??= other.Windows[i];
                if (name.Length == 0) name = other.Characters[i];
                other.Characters[i] = "";
                other.Windows[i] = null;
            }
        }
        screen.Characters[slot] = name;
        screen.Windows[slot] = window;
    }

    private void ReleaseConflictingBindings(string name, IReadOnlyDictionary<GameViewWindow, string> live)
    {
        var sameName = StringComparer.OrdinalIgnoreCase;
        var matches = live.Where(pair => sameName.Equals(pair.Value, name)).Take(2).ToArray();
        GameViewWindow? unique = matches.Length == 1 ? matches[0].Key : null;
        foreach (var screen in screens)
        {
            for (int i = 0; i < screen.Count; i++)
            {
                if (screen.Windows[i] is not { } window || !live.TryGetValue(window, out var current)) continue;
                bool assignedByName = sameName.Equals(screen.Characters[i], name);
                // A camp or character switch can leave an old window binding on
                // a different saved name. Keep that name as an offline reservation.
                if (assignedByName ? !sameName.Equals(current, name) : window == unique)
                    screen.Windows[i] = null;
            }
        }
    }

    internal GameViewMapSlot[] PreviewSlots(Screen screen, Func<GameViewWindow, string>? windowLabel = null)
    {
        if (screen.Count == 0) return [];
        Rectangle area = screen.Display is { } display ? (AvoidTaskbar ? display.WorkArea : display.Bounds) : new(0, 0, 1920, 1080);
        var bounds = GameViewGeometry.Slots(screen.Kind, area, screen.Count, screen.Edge);
        int star = UsesSwapTarget && MasterScreen == screen ? chosenSeat : -1;
        return bounds.Select((rect, i) =>
        {
            string name = screen.Characters[i];
            var window = screen.Windows[i];
            string label = name.Length == 0 && window is not null ? windowLabel?.Invoke(window) ?? $"EQ window {window.ProcessId}" : name;
            var assignment = name.Length == 0 && window is null ? null : new GameViewMapToken(name.Length > 0 ? name : null, window);
            return new GameViewMapSlot(rect, label, i == star, assignment);
        }).ToArray();
    }

    internal GameViewLayout Build(string name)
    {
        var active = screens.Where(s => s.Count > 0).ToArray();
        if (active.Length == 0) throw new InvalidOperationException("Choose at least one character slot on a screen.");
        var master = MasterScreen;
        if (UsesSwapTarget && master is null) throw new InvalidOperationException("Place the star on a slot to choose the swap destination.");
        var first = active[0].Plan;
        var windows = active.SelectMany(s => s.Windows).ToArray();
        return GameViewLayouts.Normalize(new(name, first.Kind, first.Monitor, swapping, active.SelectMany(s => s.Characters).ToArray())
        {
            Edge = first.Edge, Screens = active.Length > 1 || first.MonitorId is not null ? active.Select(s => s.Plan).ToArray() : null,
            MasterSlot = master is null ? null : active.TakeWhile(s => s != master).Sum(s => s.Count) + chosenSeat,
            Windows = windows.Any(w => w is not null) ? windows : null,
            AvoidTaskbar = AvoidTaskbar, AlwaysOnTop = AlwaysOnTop,
            MatchMasterResolution = MatchMasterResolution, HideTitleBars = HideTitleBars,
            AddSwapButton = AddSwapButton, SwapButtonStyle = SwapButtonStyle, SwapButtonText = SwapButtonText
        });
    }
}
