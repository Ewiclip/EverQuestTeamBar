using System.Windows.Automation;
using C = System.Windows.Controls;
using W = System.Windows;
namespace TeamBar;

public partial class GameViewLayoutWindow : W.Window
{
    public static string FixedPreviewClickHint => ScreenInstallation.Same(ScreenInstallation.Current, ScreenInstallation.Original)
        ? "A single left or right click goes to the game in that preview. The pointer may move briefly. Use the master slot to drag."
        : "For this EQ folder, use the master slot for clicks or turn off resolution matching to play directly in each slot.";

    private sealed record ArrangementChoice(GameViewKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly string profile;
    private readonly Func<DetectedClient[]> knownClients;
    private readonly Func<Discovery> refreshRoster;
    private readonly TeamBarApplication host;
    private bool updating;
    private int SlotCount => Capacity.SelectedItem is int count ? count : 0;
    private bool UsesSwapTarget => SwapOnClick.IsChecked == true || AddSwapButton.IsChecked == true;
    private GameViewKind Kind => (Arrangement.SelectedItem as ArrangementChoice)?.Kind ?? GameViewKind.EqualSplit;
    private GameViewEdge StripEdge => Edge.SelectedItem is GameViewEdge edge ? edge : GameViewEdge.Bottom;

    internal GameViewLayoutWindow(SetupWindow owner, string profilePath, Func<IEnumerable<string>> characterNames,
        Func<DetectedClient[]> knownClients, TeamBarApplication host, Func<GameSession[]> listWindows,
        Func<Discovery>? refreshRoster = null, string? initialLayout = null, bool createNew = false, Func<Display[]>? displayProvider = null)
    {
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
        InitializeComponent();
        SizeChanged += (_, _) => ScreenMap.Height = Math.Clamp(110 + (ActualHeight - MinHeight) * 0.18, 110, 178);
        Icon = AppBranding.WindowIcon;
        this.profile = profilePath;
        this.characterNames = characterNames;
        this.knownClients = knownClients;
        this.refreshRoster = refreshRoster ?? (() => new Discovery(knownClients(), []));
        this.listWindows = listWindows;
        this.host = host;
        listDisplays = displayProvider ?? ConnectedDisplays;
        displays = listDisplays();
        ScreenMap.ScreenSelected += device =>
        {
            if (DraftForDevice(device) is { } screen) SelectScreen(screen);
        };

        CreateSlotRows();
        Capacity.ItemsSource = Enumerable.Range(0, SlotRows.Length + 1);
        Edge.ItemsSource = Enum.GetValues<GameViewEdge>();
        var saved = GameViewLayouts.Load(profile);
        SavedLayouts.ItemsSource = saved;
        RefreshWindows();
        var requested = initialLayout ?? host.ActiveGameViewName;
        var initial = saved.FirstOrDefault(l => l.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (!createNew && initial is null && initialLayout is not null)
            throw new InvalidOperationException($"The saved layout “{initialLayout}” is no longer available.");
        initial ??= saved.FirstOrDefault();
        if (createNew || initial is null) LoadLayout(null);
        else SavedLayouts.SelectedItem = initial;
        host.GameViewChanged += Host_Changed;
        windowTimer.Tick += (_, _) => Run(() => { RefreshDisplays(); RefreshWindows(); });
        windowTimer.Start();
        Closed += (_, _) => { windowTimer.Stop(); identification?.Dispose(); monitorIdentification?.Dispose(); host.GameViewChanged -= Host_Changed; };
        RefreshActive();
        Topmost = true;
    }

    private void LoadLayout(GameViewLayout? layout)
    {
        updating = true;
        try
        {
            LayoutName.Text = layout?.Name ?? "";
            SwapOnClick.IsChecked = layout?.SwapOnClick ?? false;
            AddSwapButton.IsChecked = layout?.AddSwapButton == true;
            ButtonAppearance.Load(layout?.SwapButtonStyle ?? GameViewSwapButtonStyle.CharacterName, layout?.SwapButtonText ?? "");
            MatchMasterResolution.IsChecked = layout?.MatchMasterResolution ?? true;
            HideTitleBars.IsChecked = layout?.HideTitleBars == true;
            OverlapTaskbar.IsChecked = layout?.AvoidTaskbar != true;
            AlwaysOnTop.IsChecked = layout?.AlwaysOnTop == true;
            LoadScreens(layout);
            DesignPage.ScrollToTop();
            if (layout is not null && layout.Name.Equals(host.ActiveGameViewName, StringComparison.OrdinalIgnoreCase))
                AssignStep.IsChecked = true;
            else DesignStep.IsChecked = true;
            Feedback.Visibility = W.Visibility.Collapsed;
        }
        finally { updating = false; }
        UpdateSlots();
    }

    private void SetArrangements(GameViewKind preferred)
    {
        var kinds = new[]
        {
            GameViewKind.EqualSplit, GameViewKind.Rows, GameViewKind.Grid, GameViewKind.MainStrip, GameViewKind.Corner
        };
        var choices = kinds.Where(kind => GameViewGeometry.SupportsCount(kind, SlotCount) &&
            (SlotCount != 1 || kind == GameViewKind.EqualSplit || kind == preferred))
            .Select(kind => new ArrangementChoice(kind, GameViewGeometry.Label(kind, SlotCount))).ToArray();
        Arrangement.ItemsSource = choices;
        Arrangement.SelectedItem = choices.FirstOrDefault(c => c.Kind == preferred) ?? choices.FirstOrDefault();
    }

    private void UpdateSlots()
    {
        bool hasSlots = editing is not null && SlotCount > 0;
        Capacity.IsEnabled = editing is not null;
        EmptyScreenHint.Visibility = editing is not null && !hasSlots ? W.Visibility.Visible : W.Visibility.Collapsed;
        SeatDesign.Visibility = hasSlots ? W.Visibility.Visible : W.Visibility.Collapsed;
        AssignStep.IsEnabled = AssignSlots.IsEnabled = hasSlots;
        if (!hasSlots) DesignStep.IsChecked = true;
        EdgeOptions.Visibility = Kind == GameViewKind.MainStrip ? W.Visibility.Visible : W.Visibility.Collapsed;
        SwapOnClick.IsEnabled = AddSwapButton.IsEnabled = screens.Sum(screen => screen.Count) > 1;
        if (!SwapOnClick.IsEnabled) SwapOnClick.IsChecked = AddSwapButton.IsChecked = false;
        SwapOnClick.ToolTip = SwapOnClick.IsEnabled ? "Click a game preview to swap it into the starred slot."
            : "Configure at least two slots to enable swapping.";
        MasterOptions.Visibility = UsesSwapTarget ? W.Visibility.Visible : W.Visibility.Collapsed;
        ButtonAppearance.Visibility = AddSwapButton.IsChecked == true ? W.Visibility.Visible : W.Visibility.Collapsed;
        for (int i = 0; i < SlotRows.Length; i++)
            SlotRows[i].Visibility = i < SlotCount ? W.Visibility.Visible : W.Visibility.Collapsed;
        RefreshSlotStatus();
        DrawPreview();
    }

    private void DrawPreview()
    {
        if (updating || Diagram is null) return;
        var slots = editing is null ? [] : PreviewSlots(editing);
        DrawScreenMap(slots);
        Diagram.Children.Clear();
        if (editing is null || slots.Length == 0) return;
        var area = editing.Display?.Bounds ?? SlotArea(editing);
        // Draw at the preview's displayed height so a portrait screen keeps readable slot labels.
        double scale = Math.Min(640d / area.Width, 150d / area.Height);
        Diagram.Width = area.Width * scale;
        Diagram.Height = area.Height * scale;
        for (int i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            var rect = slot.Bounds;
            var row = SlotRows[i];
            string seat = slot.Label.Length == 0 ? (i + 1).ToString() : $"{i + 1} · {slot.Label}";
            string description = $"Slot {seat}" + (slot.SwapTarget ? " · Swap target" : "");
            var label = new C.TextBlock { Text = seat, TextTrimming = W.TextTrimming.CharacterEllipsis,
                VerticalAlignment = W.VerticalAlignment.Center, TextAlignment = W.TextAlignment.Center,
                MaxWidth = Math.Max(0, rect.Width * scale - (slot.SwapTarget ? 24 : 8)) };
            var button = new C.Button { Width = rect.Width * scale, Height = rect.Height * scale, MinHeight = 0, MinWidth = 0,
                Padding = new W.Thickness(0), FontSize = 12,
                ToolTip = $"{description} · {row.Status.Text}" };
            button.SetResourceReference(C.Button.TemplateProperty, "ScreenMapTile");
            button.SetResourceReference(C.Button.BackgroundProperty, slot.SwapTarget ? "SwapTargetSurface" : row.Clear.IsEnabled ? "BrandSurface" : "Field");
            button.SetResourceReference(C.Button.BorderBrushProperty, slot.SwapTarget ? "SwapTargetBorder" : row.Clear.IsEnabled ? "Accent" : "Line");
            if (slot.SwapTarget)
            {
                label.SetResourceReference(C.TextBlock.ForegroundProperty, "SwapTargetStar");
                var badge = new C.StackPanel { Orientation = C.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Center,
                    VerticalAlignment = W.VerticalAlignment.Center };
                var star = GameViewScreenMap.SwapStar(Math.Min(13, Math.Min(button.Width, button.Height) * 0.65));
                star.Margin = new W.Thickness(0, 0, 3, 0);
                badge.Children.Add(star);
                badge.Children.Add(label);
                button.Content = badge;
            }
            else button.Content = label;
            AutomationProperties.SetName(button, $"Assign {editing}, {description}");
            button.Click += (_, _) =>
            {
                AssignStep.IsChecked = true;
                row.BringIntoView();
                row.Character.Focus();
            };
            C.Canvas.SetLeft(button, (rect.Left - area.Left) * scale);
            C.Canvas.SetTop(button, (rect.Top - area.Top) * scale);
            Diagram.Children.Add(button);
        }
    }

    private void Saved_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!updating && SavedLayouts.SelectedItem is GameViewLayout layout) LoadLayout(layout);
    }

