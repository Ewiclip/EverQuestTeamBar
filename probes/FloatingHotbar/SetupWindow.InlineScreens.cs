using W = System.Windows;
using C = System.Windows.Controls;
using System.Windows.Automation;
using DraftScreen = TeamBar.GameViewInlineDraft.Screen;

namespace TeamBar;

public partial class SetupWindow
{
    private sealed record InlineArrangementChoice(GameViewKind Kind, string Label);
    private GameViewLayout? inlineOriginal;
    private GameViewInlineDraft? inlineDraft;
    private DraftScreen? inlineScreen;
    private int inlineSeat = -1;
    private bool loadingInline;

    private void InitializeInlineEditor()
    {
        ScreenButtonAppearance.Changed += ScreenButtonAppearance_Changed;
        InlineCapacity.ItemsSource = Enumerable.Range(0, 7);
        InlineEdge.ItemsSource = Enum.GetValues<GameViewEdge>();
        SavedScreenMap.ScreenSelected += device =>
        {
            if (InlineDisplayedScreen(device) is { } screen) SelectInlineScreen(screen);
        };
        SavedScreenMap.SlotSelected += (device, slot) =>
        {
            if (InlineDisplayedScreen(device) is { } screen) ActivateInlineSlot(screen, slot);
        };
        SavedScreenMap.SlotDropped += PlaceInlineToken;
        InlineOfflineMap.SlotSelected += (_, slot) =>
        {
            if (inlineScreen is { Display: null } screen) ActivateInlineSlot(screen, slot);
        };
        InlineOfflineMap.SlotDropped += (_, slot, token) =>
        {
            if (inlineScreen is { Display: null } screen) PlaceInlineAssignment(screen, slot, token);
        };
    }

    private DraftScreen? InlineDisplayedScreen(string device) => inlineDraft?.Screens.FirstOrDefault(
        s => StringComparer.OrdinalIgnoreCase.Equals(s.Display?.Device, device));

    internal void ShowScreenLayoutEditor()
    {
        ScreensTab.IsChecked = true;
        SetSettingsTab();
        UpdateLayout();
        if (NewScreenLayout.IsEnabled) BeginInlineEditor(ScreenLayoutPicker.SelectedItem as GameViewLayout);
    }

    private void BeginInlineEditor(GameViewLayout? selected)
    {
        if (screensHost is null || inlineDraft is not null) return;
        try
        {
            var original = selected is null ? null : GameViewLayouts.Load(profilePath)
                .FirstOrDefault(l => StringComparer.OrdinalIgnoreCase.Equals(l.Name, selected.Name))
                ?? throw new InvalidDataException("That layout is no longer saved.");
            var draft = new GameViewInlineDraft(original, GameViewMonitor.Connected());
            var windows = layoutWindows();
            inlineDraft = draft;
            ScreenButtonAppearance.Load(draft.SwapButtonStyle, draft.SwapButtonText ?? "");
            inlineOriginal = original;
            inlineScreen = draft.Screens.FirstOrDefault(s => s.Count > 0)
                ?? draft.Screens.FirstOrDefault(s => s.Display?.Primary == true) ?? draft.Screens.FirstOrDefault();
            inlineSeat = -1;
            InlineLayoutName.Text = original?.Name ?? "";
            SaveInlineLayout.Content = "Save layout";
            ScreenFeedback("");
            SetInlineMode(true);
            DrawInlineEditor();
            RefreshInlineRoster(windows);
            if (original is null) InlineLayoutName.Focus();
        }
        catch (Exception error)
        {
            if (inlineDraft is not null) EndInlineEditor(selected?.Name);
            ScreenFeedback(error.Message, error: true);
        }
    }

    private void SetInlineMode(bool editing)
    {
        var edit = editing ? W.Visibility.Visible : W.Visibility.Collapsed;
        var browse = editing ? W.Visibility.Collapsed : W.Visibility.Visible;
        InlineNameRow.Visibility = InlineTools.Visibility = edit;
        ScreenPresetActions.Visibility = ScreenLayoutSlots.Visibility = NewScreenLayout.Visibility = browse;
        ScreenLayoutDescription.Visibility = browse;
        ScreenEditorHeading.Text = editing ? (inlineOriginal is null ? "New layout" : "Edit layout") : "Saved layouts";
        SavedScreenMap.IsEditing = InlineOfflineMap.IsEditing = editing;
        SavedScreenMap.Height = editing ? 235 : 184;
        AutomationProperties.SetName(SavedScreenMap, editing ? "Edit screen slots" : "Saved layout preview");
        if (!editing) return;
        SetScreenWindowedWarning("");
        EmptyScreenLayouts.Visibility = W.Visibility.Collapsed;
        SavedScreenLayoutsPanel.Visibility = W.Visibility.Visible;
        ScreenSwitches.IsEnabled = true;
    }

