using C = System.Windows.Controls;
using W = System.Windows;
namespace TeamBar;

public partial class GameViewLayoutWindow
{
    internal sealed record Display(string Device, Rectangle Bounds, Rectangle WorkArea, bool Primary = false, string? MonitorId = null)
        : GameViewMonitor(Device, Bounds, WorkArea, Primary, MonitorId)
    {
        public override string ToString() => $"{MonitorIdentification.Label(Device)} · {Bounds.Width} × {Bounds.Height}";
    }

    private sealed class ScreenDraft(GameViewScreen plan)
    {
        internal string Device = plan.Monitor;
        internal string? MonitorId = plan.MonitorId;
        internal Display? Display;
        internal int Count = plan.Count;
        internal GameViewKind Kind = plan.Kind;
        internal GameViewEdge Edge = plan.Edge;
        internal readonly string[] Characters = new string[6];
        internal readonly GameViewWindow?[] Windows = new GameViewWindow?[6];
        internal GameViewScreen Plan => new(Device, Kind, Count, Edge, MonitorId);
        internal bool HasAssignments => Characters.Any(name => !string.IsNullOrEmpty(name)) || Windows.Any(w => w is not null);

        internal void Use(Display? display)
        {
            Display = display;
            if (display is null) return;
            Device = display.Device;
            MonitorId = display.MonitorId;
        }

        public string Label => MonitorIdentification.Label(Device) + (Display is null ? " · disconnected" : "");
        public override string ToString() => Label;
    }

    // Saved screen order defines slot and master indices. The map is independent
    // of that order and uses the physical positions reported by Windows.
    private readonly List<ScreenDraft> screens = [];
    private readonly Func<Display[]> listDisplays;
    private Display[] displays = [];
    private ScreenDraft? editing;
    private bool explicitSwapTarget;
    private MonitorIdentification? monitorIdentification;

    private static Display[] ConnectedDisplays() => GameViewMonitor.Connected()
        .Select(s => new Display(s.Device, s.Bounds, s.WorkArea, s.Primary, s.MonitorId))
        .OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();

    private void LoadScreens(GameViewLayout? layout)
    {
        screens.Clear();
        editing = null;
        int offset = 0, localMaster = 0;
        ScreenDraft? master = null;
        if (layout is not null)
        {
            int savedMaster = layout.MasterSlot ?? layout.SwapTargetIndex;
            foreach (var plan in layout.ScreenPlans)
            {
                var screen = new ScreenDraft(plan);
                Array.Copy(layout.Characters, offset, screen.Characters, 0, plan.Count);
                if (layout.Windows is not null) Array.Copy(layout.Windows, offset, screen.Windows, 0, plan.Count);
                if (savedMaster >= offset && savedMaster < offset + plan.Count)
                {
                    master = screen;
                    localMaster = savedMaster - offset;
                }
                screens.Add(screen);
                offset += plan.Count;
            }
        }
        MatchDisplays();
        var initial = screens.FirstOrDefault(s => s.Count > 0) ?? screens.FirstOrDefault(s => s.Display?.Primary == true) ?? screens.FirstOrDefault();
        explicitSwapTarget = master is not null;
        SetMasterChoices(master, localMaster);
        ShowScreen(initial);
    }

    private void MatchDisplays()
    {
        var used = new HashSet<Display>();
        foreach (var screen in screens.OrderByDescending(s => s.Count > 0).ThenByDescending(s => s.MonitorId is not null))
        {
            var match = (Display?)GameViewMonitor.Find(screen.Plan, displays);
            screen.Use(match is not null && used.Add(match) ? match : null);
        }
        screens.RemoveAll(s => s.Display is null && s.Count == 0 && !s.HasAssignments);
        foreach (var display in displays.Where(d => !used.Contains(d)))
        {
            var screen = new ScreenDraft(new(display.Device, GameViewKind.EqualSplit, 0, MonitorId: display.MonitorId));
            screen.Use(display);
            screens.Add(screen);
        }
        IdentifyScreens.IsEnabled = displays.Length > 0;
    }

    private void SetMasterChoices(ScreenDraft? previous, int local)
    {
        var choices = screens.Where(s => s.Count > 0).ToArray();
        bool removed = previous is not null && (!choices.Contains(previous) || local >= previous.Count);
        if (explicitSwapTarget && removed)
        {
            explicitSwapTarget = false;
            if (UsesSwapTarget)
            {
                SwapOnClick.IsChecked = AddSwapButton.IsChecked = false;
                Message("The swap target slot was removed. Choose a new target to turn swapping back on.");
            }
        }
        var automatic = GameViewMonitor.DefaultSwapScreen(choices.Select(s => s.Display));
        var master = explicitSwapTarget ? previous : choices.FirstOrDefault(s => s.Display is not null && s.Display == automatic);
        MasterScreen.ItemsSource = choices;
        MasterScreen.SelectedItem = master;
        SetMasterSeats(master, master == previous && !removed ? local : 0);
    }

