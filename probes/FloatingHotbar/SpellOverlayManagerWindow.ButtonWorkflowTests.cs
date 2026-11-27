using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    internal static IEnumerable<W.DependencyObject> OverlayTestControls(W.DependencyObject parent)
    {
        for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = M.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in OverlayTestControls(child)) yield return descendant;
        }
    }

    private async Task TestOverlayButtonWorkflow(string overlayId, string profile, Action<bool, string> check)
    {
        var bar = floating[overlayId];
        static T Control<T>(W.Window window, string name) where T : W.UIElement
        {
            window.UpdateLayout();
            return OverlayTestControls(window).OfType<T>().First(c => W.Automation.AutomationProperties.GetName(c) == name);
        }
        static void Click(W.Window window, string name)
        {
            var button = Control<C.Button>(window, name);
            button.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent, button));
        }
        W.Window Editor() => bar.OwnedWindows.OfType<OverlayButtonEditor>().Single(w => w.IsVisible);
        async Task Draw()
        {
            while (polling) await Task.Delay(10);
            await Poll();
            bar.UpdateLayout();
        }
        await Draw();
        bar.ToggleButtonEditing();
        Click(bar, "Add a button on the left");
        Click(Editor(), "Assist fixture");
        await Draw();
        var linked = savedLibrary.Overlays.Single(d => d.Id == overlayId).DockGroups!.Single(g => g.Side == SpellDockSide.Left);
        check(linked.ButtonIds.SequenceEqual(["assist"]) && !IsVisible, "The overlay edge picker links the chosen saved button while Settings stays closed.");
        Click(bar, "Edit Assist fixture");
        var editor = Editor();
        Control<C.TextBox>(editor, "Button name").Text = "My assist";
        Click(editor, "Make independent");
        Click(editor, "Done");
        await Draw();
        var placement = SpellOverlayStore.Load(path).Library.Overlays.Single(d => d.Id == overlayId).DockGroups!.Single(g => g.Id == linked.Id);
        var saved = StandaloneButtons.Load(profile);
        check(placement.ButtonIds.Single() != "assist" && saved.Single(b => b.Value.Id == placement.ButtonIds[0]).Value.Label == "My assist"
            && saved.Single(b => b.Value.Id == "assist").Value.Label == "Assist fixture",
            "The live editor immediately replaces just this placement with the renamed independent copy.");
        Click(bar, "Edit My assist");
        Click(Editor(), "Remove from this bar");
        await Draw();
        check(SpellOverlayStore.Load(path).Library.Overlays.Single(d => d.Id == overlayId).DockGroups!.Single(g => g.Id == linked.Id).ButtonIds.Length == 0
            && StandaloneButtons.Load(profile).Length == 2, "Removing from the floating bar leaves both definitions in the library.");

        // Reuse the empty named group, then exercise the add controls on the live overlay.
        SpellDockGroup SavedGroup() => SpellOverlayStore.Load(path).Library.Overlays.Single(d => d.Id == overlayId).DockGroups!.Single(g => g.Id == linked.Id);
        Click(bar, "Add a button on the left");
        Click(Editor(), "My assist");
        await Draw();
        string firstId = SavedGroup().ButtonIds.Single();
        string addColumn = "Add a column to " + linked.Name;
        var beforeCancel = File.ReadAllBytes(path);
        Click(bar, addColumn);
        Click(Editor(), "Close editor");
        check(File.ReadAllBytes(path).SequenceEqual(beforeCancel) && SavedGroup().Columns == 1 && StandaloneButtons.Load(profile).Length == 2,
            "Closing the new-column picker keeps the saved layout and button library intact.");

        Click(bar, addColumn);
        Click(Editor(), "Assist fixture");
        await Draw();
        var twoColumns = SavedGroup();
        check(twoColumns.Columns == 2 && twoColumns.Layout.SequenceEqual([new ButtonPlacement(firstId, 0), new("assist", 1)]),
            "Picking a synced button for a new column saves exactly one new column with the button in its first row.");
        Click(bar, $"Add a row to {linked.Name}, column 2");
        Click(Editor(), "Independent copy");
        Click(Editor(), "Assist fixture");
        await Draw();
        var withRow = SavedGroup();
        var extra = StandaloneButtons.Load(profile).Single(b => b.Value.Id != "assist" && b.Value.Id != firstId);
        check(withRow.Columns == 2 && withRow.Layout.SequenceEqual([.. twoColumns.Layout, new ButtonPlacement(extra.Value.Id, 1)]),
            "Adding a row to the second column preserves the first column and appends the independent copy to the chosen column.");
        bar.ToggleButtonEditing();
    }
}
