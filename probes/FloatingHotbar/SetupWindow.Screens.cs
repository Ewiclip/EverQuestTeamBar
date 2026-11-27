using W = System.Windows;
using C = System.Windows.Controls;
using D = System.Windows.Documents;

namespace TeamBar;

public partial class SetupWindow
{
    private TeamBarApplication? screensHost;
    private MonitorIdentification? monitorNumbers;
    private bool loadingScreenOptions;
    private string? fixedSlotOffer;
    private readonly Func<GameSession, bool> requiresWindowedMode;

    private void ShowScreenFolder(string selected)
    {
        ScreenFolderPath.Text = selected;
        var notices = new List<string>();
        try { ScreenInstallation.Validate(selected); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        { notices.Add(error.Message); }
        if (!ScreenInstallation.Same(ScreenInstallation.Current, selected))
            notices.Add("Exit Team Bar from the tray and reopen it to use this folder for layouts.");
        if (!ScreenInstallation.Same(ScreenInstallation.Original, selected))
            notices.Add($"Assign numbered windows to layout slots. Hotkeys, spell input and memory-based features require {ScreenInstallation.Original}.");
        ScreenFolderNotice.Text = string.Join(" ", notices);
        ScreenFolderNotice.Visibility = notices.Count == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
    }

    private void ChooseScreenFolder_Click(object sender, W.RoutedEventArgs e)
    {
        if (review || settingsStore is not LocalUserSettingsStore localStore) return;
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your EverQuest Live folder" };
            if (Directory.Exists(ScreenFolderPath.Text)) dialog.InitialDirectory = ScreenFolderPath.Text;
            if (dialog.ShowDialog(this) != true) return;
            var folder = ScreenInstallation.Normalize(dialog.FolderName);
            localStore.SaveScreenDirectory(folder);
            settings = localStore.Load();
            ShowScreenFolder(folder);
            ScreenFeedback("");
        }
        catch (Exception error) { ScreenFeedback(error.Message, error: true); }
    }

    private void IdentifyScreens_Click(object sender, W.RoutedEventArgs e)
    {
        monitorNumbers?.Dispose();
        monitorNumbers = new MonitorIdentification(GameViewMonitor.Connected(),
            message => ScreenFeedback(message));
    }

    private void ScreensVisibility_Changed(object sender, W.DependencyPropertyChangedEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        monitorNumbers?.Dispose();
        monitorNumbers = null;
        inlineIdentification?.Dispose();
        inlineIdentification = null;
        if (screensHost is not null) screensHost.GameViewChanged -= ScreenLayoutStateChanged;
        screensHost = null;
        SetScreenWindowedWarning("");
        if (!ScreensView.IsVisible) return;
        ChooseScreenFolder.IsEnabled = !review && settingsStore is LocalUserSettingsStore;
        string? saved = settings.ScreenGameDirectory;
        ShowScreenFolder(EqIconInstallation.IsLocalPath(saved) ? saved! : ScreenInstallation.Current);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        screensHost = review ? null : TeamBarApplication.Current;
        if (screensHost is not null) screensHost.GameViewChanged += ScreenLayoutStateChanged;
        ScreenFeedback("");
        var activeName = screensHost?.ActiveGameViewName;
        LoadScreenLayouts(activeName is not null && screensHost?.IsActiveGameView(profilePath, activeName) == true ? activeName : null);
        if (inlineDraft is not null) RefreshInlineDisplays();
        RefreshScreenLayoutStatus();
    }

