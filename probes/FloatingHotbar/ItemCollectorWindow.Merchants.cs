using C = System.Windows.Controls;
namespace TeamBar;

public partial class ItemCollectorWindow
{
    private MerchantCapture? merchantCapture;
    private string? merchantError, merchantRowsFingerprint;

    private void AcceptMerchant(MerchantCapture capture)
    { history.Accept(capture); merchantCapture = capture; FilterMerchants(); }

    private void FilterMerchants()
    {
        if (!initialized) return;
        string? selected = (MerchantList.SelectedItem as MerchantObservation)?.Key;
        string query = MerchantSearch.Text.Trim();
        var shown = history.Merchants.Where(m => query.Length == 0 || m.MerchantName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || m.ItemName.Contains(query, StringComparison.OrdinalIgnoreCase) || m.ZoneLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || m.ItemId.ToString().Contains(query, StringComparison.Ordinal)).ToArray();
        // Reading every two seconds must not rebuild rows or interrupt scrolling.
        string fingerprint = CollectorFormat.Hash(shown.Select(m => m with { LastSeenUtc = default }));
        if (fingerprint != merchantRowsFingerprint)
        {
            merchantRowsFingerprint = fingerprint;
            MerchantList.ItemsSource = shown;
            MerchantList.SelectedItem = shown.FirstOrDefault(m => m.Key == selected) ?? shown.FirstOrDefault();
        }
        ShowMerchant();
    }
    private void ShowMerchant()
    {
        string? key = (MerchantList.SelectedItem as MerchantObservation)?.Key;
        var item = history.Merchants.FirstOrDefault(m => m.Key == key);
        MerchantDetails.Text = item is null ? "Open a merchant in EverQuest to collect its unlimited-stock items."
            : $"{item.MerchantName} · Level {item.MerchantLevel} · {item.Zone.Name} · Item {item.ItemId}\n"
                + $"First seen {item.FirstSeenUtc.ToLocalTime():g} · Last seen {item.LastSeenUtc.ToLocalTime():HH:mm:ss}";
        MerchantEmpty.Visibility = MerchantList.Items.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        MerchantEmpty.Text = MerchantSearch.Text.Trim().Length > 0 ? "No matching merchant items" : "No unlimited-stock items observed yet";
    }
    private void MerchantFilter_Changed(object sender, System.Windows.RoutedEventArgs e) => FilterMerchants();
    private void Merchant_Changed(object sender, C.SelectionChangedEventArgs e) => ShowMerchant();
}
