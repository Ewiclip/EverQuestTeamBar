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
            corner.SetButtonEditing(true, false);
            check(controls.Children.OfType<C.Button>().All(b => ((M.SolidColorBrush)b.Background).Color.A == 1),
                "Shared appearance keeps all toolbar controls transparent without losing their mouse hit surfaces.");
            foreach (bool editing in new[] { false, true })
            {
                corner.SetButtonEditing(true, editing);
                corner.SetDefinition(definition with { Locked = !editing });
                foreach (int side in new[] { 14, 21, 28, 35, 42, 56 })
                    CheckToolbarIcons(controls, side, editing, check);
            }
        }
        finally { corner.CloseWithDisplay(); }
    }

    private static void CheckToolbarIcons(C.Primitives.UniformGrid controls, int side, bool editing, Action<bool, string> check)
    {
        // Measure the painted glyphs, including fractional scales that round onto device pixels.
        int width = controls.Columns * side;
        var size = new W.Size(width, side);
        controls.Measure(size); controls.Arrange(new W.Rect(size)); controls.UpdateLayout();
        var bitmap = new M.Imaging.RenderTargetBitmap(width, side, 96, 96, M.PixelFormats.Pbgra32);
        bitmap.Render(controls);
        var pixels = new byte[width * side * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var centers = new List<double>();
        bool inside = true;
        for (int i = 0; i < controls.Columns; i++)
        {
            int top = side, bottom = -1;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    if (pixels[(y * width + i * side + x) * 4 + 3] < 32) continue;
                    inside &= x > 0 && x < side - 1;
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            inside &= top > 0 && bottom < side - 1 && bottom > top;
            centers.Add((top + bottom) / 2.0);
        }
        double lockCenter = centers[^1];
        check(inside && Math.Abs(lockCenter - (side - 1) / 2.0) <= 1 && centers.All(y => Math.Abs(y - lockCenter) <= 1),
            $"At {side / 28.0:P0} scale the settings, {(editing ? "done" : "edit")}, and lock icons render fully inside their buttons on the same centerline.");
    }
}
