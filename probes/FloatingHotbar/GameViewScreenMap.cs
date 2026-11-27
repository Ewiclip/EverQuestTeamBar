using System.Windows.Automation;
using System.Windows.Automation.Peers;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using W = System.Windows;

namespace TeamBar;

internal sealed record GameViewMapToken(string? Character = null, GameViewWindow? Window = null)
{
    internal static readonly GameViewMapToken Star = new();
}

internal sealed record GameViewMapSlot(Rectangle Bounds, string Label, bool SwapTarget, GameViewMapToken? Assignment = null);

public sealed partial class GameViewScreenMap : C.Canvas
{
    private static readonly M.Geometry star = CreateStar();

    private static M.Geometry CreateStar()
    {
        var shape = M.Geometry.Parse("M 12,1 L 15.3,8.1 23.2,9 17.4,14.5 19,22.4 12,18.4 5,22.4 6.6,14.5 0.8,9 8.7,8.1 Z");
        shape.Freeze();
        return shape;
    }

    internal static System.Windows.Shapes.Path SwapStar(double size)
    {
        var path = new System.Windows.Shapes.Path { Data = star, Stretch = M.Stretch.Uniform, Width = size, Height = size, IsHitTestVisible = false };
        path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SwapTargetStar");
        return path;
    }

    private sealed class Tile(bool readOnly) : C.Button
    {
        private readonly C.Canvas face = new() { ClipToBounds = true, IsHitTestVisible = false };
        private GameViewMapSlot[] slots = [];
        private bool selected;
        private string heading = "";
        internal EditLayer? Editor { get; private set; }

        internal void SetEditing(GameViewScreenMap map, bool edit)
        {
            if ((Editor is not null) == edit) return;
            if (edit)
            {
                Editor = new EditLayer(map, ((GameViewMonitor)Tag).Device);
                SetZIndex(Editor, 1);
                map.Children.Add(Editor);
                DrawContents();
            }
            else
            {
                map.Children.Remove(Editor!);
                Editor = null;
            }
        }

        internal void Initialize()
        {
            SetResourceReference(StyleProperty, typeof(C.Button));
            SetResourceReference(TemplateProperty, "ScreenMapTile");
            MinHeight = MinWidth = 0;
            Padding = new W.Thickness(0);
            Content = face;
            face.SizeChanged += (_, _) => DrawContents();
            if (readOnly)
            {
                Cursor = W.Input.Cursors.Arrow;
                Focusable = false;
                IsTabStop = false;
            }
        }

        protected override void OnClick() { if (!readOnly) base.OnClick(); }
        protected override void OnDpiChanged(W.DpiScale oldDpi, W.DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            DrawContents();
        }
        protected override AutomationPeer OnCreateAutomationPeer() =>
            readOnly ? new FrameworkElementAutomationPeer(this) : base.OnCreateAutomationPeer();

        internal void ShowMonitor(GameViewMonitor monitor, bool isSelected, GameViewMapSlot[] current)
        {
            if (Equals(Tag, monitor) && selected == isSelected && current.SequenceEqual(slots)) return;
            Tag = monitor;
            selected = isSelected;
            slots = current;
            string name = MonitorIdentification.Label(monitor.Device);
            heading = name.StartsWith("Screen ", StringComparison.Ordinal) ? name[7..] : name;
            string capacity = slots.Length switch { 0 => "Unused", 1 => "1 slot", _ => $"{slots.Length} slots" };
            string description = $"{name} · {monitor.Bounds.Width} × {monitor.Bounds.Height} · {capacity}";
            if (selected) description += " · Selected";
            for (int i = 0; i < slots.Length; i++) description += Environment.NewLine + Describe(i, slots[i]);
            ToolTip = description;
            AutomationProperties.SetName(this, description);
            AutomationProperties.SetHelpText(this, description);
            BorderThickness = new W.Thickness(selected ? 2 : 1);
            SetResourceReference(BackgroundProperty, selected ? "SelectedSurface" : "ButtonSurface");
            SetResourceReference(BorderBrushProperty, selected ? "Accent" : "Line");
            DrawContents();
        }

        private static string Describe(int index, GameViewMapSlot slot) =>
            $"Slot {index + 1} · {(slot.Label.Length == 0 ? "Unassigned" : slot.Label)}" + (slot.SwapTarget ? " · Swap target" : "");

