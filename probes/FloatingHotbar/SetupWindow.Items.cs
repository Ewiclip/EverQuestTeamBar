using W = System.Windows;
namespace TeamBar;
public partial class SetupWindow
{
    private ItemCollectorWindow? itemCollector;
    private void SpellOverlays_Click(object sender, W.RoutedEventArgs e)
    {
        if (review) { Status("Open Spell overlays from the live Team Bar configuration."); return; }
        if (!FeatureEnabled(MemoryFeature.SpellBarData)) { OpenMemorySettings(); return; }
        Act(() => { if (TeamBarApplication.Current is { } app) app.ShowSpells(); else SpellOverlayManagerWindow.Open(); });
    }
    private void Items_Click(object sender, W.RoutedEventArgs e)
    {
        if (review) { Status("Open Data Collection from the live Team Bar configuration to read a client."); return; }
        if (TeamBarApplication.Current is { } app) { Act(app.ShowData); return; }
        if (!FeatureEnabled(MemoryFeature.DataCollection)) { OpenMemorySettings(); return; }
        if (itemCollector is not null) { itemCollector.Activate(); return; }
        itemCollector = new ItemCollectorWindow();
        itemCollector.Closed += (_, _) => itemCollector = null;
        itemCollector.Show();
    }
}
