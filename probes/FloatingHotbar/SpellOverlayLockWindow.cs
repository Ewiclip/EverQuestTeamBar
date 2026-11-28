using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using Glyph = System.Windows.Shapes.Path;
namespace TeamBar;

// The corner controls remain interactive while the rest of the spell display is locked.
internal sealed class SpellOverlayLockWindow : W.Window
{
    // All paths share an 18-DIP drawing surface centered on (9, 9).
    private const string SettingsShape =
        "M7.6,2 L10.4,2 10.8,4 11.4,4.3 13.2,3.1 14.9,4.8 13.7,6.6 14,7.2 16,7.6 " +
        "16,10.4 14,10.8 13.7,11.4 14.9,13.2 13.2,14.9 11.4,13.7 10.8,14 10.4,16 " +
        "7.6,16 7.2,14 6.6,13.7 4.8,14.9 3.1,13.2 4.3,11.4 4,10.8 2,10.4 " +
        "2,7.6 4,7.2 4.3,6.6 3.1,4.8 4.8,3.1 6.6,4.3 7.2,4 Z " +
        "M11.5,9 A2.5,2.5 0 1 1 6.5,9 A2.5,2.5 0 1 1 11.5,9 Z";
    private const string PencilShape = "M2,16 L3,12 12.7,2.3 Q14,1 15.5,2.5 Q17,4 15.7,5.3 L6,15 Z M11,4 L14,7";
    private const string DoneShape = "M2.5,9 L7,13.5 15.5,4.5";
    private const string LockBody = " M3,8 H15 V16 H3 Z";
    private const string ClosedLockShape = "M5,8 V6 A4,4 0 0 1 13,6 V8" + LockBody;
    private const string OpenLockShape = "M5,8 V6 A4,4 0 0 1 13,6" + LockBody;
    private readonly C.Button button;
    private readonly Glyph icon;
    private readonly M.SolidColorBrush accent;
    private readonly C.Primitives.UniformGrid controls = new();
    private readonly C.Button pencil;
    private readonly Glyph pencilGlyph;
    private bool displayClosing;
    private string? stateKey;
    internal int ButtonCount => controls.Columns;
    internal SpellOverlayLockWindow(SpellOverlayWindow display, FloatingTheme theme, Action? openSettings = null, bool showSettings = false)
    {
        Title = "Team Bar · Overlay lock"; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        WindowStyle = W.WindowStyle.None; ResizeMode = W.ResizeMode.NoResize; AllowsTransparency = true;
        Background = M.Brushes.Transparent; UseLayoutRounding = true; SnapsToDevicePixels = true;
        M.SolidColorBrush Brush(string value) => new((M.Color)M.ColorConverter.ConvertFromString(value));
        accent = Brush(theme.Accent);
        // A zero-alpha layered-window pixel passes native mouse input through to the game.
        // Keep only the minimum hit-test alpha; never paint an opaque button over the translucent display.
        var surface = (M.Color)M.ColorConverter.ConvertFromString(theme.Bar);
        var background = new M.SolidColorBrush(M.Color.FromArgb(1, surface.R, surface.G, surface.B));
        // Replace Windows button chrome so hover/press cannot bring back a colored square.
        var template = (C.ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}">
                <Viewbox Stretch="Uniform" StretchDirection="Both">
                  <Border Width="28" Height="28">
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
        Glyph MakeGlyph(string path) => new() { Width = 18, Height = 18, Stretch = M.Stretch.None,
            Data = M.Geometry.Parse(path), Stroke = accent, StrokeThickness = 1.6,
            StrokeLineJoin = M.PenLineJoin.Round, StrokeStartLineCap = M.PenLineCap.Round, StrokeEndLineCap = M.PenLineCap.Round };
        C.Button MakeButton(Glyph glyph) => new() { Focusable = false, FocusVisualStyle = null, Padding = new(0),
            BorderThickness = new(0), Background = background, Template = template, Cursor = System.Windows.Input.Cursors.Hand, Content = glyph };
        icon = MakeGlyph(ClosedLockShape);
        button = MakeButton(icon);
        button.Click += (_, _) => display.ToggleLayoutLock();
        controls.Columns = showSettings ? 2 : 1;
        if (showSettings)
        {
            var gear = MakeButton(MakeGlyph(SettingsShape));
            gear.ToolTip = "Open this spell overlay's settings";
            W.Automation.AutomationProperties.SetName(gear, "Open spell overlay settings");
            gear.Click += (_, _) => openSettings?.Invoke(); controls.Children.Add(gear);
        }
        pencilGlyph = MakeGlyph(PencilShape);
        pencil = MakeButton(pencilGlyph);
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
        pencilGlyph.Data = M.Geometry.Parse(editing ? DoneShape : PencilShape);
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
        icon.Data = M.Geometry.Parse(definition.Locked ? ClosedLockShape : OpenLockShape);
    }
}