    private void SetMasterSeats(ScreenDraft? master, int local)
    {
        MasterSeat.ItemsSource = Enumerable.Range(1, master?.Count ?? 0);
        MasterSeat.SelectedIndex = master is null ? -1 : Math.Clamp(local, 0, master.Count - 1);
        MasterSeat.IsEnabled = master is not null;
    }

    private void ShowScreen(ScreenDraft? selected)
    {
        editing = selected;
        var offline = screens.Where(s => s.Display is null).ToArray();
        OfflinePanel.Visibility = offline.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        OfflineScreens.ItemsSource = offline;
        OfflineScreens.SelectedItem = selected?.Display is null ? selected : null;
        var available = screens.Where(s => s.Count == 0 && s.Display is not null && !s.HasAssignments)
            .Select(s => s.Display!).ToArray();
        var canMove = selected is { Display: null } && available.Length > 0;
        ReplacementPanel.Visibility = canMove ? W.Visibility.Visible : W.Visibility.Collapsed;
        ReplacementScreen.ItemsSource = canMove ? available : [];
        ReplacementScreen.SelectedIndex = canMove ? 0 : -1;
        EditingScreenTitle.Text = selected?.Display?.ToString() ?? selected?.Label ?? "No connected screens";
        Capacity.SelectedItem = selected?.Count;
        SetArrangements(selected?.Kind ?? GameViewKind.EqualSplit);
        Edge.SelectedItem = selected?.Edge ?? GameViewEdge.Bottom;
        for (int i = 0; i < SlotRows.Length; i++)
        {
            SlotRows[i].Character.Text = selected?.Characters[i] ?? "";
            SetWindowChoices(SlotRows[i], selected?.Windows[i]);
        }
    }

    private void CaptureScreen()
    {
        if (editing is null) return;
        if (Arrangement.SelectedItem is ArrangementChoice choice) editing.Kind = choice.Kind;
        editing.Edge = StripEdge;
        for (int i = 0; i < SlotRows.Length; i++)
        {
            editing.Characters[i] = SlotRows[i].Character.Text.Trim();
            editing.Windows[i] = ChosenWindow(SlotRows[i]);
        }
    }

    private void SelectScreen(ScreenDraft selected)
    {
        if (updating || selected == editing) return;
        CaptureScreen();
        updating = true;
        try { ShowScreen(selected); }
        finally { updating = false; }
        DesignPage.ScrollToTop();
        UpdateSlots();
    }

    private Rectangle SlotArea(ScreenDraft screen) =>
        (OverlapTaskbar.IsChecked == true ? screen.Display?.Bounds : screen.Display?.WorkArea) ?? new Rectangle(0, 0, 1920, 1080);

    private GameViewMapSlot[] PreviewSlots(ScreenDraft screen)
    {
        if (screen.Count == 0) return [];
        bool current = screen == editing;
        var areas = GameViewGeometry.Slots(current ? Kind : screen.Kind, SlotArea(screen), screen.Count, current ? StripEdge : screen.Edge);
        var slots = new GameViewMapSlot[areas.Length];
        int target = UsesSwapTarget && MasterScreen.SelectedItem == screen ? MasterSeat.SelectedIndex : -1;
        for (int i = 0; i < areas.Length; i++)
        {
            string label = (current ? SlotRows[i].Character.Text : screen.Characters[i])?.Trim() ?? "";
            var window = current ? ChosenWindow(SlotRows[i]) : screen.Windows[i];
            if (label.Length == 0 && window is not null)
            {
                var live = liveChoices.FirstOrDefault(c => c.Key == window);
                label = live?.Character ?? (live is not null && windowNumbers.TryGetValue(window, out int number)
                    ? $"Window {number}" : $"Closed window · PID {window.ProcessId}");
            }
            slots[i] = new(areas[i], label, i == target);
        }
        return slots;
    }

    private void DrawScreenMap(GameViewMapSlot[] selectedSlots)
    {
        bool needsTarget = UsesSwapTarget && MasterScreen.SelectedItem is null;
        SwapTargetHint.Visibility = needsTarget ? W.Visibility.Visible : W.Visibility.Collapsed;
        ScreenMap.Update(displays, editing?.Display?.Device, device =>
        {
            var screen = DraftForDevice(device);
            return screen is null ? [] : screen == editing ? selectedSlots : PreviewSlots(screen);
        });
    }

