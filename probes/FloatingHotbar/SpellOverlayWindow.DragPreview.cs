using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private readonly C.Canvas dockDragPreviewLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private double dockDragGrabX;

    private C.Grid WithDockDragPreview(C.Viewbox content)
    {
        var layers = new C.Grid();
        layers.Children.Add(content);
        layers.Children.Add(dockDragPreviewLayer);
        return layers;
    }

    private void ShowDockDragPreview(C.Button source, W.Point origin, W.Point pointer)
    {
        HideDockDragPreview();
        W.FrameworkElement face = source.Parent as C.Border ?? (W.FrameworkElement)source;
        var bounds = face.TransformToVisual(dockDragPreviewLayer).TransformBounds(new W.Rect(face.RenderSize));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Snapshot once, before the original dims and the insertion slots rearrange its column.
        var dpi = M.VisualTreeHelper.GetDpi(dockDragPreviewLayer);
        var pixels = new Imaging.RenderTargetBitmap((int)Math.Ceiling(bounds.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, M.PixelFormats.Pbgra32);
        var drawing = new M.DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var color = Brush(theme.Button).Color;
            var backing = new M.SolidColorBrush(M.Color.FromRgb(color.R, color.G, color.B));
            var outline = new W.Rect(.5, .5, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
            context.DrawRoundedRectangle(backing, null, outline, 5, 5);
            context.DrawRectangle(new M.VisualBrush(face) { Stretch = M.Stretch.Fill }, null, new W.Rect(bounds.Size));
            context.DrawRoundedRectangle(null, new M.Pen(Brush(theme.Accent), 1), outline, 5, 5);
        }
        pixels.Render(drawing);
        pixels.Freeze();
        var image = new C.Image { Source = pixels, Width = bounds.Width, Height = bounds.Height, Stretch = M.Stretch.Fill,
            IsHitTestVisible = false };
        dockDragGrabX = Math.Clamp(TranslatePoint(origin, dockDragPreviewLayer).X - bounds.Left, 0, bounds.Width);
        dockDragPreviewLayer.Children.Add(image);
        MoveDockDragPreview(pointer);
    }

    private void MoveDockDragPreview(W.Point pointer)
    {
        if (dockDragPreviewLayer.Children.Count == 0) return;
        var preview = (C.Image)dockDragPreviewLayer.Children[0];
        var cursor = TranslatePoint(pointer, dockDragPreviewLayer);
        bool inside = new W.Rect(dockDragPreviewLayer.RenderSize).Contains(cursor);
        preview.Visibility = inside ? W.Visibility.Visible : W.Visibility.Hidden;
        if (!inside) return;

        // Leave the slot under the pointer uncovered. Near the bottom, show the button above it.
        const double gap = 9;
        double top = cursor.Y + gap + preview.Height <= dockDragPreviewLayer.ActualHeight
            ? cursor.Y + gap : cursor.Y - gap - preview.Height;
        C.Canvas.SetTop(preview, Math.Max(0, top));
        C.Canvas.SetLeft(preview, Math.Clamp(cursor.X - dockDragGrabX, 0, Math.Max(0, dockDragPreviewLayer.ActualWidth - preview.Width)));
    }

    private void HideDockDragPreview() => dockDragPreviewLayer.Children.Clear();
}
