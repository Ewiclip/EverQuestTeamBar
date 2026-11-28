using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private sealed record DockButtonPress(C.Button Source, SpellDockGroup Group, string ButtonId, W.Point Origin);
    private sealed record DockDropTarget(SpellDockGroup Group, int Column, string? ButtonId = null, bool NewColumn = false, int? Row = null);
    private DockButtonPress? dockPress;
    private bool draggingDockButton, overDockDropSlot;
    private readonly List<C.Grid> dockDropGrids = [];

    private void InitializeDockButtonDrag()
    {
        PreviewMouseMove += (_, e) =>
        {
            if (dockPress is not null && e.LeftButton == I.MouseButtonState.Pressed && UpdateDockButtonDrag(e.GetPosition(this)))
                e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (EndDockButtonDrag(e.GetPosition(this))) e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == I.Key.Escape && draggingDockButton) { EndDockButtonDrag(); e.Handled = true; }
        };
        LostMouseCapture += (_, e) =>
        {
            if (dockPress is { } press && (draggingDockButton ? e.OriginalSource == this : e.OriginalSource == press.Source))
                EndDockButtonDrag();
        };
        Deactivated += (_, _) => EndDockButtonDrag();
        QueryCursor += (_, e) =>
        {
            if (!draggingDockButton) return;
            e.Cursor = overDockDropSlot ? I.Cursors.SizeAll : I.Cursors.No;
            e.Handled = true;
        };
    }

    private void BeginDockButtonPress(C.Button source, SpellDockGroup group, string buttonId, W.Point point)
    {
        if (editingButtons && dockPress is null) dockPress = new(source, group, buttonId, point);
    }

    private bool UpdateDockButtonDrag(W.Point point)
    {
        if (dockPress is not { } press || !editingButtons) return false;
        if (!draggingDockButton)
        {
            if (Math.Abs(point.X - press.Origin.X) < W.SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - press.Origin.Y) < W.SystemParameters.MinimumVerticalDragDistance) return false;
            // Taking capture from the button cancels its normal click-on-release gesture.
            draggingDockButton = true;
            if (!CaptureMouse() || dockPress != press) { EndDockButtonDrag(); return false; }
            var pointerOnScreen = PointToScreen(point);
            CloseDockEditorsForDrag();
            if (dockPress != press) return false;
            ShowDockDragPreview(press.Source, press.Origin, point);
            press.Source.Opacity = .55;
            ShowDockDropSlots(press);
            if (dockPress != press) return false;
            // Adding insertion rows can move the anchored overlay underneath a stationary pointer.
            point = PointFromScreen(pointerOnScreen);
        }
        MoveDockDragPreview(point);
        bool hit = HighlightDockDropSlot(point);
        if (hit != overDockDropSlot) { overDockDropSlot = hit; I.Mouse.UpdateCursor(); }
        return true;
    }

    private void CloseDockEditorsForDrag()
    {
        buttonEditor?.Close();
        if (W.Application.Current is not { } app) return;
        foreach (var other in app.Windows.OfType<SpellOverlayWindow>().Where(w => w != this && w.buttonEditor is not null).ToArray())
            other.buttonEditor?.Close();
    }

    private bool EndDockButtonDrag(W.Point? dropAt = null)
    {
        var press = dockPress;
        bool dragging = draggingDockButton;
        dockPress = null; draggingDockButton = false; overDockDropSlot = false;
        if (press is null || !dragging) return false;
        var drop = editingButtons && dropAt is { } point ? FindDockDropSlot(point) : null;
        HideDockDragPreview();
        HideDockDropSlots();
        press.Source.ClearValue(W.UIElement.OpacityProperty);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (drop is not null)
        {
            buttonEditError = CommitLayout(new(ButtonMove: drop)) ? null : layoutError;
            UpdateClickHint();
        }
        if (editingButtons && IsVisible && currentFrame is not null) RefreshLayout();
        return true;
    }
}
