using W = System.Windows;

namespace TeamBar;

public partial class ItemCollectorWindow
{
    private static void TestCollectorDiscoveryWarnings(ItemCollectorWindow window, Action<bool, string> check)
    {
        var live = new DetectedClient(1, 100, 11, 0, new("Fellwort", 16, 130));
        var beta = live with { ProcessId = 80420, Window = 22, ClientChannel = "Beta", BuildSha256 = CollectorBuild.BetaBuildHash };
        const string unavailable = "Client PID 80420 unavailable: Beta client: This EQ build needs a detector update.";

        window.ApplyDiscovery(new([live], [unavailable]), null);
        check(window.ClientChoice.Items.Count == 1 && Equals(window.ClientChoice.SelectedItem, live)
            && window.DiscoveryWarning.Visibility == W.Visibility.Visible
            && window.DiscoveryWarningText.Text == unavailable,
            "valid Live selection does not hide the rejected Beta client's process and build-update explanation");
        window.UpdateSummary();
        window.DataTabs.SelectedItem = window.RecipesTab;
        window.UpdateSummary();
        check(window.DiscoveryWarning.Visibility == W.Visibility.Visible && window.DiscoveryWarningText.Text == unavailable,
            "discovery warning survives successful-client summary updates and tab changes");

        window.ApplyDiscovery(new([live, beta], []), live);
        check(window.DiscoveryWarning.Visibility == W.Visibility.Collapsed && window.DiscoveryWarningText.Text.Length == 0
            && window.ClientChoice.Items.Count == 2 && Equals(window.ClientChoice.SelectedItem, live),
            "a successful refresh clears obsolete failures without switching the existing selected client");
        window.ClientChoice.SelectedItem = beta;
        window.ApplyDiscovery(new([live, beta], []), beta);
        check(Equals(window.ClientChoice.SelectedItem, beta),
            "refresh preserves selected Beta when a same-name Live character is also present");

        window.ApplyDiscovery(new([], [unavailable]), beta);
        check(window.ClientChoice.Items.Count == 0 && window.ClientChoice.SelectedItem is null
            && window.DiscoveryWarning.Visibility == W.Visibility.Visible
            && window.DiscoveryWarningText.Text.Contains("detector update"),
            "an unsupported-only scan shows its failure without manufacturing a selectable character");
        window.ApplyDiscovery(new([], []), null);
        check(window.DiscoveryWarning.Visibility == W.Visibility.Collapsed && window.StatusText.Text.Contains("No supported clients"),
            "an empty refresh clears stale failures and explains that no supported clients were found");
        window.DataTabs.SelectedItem = window.InventoryTab;
    }
}