        private static C.TextBlock Text(string text, double size, string brush)
        {
            var label = new C.TextBlock { Text = text, FontSize = size, TextAlignment = W.TextAlignment.Center, TextTrimming = W.TextTrimming.CharacterEllipsis,
                VerticalAlignment = W.VerticalAlignment.Center, HorizontalAlignment = W.HorizontalAlignment.Center };
            label.SetResourceReference(C.TextBlock.ForegroundProperty, brush);
            return label;
        }

        private void DrawContents()
        {
            face.Children.Clear();
            double width = face.ActualWidth, height = face.ActualHeight;
            if (Tag is not GameViewMonitor monitor || width < 1 || height < 1) return;
            Editor?.SetMonitor(slots.Length, width, height);
            var header = Text(heading, slots.Length == 0 ? 13 : 11, selected ? "Accent" : "Text");
            header.FontWeight = W.FontWeights.SemiBold;
            if (slots.Length == 0)
            {
                double headerHeight = Math.Min(18, height / 2);
                Place(new C.Viewbox { StretchDirection = C.StretchDirection.DownOnly, Child = header },
                    0, height / 2 - headerHeight, width, headerHeight);
                if (height >= 30) Place(Text("Unused", 10, "Muted"), 0, height / 2, width, Math.Min(16, height / 2));
                return;
            }
            // The face represents the whole physical display, including any taskbar.
            // Round the shared edges so adjoining slots meet at the same device pixel.
            var dpi = M.VisualTreeHelper.GetDpi(face);
            var bounds = monitor.Bounds;
            double X(int x) => Snap(((double)x - bounds.Left) / bounds.Width * width, dpi.DpiScaleX);
            double Y(int y) => Snap(((double)y - bounds.Top) / bounds.Height * height, dpi.DpiScaleY);
            W.Rect? swapTarget = null;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                double x = X(slot.Bounds.Left), y = Y(slot.Bounds.Top);
                double w = X(slot.Bounds.Right) - x, h = Y(slot.Bounds.Bottom) - y;
                if (slot.SwapTarget) swapTarget = new W.Rect(x, y, w, h);
                var visual = DrawSlot(i, slot, w, h, out var starIcon);
                Place(visual, x, y, w, h);
                Editor?.SetSlot(i, slot, Describe(i, slot), new W.Rect(x, y, w, h), starIcon);
            }
            // Overlay the monitor number without taking room from any slot.
            var badge = new C.Border { Child = header, Padding = new W.Thickness(3, 0, 3, 0),
                CornerRadius = new W.CornerRadius(2), IsHitTestVisible = false };
            badge.SetResourceReference(C.Border.BackgroundProperty, "Surface");
            var space = new W.Size(Math.Max(0, width - 4), Math.Max(0, height - 4));
            badge.Measure(space);
            var size = new W.Size(Math.Min(space.Width, Math.Ceiling(badge.DesiredSize.Width) + 1),
                Math.Min(space.Height, Math.Ceiling(badge.DesiredSize.Height)));
            var badgeArea = new W.Rect(new W.Point(Math.Max(0, width - size.Width - 2), 2), size);
            if (swapTarget is { } target && target.IntersectsWith(badgeArea))
            {
                var opposite = new W.Rect(new W.Point(2, 2), size);
                if (!target.IntersectsWith(opposite)) badgeArea = opposite;
            }
            Place(badge, badgeArea.X, badgeArea.Y, badgeArea.Width, badgeArea.Height);
        }

