using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

// Only the small corner lock accepts clicks while the spell display is locked.
internal sealed class SpellOverlayLockWindow : W.Window
{
    private readonly C.Button button = new() { Focusable = false, Padding = new(6), BorderThickness = new(0) };
    private readonly System.Windows.Shapes.Path icon = new() { StrokeThickness = 1.6, Width = 14, Height = 16, Stretch = M.Stretch.Uniform };
    private readonly M.SolidColorBrush accent;
    private readonly C.Primitives.UniformGrid controls = new();
    private readonly C.Button pencil;
    private readonly C.TextBlock pencilGlyph;
    private bool displayClosing;
    private string? stateKey;
    internal int ButtonCount => controls.Columns;
    internal SpellOverlayLockWindow(SpellOverlayWindow display, FloatingTheme theme, Action? openSettings = null, bool showSettings = false)
    {
        Title = "Team Bar · Overlay lock"; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        WindowStyle = W.WindowStyle.None; ResizeMode = W.ResizeMode.NoResize; AllowsTransparency = true;
        Background = M.Brushes.Transparent; UseLayoutRounding = true; SnapsToDevicePixels = true;
        M.SolidColorBrush Brush(string value) => new((M.Color)M.ColorConverter.ConvertFromString(value));
        accent = Brush(theme.Accent); icon.Stroke = accent;
        // A zero-alpha layered-window pixel passes native mouse input through to the game.
        // Keep only the minimum hit-test alpha; never paint an opaque button over the translucent display.
        var surface = (M.Color)M.ColorConverter.ConvertFromString(theme.Bar);
        button.Background = new M.SolidColorBrush(M.Color.FromArgb(1, surface.R, surface.G, surface.B));
        button.Content = icon; button.Cursor = System.Windows.Input.Cursors.Hand;
        button.FocusVisualStyle = null;
        // Replace Windows button chrome so hover/press cannot bring back a colored square.
        button.Template = (C.ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}">
                <Viewbox Stretch="Uniform" StretchDirection="Both">
                  <Border Width="28" Height="28" Padding="{TemplateBinding Padding}">
                    <ContentPresenter x:Name="Glyph" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                      HorizontalAlignment="Center" VerticalAlignment="Center" Opacity="0.85"/>
                  </Border>
                </Viewbox>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Glyph" Property="Opacity" Value="1"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Glyph" Property="Opacity" Value="0.6"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        button.Click += (_, _) => display.ToggleLayoutLock();
        controls.Columns = showSettings ? 2 : 1;
        if (showSettings)
        {
            var gear = new C.Button { Focusable = false, FocusVisualStyle = null, Padding = new(4), BorderThickness = new(0),
                Background = button.Background, Template = button.Template, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Open this spell overlay's settings", Content = new C.TextBlock { Text = "⚙", FontFamily = new("Segoe UI Symbol"), FontSize = 21, Foreground = accent } };
            W.Automation.AutomationProperties.SetName(gear, "Open spell overlay settings");
            gear.Click += (_, _) => openSettings?.Invoke(); controls.Children.Add(gear);
        }
        pencilGlyph = new C.TextBlock { Text = "✎", FontFamily = new("Segoe UI Symbol"), FontSize = 21, Foreground = accent };
        pencil = new C.Button { Focusable = false, FocusVisualStyle = null, Padding = new(4), BorderThickness = new(0),
            Background = button.Background, Template = button.Template, Cursor = System.Windows.Input.Cursors.Hand, Content = pencilGlyph };
        pencil.Click += (_, _) => display.ToggleButtonEditing();
        if (showSettings) { controls.Children.Add(button); Content = controls; }
        else Content = button;
        Closing += (_, e) => { if (!displayClosing) { e.Cancel = true; display.ToggleLayoutLock(true); } };
    }
    internal void CloseWithDisplay() { displayClosing = true; Close(); }
    internal void SetButtonEditing(bool available, bool editing)
    {
        if (available && Content == button)
        {
            Content = null; controls.Children.Add(button); Content = controls;
        }
        if (available && !controls.Children.Contains(pencil)) controls.Children.Insert(controls.Children.IndexOf(button), pencil);
        if (!available && controls.Children.Contains(pencil)) controls.Children.Remove(pencil);
        controls.Columns = Math.Max(1, controls.Children.Count);
        var action = editing ? "Done editing buttons" : "Edit buttons";
        pencilGlyph.Text = editing ? "✓" : "✎";
        pencil.ToolTip = action;
        W.Automation.AutomationProperties.SetName(pencil, action);
    }
    internal void SetTheme(FloatingTheme theme)
    {
        accent.Color = (M.Color)M.ColorConverter.ConvertFromString(theme.Accent);
        var surface = (M.Color)M.ColorConverter.ConvertFromString(theme.Bar);
        ((M.SolidColorBrush)button.Background).Color = M.Color.FromArgb(1, surface.R, surface.G, surface.B);
        stateKey = null;
    }
    internal void SetDefinition(SpellOverlayDefinition definition, string? error = null)
    {
        string key = $"{definition.Locked}:{error}";
        if (stateKey == key) return;
        stateKey = key;
        string action = definition.Locked ? "Unlock moving and resizing" : "Lock moving and resizing";
        button.ToolTip = error is not null ? "Layout not saved: " + error
            : action + (definition.Locked ? "" : " · automatically locks after 15 seconds without movement");
        W.Automation.AutomationProperties.SetName(button, action);
        icon.Stroke = error is null ? accent : new M.SolidColorBrush(M.Color.FromRgb(237, 136, 146));
        icon.Data = M.Geometry.Parse(definition.Locked
            ? "M4,8 L4,5 C4,0 12,0 12,5 L12,8 M2,8 L14,8 L14,17 L2,17 Z"
            : "M4,8 L4,5 C4,0 12,0 12,5 M2,8 L14,8 L14,17 L2,17 Z");
    }
}
