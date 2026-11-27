using System.Diagnostics;
using System.Text.Json;
using W = System.Windows;
using I = System.Windows.Input;
using M = System.Windows.Media;
using D = System.Windows.Threading;

namespace TeamBar;

// Opt-in local diagnostics for a specific troubleshooting run. No key values,
// pointer coordinates, character names, or game data are recorded.
internal sealed class UiInteractionTrace : IDisposable
{
    private readonly StreamWriter writer;
    private readonly D.DispatcherTimer timer = new(D.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<double> inputAge = [], inputGaps = [], frameGaps = [], dispatchGaps = [];
    private bool dragging, disposed;
    private double started, lastInput, lastFrame, lastDispatch;
    private string control = "";
    private UiInteractionTrace(string path)
    {
        writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        I.InputManager.Current.PreProcessInput += Input;
        timer.Tick += (_, _) =>
        {
            if (!dragging) return;
            double now = clock.Elapsed.TotalMilliseconds;
            if (lastDispatch > 0) dispatchGaps.Add(now - lastDispatch); lastDispatch = now;
            if (I.Mouse.LeftButton != I.MouseButtonState.Pressed) Finish("release-or-cancel");
        };
    }
    internal static UiInteractionTrace? TryStart()
    {
        string? path = Environment.GetEnvironmentVariable("TEAMBAR_UI_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return new(Path.GetFullPath(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private void Input(object sender, I.PreProcessInputEventArgs args)
    {
        if (disposed || args.StagingItem.Input is not I.MouseEventArgs e) return;
        if (e.RoutedEvent == I.Mouse.PreviewMouseDownEvent && e is I.MouseButtonEventArgs { ChangedButton: I.MouseButton.Left })
        {
            if (dragging) Finish("new-gesture");
            dragging = true; started = clock.Elapsed.TotalMilliseconds;
            control = e.Source?.GetType().Name ?? "Mouse";
            inputAge.Clear(); inputGaps.Clear(); frameGaps.Clear(); dispatchGaps.Clear(); lastInput = lastFrame = lastDispatch = 0;
            M.CompositionTarget.Rendering += Rendering; timer.Start();
        }
        if (!dragging) return;
        if (e.RoutedEvent == I.Mouse.PreviewMouseMoveEvent)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            inputAge.Add(unchecked((uint)(Environment.TickCount - e.Timestamp)));
            if (lastInput > 0) inputGaps.Add(now - lastInput); lastInput = now;
        }
        if (e.RoutedEvent == I.Mouse.PreviewMouseUpEvent && e is I.MouseButtonEventArgs { ChangedButton: I.MouseButton.Left }) Finish("release");
    }
    private void Rendering(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (lastFrame > 0) frameGaps.Add(now - lastFrame); lastFrame = now;
    }
    private static object Stats(List<double> source)
    {
        var values = source.Order().ToArray();
        return new { count = values.Length, p95Ms = values.Length == 0 ? 0 : values[(int)((values.Length - 1) * .95)], maxMs = values.Length == 0 ? 0 : values[^1] };
    }
    private void Finish(string reason)
    {
        if (!dragging) return;
        dragging = false; timer.Stop(); M.CompositionTarget.Rendering -= Rendering;
        if (inputAge.Count < 3) return;
        try { writer.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, control, reason,
            durationMs = clock.Elapsed.TotalMilliseconds - started, renderMode = M.RenderOptions.ProcessRenderMode.ToString(),
            inputAge = Stats(inputAge), inputGaps = Stats(inputGaps), frameGaps = Stats(frameGaps), dispatcherGaps = Stats(dispatchGaps) })); }
        catch (IOException) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        Finish("shutdown"); disposed = true; timer.Stop();
        I.InputManager.Current.PreProcessInput -= Input; M.CompositionTarget.Rendering -= Rendering; writer.Dispose();
    }
}
