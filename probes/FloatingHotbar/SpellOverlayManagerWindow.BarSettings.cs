using W = System.Windows;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private readonly HashSet<string> expandedBarSettings = [];
    private void ApplyBarSettingsExpansion()
    {
        bool expanded = selected is not null && expandedBarSettings.Contains(selected.Id);
        BarSettingsPanel.Visibility = expanded ? W.Visibility.Visible : W.Visibility.Collapsed;
        BarSettingsToggle.Content = expanded ? "⌄  Bar settings" : "›  Bar settings";
        BarSettingsToggle.IsEnabled = selected is not null;
        W.Automation.AutomationProperties.SetName(BarSettingsToggle, expanded ? "Collapse bar settings" : "Expand bar settings");
    }
    private void ToggleBarSettings_Click(object sender, W.RoutedEventArgs e)
    {
        e.Handled = true;
        if (selected is not { } definition) return;
        if (!expandedBarSettings.Add(definition.Id)) expandedBarSettings.Remove(definition.Id);
        ApplyBarSettingsExpansion();
    }
    private void Placement_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || loading || selected is null || PlacementChoice.SelectedIndex < 0) return;
        bool follow = PlacementChoice.SelectedIndex == 0;
        var position = selected.FreePosition;
        if (!follow && position is null && floating.TryGetValue(selected.Id, out var window)) position = window.DesktopPosition;
        if (!follow && position is null)
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var point = AnchorPositions.Project(new(selected.X, selected.Y), new(selected.Width, 200), area);
            position = new(point.X, point.Y);
        }
        Replace(selected with { FollowCharacterWindow = follow, FreePosition = position });
        UpdatePlacementHint();
    }
    private void UpdatePlacementHint() => PlacementHint.Text = PlacementChoice.SelectedIndex == 1
        ? "Stays where you place it on the desktop. Unlock the corner padlock to move or resize."
        : "Follows this character’s game window, remembering placement per monitor. Unlock the corner padlock to adjust it.";
}
