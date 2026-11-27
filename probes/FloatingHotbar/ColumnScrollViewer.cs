using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace TeamBar;

// The columns form one horizontal workspace. Ordinary wheel input should move
// that workspace, except over a list that has its own vertical overflow.
internal sealed class ColumnScrollViewer : C.ScrollViewer
{
    private double? requestedOffset;
    internal ColumnScrollViewer()
    {
        HorizontalScrollBarVisibility = C.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = C.ScrollBarVisibility.Disabled;
        ScrollChanged += (_, e) =>
        {
            if (e.OriginalSource != this) return;
            if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0
                || requestedOffset is { } target && Math.Abs(HorizontalOffset - target) < .01)
                requestedOffset = null;
        };
        PreviewMouseDown += (_, _) => requestedOffset = null;
    }
    protected override void OnPreviewMouseWheel(I.MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (!e.Handled && MoveWheel(e.Delta, I.Keyboard.Modifiers, e.OriginalSource as W.DependencyObject, W.SystemParameters.WheelScrollLines)) e.Handled = true;
    }
    internal bool MoveWheel(int delta, I.ModifierKeys modifiers, W.DependencyObject? origin, int lines)
    {
        if (delta == 0 || lines == 0 || ScrollableWidth <= 0 || (modifiers & ~I.ModifierKeys.Shift) != 0) return false;
        if (modifiers != I.ModifierKeys.Shift)
            for (var element = origin; element is not null && element != this; element = GetParent(element))
                if (element is C.ScrollViewer { ScrollableHeight: > 0 }) return false;
        double current = requestedOffset ?? HorizontalOffset;
        double step = lines < 0 ? ViewportWidth : lines * 40d;
        double next = Math.Clamp(current - delta / 120d * step, 0, ScrollableWidth);
        if (Math.Abs(next - current) < .01) return false;
        requestedOffset = next; ScrollToHorizontalOffset(next); return true;
    }
    private static W.DependencyObject? GetParent(W.DependencyObject element) => element is M.Visual or M.Media3D.Visual3D
        ? M.VisualTreeHelper.GetParent(element) : W.LogicalTreeHelper.GetParent(element);
}
