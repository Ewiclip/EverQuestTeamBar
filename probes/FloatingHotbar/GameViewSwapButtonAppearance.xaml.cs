using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace TeamBar;

public partial class GameViewSwapButtonAppearance : C.UserControl
{
    private sealed record Choice(GameViewSwapButtonStyle Value, string Label);
    private bool loading;
    private string committedText = "";
    internal event EventHandler? Changed;
    internal GameViewSwapButtonStyle ButtonStyle => StylePicker.SelectedValue is GameViewSwapButtonStyle style
        ? style : GameViewSwapButtonStyle.CharacterName;
    internal string ButtonText => LabelField.Text.Trim();

    public GameViewSwapButtonAppearance()
    {
        InitializeComponent();
        StylePicker.ItemsSource = new[] { new Choice(GameViewSwapButtonStyle.CharacterName, "Character name"),
            new Choice(GameViewSwapButtonStyle.Icon, "Swap icon"), new Choice(GameViewSwapButtonStyle.CustomText, "Custom text") };
        LabelField.MaxLength = GameViewLayout.MaxSwapButtonTextLength;
        Load(GameViewSwapButtonStyle.CharacterName, "");
    }

    internal void Load(GameViewSwapButtonStyle style, string text)
    {
        loading = true;
        try
        {
            StylePicker.SelectedValue = style;
            LabelField.Text = committedText = text;
            ShowTextOptions();
        }
        finally { loading = false; }
    }

    private void ShowTextOptions() =>
        TextOptions.Visibility = ButtonStyle == GameViewSwapButtonStyle.CustomText ? W.Visibility.Visible : W.Visibility.Collapsed;

    private void Style_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (loading || TextOptions is null || StylePicker.SelectedIndex < 0) return;
        committedText = ButtonText;
        ShowTextOptions();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CommitLabel()
    {
        if (loading || ButtonText == committedText) return;
        committedText = ButtonText;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Label_KeyDown(object sender, I.KeyEventArgs e)
    {
        if (e.Key != I.Key.Enter) return;
        e.Handled = true;
        CommitLabel();
    }

    private void Label_LostKeyboardFocus(object sender, I.KeyboardFocusChangedEventArgs e) => CommitLabel();
}