    private void EndInlineEditor(string? preferred)
    {
        inlineDraft = null;
        inlineScreen = null;
        inlineOriginal = null;
        inlineSeat = -1;
        ResetInlinePalette();
        SetInlineMode(false);
        LoadScreenLayouts(preferred);
    }

    private void CancelInline_Click(object sender, W.RoutedEventArgs e)
    {
        ScreenFeedback("");
        EndInlineEditor(inlineOriginal?.Name);
        (SavedScreenLayoutsPanel.IsVisible ? EditScreenLayout : NewScreenLayout).Focus();
    }

    private void InlineName_Changed(object sender, C.TextChangedEventArgs e)
    {
        if (inlineDraft is null) return;
        bool copy = inlineOriginal is not null && !StringComparer.OrdinalIgnoreCase.Equals(inlineOriginal.Name, InlineLayoutName.Text.Trim());
        SaveInlineLayout.Content = copy ? "Save copy" : "Save layout";
    }

    private void SaveInline_Click(object sender, W.RoutedEventArgs e)
    {
        if (inlineDraft is null || screensHost is not { } host) return;
        try
        {
            var name = InlineLayoutName.Text.Trim();
            if (name.Length == 0) { InlineLayoutName.Focus(); throw new InvalidOperationException("Enter a layout name."); }
            inlineDraft.SwapButtonText = ScreenButtonAppearance.ButtonText;
            var layout = GameViewLayouts.SaveEdit(profilePath, inlineOriginal, inlineDraft.Build(name));
            try
            {
                if (inlineOriginal is { } original && StringComparer.OrdinalIgnoreCase.Equals(original.Name, layout.Name))
                    ApplySavedScreenChange(host, original, layout);
            }
            finally
            {
                EndInlineEditor(layout.Name);
                EditScreenLayout.Focus();
            }
            ScreenFeedback($"Saved {layout.Name}.");
        }
        catch (Exception error) { ScreenFeedback(error.Message, error: true); }
    }

    private void RefreshInlineDisplays()
    {
        if (inlineDraft is null) return;
        inlineDraft.RefreshDisplays(GameViewMonitor.Connected());
        if (inlineScreen is null || !inlineDraft.Screens.Contains(inlineScreen))
        {
            inlineScreen = inlineDraft.Screens.FirstOrDefault();
            inlineSeat = -1;
        }
        DrawInlineEditor();
    }

