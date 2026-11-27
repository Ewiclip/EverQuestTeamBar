using W = System.Windows;
using C = System.Windows.Controls;
using P = System.Windows.Controls.Primitives;
namespace TeamBar;

// One editor for both kinds of buttons. Each modifier is independent; choosing
// Shift must not silently replace Ctrl or Alt that the user already selected.
public sealed class ModifierPicker : C.UserControl
{
    public static readonly W.DependencyProperty ValueProperty = W.DependencyProperty.Register(nameof(Value), typeof(int), typeof(ModifierPicker),
        new W.FrameworkPropertyMetadata(0, W.FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ModifierPicker)d).Refresh()), v => v is int mask && mask is >= 0 and <= 7);
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public event W.RoutedEventHandler? ValueChanged;
    private readonly List<(P.ToggleButton Button, int Bit)> buttons = [];
    private bool refreshing;
    public ModifierPicker()
    {
        var panel = new C.Primitives.UniformGrid { Columns = 3 };
        foreach (var (name, bit) in new[] { ("Ctrl", 2), ("Shift", 1), ("Alt", 4) })
        {
            var button = new P.ToggleButton { Content = name, MinHeight = 34, Padding = new(7, 5, 7, 5), Margin = new(0, 0, 4, 0),
                ToolTip = "Hold " + name + " with the key. You can select multiple modifiers." };
            var border = new W.FrameworkElementFactory(typeof(C.Border));
            border.SetValue(C.Border.CornerRadiusProperty, new W.CornerRadius(5));
            border.SetValue(C.Border.BorderThicknessProperty, new W.Thickness(1));
            foreach (var property in new[] { C.Border.BackgroundProperty, C.Border.BorderBrushProperty, C.Border.PaddingProperty })
                border.SetBinding(property, new System.Windows.Data.Binding(property.Name) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new W.FrameworkElementFactory(typeof(C.ContentPresenter));
            presenter.SetValue(W.FrameworkElement.HorizontalAlignmentProperty, W.HorizontalAlignment.Center);
            presenter.SetValue(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center);
            presenter.SetBinding(C.ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(presenter); button.Template = new C.ControlTemplate(typeof(P.ToggleButton)) { VisualTree = border };
            W.Automation.AutomationProperties.SetName(button, "Hold " + name);
            button.SetResourceReference(C.Control.ForegroundProperty, "Text");
            button.SetResourceReference(C.Control.BorderBrushProperty, "Line");
            button.Click += (_, _) => { if (!refreshing) SetCurrentValue(ValueProperty, button.IsChecked == true ? Value | bit : Value & ~bit); };
            buttons.Add((button, bit)); panel.Children.Add(button);
        }
        Content = panel; Refresh();
    }
    private void Refresh()
    {
        refreshing = true;
        foreach (var (button, bit) in buttons)
        {
            button.IsChecked = (Value & bit) != 0;
            button.SetResourceReference(C.Control.BackgroundProperty, button.IsChecked == true ? "SelectedSurface" : "Field");
            button.SetResourceReference(C.Control.BorderBrushProperty, button.IsChecked == true ? "Accent" : "Line");
        }
        refreshing = false; ValueChanged?.Invoke(this, new());
    }
}
