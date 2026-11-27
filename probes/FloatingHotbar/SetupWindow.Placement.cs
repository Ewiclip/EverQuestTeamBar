using System.Collections.ObjectModel;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

internal sealed record AnchorChoice(string? Id, string Label);
internal sealed record ScreenPlacementRow(string Label, string Detail);

public partial class SetupWindow
{
    private readonly ObservableCollection<AnchorChoice> anchorChoices = [];
    private readonly ObservableCollection<ScreenPlacementRow> screenPlacements = [];
    private bool loadingPlacement;
    private Func<string, AnchorLayout> readPlacements = AnchorPositions.Load;
    private string? SelectedAnchor => IsStandalone ? selected?.FollowCharacterId : selectedGroup?.FollowCharacterId;
    private string PlacementPath => IsStandalone && selected is not null ? StandaloneButtons.PathFor(profilePath, selected.Id)
        : selectedGroup is not null ? ButtonGroups.PositionPath(profilePath, selectedGroup.Id) : profilePath;
    private void ShowPlacement(bool show)
    {
        if (show && IsStandalone && selected is null) { Status("Select or add a standalone button first."); return; }
        if (show && !IsStandalone && selectedGroup is null) { Status("Select or add a group first."); return; }
        PlacementView.Visibility = show ? W.Visibility.Visible : W.Visibility.Collapsed;
        EditorBody.Visibility = show ? W.Visibility.Collapsed : W.Visibility.Visible;
        WindowLayoutActions.Visibility = show ? W.Visibility.Collapsed : W.Visibility.Visible;
        PlacementButton.Content = show ? "←  Back to buttons" : "Position";
        if (show) LoadPlacement();
    }
    private void Placement_Click(object sender, W.RoutedEventArgs e) => ShowPlacement(PlacementView.Visibility != W.Visibility.Visible);
    private void LoadPlacement()
    {
        loadingPlacement = true;
        try
        {
            PlacementTargetName.Text = IsStandalone ? selected!.ManagementName : selectedGroup!.ManagementName;
            PlacementTargetHint.Text = IsStandalone ? "This standalone button has its own placement." : "Applies to all buttons grouped on this bar.";
            anchorChoices.Clear(); anchorChoices.Add(new(null, "Free placement · stay where I put it"));
            foreach (var character in team.Characters) anchorChoices.Add(new(character.Id, "Follow " + character.Name + "’s game window"));
            PlacementCharacter.SelectedItem = anchorChoices.Single(c => string.Equals(c.Id, SelectedAnchor, StringComparison.OrdinalIgnoreCase));
        }
        finally { loadingPlacement = false; }
        RefreshPlacementSummary();
    }
    private void PlacementCharacter_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!initialized || loadingPlacement || PlacementCharacter.SelectedItem is not AnchorChoice choice) return;
        if (IsStandalone && selected is not null) selected.FollowCharacterId = choice.Id;
        else if (selectedGroup is not null) ChangeGroup(selectedGroup with { FollowCharacterId = choice.Id });
        MarkChanged(); RefreshPlacementSummary();
    }
    private void RefreshPlacements_Click(object sender, W.RoutedEventArgs e) => RefreshPlacementSummary();
    private void RefreshPlacementSummary()
    {
        screenPlacements.Clear(); string? anchor = SelectedAnchor;
        FollowDetails.Visibility = anchor is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        if (anchor is null) { PlacementCurrentHint.Text = "This control keeps its desktop position when game windows move."; return; }
        string name = team.Characters.Single(c => string.Equals(c.Id, anchor, StringComparison.OrdinalIgnoreCase)).Name;
        bool selectedClient = SelectedClients().Any(c => string.Equals(c.Character.Name, name, StringComparison.OrdinalIgnoreCase));
        PlacementCurrentHint.Text = selectedClient ? $"Follows {name} across personas. Hidden while the game window is minimized or unavailable."
            : $"Select {name} in your team to show this control. Its follow setting can still be saved now.";
        try
        {
            var history = AnchorPositions.Find(readPlacements(PlacementPath), anchor);
            foreach (var screen in history?.Screens ?? [])
                screenPlacements.Add(new(screen.Label, $"Horizontal {screen.Position.X:P0} · Vertical {screen.Position.Y:P0} of available space"));
            NoScreenPositions.Text = "No monitor overrides yet. The first placement uses a proportional fallback.";
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        { NoScreenPositions.Text = "Saved positions could not be read: " + error.Message; }
        NoScreenPositions.Visibility = screenPlacements.Count == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
    }
#if !TEAMBAR_PUBLIC_RELEASE
    internal void PlacementSelfTest()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        Show(); long until = Environment.TickCount64 + 10000;
        while ((refreshing || clients.Count == 0) && Environment.TickCount64 < until) { Settle(); Thread.Sleep(5); }
        StandaloneTests.Check(clients.Count == 2, "Placement editor discovery failed.");
        BarLibraryMode.IsChecked = true;
        ShowPlacement(true); PlacementCharacter.SelectedItem = anchorChoices.Single(c => c.Id == "runa");
        StandaloneTests.Check(changed && CurrentTeam().Groups!.Single().FollowCharacterId == "runa", "Group placement did not update the selected group.");
        Save(); ShowPlacement(false); StandaloneLibraryMode.IsChecked = true;
        ShowPlacement(true); PlacementCharacter.SelectedItem = anchorChoices.Single(c => c.Id == "fellwort");
        StandaloneTests.Check(selected!.Dirty && CurrentTeam().Groups!.Single().FollowCharacterId == "runa", "Standalone placement overwrote the group's character.");
        ShowGeneralSettings(true); LightThemeChoice.IsChecked = true; ShowGeneralSettings(false); ShowPlacement(true);
        StandaloneTests.Check(SelectedAnchor == "fellwort" && PlacementView.Visibility == W.Visibility.Visible, "Settings navigation lost the anchor draft.");
        SaveStandalone(); ShowPlacement(false); Close();
    }
#endif
}
