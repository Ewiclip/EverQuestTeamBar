using D = System.Drawing;
using C = System.Windows.Controls;

namespace TeamBar;

internal static class ButtonIconTests
{
    internal static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidDataException(message);
            checks.Add(message);
        }
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        EqIconArtworkTests.Run(Check, output);
        EqIconCatalogTests.Run(Check, output);
        OverlayButtonEditorTests.Run(Check, output);
        bool localRenderingChecked = SpellOverlayWindow.TestButtonArtwork(Check);
        CollectorFormat.WriteNew(output, new { passed = true, checks, localRenderingChecked, gameInputSent = false });
    }
}

internal sealed partial class SpellOverlayWindow
{
    internal static bool TestButtonArtwork(Action<bool, string> check)
    {
        const string key = "eq:item:584";
        if (EqIconArtwork.Shared.Get(key) is not { } source) return false;
        var original = SpellDockTests.Buttons()[0];
        var saved = original with { Value = original.Value with { Icon = key } };
        var clients = SpellDockTests.Clients();
        var window = new SpellOverlayWindow(FloatingThemes.Presets[1]);
        try
        {
            window.SetDockButtons([saved], clients);
            window.RenderDockGroups(new("icon-test", "Icon test", "Fixture", [], DockGroups:
                [new("actions", "Actions", SpellDockSide.Top, [saved.Value.Id])]));
            var face = window.dockRows.Single().Border.Child;
            var content = face as C.DockPanel;
            var thumbnail = content?.Children.OfType<C.Image>().SingleOrDefault();
            if (!ReferenceEquals(thumbnail?.Source, source)) throw new InvalidDataException(
                $"Docked {key} resolved to {face?.GetType().Name} with image {thumbnail?.Source?.GetType().Name ?? "missing"}.");
            check(true, "The docked action uses its selected item image.");
            var label = content!.Children.OfType<C.TextBlock>().Single();
            check(label.Text == saved.Value.Label, $"The docked action keeps its name beside the artwork ({label.Text}).");
            var iconOnly = saved with { Value = saved.Value with { Label = "" } };
            window.SetDockButtons([iconOnly], clients);
            var definition = new SpellOverlayDefinition("icon-test", "", "Fixture", [], DockGroups:
                [new("actions", "", SpellDockSide.Top, [iconOnly.Value.Id])]);
            window.Render(definition, new("Test target", "", []));
            var dockButton = window.dockRows.Single().Border;
            var panel = window.dockPanels["actions"].Panel;
            check(dockButton.Child is C.Image { HorizontalAlignment: System.Windows.HorizontalAlignment.Center } icon && ReferenceEquals(icon.Source, source)
                && System.Windows.Automation.AutomationProperties.GetName(dockButton) == iconOnly.Value.ManagementName,
                "A nameless docked button centers the icon and retains an accessible name.");
            var stack = ((C.Grid)panel.Child).Children.OfType<C.StackPanel>().Single();
            check(stack.Children.OfType<C.TextBlock>().Count() == 0 && window.heading.Text == definition.CharacterPersona,
                "Nameless groups omit their title row and nameless spell bars show the character without a separator.");
            window.Render(definition with { DockGroups = [definition.DockGroups![0].WithLayout([])] }, new("Test target", "", []));
            check(window.dockPanels.Count == 0 && window.dockRows.Count == 0,
                "Removing the last placement removes the panel and all of its click targets on the next render.");
        }
        finally { window.Close(); }

        var action = StandaloneButtons.Display(saved.Value, clients) with { Label = "" };
        var appearance = new FloatingAppearance();
        using var plain = new D.Bitmap(224, 60); using var withArt = new D.Bitmap(224, 60);
        using (var g = D.Graphics.FromImage(plain)) FloatingPainter.Draw(g, "", [action with { Icon = null }], appearance);
        using (var g = D.Graphics.FromImage(withArt)) FloatingPainter.Draw(g, "", [action], appearance);
        var bounds = FloatingPainter.ActionBounds(0, FloatingPainter.ShowsHeader("", appearance));
        int x = (int)(bounds.Left + (bounds.Width - 24) / 2), y = (int)bounds.Top + ((int)bounds.Height - 24) / 2;
        check(Enumerable.Range(x, 24).Any(px => Enumerable.Range(y, 24).Any(py => plain.GetPixel(px, py) != withArt.GetPixel(px, py))),
            "Nameless floating buttons and groups center the artwork inside the clickable button.");
        return true;
    }
}
