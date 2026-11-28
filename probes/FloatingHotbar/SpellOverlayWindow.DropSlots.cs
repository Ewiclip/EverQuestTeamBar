using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using S = System.Windows.Shapes;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private const double DropSlotHeight = 13;
    private readonly List<S.Rectangle> dockDropSlots = [];
    private readonly List<(W.FrameworkElement Element, W.Thickness Margin, W.Visibility Visibility)> dockDragCells = [];
    private S.Rectangle? highlightedDockSlot;

    private void ShowDockDropSlots(DockButtonPress press)
    {
        foreach (var edge in dockRoot.Children.OfType<C.Button>())
        {
            dockDragCells.Add((edge, edge.Margin, edge.Visibility));
            edge.Visibility = W.Visibility.Hidden;
        }
        foreach (var grid in dockDropGrids)
        {
            // Reuse each edit control's grid cell so the drop target matches the visible destination.
            var cells = grid.Children.OfType<W.FrameworkElement>().ToArray();
            foreach (var cell in cells)
            {
                if (cell.Tag is not DockDropTarget target) continue;
                bool eligible = target.Group.Id == press.Group.Id ||
                    !target.Group.ButtonIds.Contains(press.ButtonId, StringComparer.OrdinalIgnoreCase);
                if (!eligible && target.ButtonId is not null) continue;
                var margin = cell.Margin;
                dockDragCells.Add((cell, margin, cell.Visibility));
                if (target.ButtonId is null)
                    cell.Visibility = W.Visibility.Hidden;
                else
                    cell.Margin = new(margin.Left, margin.Top + DropSlotHeight + 2, margin.Right, margin.Bottom);
                if (!eligible) continue;

                var move = new DockButtonMove(press.Group, press.ButtonId, target.Group, target.Column, target.ButtonId, target.NewColumn, target.Row);
                var slot = new S.Rectangle
                {
                    Tag = move, IsHitTestVisible = false, RadiusX = 3, RadiusY = 3, Margin = new(3, 2, 3, 1),
                    Stroke = Brush(theme.Accent), StrokeThickness = 1.3, StrokeDashArray = new() { .5, 2.5 },
                    StrokeDashCap = M.PenLineCap.Round, Fill = M.Brushes.Transparent, Opacity = .78
                };
                if (target.ButtonId is not null)
                {
                    slot.Height = DropSlotHeight;
                    slot.VerticalAlignment = W.VerticalAlignment.Top;
                }
                else if (C.Grid.GetRow(cell) == grid.RowDefinitions.Count - 1)
                    slot.Margin = cell.Margin;
                string location = target.NewColumn ? $"Start a new column at row {target.Row + 1}"
                    : target.ButtonId is not null ? "Drop before " + W.Automation.AutomationProperties.GetName(cell)
                    : target.Row.HasValue ? $"Drop in row {target.Row + 1}" : "Drop at end of column";
                W.Automation.AutomationProperties.SetName(slot, $"{location} · {target.Group.EditorName} · Column {target.Column + 1}");
                C.Grid.SetColumn(slot, C.Grid.GetColumn(cell)); C.Grid.SetRow(slot, C.Grid.GetRow(cell));
                C.Grid.SetRowSpan(slot, C.Grid.GetRowSpan(cell));
                grid.Children.Add(slot); dockDropSlots.Add(slot);
            }
        }
        measureNeeded = true;
        UpdateLayout();
        RefreshLayout();
    }

    private S.Rectangle? DockSlotAt(W.Point point) => dockDropSlots.FirstOrDefault(slot =>
        slot.IsVisible && new W.Rect(slot.RenderSize).Contains(TranslatePoint(point, slot)));

    private DockButtonMove? FindDockDropSlot(W.Point point) => DockSlotAt(point)?.Tag as DockButtonMove;

    private bool HighlightDockDropSlot(W.Point point)
    {
        var slot = DockSlotAt(point);
        if (slot == highlightedDockSlot) return slot is not null;
        if (highlightedDockSlot is { } previous) { previous.Fill = M.Brushes.Transparent; previous.Opacity = .78; }
        highlightedDockSlot = slot;
        if (slot is null) return false;
        var fill = Brush(theme.Accent);
        fill.Opacity = .28;
        slot.Fill = fill; slot.Opacity = 1;
        return true;
    }

    private void HideDockDropSlots()
    {
        highlightedDockSlot = null;
        foreach (var slot in dockDropSlots) (slot.Parent as C.Grid)?.Children.Remove(slot);
        dockDropSlots.Clear();
        foreach (var (element, margin, visibility) in dockDragCells)
        {
            element.Margin = margin;
            element.Visibility = visibility;
        }
        dockDragCells.Clear();
        measureNeeded = true;
    }
}
