using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private bool refreshingRules;
    private int CurrentRuleIndex => (WatchList.SelectedItem as SpellWatchChoice)?.Index ?? -1;
    private static bool InSection(SpellWatch rule, SpellTargetKind section) => rule.Target == section || rule.Target == SpellTargetKind.Either;
    private void SelectRule(int index, string? section = null)
    {
        var choices = WatchList.Items.OfType<SpellWatchChoice>().Where(c => c.Index == index).ToArray();
        WatchList.SelectedItem = choices.FirstOrDefault(c => c.Section == section) ?? choices.FirstOrDefault();
    }
    private bool CurrentChoice(SpellWatchChoice? choice) => selected is not null && choice is not null
        && choice.Index >= 0 && choice.Index < selected.Spells.Length && ReferenceEquals(selected.Spells[choice.Index], choice.Rule);
    private void ShowConfigure() { SpellTabs.SelectedItem = ConfigureTab; ConfigureScroll.ScrollToTop(); }
    private void ShowAdd_Click(object sender, W.RoutedEventArgs e) { SpellTabs.SelectedItem = AddTab; SpellSearch.Focus(); }
    private void Watch_MouseDown(object sender, I.MouseButtonEventArgs e)
    {
        if (C.ItemsControl.ContainerFromElement(WatchList, e.OriginalSource as W.DependencyObject) is C.ListBoxItem)
            ShowConfigure();
    }
    private void Watch_KeyDown(object sender, I.KeyEventArgs e)
    {
        if (I.Keyboard.Modifiers != I.ModifierKeys.Alt) return;
        var key = e.Key == I.Key.System ? e.SystemKey : e.Key;
        if (key is I.Key.Up or I.Key.Down) { Move(key == I.Key.Up ? -1 : 1); e.Handled = true; }
    }
    private bool MoveRule(SpellWatchChoice source, SpellWatchChoice target, bool after)
    {
        if (!CurrentChoice(source) || !CurrentChoice(target) || source.Section != target.Section || source.Index == target.Index) return false;
        var kind = source.Section == "Player" ? SpellTargetKind.Player : SpellTargetKind.Npc;
        var indices = selected!.Spells.Select((r, i) => (r, i)).Where(e => InSection(e.r, kind)).Select(e => e.i).ToArray();
        var ordered = indices.Select(i => selected.Spells[i]).ToList();
        int from = Array.IndexOf(indices, source.Index), to = Array.IndexOf(indices, target.Index) + (after ? 1 : 0);
        if (from < to) to--;
        if (from == to) return false;
        ordered.RemoveAt(from); ordered.Insert(to, source.Rule);
        var rules = selected.Spells.ToArray();
        for (int i = 0; i < indices.Length; i++) rules[indices[i]] = ordered[i];
        Replace(selected with { Spells = rules }); RefreshRules(indices[to]);
        SelectRule(indices[to], source.Section); WatchList.ScrollIntoView(WatchList.SelectedItem);
        return true;
    }

    private sealed record SpellDrag(SpellOverlayManagerWindow Owner, string OverlayId, SpellWatchChoice Source);
    private (SpellDrag Drag, W.Point Start)? pendingDrag;
    private DropLine? dropLine;
    private long lastDragScroll;
    private void Grip_MouseDown(object sender, I.MouseButtonEventArgs e)
    {
        if (selected is not null && sender is W.FrameworkElement { DataContext: SpellWatchChoice choice })
            pendingDrag = (new(this, selected.Id, choice), e.GetPosition(WatchList));
    }
    private void Watch_MouseMove(object sender, I.MouseEventArgs e)
    {
        if (e.LeftButton != I.MouseButtonState.Pressed) { pendingDrag = null; return; }
        if (pendingDrag is not { } pending) return;
        var delta = e.GetPosition(WatchList) - pending.Start;
        if (Math.Abs(delta.X) < W.SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < W.SystemParameters.MinimumVerticalDragDistance) return;
        pendingDrag = null;
        if (!ValidDrag(pending.Drag)) return;
        try { W.DragDrop.DoDragDrop(WatchList, new W.DataObject(typeof(SpellDrag), pending.Drag), W.DragDropEffects.Move); }
        finally { ClearDropLine(); }
        e.Handled = true;
    }
    private bool ValidDrag(SpellDrag drag) => ReferenceEquals(drag.Owner, this) && drag.OverlayId == selected?.Id && CurrentChoice(drag.Source);
    private (SpellDrag Drag, SpellWatchChoice Target, C.ListBoxItem Item, bool After)? DropTarget(W.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(SpellDrag)) || e.Data.GetData(typeof(SpellDrag)) is not SpellDrag drag || !ValidDrag(drag)) return null;
        if (C.ItemsControl.ContainerFromElement(WatchList, e.OriginalSource as W.DependencyObject) is not C.ListBoxItem { DataContext: SpellWatchChoice target } item
            || !CurrentChoice(target) || drag.Source.Section != target.Section) return null;
        return (drag, target, item, e.GetPosition(item).Y >= item.ActualHeight / 2);
    }
    private void Watch_DragOver(object sender, W.DragEventArgs e)
    {
        e.Handled = true; ClearDropLine();
        var target = DropTarget(e); e.Effects = target is null ? W.DragDropEffects.None : W.DragDropEffects.Move;
        if (target is { } value)
        {
            dropLine = new(value.Item, value.After, (M.Brush)FindResource("Accent"));
            System.Windows.Documents.AdornerLayer.GetAdornerLayer(value.Item)?.Add(dropLine);
        }
        if (e.Data.GetDataPresent(typeof(SpellDrag)) && e.Data.GetData(typeof(SpellDrag)) is SpellDrag drag && ValidDrag(drag)
            && Environment.TickCount64 - lastDragScroll >= 80 && FindScroll(WatchList) is { } scroll)
        {
            double y = e.GetPosition(WatchList).Y;
            if (y < 32) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 22);
            else if (y > WatchList.ActualHeight - 32) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 22);
            lastDragScroll = Environment.TickCount64;
        }
    }
    private void Watch_Drop(object sender, W.DragEventArgs e)
    {
        e.Handled = true; ClearDropLine();
        if (DropTarget(e) is { } value) MoveRule(value.Drag.Source, value.Target, value.After);
    }
    private void Watch_DragLeave(object sender, W.DragEventArgs e) => ClearDropLine();
    private void ClearDropLine()
    {
        if (dropLine is null) return;
        System.Windows.Documents.AdornerLayer.GetAdornerLayer(dropLine.AdornedElement)?.Remove(dropLine); dropLine = null;
    }
    private static C.ScrollViewer? FindScroll(W.DependencyObject root)
    {
        if (root is C.ScrollViewer scroll) return scroll;
        for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(M.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private sealed class DropLine(W.UIElement element, bool after, M.Brush brush) : System.Windows.Documents.Adorner(element)
    {
        protected override void OnRender(M.DrawingContext context)
        {
            IsHitTestVisible = false;
            double y = after ? AdornedElement.RenderSize.Height - 1 : 1;
            context.DrawLine(new(brush, 2), new(0, y), new(AdornedElement.RenderSize.Width, y));
        }
    }
}