    private void New_Click(object sender, W.RoutedEventArgs e)
    {
        SavedLayouts.SelectedItem = null;
        LoadLayout(null);
        LayoutName.Focus();
    }

    private void Capacity_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (updating || editing is null || Capacity.SelectedItem is not int count) return;
        CaptureScreen();
        updating = true;
        try
        {
            editing.Count = count;
            SetArrangements(editing.Kind);
            SetMasterChoices((ScreenDraft?)MasterScreen.SelectedItem, MasterSeat.SelectedIndex);
        }
        finally { updating = false; }
        UpdateSlots();
    }

    private void Arrangement_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (updating || Arrangement.SelectedItem is null) return;
        UpdateSlots();
    }

    private void Step_Changed(object sender, W.RoutedEventArgs e)
    {
        if (DesignPage is null || AssignmentsPage is null) return;
        bool assignments = AssignStep.IsChecked == true;
        AssignmentsPage.Visibility = assignments ? W.Visibility.Visible : W.Visibility.Collapsed;
        DesignPage.Visibility = assignments ? W.Visibility.Collapsed : W.Visibility.Visible;
    }

    private void Assign_Click(object sender, W.RoutedEventArgs e) => AssignStep.IsChecked = true;
    private void Preview_Changed(object sender, W.RoutedEventArgs e)
    {
        if (updating) return;
        if (sender == MasterSeat && UsesSwapTarget && MasterSeat.SelectedIndex >= 0)
            explicitSwapTarget = true;
        DrawPreview();
    }

    private GameViewLayout ReadLayout()
    {
        string name = LayoutName.Text.Trim();
        if (name.Length == 0)
        {
            DesignStep.IsChecked = true;
            LayoutName.Focus();
            throw new InvalidOperationException("Enter a layout name.");
        }
        return ReadScreens(name);
    }

    private void SaveLayout(GameViewLayout layout)
    {
        bool sameSelection = SavedLayouts.SelectedItem is GameViewLayout selected && StringComparer.OrdinalIgnoreCase.Equals(selected.Name, layout.Name);
        if (!sameSelection && GameViewLayouts.Load(profile).Any(l => StringComparer.OrdinalIgnoreCase.Equals(l.Name, layout.Name)))
            throw new InvalidOperationException("That layout already exists. Select it from Saved layouts to edit it, or use another name.");
        GameViewLayouts.Save(profile, layout);
        explicitSwapTarget = layout.MasterSlot.HasValue;
        var saved = GameViewLayouts.Load(profile);
        updating = true;
        try
        {
            SavedLayouts.ItemsSource = saved;
            SavedLayouts.SelectedItem = saved.Single(l => StringComparer.OrdinalIgnoreCase.Equals(l.Name, layout.Name));
        }
        finally { updating = false; }
    }

    private void Save_Click(object sender, W.RoutedEventArgs e) => Run(() =>
    {
        var layout = ReadLayout();
        SaveLayout(layout);
        Message($"Saved {layout.Name}.");
    });

    private void Apply_Click(object sender, W.RoutedEventArgs e) => Run(() =>
    {
        var layout = ReadLayout();
        host.ApplyProfileLayout(profile, layout, listWindows, refreshRoster, () => SaveLayout(layout));
        DialogResult = true;
    });

    private void End_Click(object sender, W.RoutedEventArgs e) => Run(() =>
    {
        host.EndGameView();
        Feedback.Visibility = W.Visibility.Collapsed;
    });

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { Message(error.Message, error: true); }
    }

    private void Message(string text, bool error = false)
    {
        Feedback.Text = text;
        Feedback.SetResourceReference(C.TextBlock.ForegroundProperty, error ? "Error" : "Success");
        Feedback.Visibility = W.Visibility.Visible;
    }

    private void Host_Changed(object? sender, EventArgs e) => RefreshActive();

    private void RefreshActive()
    {
        ActiveStatus.Text = host.GameViewStatus;
        ActiveStatus.Visibility = ActiveStatus.Text.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        EndLayout.Visibility = host.ActiveGameViewName is null ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
}
