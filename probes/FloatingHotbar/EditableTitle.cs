using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;
namespace TeamBar;

// A stable editor: typing updates autosave without rebuilding the surrounding layout.
internal sealed class EditableTitle : C.Grid
{
    internal C.TextBox Editor { get; } = new() { MaxLength = 40, MinHeight = 32, Visibility = W.Visibility.Collapsed };
    private readonly C.TextBlock title = new() { FontWeight = W.FontWeights.SemiBold, TextTrimming = W.TextTrimming.CharacterEllipsis, TextWrapping = W.TextWrapping.NoWrap, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly C.Button pencil;
    private readonly C.TextBlock error = new() { Text = "Enter a name.", FontSize = 11, Visibility = W.Visibility.Collapsed };
    private string beforeEdit;
    private readonly bool allowEmpty;
    internal event Action<string>? Changed;
    internal EditableTitle(string value, string description, double size = 16, bool allowEmpty = false)
    {
        this.allowEmpty = allowEmpty;
        beforeEdit = value; title.Text = value; title.FontSize = size; title.ToolTip = value;
        Editor.Text = value; Editor.FontSize = size;
        ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        RowDefinitions.Add(new() { Height = W.GridLength.Auto }); RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        title.SetResourceReference(C.TextBlock.ForegroundProperty, "Text"); error.SetResourceReference(C.TextBlock.ForegroundProperty, "Error");
        pencil = new C.Button { Content = "✎", FontFamily = new("Segoe UI Symbol"), FontSize = 18, Background = M.Brushes.Transparent,
            BorderThickness = new(0), Padding = new(6, 2, 6, 2), Margin = new(4, 0, 0, 0), ToolTip = "Rename " + description };
        pencil.SetResourceReference(C.Control.ForegroundProperty, "Muted");
        W.Automation.AutomationProperties.SetName(pencil, "Rename " + description);
        W.Automation.AutomationProperties.SetName(Editor, description + " name");
        SetColumn(pencil, 1); SetColumnSpan(Editor, 2); SetRow(error, 1); SetColumnSpan(error, 2);
        Children.Add(title); Children.Add(pencil); Children.Add(Editor); Children.Add(error);
        pencil.Click += (_, _) => BeginEdit();
        Editor.TextChanged += (_, _) =>
        {
            bool blank = string.IsNullOrWhiteSpace(Editor.Text);
            title.Text = blank ? "" : Editor.Text; title.ToolTip = title.Text;
            error.Visibility = blank && !allowEmpty ? W.Visibility.Visible : W.Visibility.Collapsed;
            Changed?.Invoke(blank && allowEmpty ? "" : Editor.Text);
        };
        Editor.KeyDown += (_, e) =>
        {
            if (e.Key == I.Key.Enter) { Commit(); e.Handled = true; }
            if (e.Key == I.Key.Escape) { Editor.Text = beforeEdit; Commit(); e.Handled = true; }
        };
        Editor.LostKeyboardFocus += (_, _) => Commit();
    }
    internal void BeginEdit()
    {
        if (Editor.IsVisible) return;
        beforeEdit = Editor.Text; title.Visibility = pencil.Visibility = W.Visibility.Collapsed;
        Editor.Visibility = W.Visibility.Visible; Editor.Focus(); Editor.SelectAll();
    }
    internal bool Commit()
    {
        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            if (!allowEmpty) { error.Visibility = W.Visibility.Visible; return false; }
            Editor.Text = "";
        }
        beforeEdit = Editor.Text; Editor.Visibility = W.Visibility.Collapsed;
        title.Visibility = pencil.Visibility = W.Visibility.Visible; error.Visibility = W.Visibility.Collapsed;
        return true;
    }
}
