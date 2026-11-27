using System.Diagnostics;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using D = System.Windows.Threading;

namespace TeamBar;

// Isolated, animated UI benchmark: no profile writes, game reads, hooks, or input.
internal static class InteractionPerformanceTests
{
    internal static void Run(string output, bool software, bool interactive = false)
    {
        if (File.Exists(output)) throw new IOException("Use a new interaction-performance report.");
        using var access = GameDataAccess.FixtureScope(false);
        // The isolated comparison controls its window's renderer explicitly;
        // do not inherit the application's normal software-rendering policy.
        M.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var window = new W.Window { Title = "Team Bar · Interaction benchmark", Width = 1060, Height = 700,
            WindowStartupLocation = W.WindowStartupLocation.CenterScreen };
        ThemePalette.Apply(window.Resources, AppearanceTheme.Dark);
        window.Resources.MergedDictionaries.Add(new() { Source = new Uri("/TeamBar;component/SpellOverlayStyles.xaml", UriKind.Relative) });
        window.SetResourceReference(C.Control.BackgroundProperty, "Window");
        window.SetResourceReference(C.Control.ForegroundProperty, "Text");
        var columns = new ButtonColumnsEditor();
        columns.SetLayout(Enumerable.Range(0, 48).Select(i => new ButtonPlacement("button-" + i, i / 8)).ToArray(), 6, id => "Saved " + id);
        var slide = new SlideConfirmation("test");
        var root = new C.Grid { Margin = new(24) };
        root.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        var title = new C.StackPanel();
        title.Children.Add(new C.TextBlock { Text = interactive ? "Scroll and slider test · no data is changed" : "Measuring scroll and slider rendering · isolated test", FontSize = 20, Margin = new(0, 0, 0, 12) });
        var useSoftware = new C.CheckBox { Content = "Use CPU drawing (compare with GPU drawing)", IsChecked = software, Margin = new(0, 0, 0, 20), Visibility = interactive ? W.Visibility.Visible : W.Visibility.Collapsed };
        useSoftware.Checked += (_, _) => SetMode(true); useSoftware.Unchecked += (_, _) => SetMode(false);
        title.Children.Add(useSoftware); root.Children.Add(title);
        void SetMode(bool value) { software = value; if (W.PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource source) source.CompositionTarget.RenderMode = value ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default; }
        C.Grid.SetRow(columns, 1); root.Children.Add(columns); C.Grid.SetRow(slide, 2); root.Children.Add(slide); window.Content = root;
        window.SourceInitialized += (_, _) =>
        {
            if (software && W.PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource source)
                source.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        };
        var timer = new D.DispatcherTimer(D.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        var elapsed = new Stopwatch(); var ticks = new List<double>(); var frames = new List<double>(); var work = new List<double>();
        var inputAge = new List<double>(); var pointerGaps = new List<double>(); double lastPointer = 0; int moves = 0;
        window.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { lastPointer = 0; return; }
            double now = elapsed.Elapsed.TotalMilliseconds; moves++;
            inputAge.Add(unchecked((uint)(Environment.TickCount - e.Timestamp)));
            if (lastPointer > 0) pointerGaps.Add(now - lastPointer); lastPointer = now;
        };
        double lastTick = 0, lastFrame = 0;
        var scroll = (C.ScrollViewer)columns.Content;
        EventHandler render = (_, _) => { if (!elapsed.IsRunning) return; double now = elapsed.Elapsed.TotalMilliseconds; if (lastFrame > 500) frames.Add(now - lastFrame); lastFrame = now; };
        timer.Tick += (_, _) =>
        {
            double now = elapsed.Elapsed.TotalMilliseconds;
            if (lastTick > 500) ticks.Add(now - lastTick); lastTick = now;
            if (interactive) return;
            double position = (1 - Math.Cos(now / 800)) / 2;
            var step = Stopwatch.StartNew();
            slide.MovePointer(position * slide.Travel);
            scroll.ScrollToHorizontalOffset(position * scroll.ScrollableWidth);
            work.Add(step.Elapsed.TotalMilliseconds);
            if (now < 8000) return;
            timer.Stop(); elapsed.Stop(); M.CompositionTarget.Rendering -= render; slide.Cancel(); window.Close(); app.Shutdown();
        };
        window.Loaded += (_, _) => { if (!interactive) slide.BeginPointerDrag(0); M.CompositionTarget.Rendering += render; elapsed.Start(); timer.Start(); };
        window.Closed += (_, _) => { timer.Stop(); elapsed.Stop(); M.CompositionTarget.Rendering -= render; app.Shutdown(); };
        app.Run(window);
        object Stats(List<double> values) { var sorted = values.Order().ToArray(); return new { count = sorted.Length, meanMs = sorted.Length == 0 ? 0 : sorted.Average(), p95Ms = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)], maxMs = sorted.Length == 0 ? 0 : sorted[^1] }; }
        CollectorFormat.WriteNew(output, new { software, interactive, moves, inputAge = Stats(inputAge), pointerGaps = Stats(pointerGaps), renderTier = M.RenderCapability.Tier >> 16, uiTicks = Stats(ticks), renderFrames = Stats(frames), updateWork = Stats(work), gameInputSent = false, processMemoryRead = false, profileChanged = false });
    }
}
