using W = System.Windows;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private void Alias_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!initialized || loading || selected is null || CurrentRuleIndex < 0) return;
        int index = CurrentRuleIndex;
        var spells = selected.Spells.ToArray();
        spells[index] = spells[index] with { Alias = string.IsNullOrWhiteSpace(SpellAlias.Text) ? null : SpellAlias.Text };
        Replace(selected with { Spells = spells });
        // Refresh the list label without resetting the alias text box's caret.
        RefreshRules(index);
    }
    private void OpenOverlaySettings(string id)
    {
        ReopenSettings();
        if (definitions.FirstOrDefault(d => d.Id == id) is { } definition) OverlayList.SelectedItem = definition;
    }
}
