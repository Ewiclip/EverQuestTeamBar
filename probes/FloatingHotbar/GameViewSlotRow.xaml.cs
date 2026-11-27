using System.Windows.Automation;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class GameViewSlotRow : C.UserControl
{
    internal GameViewSlotRow(int number)
    {
        InitializeComponent();
        Number.Text = $"Slot {number}";
        AutomationProperties.SetName(Character, $"Slot {number} character");
        AutomationProperties.SetName(WindowPicker, $"Slot {number} EQ window");
        AutomationProperties.SetName(Clear, $"Clear slot {number}");
    }
}
