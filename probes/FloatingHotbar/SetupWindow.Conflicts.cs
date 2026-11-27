using W = System.Windows;

namespace TeamBar;

public partial class SetupWindow
{
    private void RefreshMappingConflicts()
    {
        RoutingConflict[] selectedConflicts = [];
        foreach (var draft in standaloneDrafts)
        {
            var conflicts = Routing.Conflicts(DocumentFor(draft).AsTeam());
            draft.SetConflicts(string.Join("\n", conflicts.Select(conflict => conflict.Message)));
            if (draft == selected) selectedConflicts = conflicts;
        }
        for (int index = 0; index < rows.Count; index++)
            rows[index].SetConflicts(string.Join("\n", selectedConflicts
                .Where(conflict => conflict.RuleIndices.Contains(index)).Select(conflict => conflict.Message)));

        ButtonConflictMessage.Visibility = IsStandalone && selectedConflicts.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (ButtonConflictMessage.Visibility != W.Visibility.Visible) { ButtonConflictMessage.Text = ""; return; }
        bool active = StandaloneButtons.Conflicts(DocumentFor(selected!), SelectedClients()).Length > 0;
        ButtonConflictMessage.Text = (active
            ? "This button is disabled for the selected characters. Other buttons can still connect. "
            : "A saved persona has conflicting recipients. This button will be disabled while that persona is connected. ")
            + "Choose the same recipient for an ordered sequence, or remove the overlapping rule. Your mappings remain editable and saved.";
    }
}
