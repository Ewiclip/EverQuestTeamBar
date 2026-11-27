using W = System.Windows;
using C = System.Windows.Controls;
using J = System.Text.Json;

namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    internal static async Task TestFreePlacement(Action<bool, string> check)
    {
        var (definition, capture) = SpellOverlayTests.Fixture();
        var oldJson = System.Text.Json.Nodes.JsonNode.Parse(J.JsonSerializer.Serialize(definition, SpellOverlayStore.Json))!;
        oldJson.AsObject().Remove("followCharacterWindow"); oldJson.AsObject().Remove("freePosition");
        check(J.JsonSerializer.Deserialize<SpellOverlayDefinition>(oldJson, SpellOverlayStore.Json)!.FollowCharacterWindow,
            "existing overlays without placement settings continue following their character");
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var saved = definition with { FollowCharacterWindow = false, Locked = false, FreePosition = new(area.X + 100, area.Y + 80) };
        var roundTrip = J.JsonSerializer.Deserialize<SpellOverlayDefinition>(J.JsonSerializer.Serialize(saved, SpellOverlayStore.Json), SpellOverlayStore.Json)!;
        check(!roundTrip.FollowCharacterWindow && roundTrip.FreePosition == saved.FreePosition, "free placement and desktop position survive serialization");
        int saves = 0;
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1], (_, change) => { saves++; return saved = change.Apply(saved); });
        display.ReadDesktopAreas = () => [area];
        var frame = new GameFrame("free-fixture", "Game monitor", new(area.X + 30, area.Y + 30, 700, 600));
        var state = SpellOverlayRules.Evaluate(definition, capture);
        void Draw() { display.Render(saved, state); display.Place(saved, frame); display.UpdateLayout(); }
        try
        {
            Draw(); await W.Threading.Dispatcher.Yield(W.Threading.DispatcherPriority.ContextIdle);
            var initial = display.wholeBounds;
            frame = frame with { Area = new(area.X + 200, area.Y + 130, 450, 350) }; Draw();
            check(display.wholeBounds == initial, "free placement ignores movement and resizing of the character's game window");
            check(display.BeginLayoutEdit(false, new(100, 100)), "unlocked free overlay starts a drag");
            display.UpdateLayoutEdit(new(145, 125)); Draw();
            check(display.wholeBounds.Location == new System.Drawing.Point(initial.X + 45, initial.Y + 25) && saves == 0,
                "free drag follows the cursor without polling snap-back or intermediate saves");
            display.EndLayoutEdit(false);
            check(saves == 1 && saved.FreePosition == new OverlayDesktopPosition(initial.X + 45, initial.Y + 25)
                && saved.X == definition.X && saved.Y == definition.Y && saved.Screens == definition.Screens,
                "free drag saves once while retaining independent character-following positions");
            var freePosition = saved.FreePosition;
            display.BeginLayoutEdit(true, new(100, 100)); display.UpdateLayoutEdit(new(120, 120)); display.EndLayoutEdit(false);
            check(saved.Scale > definition.Scale && saved.FreePosition == freePosition, "free resize preserves its desktop anchor");
            var beforeCancel = saved;
            display.BeginLayoutEdit(false, new(100, 100)); display.UpdateLayoutEdit(new(130, 130)); display.EndLayoutEdit(true);
            check(saved == beforeCancel && display.DesktopPosition == freePosition, "canceling a free drag restores its saved location");
            saved = saved with { FollowCharacterWindow = true }; Draw();
            check(frame.Area.Contains(display.wholeBounds), "following placement constrains the overlay to its game again");
            saved = saved with { FollowCharacterWindow = false }; Draw();
            check(display.DesktopPosition == freePosition, "returning to free placement restores its independent desktop location");
            display.ToggleLayoutLock(true);
            check(!display.BeginLayoutEdit(false, new(100, 100)), "the movement lock still prevents dragging in free mode");
            display.Place(saved, null);
            check(!display.IsVisible && !display.LayoutLock!.IsVisible, "free placement still hides when the owning character window is unavailable");
            Draw(); check(display.DesktopPosition == freePosition, "free placement survives hide and restore");
            var negative = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
            check(SpellOverlayLayout.DesktopFrame(new(-1500, 120), [negative, new(0, 0, 1920, 1080)]).Area == negative,
                "free placement supports monitors with negative desktop coordinates");
            check(SpellOverlayLayout.DesktopFrame(new(-200, 120), [negative, new(0, 0, 1920, 1080)], new(500, 300)).Area.Left == 0,
                "cross-monitor dragging selects the screen containing most of the overlay");
            saved = saved with { FreePosition = new(-50000, -50000) }; Draw();
            check(area.Contains(display.wholeBounds), "a disconnected monitor's free position is clamped onto an available screen");
        }
        finally { display.Close(); }
    }
}

public partial class SpellOverlayManagerWindow
{
    private void TestBarSettings(Action<bool, string> check)
    {
        var original = selected!; bool oldDirty = dirty; var expanded = expandedBarSettings.ToArray();
        try
        {
            expandedBarSettings.Clear(); ApplyBarSettingsExpansion(); UpdateLayout();
            double fullHeight = SpellWorkspace.ActualHeight;
            check(BarSettingsPanel.Visibility == W.Visibility.Collapsed, "bar-wide settings start collapsed");
            static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject root)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
                    foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            var row = (C.ListBoxItem)OverlayList.ItemContainerGenerator.ContainerFromItem(selected);
            var remove = Descendants(row).OfType<C.Button>().Single();
            check(remove.IsVisible && remove.Content.ToString() == "×", "the bar library uses an inline delete X beside its name");
            var toggle = BarSettingsToggle;
            check(toggle.IsVisible && toggle.Content.ToString() == "›  Bar settings", "bar settings have their own labeled toggle in the editor");
            toggle.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent)); UpdateLayout();
            check(BarSettingsPanel.IsVisible && toggle.Content.ToString() == "⌄  Bar settings" && fullHeight - SpellWorkspace.ActualHeight > 160,
                "expanding reveals general settings and collapsing can return that height to the spell editor");
            toggle.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent)); UpdateLayout();
            check(SpellWorkspace.ActualHeight == fullHeight && selected == original && dirty == oldDirty,
                "collapsing reclaims all panel space without changing or saving the overlay");
            PlacementChoice.SelectedIndex = 1;
            check(selected is { FollowCharacterWindow: false, FreePosition: not null } && selected.Locked == original.Locked,
                "free placement captures a desktop position without changing the movement lock");
            var free = selected!.FreePosition; PlacementChoice.SelectedIndex = 0;
            check(selected.FollowCharacterWindow && selected.FreePosition == free, "switching to follow mode retains the free position for later");
            expandedBarSettings.Add(selected.Id); ApplyBarSettingsExpansion();
            Replace(selected with { Name = selected.Name + " edited" }); UpdateLayout();
            check(BarSettingsPanel.IsVisible, "editing a bar does not close its expanded settings");
        }
        finally
        {
            expandedBarSettings.Clear(); expandedBarSettings.UnionWith(expanded);
            Replace(original); LoadEditor(); dirty = oldDirty;
        }
    }
    internal void ExpandBarSettingsForReview(bool expanded)
    {
        if (selected is null) return;
        if (expanded) expandedBarSettings.Add(selected.Id); else expandedBarSettings.Remove(selected.Id);
        ApplyBarSettingsExpansion();
    }
}
