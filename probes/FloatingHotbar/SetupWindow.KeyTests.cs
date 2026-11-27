using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

public partial class SetupWindow
{
    private void KeyPickerSelfTest(Action<bool, string> check)
    {
        static IEnumerable<W.DependencyObject> Children(W.DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Children(M.VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        Settle();
        var picker = Children(RuleList).OfType<C.ComboBox>()
            .First(c => W.Automation.AutomationProperties.GetName(c) == "Key to send");
        var row = (RuleRow)picker.DataContext; string original = row.Key;
        foreach (string key in new[] { "[", "]", "A", "F12", "NumpadAdd", "Up", "Down", "Left", "Right", "Home", "End",
            "PageUp", "PageDown", "Insert", "Delete", "NumpadEnter", "NumpadDivide" })
        {
            picker.SelectedItem = key; Settle();
            check(picker.SelectedItem as string == key && row.Key == key && selected!.Rules.Any(r => r.Key == key),
                "The compiled key picker must update its mapping for " + key);
        }
        var modifierPicker = Children(RuleList).OfType<ModifierPicker>()
            .First(c => W.Automation.AutomationProperties.GetName(c) == "Modifiers to hold");
        check(modifierPicker.TranslatePoint(new(), RuleList).X < picker.TranslatePoint(new(), RuleList).X
            && Children(RuleList).OfType<C.TextBlock>().Any(t => t.Text == "Hold Key(s)")
            && Children(RuleList).OfType<C.TextBlock>().Any(t => t.Text == "Click Key"),
            "Button key editor places Hold Key(s) on the left and Click Key on the right.");
        foreach (int modifiers in Enumerable.Range(0, 8))
        {
            modifierPicker.Value = modifiers; picker.SelectedItem = "6"; Settle();
            check(row.Modifiers == modifiers && selected!.Rules.Any(r => r.Key == SpellOverlayKey.Format("6", modifiers)),
                "The compiled modifier picker must update saved key combinations.");
        }
        modifierPicker.Value = 0; picker.SelectedItem = original; Settle();
        var originalRules = selected!.Rules.ToArray();
        AddRow(row.Target, "Alt+7"); CaptureRules(); MarkChanged();
        check(SaveControl.IsEnabled, "Multiple keypresses for one recipient must be saveable.");
        var added = rows.Last(); MoveRule(added, 0);
        check(selected.Rules[0].Key == "Alt+7", "Reordering a keypress lost its modifier or position.");
        LoadButton();
        check(rows[0].Key == "7" && rows[0].Modifiers == 4, "Reloading the editor lost a modifier chord.");
        selected.Rules = originalRules; LoadButton();
    }
}
