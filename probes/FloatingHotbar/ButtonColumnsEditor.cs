using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
namespace TeamBar;

internal sealed class ButtonColumnsEditor : C.UserControl
{
    private ButtonPlacement[] entries = [];
    private int columns;
    private Func<string, string> label = id => id;
    private readonly C.StackPanel root = new() { Orientation = C.Orientation.Horizontal };
    private W.Point? dragStart;
    private string? dragId;
    private string[] columnNames = [];
    internal event Action<ButtonPlacement[]>? Changed;
    internal event Action<string>? EditRequested;
    internal event Action? ColumnAdded;
    internal event Action<int, string>? ColumnRenamed;
    internal event Action<int>? EmptyColumnRemoved;
    internal event Action<int>? AddRequested;
    internal Func<W.Window, DeleteRequest, bool> ConfirmRemoval = DeleteConfirmation.Ask;
    internal int EntryCount => entries.Length;
    internal int MaxEntries { get; set; } = int.MaxValue;
    private sealed record DragItem(ButtonColumnsEditor Owner, string Button);
    internal ButtonColumnsEditor()
    {
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/TeamBar;component/GroupEditorStyles.xaml", UriKind.Relative) });
        Content = new ColumnScrollViewer { Content = root };
    }
    internal void SetLayout(ButtonPlacement[] value, int count, Func<string, string> names, string[]? headers = null)
    {
        entries = value; columns = count; label = names; columnNames = ButtonColumns.Names(headers, count); Render();
    }
    private void Change(ButtonPlacement[] value)
    {
        ButtonColumns.Validate(value, columns); entries = value; Render(); Changed?.Invoke(entries);
    }
    private void Render()
    {
        root.Children.Clear(); dragStart = null;
            for (int col = 0; col < columns; col++)
            {
                int column = col;
                var stack = new C.StackPanel { Width = 224, Margin = new(0, 0, 14, 0) };
                var header = new EditableTitle(columnNames[col], $"column {col + 1}") { Margin = new(0, 0, 0, 12), MinHeight = 36, HorizontalAlignment = W.HorizontalAlignment.Left, MaxWidth = 224 };
                header.Changed += value => { columnNames[column] = value; ColumnRenamed?.Invoke(column, value); };
                var menu = new C.ContextMenu(); var removeColumn = new C.MenuItem { Header = "Remove empty column", IsEnabled = columns > 1 && !entries.Any(e => e.Column == column) };
                removeColumn.Click += (_, _) => EmptyColumnRemoved?.Invoke(column); menu.Items.Add(removeColumn); header.ContextMenu = menu;
                stack.Children.Add(header);
                var list = new C.ListBox { MinHeight = 40, MaxHeight = 320, AllowDrop = true, Tag = column, BorderThickness = new(0),
                    HorizontalContentAlignment = W.HorizontalAlignment.Stretch, Padding = new(3) };
                list.Background = System.Windows.Media.Brushes.Transparent;
                list.ItemContainerStyle = (W.Style)FindResource("GroupColumnRow");
                list.SetResourceReference(C.Control.ForegroundProperty, "Text");
                C.ScrollViewer.SetHorizontalScrollBarVisibility(list, C.ScrollBarVisibility.Disabled);
                W.Automation.AutomationProperties.SetName(list, $"Buttons in column {col + 1}");
                foreach (var entry in entries.Where(e => e.Column == col))
                {
                    string id = entry.ButtonId;
                    var row = new C.Grid { Tag = id, MinHeight = 36, Margin = new(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new() { Width = new(22) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new(26) });
                    var grip = new C.TextBlock { Text = "⠿", FontSize = 20, Cursor = I.Cursors.SizeAll, ToolTip = "Drag to reorder or move to another column" };
                    grip.SetResourceReference(C.TextBlock.ForegroundProperty, "Muted"); grip.VerticalAlignment = W.VerticalAlignment.Center;
                    grip.PreviewMouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(this); dragId = id; e.Handled = true; }; row.Children.Add(grip);
                    var name = new C.TextBlock { Text = label(id), TextTrimming = W.TextTrimming.CharacterEllipsis, TextWrapping = W.TextWrapping.NoWrap, ToolTip = label(id), VerticalAlignment = W.VerticalAlignment.Center };
                    if (EditRequested is not null)
                    {
                        var edit = new C.Button { Content = name, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new(0),
                            Padding = new(1, 2, 1, 2), MinHeight = 24, HorizontalContentAlignment = W.HorizontalAlignment.Left,
                            ToolTip = "Edit this shared button's mappings and visibility" };
                        edit.SetResourceReference(C.Control.ForegroundProperty, "Text");
                        edit.Style = (W.Style)FindResource("GroupTextButton");
                        W.Automation.AutomationProperties.SetName(edit, "Edit " + label(id));
                        edit.Click += (_, _) => EditRequested?.Invoke(id);
                        C.Grid.SetColumn(edit, 1); row.Children.Add(edit);
                    }
                    else { C.Grid.SetColumn(name, 1); row.Children.Add(name); }
                    var remove = new C.Button { Content = "×", Padding = new(2, 0, 2, 0), MinHeight = 22, ToolTip = "Remove from this group only" };
                    C.Grid.SetColumn(remove, 2); row.Children.Add(remove);
                    W.Automation.AutomationProperties.SetName(remove, "Remove " + label(id) + " from group");
                    remove.Click += (_, _) =>
                    {
                        if (W.Window.GetWindow(this) is not { } owner) return;
                        if (ConfirmRemoval(owner, new("Remove from this group?", label(id), "The saved button stays in your library and in any other groups.", "remove")))
                            Change(entries.Where(e => e.ButtonId != id).ToArray());
                    };
                    list.Items.Add(row);
                }
                list.ToolTip = "Drag a dotted grip to reorder. Keyboard: select a row, then Alt + arrows to reorder or move columns.";
                list.PreviewKeyDown += (_, e) =>
                {
                    if (I.Keyboard.Modifiers != I.ModifierKeys.Alt || list.SelectedItem is not C.Grid { Tag: string id }) return;
                    var key = e.Key == I.Key.System ? e.SystemKey : e.Key;
                    if (key is not (I.Key.Up or I.Key.Down or I.Key.Left or I.Key.Right)) return;
                    MoveWithKeyboard(id, key == I.Key.Up ? -1 : key == I.Key.Down ? 1 : 0, key == I.Key.Left ? -1 : key == I.Key.Right ? 1 : 0);
                    e.Handled = true;
                };
                list.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton != I.MouseButtonState.Pressed) { dragStart = null; return; }
                    if (dragStart is not { } start || dragId is not { } id) return;
                    var point = e.GetPosition(this);
                    if (Math.Abs(point.X - start.X) < W.SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(point.Y - start.Y) < W.SystemParameters.MinimumVerticalDragDistance) return;
                    dragStart = null;
                    W.DragDrop.DoDragDrop(this, new W.DataObject(typeof(DragItem), new DragItem(this, id)), W.DragDropEffects.Move);
                };
                list.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
                list.DragOver += (_, e) => { e.Effects = Valid(e) ? W.DragDropEffects.Move : W.DragDropEffects.None; e.Handled = true; };
                list.Drop += (_, e) =>
                {
                    if (Valid(e) && e.Data.GetData(typeof(DragItem)) is DragItem drag)
                    {
                        string? before = null;
                        if (C.ItemsControl.ContainerFromElement(list, e.OriginalSource as W.DependencyObject) is C.ListBoxItem { Content: C.Grid { Tag: string target } } item)
                        {
                            var inColumn = entries.Where(p => p.Column == column).Select(p => p.ButtonId).ToList();
                            int index = inColumn.IndexOf(target) + (e.GetPosition(item).Y > item.ActualHeight / 2 ? 1 : 0);
                            before = index < inColumn.Count ? inColumn[index] : null;
                        }
                        if (before != drag.Button) Change(ButtonColumns.Move(entries, drag.Button, column, before));
                    }
                    e.Handled = true;
                };
                var content = new C.StackPanel(); content.Children.Add(list);
                var add = new C.Button { Content = "+ Button", Margin = new(8), Padding = new(8, 8, 8, 8), ToolTip = "Choose a saved button for " + columnNames[col] };
                add.IsEnabled = entries.Length < MaxEntries;
                W.Automation.AutomationProperties.SetName(add, "Add button to " + columnNames[col]);
                add.Click += (_, _) => AddRequested?.Invoke(column); content.Children.Add(add);
                var frame = new C.Border { CornerRadius = new(7), BorderThickness = new(1), Child = content };
                frame.SetResourceReference(C.Border.BackgroundProperty, "Field"); frame.SetResourceReference(C.Border.BorderBrushProperty, "Line"); stack.Children.Add(frame);
                root.Children.Add(stack);
            }
        var addColumn = new C.Button { Content = "+ Column", VerticalAlignment = W.VerticalAlignment.Top, Height = 36, Padding = new(10, 4, 10, 4), IsEnabled = columns < ButtonColumns.MaxColumns,
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new(0), ToolTip = columns < ButtonColumns.MaxColumns ? "Add a column" : "Up to six columns per group" };
        addColumn.SetResourceReference(C.Control.ForegroundProperty, "Accent"); W.Automation.AutomationProperties.SetName(addColumn, "Add column");
        addColumn.Click += (_, _) => ColumnAdded?.Invoke(); root.Children.Add(addColumn);
    }
    internal void MoveWithKeyboard(string id, int rowDelta, int columnDelta)
    {
        int col = entries.Single(e => e.ButtonId == id).Column;
        if (columnDelta != 0) { int next = Math.Clamp(col + columnDelta, 0, columns - 1); if (next != col) Change(ButtonColumns.Move(entries, id, next)); }
        else
        {
            var ids = entries.Where(e => e.Column == col).Select(e => e.ButtonId).ToArray(); int old = Array.IndexOf(ids, id), next = old + rowDelta;
            if (next < 0 || next >= ids.Length) return;
            Change(ButtonColumns.Move(entries, id, col, rowDelta < 0 ? ids[next] : next + 1 < ids.Length ? ids[next + 1] : null));
        }
        foreach (var stack in root.Children.OfType<C.StackPanel>())
            foreach (var list in ((stack.Children.OfType<C.Border>().Single().Child as C.StackPanel)!).Children.OfType<C.ListBox>())
                if (list.Items.OfType<C.Grid>().FirstOrDefault(r => r.Tag as string == id) is { } row)
                { list.SelectedItem = row; list.UpdateLayout(); (list.ItemContainerGenerator.ContainerFromItem(row) as C.ListBoxItem)?.Focus(); }
    }
    private bool Valid(W.DragEventArgs e) => e.Data.GetData(typeof(DragItem)) is DragItem drag && drag.Owner == this && entries.Any(p => p.ButtonId == drag.Button);
}