        private static C.Border DrawSlot(int index, GameViewMapSlot slot, double width, double height, out W.FrameworkElement? starIcon)
        {
            starIcon = null;
            var border = new C.Border { Tag = slot, ClipToBounds = true, BorderThickness = new W.Thickness(.8) };
            border.SetResourceReference(C.Border.BackgroundProperty, slot.SwapTarget ? "SwapTargetSurface" : slot.Label.Length > 0 ? "KeySurface" : "Field");
            border.SetResourceReference(C.Border.BorderBrushProperty, slot.SwapTarget ? "SwapTargetBorder" : "PreviewBorder");
            var contents = new C.Grid { IsHitTestVisible = false, ClipToBounds = true };
            string brush = slot.SwapTarget ? "SwapTargetStar" : slot.Label.Length > 0 ? "Text" : "Muted";
            double starSize = slot.SwapTarget ? Math.Min(14, Math.Max(0, Math.Min(width, height) - 3)) : 0;
            bool showText = height >= 12 && width >= 13 + starSize;
            if (showText)
            {
                string text = (index + 1).ToString();
                if (slot.Label.Length > 0 && width - starSize >= 42) text += $" · {slot.Label}";
                else if (slot.Label.Length == 0 && width - starSize >= 85) text += " · Unassigned";
                var label = Text(text, Math.Min(11, height - 2), brush);
                label.Margin = new W.Thickness(starSize > 0 ? starSize + 2 : 1, 0, 1, 0);
                contents.Children.Add(label);
            }
            if (slot.SwapTarget)
            {
                var mark = SwapStar(starSize);
                starIcon = mark;
                if (showText) { mark.HorizontalAlignment = W.HorizontalAlignment.Left; mark.Margin = new W.Thickness(1, 0, 0, 0); }
                contents.Children.Add(mark);
            }
            border.Child = contents;
            border.ToolTip = Describe(index, slot);
            AutomationProperties.SetName(border, Describe(index, slot));
            return border;
        }

        private void Place(W.FrameworkElement element, double x, double y, double width, double height)
        {
            element.Width = width;
            element.Height = height;
            SetLeft(element, x);
            SetTop(element, y);
            face.Children.Add(element);
        }
    }

    private readonly Dictionary<string, Tile> tiles = new(StringComparer.OrdinalIgnoreCase);
    private static double Snap(double value, double dpi) => Math.Round(value * dpi) / dpi;
    public bool IsReadOnly { get; set; }
    internal event Action<string>? ScreenSelected;

    public GameViewScreenMap()
    {
        ClipToBounds = true;
        AutomationProperties.SetName(this, "Connected screens");
        SizeChanged += (_, _) => PositionTiles();
    }
    protected override void OnDpiChanged(W.DpiScale oldDpi, W.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PositionTiles();
    }

    internal void Update(IReadOnlyList<GameViewMonitor> displays, string? selectedDevice, Func<string, GameViewMapSlot[]> slotsForDevice)
    {
        var connected = displays.Where(d => d.Bounds.Width > 0 && d.Bounds.Height > 0)
            .OrderBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top).ToArray();
        var connectedNames = connected.Select(d => d.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in tiles.Keys.Where(d => !connectedNames.Contains(d)).ToArray())
        {
            tiles[device].SetEditing(this, false);
            Children.Remove(tiles[device]);
            tiles.Remove(device);
        }
        for (int i = 0; i < connected.Length; i++)
        {
            var monitor = connected[i];
            if (!tiles.TryGetValue(monitor.Device, out var tile))
            {
                tile = new Tile(IsReadOnly);
                tile.Initialize();
                tile.Click += (_, _) => ScreenSelected?.Invoke(monitor.Device);
                tiles.Add(monitor.Device, tile);
                Children.Add(tile);
            }
            tile.TabIndex = i;
            tile.ShowMonitor(monitor, string.Equals(monitor.Device, selectedDevice, StringComparison.OrdinalIgnoreCase),
                slotsForDevice(monitor.Device));
            tile.SetEditing(this, IsEditing);
        }
        PositionTiles();
    }

    private void PositionTiles()
    {
        if (tiles.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var desktop = tiles.Values.Select(t => ((GameViewMonitor)t.Tag).Bounds).Aggregate(Rectangle.Union);
        double scale = Math.Min(ActualWidth / desktop.Width, ActualHeight / desktop.Height);
        double left = (ActualWidth - desktop.Width * scale) / 2;
        double top = (ActualHeight - desktop.Height * scale) / 2;
        var dpi = M.VisualTreeHelper.GetDpi(this);
        double X(int x) => Snap(left + ((double)x - desktop.Left) * scale, dpi.DpiScaleX);
        double Y(int y) => Snap(top + ((double)y - desktop.Top) * scale, dpi.DpiScaleY);
        foreach (var tile in tiles.Values)
        {
            var bounds = ((GameViewMonitor)tile.Tag).Bounds;
            double x = X(bounds.Left), y = Y(bounds.Top);
            SetLeft(tile, x);
            SetTop(tile, y);
            tile.Width = X(bounds.Right) - x;
            tile.Height = Y(bounds.Bottom) - y;
            if (tile.Editor is { } layer)
            {
                SetLeft(layer, x);
                SetTop(layer, y);
                layer.Width = tile.Width;
                layer.Height = tile.Height;
            }
        }
    }
}