    private void DrawInlineEditor()
    {
        if (inlineDraft is not { } draft) return;
        loadingInline = loadingScreenOptions = true;
        try
        {
            ScreenSwappingSwitch.IsEnabled = draft.TotalSlots > 1 || draft.SwapOnClick;
            ScreenSwappingSwitch.IsChecked = draft.SwapOnClick;
            ScreenAddSwapButton.IsEnabled = draft.TotalSlots > 1 || draft.AddSwapButton;
            ScreenAddSwapButton.IsChecked = draft.AddSwapButton;
            ScreenButtonAppearance.Visibility = draft.AddSwapButton ? W.Visibility.Visible : W.Visibility.Collapsed;
            ScreenMatchMasterResolutionSwitch.IsChecked = draft.MatchMasterResolution;
            ScreenHideTitleBarsSwitch.IsChecked = draft.HideTitleBars;
            ScreenSwappingSwitch.ToolTip = draft.TotalSlots > 1 ? "Swap to the starred slot." : "Configure two slots to enable swapping.";
            ScreenOverlapTaskbarSwitch.IsChecked = !draft.AvoidTaskbar;
            ScreenAlwaysOnTopSwitch.IsChecked = draft.AlwaysOnTop;
            InlineScreenPicker.ItemsSource = draft.Screens.ToArray();
            InlineScreenPicker.SelectedItem = inlineScreen;
            InlineCapacity.IsEnabled = inlineScreen is not null;
            InlineCapacity.SelectedItem = inlineScreen?.Count;
            var screen = inlineScreen;
            var kinds = screen is null ? [] : Enum.GetValues<GameViewKind>().Where(kind =>
                GameViewGeometry.SupportsCount(kind, screen.Count) &&
                (screen.Count != 1 || kind == GameViewKind.EqualSplit || kind == screen.Kind))
                .OrderBy(k => k == GameViewKind.EqualSplit ? 0 : 1)
                .Select(kind => new InlineArrangementChoice(kind, GameViewGeometry.Label(kind, screen.Count))).ToArray();
            InlineArrangement.ItemsSource = kinds;
            InlineArrangement.SelectedValue = screen?.Kind;
            InlineArrangement.IsEnabled = kinds.Length > 1;
            InlineEdge.SelectedItem = screen?.Edge;
            InlineEdgeOptions.Visibility = screen is { Count: > 0, Kind: GameViewKind.MainStrip } ? W.Visibility.Visible : W.Visibility.Collapsed;
            var offline = screen is { Display: null };
            var unused = draft.Screens.Where(s => s is { Display: not null, Count: 0 }).ToArray();
            InlineReplacement.ItemsSource = unused;
            InlineReplacement.SelectedIndex = 0;
            InlineOfflineRow.Visibility = offline && unused.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
            InlineOfflineMap.Visibility = offline && screen!.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
            if (offline && screen!.Count > 0)
            {
                var bounds = new Rectangle(0, 0, 1920, 1080);
                InlineOfflineMap.Update([new GameViewMonitor(screen.Device, bounds, bounds)], screen.Device, _ => draft.PreviewSlots(screen, InlineWindowLabel));
                InlineOfflineMap.SelectSlot(screen.Device, inlineSeat);
            }
            var displays = draft.Screens.Select(s => s.Display).OfType<GameViewMonitor>().ToArray();
            SavedScreenMap.Update(displays, screen?.Display?.Device, device => draft.PreviewSlots(InlineDisplayedScreen(device)!, InlineWindowLabel));
            SavedScreenMap.SelectSlot(screen?.Display?.Device, inlineSeat);
            InlineClearSlot.IsEnabled = screen is not null && inlineSeat >= 0 && inlineSeat < screen.Count &&
                (screen.Characters[inlineSeat].Length > 0 || screen.Windows[inlineSeat] is not null);
            UpdateInlinePalette();
        }
        finally { loadingInline = loadingScreenOptions = false; }
    }

    private void InlineScreen_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!loadingInline && InlineScreenPicker.SelectedItem is DraftScreen screen) SelectInlineScreen(screen);
    }

    private void SelectInlineScreen(DraftScreen screen)
    {
        if (inlineScreen == screen) return;
        inlineScreen = screen;
        inlineSeat = -1;
        DrawInlineEditor();
    }

    private void InlineGeometry_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (loadingInline || inlineDraft is null || inlineScreen is not { } screen || InlineCapacity.SelectedItem is not int count) return;
        var kind = InlineArrangement.SelectedValue is GameViewKind selected ? selected : screen.Kind;
        var edge = InlineEdge.SelectedItem is GameViewEdge side ? side : screen.Edge;
        inlineDraft.Configure(screen, count, kind, edge);
        if (inlineSeat >= count) inlineSeat = -1;
        DrawInlineEditor();
    }

    private void MoveInlineScreen_Click(object sender, W.RoutedEventArgs e)
    {
        if (inlineDraft is not { } draft || inlineScreen is not { Display: null } screen ||
            InlineReplacement.SelectedItem is not DraftScreen replacement) return;
        draft.MoveScreen(screen, replacement);
        DrawInlineEditor();
    }

    private void ActivateInlineSlot(DraftScreen screen, int slot)
    {
        if (inlineToken is { } token) { PlaceInlineAssignment(screen, slot, token); return; }
        inlineScreen = screen;
        inlineSeat = slot;
        DrawInlineEditor();
    }

    internal void PlaceInlineToken(string device, int slot, GameViewMapToken token)
    {
        if (InlineDisplayedScreen(device) is { } screen) PlaceInlineAssignment(screen, slot, token);
    }

    private void PlaceInlineAssignment(DraftScreen screen, int slot, GameViewMapToken token)
    {
        if (inlineDraft is null) return;
        try
        {
            if (token == GameViewMapToken.Star) inlineDraft.SetMaster(screen, slot);
            else AssignInlineCharacter(screen, slot, token);
            ScreenFeedback("");
            SelectInlineToken(null);
            inlineScreen = screen;
            inlineSeat = slot;
            DrawInlineEditor();
        }
        catch (Exception error) { ScreenFeedback(error.Message, error: true); }
    }

    private void InlineClear_Click(object sender, W.RoutedEventArgs e)
    {
        if (inlineDraft is null || inlineScreen is null || inlineSeat < 0 || inlineSeat >= inlineScreen.Count) return;
        inlineDraft.Clear(inlineScreen, inlineSeat);
        SelectInlineToken(null);
        DrawInlineEditor();
    }
}
