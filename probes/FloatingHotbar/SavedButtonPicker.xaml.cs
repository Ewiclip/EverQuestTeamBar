using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

internal sealed record PickerButtonInfo(string Id, string Name, string[] Mappings, string Visibility, bool Included)
{
    public string Membership => Included ? "Already in this group" : "";
    internal string SearchText => Name + " " + string.Join(" ", Mappings) + " " + Visibility;
    internal static PickerButtonInfo From(StandaloneButton button, bool included)
    {
        string Character(string id) => button.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "Unknown character";
        string Target(TargetSelector target)
        {
            if (target.PersonaId is { } id)
            { var persona = button.Personas.FirstOrDefault(p => p.Id == id); return persona is null ? "Unknown persona" : Character(persona.CharacterId) + " · " + (persona.Class ?? "any class"); }
            return target.CharacterId is { } character ? Character(character) + " · " + (target.Class ?? "any class") : "Any " + target.Class;
        }
        return new(button.Id, button.ManagementName, button.Rules.Select((r, i) => $"{i + 1}. {Target(r.Target)} → {r.Key}").ToArray(),
            button.ShowFor.Length == 0 ? "Always visible" : string.Join("\n", button.ShowFor.Select(Target)), included);
    }
}

public partial class SavedButtonPicker : W.Window
{
    private readonly PickerButtonInfo[] buttons;
    internal string? SelectedButtonId { get; private set; }
    internal SavedButtonPicker(W.Window owner, string columnName, SavedButton[] saved, IEnumerable<string> included)
    {
        Owner = owner; Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/TeamBar;component/GroupEditorStyles.xaml", UriKind.Relative) });
        var ids = included.ToHashSet(StringComparer.OrdinalIgnoreCase);
        buttons = saved.Select(b => PickerButtonInfo.From(b.Value, ids.Contains(b.Value.Id))).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        InitializeComponent(); Heading.Text = "Add to " + columnName; Heading.ToolTip = Heading.Text;
        Icon = AppBranding.WindowIcon;
        Add.Content = "Add to " + columnName; Add.MaxWidth = 300;
        W.Automation.AutomationProperties.SetName(Add, "Add selected button to " + columnName);
        Filter(); Loaded += (_, _) => Search.Focus();
    }
    internal static string? Choose(W.Window owner, string columnName, SavedButton[] saved, IEnumerable<string> included)
    {
        var picker = new SavedButtonPicker(owner, columnName, saved, included);
        return picker.ShowDialog() == true ? picker.SelectedButtonId : null;
    }
    private void Search_Changed(object sender, C.TextChangedEventArgs e) { if (Choices is not null) Filter(); }
    private void Filter()
    {
        string query = Search.Text.Trim(); var old = (Choices.SelectedItem as PickerButtonInfo)?.Id;
        var visible = buttons.Where(b => b.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Choices.ItemsSource = visible;
        Choices.SelectedItem = visible.FirstOrDefault(b => b.Id == old) ?? visible.FirstOrDefault(b => !b.Included) ?? visible.FirstOrDefault();
        SearchHint.Visibility = query.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        Empty.Text = buttons.Length == 0 ? "No saved buttons yet. Create one in Hot Keys → Buttons." : "No matching buttons.";
        Empty.Visibility = visible.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed; ShowDetails();
    }
    private void Selection_Changed(object sender, C.SelectionChangedEventArgs e) { if (Details is not null) ShowDetails(); }
    private void ShowDetails()
    {
        Details.Children.Clear(); var selected = Choices.SelectedItem as PickerButtonInfo;
        Add.IsEnabled = selected is { Included: false };
        Add.ToolTip = selected?.Included == true ? "This button is already in the group. Move its existing row to another column." : null;
        if (selected is null) return;
        C.TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        var title = Text(selected.Name, 20); title.FontWeight = W.FontWeights.SemiBold; Details.Children.Add(title);
        void Heading(string label) { var block = Text(label, 12); block.Margin = new(0, 14, 0, 8); block.SetResourceReference(C.TextBlock.ForegroundProperty, "Muted"); Details.Children.Add(block); }
        Heading("KEYPRESSES · IN ORDER");
        foreach (var mapping in selected.Mappings)
        {
            var panel = new C.Border { CornerRadius = new(5), BorderThickness = new(1), Padding = new(10), Margin = new(0, 0, 0, 7), Child = Text(mapping) };
            panel.SetResourceReference(C.Border.BackgroundProperty, "Field"); panel.SetResourceReference(C.Border.BorderBrushProperty, "Line"); Details.Children.Add(panel);
        }
        Heading("VISIBILITY"); Details.Children.Add(Text(selected.Visibility));
        if (selected.Included) { Heading("ALREADY ADDED"); Details.Children.Add(Text("Move this button using its grip in the group.")); }
    }
    private void Add_Click(object sender, W.RoutedEventArgs e)
    { if (Choices.SelectedItem is PickerButtonInfo { Included: false } button) { SelectedButtonId = button.Id; DialogResult = true; } }
    private void Close_Click(object sender, W.RoutedEventArgs e) => DialogResult = false;
}