    private ScreenDraft? DraftForDevice(string device) =>
        screens.FirstOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Display?.Device, device));

    private void OfflineScreen_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (OfflineScreens.SelectedItem is ScreenDraft selected) SelectScreen(selected);
    }

    private void MoveToScreen_Click(object sender, W.RoutedEventArgs e)
    {
        if (editing is not { Display: null } || ReplacementScreen.SelectedItem is not Display target) return;
        var unused = screens.FirstOrDefault(s => s.Display == target && s.Count == 0 && !s.HasAssignments);
        if (unused is null) return;
        CaptureScreen();
        screens.Remove(unused);
        editing.Use(target);
        updating = true;
        try
        {
            SetMasterChoices((ScreenDraft?)MasterScreen.SelectedItem, MasterSeat.SelectedIndex);
            ShowScreen(editing);
        }
        finally { updating = false; }
        UpdateSlots();
    }

    private void Swap_Changed(object sender, W.RoutedEventArgs e)
    {
        if (updating || editing is null) return;
        if (UsesSwapTarget)
        {
            updating = true;
            try { SetMasterChoices((ScreenDraft?)MasterScreen.SelectedItem, MasterSeat.SelectedIndex); }
            finally { updating = false; }
        }
        else if (SwapOnClick.IsEnabled && MasterScreen.SelectedItem is not null && MasterSeat.SelectedIndex >= 0)
            explicitSwapTarget = true;
        UpdateSlots();
    }

    private void MasterScreen_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (updating || MasterScreen.SelectedItem is not ScreenDraft master) return;
        explicitSwapTarget = true;
        SetMasterSeats(master, 0);
        DrawPreview();
    }

    internal void RefreshDisplays()
    {
        var current = listDisplays();
        if (current.SequenceEqual(displays)) return;
        CaptureScreen();
        var master = (ScreenDraft?)MasterScreen.SelectedItem;
        int local = MasterSeat.SelectedIndex;
        displays = current;
        updating = true;
        try
        {
            MatchDisplays();
            SetMasterChoices(master, local);
            ShowScreen(editing is not null && screens.Contains(editing) ? editing : screens.FirstOrDefault());
        }
        finally { updating = false; }
        UpdateSlots();
    }

    private void IdentifyScreens_Click(object sender, W.RoutedEventArgs e) => Run(() =>
    {
        RefreshDisplays();
        monitorIdentification?.Dispose();
        monitorIdentification = new(displays, message => Message(message));
    });

    private GameViewLayout ReadScreens(string name)
    {
        CaptureScreen();
        var active = screens.Where(s => s.Count > 0).ToArray();
        if (active.Length == 0)
        {
            DesignStep.IsChecked = true;
            Capacity.Focus();
            throw new InvalidOperationException("Choose at least one character slot on a screen.");
        }
        var plans = active.Select(s => s.Plan).ToArray();
        var characters = active.SelectMany(s => s.Characters.Take(s.Count)).Select(c => c ?? "").ToArray();
        var windows = active.SelectMany(s => s.Windows.Take(s.Count)).ToArray();
        int? masterIndex = null;
        bool swapping = SwapOnClick.IsChecked == true;
        if ((UsesSwapTarget || explicitSwapTarget) && MasterScreen.SelectedItem is ScreenDraft master &&
            active.Contains(master) && MasterSeat.SelectedIndex >= 0 && MasterSeat.SelectedIndex < master.Count)
        {
            masterIndex = active.TakeWhile(s => s != master).Sum(s => s.Count) + MasterSeat.SelectedIndex;
        }
        else if (UsesSwapTarget)
        {
            DesignStep.IsChecked = true;
            MasterScreen.Focus();
            throw new InvalidOperationException("Choose a swap screen and slot.");
        }
        var first = plans[0];
        return new(name, first.Kind, first.Monitor, swapping, characters)
        {
            Screens = plans.Length > 1 || first.MonitorId is not null ? plans : null,
            MasterSlot = masterIndex,
            AddSwapButton = AddSwapButton.IsChecked == true,
            SwapButtonStyle = ButtonAppearance.ButtonStyle,
            SwapButtonText = ButtonAppearance.ButtonText,
            Windows = windows.Any(w => w is not null) ? windows : null,
            Edge = first.Edge,
            AvoidTaskbar = OverlapTaskbar.IsChecked != true,
            MatchMasterResolution = MatchMasterResolution.IsChecked == true,
            HideTitleBars = HideTitleBars.IsChecked == true,
            AlwaysOnTop = AlwaysOnTop.IsChecked == true
        };
    }
}
