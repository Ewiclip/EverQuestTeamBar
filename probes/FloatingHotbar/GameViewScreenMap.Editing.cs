using System.Windows.Automation;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;
using W = System.Windows;

namespace TeamBar;

public sealed partial class GameViewScreenMap
{
    private bool editingSlots;
    private string? selectedSlotDevice;
    private int selectedSlotIndex = -1;
    internal event Action<string, int>? SlotSelected;
    internal event Action<string, int, GameViewMapToken>? SlotDropped;

    internal bool IsEditing
    {
        get => editingSlots;
        set
        {
            if (editingSlots == value) return;
            editingSlots = value;
            if (!value) SelectSlot(null, -1);
            foreach (var tile in tiles.Values) tile.SetEditing(this, value);
            PositionTiles();
        }
    }

    internal void SelectSlot(string? device, int index)
    {
        selectedSlotDevice = device;
        selectedSlotIndex = index;
        foreach (var tile in tiles.Values) tile.Editor?.RefreshSelection();
    }

    internal static void BeginDrag(W.DependencyObject source, GameViewMapToken token) =>
        W.DragDrop.DoDragDrop(source, new W.DataObject(typeof(GameViewMapToken), token), W.DragDropEffects.Move);

    // A sibling of the monitor artwork. Its hit targets share the artwork's snapped pixel edges;
    // the monitor number and star can never intercept a drop or shift it into an adjacent slot.
    private sealed class EditLayer : C.Canvas
    {
        private readonly GameViewScreenMap map;
        private readonly string device;
        private readonly C.Button monitor;
        private readonly List<SlotButton> targets = [];

        internal EditLayer(GameViewScreenMap map, string device)
        {
            this.map = map;
            this.device = device;
            ClipToBounds = true;
            monitor = new C.Button();
            Prepare(monitor);
            string label = MonitorIdentification.Label(device);
            AutomationProperties.SetName(monitor, label);
            monitor.ToolTip = label;
            monitor.Click += (_, _) => map.ScreenSelected?.Invoke(device);
            Children.Add(monitor);
        }

        private static void Prepare(C.Button button)
        {
            button.SetResourceReference(C.Control.TemplateProperty, "ScreenMapTile");
            button.MinWidth = button.MinHeight = 0;
            button.Padding = new W.Thickness(0);
            button.Background = button.BorderBrush = M.Brushes.Transparent;
            button.BorderThickness = new W.Thickness(2);
            button.Cursor = I.Cursors.Hand;
        }

        internal void SetMonitor(int count, double width, double height)
        {
            monitor.Width = width;
            monitor.Height = height;
            while (targets.Count > count)
            {
                Children.Remove(targets[^1]);
                targets.RemoveAt(targets.Count - 1);
            }
            while (targets.Count < count)
            {
                var button = new SlotButton(this, targets.Count);
                Prepare(button);
                targets.Add(button);
                Children.Add(button);
            }
        }

        internal void SetSlot(int index, GameViewMapSlot slot, string description, W.Rect rect, W.FrameworkElement? starIcon)
        {
            var button = targets[index];
            SetLeft(button, rect.X);
            SetTop(button, rect.Y);
            button.Width = rect.Width;
            button.Height = rect.Height;
            button.Tag = slot;
            button.StarIcon = starIcon;
            button.ToolTip = description;
            AutomationProperties.SetName(button, $"{MonitorIdentification.Label(device)} · {description}");
            button.Highlight();
        }

        internal void RefreshSelection()
        {
            foreach (var button in targets) button.Highlight();
        }

        private sealed class SlotButton : C.Button
        {
            private readonly EditLayer layer;
            private readonly int index;
            internal W.FrameworkElement? StarIcon;
            private W.Point down;
            private GameViewMapToken? pendingDrag;
            private bool dragOver;

            internal SlotButton(EditLayer layer, int index)
            {
                this.layer = layer;
                this.index = index;
                AllowDrop = true;
                Click += (_, _) =>
                {
                    if (IsCurrentTarget) layer.map.SlotSelected?.Invoke(layer.device, index);
                };
            }

            private bool IsCurrentTarget => layer.map.IsEditing && layer.Parent == layer.map && Parent == layer;

            internal void Highlight()
            {
                bool selected = StringComparer.OrdinalIgnoreCase.Equals(layer.device, layer.map.selectedSlotDevice) &&
                    index == layer.map.selectedSlotIndex;
                AutomationProperties.SetItemStatus(this, selected ? "Selected" : "");
                if (dragOver || selected) SetResourceReference(BorderBrushProperty, "Accent");
                else BorderBrush = M.Brushes.Transparent;
                BorderThickness = new W.Thickness(dragOver ? 3 : 2);
            }

            protected override void OnPreviewMouseLeftButtonDown(I.MouseButtonEventArgs e)
            {
                down = e.GetPosition(this);
                var slot = (GameViewMapSlot)Tag;
                pendingDrag = slot.Assignment;
                if (slot.SwapTarget && (pendingDrag is null || StarIcon is { } icon &&
                    new W.Rect(icon.RenderSize).Contains(e.GetPosition(icon))))
                    pendingDrag = GameViewMapToken.Star;
                base.OnPreviewMouseLeftButtonDown(e);
            }

            protected override void OnPreviewMouseMove(I.MouseEventArgs e)
            {
                base.OnPreviewMouseMove(e);
                if (pendingDrag is not { } token || e.LeftButton != I.MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - down.X) < W.SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - down.Y) < W.SystemParameters.MinimumVerticalDragDistance) return;
                pendingDrag = null;
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
                BeginDrag(this, token);
            }

            protected override void OnLostMouseCapture(I.MouseEventArgs e)
            {
                pendingDrag = null;
                base.OnLostMouseCapture(e);
            }

            private GameViewMapToken? Token(W.DragEventArgs e) =>
                IsCurrentTarget && (e.AllowedEffects & W.DragDropEffects.Move) != 0 && e.Data.GetDataPresent(typeof(GameViewMapToken))
                ? e.Data.GetData(typeof(GameViewMapToken)) as GameViewMapToken : null;

            protected override void OnDragOver(W.DragEventArgs e)
            {
                dragOver = Token(e) is not null;
                e.Effects = dragOver ? W.DragDropEffects.Move : W.DragDropEffects.None;
                e.Handled = true;
                Highlight();
            }

            protected override void OnDragLeave(W.DragEventArgs e)
            {
                dragOver = false;
                Highlight();
                base.OnDragLeave(e);
            }

            protected override void OnDrop(W.DragEventArgs e)
            {
                dragOver = false;
                Highlight();
                var token = Token(e);
                e.Effects = token is null ? W.DragDropEffects.None : W.DragDropEffects.Move;
                e.Handled = true;
                if (token is not null) layer.map.SlotDropped?.Invoke(layer.device, index, token);
            }
        }
    }
}
