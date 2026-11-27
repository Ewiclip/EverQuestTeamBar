using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private void LoadAlternatives(SpellWatch? rule)
    {
        AlternativeEditor.IsEnabled = rule is not null;
        AlternativeList.ItemsSource = (rule?.Alternatives ?? []).Select(a => SearchChoice(a.SpellId, a.Name, a.IconId)).ToArray();
        NoAlternatives.Visibility = rule?.Alternatives is { Length: > 0 } ? W.Visibility.Collapsed : W.Visibility.Visible;
        UpdateAlternativeAction();
    }

    private void UpdateAlternativeAction()
    {
        var rule = (WatchList.SelectedItem as SpellWatchChoice)?.Rule;
        AddAlternativeButton.IsEnabled = rule is not null && SearchResults.SelectedItem is SpellSearchChoice;
        AlternativeTarget.Text = rule is null ? "Select a watched spell to add an alternative." : "Alternative for: " + rule.Name;
        AlternativeTarget.ToolTip = AlternativeTarget.Text;
        AddAlternativeButton.ToolTip = rule is null ? AlternativeTarget.Text : "Add the selected result as an alternative for " + rule.Name;
    }

    private void Picker_Changed(object sender, C.SelectionChangedEventArgs e)
    { if (initialized) UpdateAlternativeAction(); }

    private bool SameSpellFamily(int first, int second) => first == second || spellCatalog?.SameRankFamily(first, second) == true;

    private void AddAlternative_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (!initialized || loading || selected is null || CurrentRuleIndex < 0
            || SearchResults.SelectedItem is not SpellSearchChoice choice) return;
        int index = CurrentRuleIndex;
        var rule = selected.Spells[index];
        var alternatives = rule.Alternatives ?? [];
        int existing = Array.FindIndex(alternatives, alternative => SameSpellFamily(alternative.SpellId, choice.Id));
        if (SameSpellFamily(rule.SpellId, choice.Id) || existing >= 0)
        {
            AlternativeList.SelectedIndex = existing;
            Status("That spell or another rank already counts as present for " + rule.Name + ".");
            return;
        }
        if (alternatives.Length >= SpellOverlayStore.MaxAlternatives)
            throw new InvalidOperationException($"Use up to {SpellOverlayStore.MaxAlternatives} alternatives per watched spell.");
        var rules = selected.Spells.ToArray();
        rules[index] = rule with { Alternatives = [.. alternatives, new(choice.Id, choice.Name, choice.Icon)] };
        Replace(selected with { Spells = rules }); RefreshRules(index);
        AlternativeList.SelectedIndex = alternatives.Length; ShowConfigure();
    });

    private void RemoveAlternative_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        if (!initialized || loading || selected is null || CurrentRuleIndex < 0
            || sender is not W.FrameworkElement { Tag: int spellId }) return;
        int index = CurrentRuleIndex;
        var rule = selected.Spells[index];
        var alternatives = rule.Alternatives ?? [];
        if (!alternatives.Any(a => a.SpellId == spellId)) return;
        var remaining = alternatives.Where(a => a.SpellId != spellId).ToArray();
        var rules = selected.Spells.ToArray();
        rules[index] = rule with { Alternatives = remaining.Length == 0 ? null : remaining };
        Replace(selected with { Spells = rules }); RefreshRules(index);
    });
}
