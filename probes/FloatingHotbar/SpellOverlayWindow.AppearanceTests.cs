using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    internal void TestSharedAppearance(Action<bool, string> check)
    {
        var original = theme; var definition = currentDefinition!; var state = currentState!;
        nint handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        foreach (var mode in Enum.GetValues<AppearanceTheme>())
        {
            var palette = FloatingThemes.ForAppTheme(mode);
            SetTheme(palette); Render(definition, state);
            check(((M.SolidColorBrush)shell.Background).Color == Brush(palette.Bar).Color
                && ((M.SolidColorBrush)target.Foreground).Color == Brush(palette.Text).Color,
                mode + ": spell overlay follows the same app palette as floating buttons.");
            var active = state.Rows.First(r => !r.Missing);
            check(((M.SolidColorBrush)rows[(active.Priority, active.Rule.SpellId, false)].Border.Background).Color == Brush(palette.Button).Color,
                mode + ": existing spell rows refresh their surface color.");
            check(new System.Windows.Interop.WindowInteropHelper(this).Handle == handle,
                mode + ": changing appearance preserves the overlay window and input registration.");
        }
        var before = theme; clickBusy = true;
        SetTheme(original); check(theme == before, "A theme change waits until an in-flight spell click finishes.");
        clickBusy = false; SetTheme(original); Render(definition, state);
        var corner = new SpellOverlayLockWindow(this, original, showSettings: true);
        try
        {
            corner.SetTheme(FloatingThemes.ForAppTheme(AppearanceTheme.Light)); corner.SetDefinition(definition);
            var controls = (C.Primitives.UniformGrid)corner.Content;
            check(controls.Children.OfType<C.Button>().All(b => ((M.SolidColorBrush)b.Background).Color.A == 1),
                "Shared appearance keeps the lock and gear transparent without losing their mouse hit surface.");
        }
        finally { corner.CloseWithDisplay(); }
    }
}
