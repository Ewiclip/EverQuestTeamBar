using W = System.Windows;
using I = System.Windows.Input;
using M = System.Windows.Media;
namespace TeamBar;

public partial class SetupWindow
{
    private sealed record RuleDrag(SetupWindow Owner, string ButtonId, RuleRow Row);
    private RuleDrag? pendingRuleDrag;
    private W.Point ruleDragStart;
    private void RuleGrip_MouseDown(object sender, I.MouseButtonEventArgs e)
    {
        if (selected is null || sender is not W.FrameworkElement { DataContext: RuleRow row }) return;
        pendingRuleDrag = new(this, selected.Id, row); ruleDragStart = e.GetPosition(RuleList); e.Handled = true;
    }
    private void Rule_MouseUp(object sender, I.MouseButtonEventArgs e) => pendingRuleDrag = null;
    private void Rule_MouseMove(object sender, I.MouseEventArgs e)
    {
        if (e.LeftButton != I.MouseButtonState.Pressed) { pendingRuleDrag = null; return; }
        if (pendingRuleDrag is not { } drag) return;
        var point = e.GetPosition(RuleList);
        if (Math.Abs(point.X - ruleDragStart.X) < W.SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - ruleDragStart.Y) < W.SystemParameters.MinimumVerticalDragDistance) return;
        pendingRuleDrag = null;
        W.DragDrop.DoDragDrop(RuleList, new W.DataObject(typeof(RuleDrag), drag), W.DragDropEffects.Move);
    }
    private RuleDrag? ValidRuleDrag(W.DragEventArgs e) => e.Data.GetData(typeof(RuleDrag)) is RuleDrag drag
        && drag.Owner == this && drag.ButtonId == selected?.Id && rows.Contains(drag.Row) ? drag : null;
    private void Rule_DragOver(object sender, W.DragEventArgs e)
    { e.Effects = ValidRuleDrag(e) is null ? W.DragDropEffects.None : W.DragDropEffects.Move; e.Handled = true; }
    private void Rule_Drop(object sender, W.DragEventArgs e)
    {
        e.Handled = true;
        if (ValidRuleDrag(e) is not { } drag) return;
        W.DependencyObject? element = e.OriginalSource as W.DependencyObject;
        while (element is not null && !(element is W.FrameworkElement { DataContext: RuleRow }))
            element = M.VisualTreeHelper.GetParent(element);
        int index = rows.Count;
        if (element is W.FrameworkElement { DataContext: RuleRow target })
        {
            var container = RuleList.ItemContainerGenerator.ContainerFromItem(target) as W.FrameworkElement ?? (W.FrameworkElement)element;
            index = rows.IndexOf(target) + (e.GetPosition(container).Y > container.ActualHeight / 2 ? 1 : 0);
        }
        MoveRule(drag.Row, index);
    }
    private void MoveRule(RuleRow row, int insertionIndex)
    {
        int from = rows.IndexOf(row); if (from < 0) return;
        int to = Math.Clamp(insertionIndex - (insertionIndex > from ? 1 : 0), 0, rows.Count - 1);
        if (from == to) return;
        rows.Move(from, to); CaptureRules(); MarkChanged();
    }
}