    private void LoadScreenLayouts(string? preferred = null)
    {
        preferred ??= (ScreenLayoutPicker.SelectedItem as GameViewLayout)?.Name ?? screensHost?.ActiveGameViewName;
        try
        {
            var saved = GameViewLayouts.Load(profilePath);
            ScreenLayoutPicker.ItemsSource = saved;
            ScreenLayoutPicker.SelectedItem = saved.FirstOrDefault(layout => layout.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
                saved.FirstOrDefault();
            EmptyScreenLayouts.Visibility = saved.Length == 0 && inlineDraft is null ? W.Visibility.Visible : W.Visibility.Collapsed;
            SavedScreenLayoutsPanel.Visibility = saved.Length == 0 && inlineDraft is null ? W.Visibility.Collapsed : W.Visibility.Visible;
            NewScreenLayout.IsEnabled = screensHost is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ScreenLayoutPicker.ItemsSource = null;
            EmptyScreenLayouts.Visibility = SavedScreenLayoutsPanel.Visibility = W.Visibility.Collapsed;
            NewScreenLayout.IsEnabled = false;
            ScreenFeedback(error.Message, error: true);
        }
    }

    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (!ScreensView.IsVisible) return;
        if (inlineDraft is not null) RefreshInlineDisplays();
        else ShowSelectedScreenLayout();
    });

    private void ScreenLayoutSelection_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        ShowSelectedScreenLayout();
        RefreshScreenWindowedWarning();
    }

    // Return whether an open fullscreen window could need a fresh character lookup.
    // The existing roster monitor can then verify named assignments even on a same-window relog.
    private bool RefreshScreenWindowedWarning(GameSession[]? windows = null)
    {
        if (screensHost is null || inlineDraft is not null || ScreenLayoutPicker.SelectedItem is not GameViewLayout layout)
        {
            SetScreenWindowedWarning("");
            return false;
        }
        try
        {
            windows ??= layoutWindows();
            var fullscreen = windows.Where(requiresWindowedMode).Select(GameViewWindow.From).ToHashSet();
            if (fullscreen.Count == 0)
            {
                SetScreenWindowedWarning("");
                return false;
            }
            var roster = clients.Select(c => c.Client).ToArray();
            var seats = layout.Resolve(windows, roster, screensHost.PreviousLayoutSeats(layout));
            var names = new List<string>();
            bool hasNamedLookup = false;
            for (int i = 0; i < seats.Length; i++)
            {
                var seat = seats[i];
                var key = seat is null ? null : GameViewWindow.From(seat);
                bool pinned = key is not null && layout.Windows?[i] == key;
                hasNamedLookup |= !pinned && layout.Characters[i].Length > 0;
                if (key is null || !fullscreen.Contains(key)) continue;
                if (pinned) fullscreen.Remove(key);
                var name = InlineWindowCharacter(key) ?? layout.Characters[i];
                names.Add(name.Length > 0 ? name : $"Slot {i + 1} (EQ window {key.ProcessId})");
            }
            SetScreenWindowedWarning(names.Count > 0 ? GameViewWindowedModeException.Describe(names) : "");
            return hasNamedLookup && fullscreen.Count > 0;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // A client can disappear between enumeration and reading its window style.
            // Applying a layout independently verifies every window again.
            SetScreenWindowedWarning("");
            return false;
        }
    }

    private void SetScreenWindowedWarning(string message)
    {
        if (ScreenWindowedWarning.Text == message) return;
        ScreenWindowedWarning.Text = message;
        ScreenWindowedWarning.Visibility = message.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
    }

    private static GameViewMonitor?[] MatchScreenPlans(GameViewScreen[] screens, GameViewMonitor[] connected)
    {
        // A renumbered panel can also match a legacy name-only entry. Give its
        // physical ID first claim so the preview shows each real display once.
        var monitors = new GameViewMonitor?[screens.Length];
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int group in Enumerable.Range(0, screens.Length).OrderByDescending(i => screens[i].MonitorId is not null))
        {
            if (GameViewMonitor.Find(screens[group], connected) is { } match && claimed.Add(match.Device))
                monitors[group] = match;
        }
        return monitors;
    }

    private void ShowSelectedScreenLayout()
    {
        if (inlineDraft is not null || ScreenLayoutPicker.SelectedItem is not GameViewLayout layout) return;
        if (fixedSlotOffer is not null && !layout.Name.Equals(fixedSlotOffer, StringComparison.OrdinalIgnoreCase))
            ScreenFeedback("");
        var screens = layout.ScreenPlans;
        bool multiple = screens.Length > 1;
        var connected = GameViewMonitor.Connected();
        var monitors = MatchScreenPlans(screens, connected);
        var labels = screens.Select((screen, group) => monitors[group] is { } current
            ? MonitorIdentification.Label(current.Device)
            : $"{MonitorIdentification.Label(screen.Monitor)} (disconnected)").ToArray();
        var preview = new Dictionary<string, GameViewMapSlot[]>(StringComparer.OrdinalIgnoreCase);
        string ScreenHeading(int index) =>
            $"{GameViewGeometry.Label(screens[index].Kind, screens[index].Count)}   ·   {labels[index]}";
        ScreenLayoutDescription.Text = multiple
            ? $"{screens.Length} screens   ·   {layout.Characters.Length} slots" : ScreenHeading(0);
        ScreenLayoutSlots.Inlines.Clear();
        int index = 0;
        for (int group = 0; group < screens.Length; group++)
        {
            var screen = screens[group];
            if (index > 0) { ScreenLayoutSlots.Inlines.Add(new D.LineBreak()); ScreenLayoutSlots.Inlines.Add(new D.LineBreak()); }
            if (multiple) ScreenLayoutSlots.Inlines.Add(new D.Bold(new D.Run(ScreenHeading(group))));
            var lines = new string[screen.Count];
            var monitor = monitors[group];
            Rectangle[] rectangles = monitor is null ? [] : GameViewGeometry.Slots(screen.Kind,
                layout.AvoidTaskbar ? monitor.WorkArea : monitor.Bounds, screen.Count, screen.Edge);
            var cells = new GameViewMapSlot[rectangles.Length];
            for (int slot = 1; slot <= screen.Count; slot++, index++)
            {
                string assignment = layout.Characters[index];
                if (assignment.Length == 0 && layout.Windows?[index] is { } window)
                    assignment = $"EQ window {window.ProcessId}";
                if (cells.Length > 0)
                    cells[slot - 1] = new(rectangles[slot - 1], assignment, index == layout.SwapTargetIndex);
                lines[slot - 1] = $"{slot}   {(assignment.Length > 0 ? assignment : "Unassigned")}";
            }
            if (monitor is not null) preview.Add(monitor.Device, cells);
            string assignments = string.Join(Environment.NewLine, lines);
            if (multiple)
            {
                ScreenLayoutSlots.Inlines.Add(new D.LineBreak());
                ScreenLayoutSlots.Inlines.Add(new D.Run(assignments));
            }
            else ScreenLayoutSlots.Text = assignments;
        }
        SavedScreenMap.Update(connected, null, device => preview.TryGetValue(device, out var cells) ? cells : []);
        loadingScreenOptions = true;
        try
        {
            ScreenSwappingSwitch.IsChecked = layout.SwapOnClick;
            ScreenAddSwapButton.IsChecked = layout.AddSwapButton;
            ScreenButtonAppearance.Load(layout.SwapButtonStyle, layout.SwapButtonText ?? "");
            ScreenButtonAppearance.Visibility = layout.AddSwapButton ? W.Visibility.Visible : W.Visibility.Collapsed;
            ScreenMatchMasterResolutionSwitch.IsChecked = layout.MatchMasterResolution;
            ScreenHideTitleBarsSwitch.IsChecked = layout.HideTitleBars;
            ScreenOverlapTaskbarSwitch.IsChecked = !layout.AvoidTaskbar;
            ScreenAlwaysOnTopSwitch.IsChecked = layout.AlwaysOnTop;
        }
        finally { loadingScreenOptions = false; }
        ScreenSwitches.IsEnabled = screensHost is not null;
        ScreenSwappingSwitch.IsEnabled = layout.Characters.Length > 1 || layout.SwapOnClick;
        ScreenAddSwapButton.IsEnabled = layout.Characters.Length > 1 || layout.AddSwapButton;
        ScreenSwappingSwitch.ToolTip = ScreenSwappingSwitch.IsEnabled
            ? "Click a preview to swap that game into the starred slot."
            : "Add another slot in Edit layout to enable swapping.";
        EditScreenLayout.IsEnabled = ApplyScreenLayout.IsEnabled = screensHost is not null;
    }

    private static int DefaultSwapSlot(GameViewLayout layout)
    {
        var screens = layout.ScreenPlans;
        if (screens.Length == 1) return 0;
        var matches = MatchScreenPlans(screens, GameViewMonitor.Connected());
        var target = GameViewMonitor.DefaultSwapScreen(matches) ?? throw new InvalidOperationException(
            "Choose the edit pencil and place the swap star on a slot.");
        return screens.Take(Array.IndexOf(matches, target)).Sum(screen => screen.Count);
    }

    private void ScreenOption_Changed(object sender, W.RoutedEventArgs e)
    {
        if (loadingScreenOptions || screensHost is not { } host || sender is not C.CheckBox toggle) return;
        bool enabled = toggle.IsChecked == true;
        if (inlineDraft is not null)
        {
            if (toggle == ScreenSwappingSwitch) inlineDraft.SwapOnClick = enabled;
            else if (toggle == ScreenAddSwapButton) inlineDraft.AddSwapButton = enabled;
            else if (toggle == ScreenMatchMasterResolutionSwitch) inlineDraft.MatchMasterResolution = enabled;
            else if (toggle == ScreenHideTitleBarsSwitch) inlineDraft.HideTitleBars = enabled;
            else if (toggle == ScreenOverlapTaskbarSwitch) inlineDraft.AvoidTaskbar = !enabled;
            else if (toggle == ScreenAlwaysOnTopSwitch) inlineDraft.AlwaysOnTop = enabled;
            DrawInlineEditor();
            return;
        }
        ChangeSelectedScreenLayout(host, layout =>
        {
            if (toggle == ScreenSwappingSwitch || toggle == ScreenAddSwapButton)
            {
                int? destination = layout.MasterSlot;
                if (layout.SwapTargetIndex >= 0) destination = layout.SwapTargetIndex;
                else if (enabled) destination ??= DefaultSwapSlot(layout);
                return toggle == ScreenSwappingSwitch ? layout with { MasterSlot = destination, SwapOnClick = enabled }
                    : layout with { MasterSlot = destination, AddSwapButton = enabled };
            }
            if (toggle == ScreenMatchMasterResolutionSwitch) return layout with { MatchMasterResolution = enabled };
            if (toggle == ScreenHideTitleBarsSwitch) return layout with { HideTitleBars = enabled };
            if (toggle == ScreenOverlapTaskbarSwitch) return layout with { AvoidTaskbar = !enabled };
            return layout with { AlwaysOnTop = enabled };
        });
    }

    private void ScreenButtonAppearance_Changed(object? sender, EventArgs e)
    {
        if (loadingScreenOptions || screensHost is not { } host) return;
        var style = ScreenButtonAppearance.ButtonStyle;
        string text = ScreenButtonAppearance.ButtonText;
        if (inlineDraft is { } draft)
        {
            draft.SwapButtonStyle = style;
            draft.SwapButtonText = text;
            return;
        }
        ChangeSelectedScreenLayout(host, layout => layout with { SwapButtonStyle = style, SwapButtonText = text });
    }

    private void ChangeSelectedScreenLayout(TeamBarApplication host, Func<GameViewLayout, GameViewLayout> change)
    {
        if (ScreenLayoutPicker.SelectedItem is not GameViewLayout selected) return;
        ScreenFeedback("");
        try
        {
            var previous = selected;
            var updated = GameViewLayouts.Update(profilePath, selected.Name, layout =>
            {
                previous = layout;
                return change(layout);
            });
            ApplySavedScreenChange(host, previous, updated);
        }
        catch (Exception error)
        {
            ScreenApplyError(error);
        }
        LoadScreenLayouts();
    }

    private void ApplySavedScreenChange(TeamBarApplication host, GameViewLayout previous, GameViewLayout updated)
    {
        if (!host.IsActiveGameView(profilePath, updated.Name)) return;
        try { host.ApplyProfileLayout(profilePath, updated, layoutWindows, layoutRoster, keepFocus: true); }
        catch (Exception error) when (host.IsActiveGameView(profilePath, updated.Name))
        {
            // The running session rolled back. Restore the preset and its switches
            // as well, but preserve any newer edit made by another Settings window.
            try { GameViewLayouts.SaveEdit(profilePath, updated, previous); }
            catch (Exception rollback)
            {
                throw new InvalidOperationException($"The active layout could not apply the change. {error.Message} " +
                    $"The saved preset could not be restored. {rollback.Message}", error);
            }
            if (error is GameViewWindowedModeException) throw;
            throw new InvalidOperationException($"The previous layout settings are still active. {error.Message}", error);
        }
    }

    private void RefreshScreenLayoutStatus()
    {
        ScreenLayoutStatus.Text = string.IsNullOrWhiteSpace(screensHost?.GameViewStatus) ? "No active layout" : screensHost.GameViewStatus;
        EndScreenLayout.Visibility = screensHost?.ActiveGameViewName is null ? W.Visibility.Collapsed : W.Visibility.Visible;
    }

    private void ScreenLayoutStateChanged(object? sender, EventArgs e) => RefreshScreenLayoutStatus();

    private void ScreenFeedback(string message, bool error = false)
    {
        fixedSlotOffer = null;
        UseFixedSlotSizes.Visibility = W.Visibility.Collapsed;
        ScreenActionFeedback.Text = message;
        ScreenActionFeedback.SetResourceReference(C.TextBlock.ForegroundProperty, error ? "Error" : "Muted");
        ScreenActionFeedback.Visibility = message.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
    }

    private void ScreenApplyError(Exception error)
    {
        if (error is GameViewWindowedModeException)
        {
            ScreenFeedback("");
            SetScreenWindowedWarning(error.Message);
            return;
        }
        ScreenFeedback(error.Message, error: true);
        if (error.GetBaseException() is FixedSlotResolutionException && ScreenLayoutPicker.SelectedItem is GameViewLayout layout)
        {
            fixedSlotOffer = layout.Name;
            UseFixedSlotSizes.Visibility = W.Visibility.Visible;
        }
    }

    private void UseFixedSlotSizes_Click(object sender, W.RoutedEventArgs e)
    {
        if (screensHost is not { } host || ScreenLayoutPicker.SelectedItem is not GameViewLayout selected ||
            !selected.Name.Equals(fixedSlotOffer, StringComparison.OrdinalIgnoreCase)) return;
        ScreenFeedback("");
        try
        {
            var fixedSlots = selected with { SwapOnClick = false, MatchMasterResolution = false };
            // Save only after the runtime has accepted both switches together.
            // If the preset changed concurrently, reapply its latest saved settings.
            host.ApplyProfileLayout(profilePath, fixedSlots, layoutWindows, layoutRoster, keepFocus: true);
            try { GameViewLayouts.SaveEdit(profilePath, selected, fixedSlots); }
            catch
            {
                var latest = GameViewLayouts.Load(profilePath).FirstOrDefault(l => l.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase));
                if (latest is null) host.EndGameView();
                else host.ApplyProfileLayout(profilePath, latest, layoutWindows, layoutRoster, keepFocus: true);
                throw;
            }
        }
        catch (Exception error) { ScreenApplyError(error); }
        LoadScreenLayouts();
    }

    private void NewScreenLayout_Click(object sender, W.RoutedEventArgs e) => BeginInlineEditor(null);

    private void EditScreenLayout_Click(object sender, W.RoutedEventArgs e)
    {
        if (ScreenLayoutPicker.SelectedItem is not GameViewLayout layout) return;
        BeginInlineEditor(layout);
    }

    private void ApplyScreenLayout_Click(object sender, W.RoutedEventArgs e)
    {
        if (screensHost is not { } host || ScreenLayoutPicker.SelectedItem is not GameViewLayout selected) return;
        ScreenFeedback("");
        try
        {
            var current = GameViewLayouts.Load(profilePath).FirstOrDefault(layout => layout.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                LoadScreenLayouts();
                ScreenFeedback("That layout is no longer saved.", error: true);
                return;
            }
            ApplyTrayLayout(host, current);
            LoadScreenLayouts();
        }
        catch (Exception error) { ScreenApplyError(error); }
    }

    private void EndScreenLayout_Click(object sender, W.RoutedEventArgs e)
    {
        ScreenFeedback("");
        try { screensHost?.EndGameView(); }
        catch (Exception error) { ScreenFeedback(error.Message, error: true); }
    }
}
