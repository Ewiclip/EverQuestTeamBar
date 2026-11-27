using W = System.Windows;
using C = System.Windows.Controls;
using P = System.Windows.Controls.Primitives;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace TeamBar;

// Completing the movement only arms confirmation. Release is required; clicks
// on the track, Enter, and canceled gestures never confirm an action.
internal sealed class SlideConfirmation : C.UserControl
{
    private readonly C.Canvas canvas = new() { Height = 52, ClipToBounds = true };
    private readonly C.Border progress = new() { Height = 52, CornerRadius = new(9), Opacity = .18, IsHitTestVisible = false };
    private readonly C.TextBlock caption = new() { HorizontalAlignment = W.HorizontalAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center,
        FontSize = 14, FontWeight = W.FontWeights.SemiBold, IsHitTestVisible = false };
    private readonly C.TextBlock readyCaption = new() { HorizontalAlignment = W.HorizontalAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center,
        FontSize = 14, FontWeight = W.FontWeights.SemiBold, IsHitTestVisible = false, Opacity = 0 };
    private readonly M.TranslateTransform handlePosition = new();
    private readonly M.ScaleTransform progressScale = new(0, 1);
    internal readonly P.Thumb Handle = new() { Width = 46, Height = 44, Focusable = true, Cursor = I.Cursors.SizeWE };
    private readonly W.Threading.DispatcherTimer keyboardTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private bool dragging, keyboard;
    private double value, dragOrigin;
    private long keyboardStart;
    internal Func<long> Clock = () => Environment.TickCount64;
    internal event Action? Confirmed;
    internal double Progress => value;
    internal double Travel => Math.Max(0, canvas.ActualWidth - Handle.Width - 8);

    internal SlideConfirmation(string verb)
    {
        var grid = new C.Grid();
        var track = new C.Border { CornerRadius = new(9), BorderThickness = new(1) };
        track.SetResourceReference(C.Border.BackgroundProperty, "Field"); track.SetResourceReference(C.Border.BorderBrushProperty, "Line");
        progress.SetResourceReference(C.Border.BackgroundProperty, "Error");
        caption.SetResourceReference(C.TextBlock.ForegroundProperty, "Text");
        readyCaption.SetResourceReference(C.TextBlock.ForegroundProperty, "Text");
        caption.Text = "Slide to " + verb; readyCaption.Text = "Release to " + verb;
        Handle.RenderTransform = handlePosition; progress.RenderTransform = progressScale;
        var shell = new W.FrameworkElementFactory(typeof(C.Border));
        shell.Name = "HandleShell"; shell.SetValue(C.Border.CornerRadiusProperty, new W.CornerRadius(7));
        shell.SetResourceReference(C.Border.BackgroundProperty, "ButtonSurface"); shell.SetResourceReference(C.Border.BorderBrushProperty, "Error");
        shell.SetValue(C.Border.BorderThicknessProperty, new W.Thickness(1));
        var arrow = new W.FrameworkElementFactory(typeof(C.TextBlock)); arrow.SetValue(C.TextBlock.TextProperty, "→");
        arrow.SetValue(C.TextBlock.FontSizeProperty, 24d); arrow.SetResourceReference(C.TextBlock.ForegroundProperty, "Error");
        arrow.SetValue(W.FrameworkElement.HorizontalAlignmentProperty, W.HorizontalAlignment.Center);
        arrow.SetValue(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center); shell.AppendChild(arrow);
        var template = new C.ControlTemplate(typeof(P.Thumb)) { VisualTree = shell };
        var focus = new W.Trigger { Property = W.UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new W.Setter(C.Border.BorderThicknessProperty, new W.Thickness(3), "HandleShell")); template.Triggers.Add(focus);
        Handle.Template = template;
        W.Automation.AutomationProperties.SetName(Handle, "Slide to " + verb);
        W.Automation.AutomationProperties.SetHelpText(Handle, "Drag all the way right and release. With keyboard focus, hold Right Arrow until the handle reaches the right edge, then release.");
        canvas.Children.Add(progress); canvas.Children.Add(Handle); C.Canvas.SetTop(Handle, 4); C.Canvas.SetLeft(Handle, 4);
        grid.Children.Add(track); grid.Children.Add(caption); grid.Children.Add(readyCaption); grid.Children.Add(canvas); Content = grid;
        // Thumb deltas are relative to the thumb itself. Use the stationary track
        // instead so deferred layout/rendering cannot accumulate pointer error.
        Handle.DragStarted += (_, _) => { Handle.Focus(); BeginPointerDrag(I.Mouse.GetPosition(canvas).X); };
        Handle.DragDelta += (_, _) => MovePointer(I.Mouse.GetPosition(canvas).X);
        Handle.DragCompleted += (_, e) => { if (!e.Canceled) MovePointer(I.Mouse.GetPosition(canvas).X); EndDrag(e.Canceled); };
        Handle.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == I.Key.Right) { if (!e.IsRepeat) BeginKeyboard(); e.Handled = true; }
            else if (e.Key is I.Key.Left or I.Key.Home or I.Key.End or I.Key.Enter or I.Key.Space) { Cancel(); e.Handled = true; }
        };
        Handle.PreviewKeyUp += (_, e) => { if (e.Key == I.Key.Right) { EndKeyboard(); e.Handled = true; } };
        Handle.LostKeyboardFocus += (_, _) => { if (keyboard) Cancel(); };
        keyboardTimer.Tick += (_, _) => AdvanceKeyboard();
        canvas.SizeChanged += (_, _) => { Cancel(); progress.Width = canvas.ActualWidth; };
        Unloaded += (_, _) => Cancel();
        Draw();
    }
    internal void BeginDrag() { Cancel(); dragging = Travel > 0; }
    internal void DragBy(double pixels) { if (dragging && Travel > 0) { value = Math.Clamp(value + pixels / Travel, 0, 1); Draw(); } }
    internal void BeginPointerDrag(double x) { BeginDrag(); dragOrigin = x; }
    internal void MovePointer(double x) { if (dragging && Travel > 0) { value = Math.Clamp((x - dragOrigin) / Travel, 0, 1); Draw(); } }
    internal void EndDrag(bool canceled) { if (!dragging) return; Finish(!canceled); }
    internal void BeginKeyboard()
    {
        if (dragging || keyboard) return;
        keyboard = true; keyboardStart = Clock(); value = 0; keyboardTimer.Start(); Draw();
    }
    internal void AdvanceKeyboard() { if (keyboard) { value = Math.Clamp((Clock() - keyboardStart) / 1200d, 0, 1); Draw(); } }
    internal void EndKeyboard() { if (!keyboard) return; AdvanceKeyboard(); Finish(true); }
    private void Finish(bool released)
    {
        bool complete = released && value >= 1;
        Cancel(); if (complete) Confirmed?.Invoke();
    }
    internal void Cancel() { dragging = keyboard = false; keyboardTimer.Stop(); value = 0; Draw(); }
    private void Draw()
    {
        double x = 4 + value * Travel;
        // Render-only updates: moving a handle must not remeasure or rearrange
        // the SizeToContent dialog on every mouse event. Both captions stay laid out.
        handlePosition.X = value * Travel;
        progressScale.ScaleX = value > 0 && canvas.ActualWidth > 0 ? (x + Handle.Width) / canvas.ActualWidth : 0;
        caption.Opacity = value >= 1 ? 0 : 1; readyCaption.Opacity = value >= 1 ? 1 : 0;
    }
}
