using W = System.Windows;
using M = System.Windows.Media;
using I = System.Windows.Input;

namespace TeamBar;

// Interactive diagnostics only: this window has no profile or delete action.
internal static class SlidePreview
{
    internal static void Run(string output)
    {
        if (File.Exists(output)) throw new IOException("Use a new slider-preview report.");
        using var access = GameDataAccess.FixtureScope(false);
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var owner = new W.Window { Title = "Team Bar slider preview", Width = 600, Height = 500, WindowStartupLocation = W.WindowStartupLocation.CenterScreen };
        ThemePalette.Apply(owner.Resources, AppearanceTheme.Dark); owner.Show();
        var dialog = new DeleteConfirmation(owner, new("Slider preview", "No data will be deleted", "Drag the handle to test responsiveness. This isolated preview has no saved buttons and cannot send game input."));
        var slide = dialog.Slide; var track = (W.FrameworkElement)slide.Handle.Parent;
        int pointerUpdates = 0, frames = 0; double origin = 0, maxPointerError = 0, maxFrameGap = 0, previousFrame = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        slide.Handle.DragStarted += (_, _) => { origin = I.Mouse.GetPosition(track).X; previousFrame = 0; };
        slide.Handle.DragDelta += (_, _) =>
        {
            pointerUpdates++;
            double expected = Math.Clamp(I.Mouse.GetPosition(track).X - origin, 0, slide.Travel);
            double rendered = ((M.TranslateTransform)slide.Handle.RenderTransform).X;
            maxPointerError = Math.Max(maxPointerError, Math.Abs(expected - rendered));
        };
        EventHandler render = (_, _) =>
        {
            if (!slide.Handle.IsDragging) return;
            double now = watch.Elapsed.TotalMilliseconds;
            if (previousFrame > 0) maxFrameGap = Math.Max(maxFrameGap, now - previousFrame);
            previousFrame = now; frames++;
        };
        M.CompositionTarget.Rendering += render;
        bool completed;
        try { completed = dialog.ShowDialog() == true; }
        finally { M.CompositionTarget.Rendering -= render; owner.Close(); }
        CollectorFormat.WriteNew(output, new { completed, pointerUpdates, frames, maxPointerErrorDip = maxPointerError, maxFrameGapMs = maxFrameGap, profileChanged = false, gameInputSent = false, gameMemoryRead = false });
    }
}
