using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

public partial class ItemCollectorWindow
{
    private static async Task TestMerchants(ItemCollectorWindow window, Action<bool, string> check, string theme)
    {
        async Task Layout()
        { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        window.history = new(); window.merchantCapture = null; window.merchantRowsFingerprint = null;
        window.MerchantSearch.Text = ""; window.FilterMerchants(); window.DataTabs.SelectedItem = window.MerchantsTab;
        window.UpdateSummary(); await Layout();
        check(window.MerchantEmpty.IsVisible && !window.ExportControl.IsEnabled, $"{theme}: empty merchant history has an empty state and no fabricated export");
        var capture = new MerchantCapture(1, new(2026, 11, 9, 19, 0, 0, TimeSpan.Zero), "Live",
            AdvancedLootMemoryLayout.NovemberBuildHash, MerchantDecoder.Decoder, "merchant-unlimited", "Fixture", "Shaman",
            new(2935, "guildhalllrg_int", "Palatial Guild Hall"), true, new("General Supply Merchant", 70),
            [new(13006, "Water Flask"), new(13005, "Iron Ration"), new(47021, "Class XXI Augmentation Distiller")], 55, MerchantDecoder.Completeness);
        window.AcceptMerchant(capture); window.UpdateSummary(); await Layout();
        check(window.MerchantList.Items.Count == 3 && !window.MerchantEmpty.IsVisible && window.ExportControl.IsEnabled
            && window.ComparisonHint.Text.Contains("55 limited-stock") && window.SourceState.Text.Contains(capture.BuildSha256[..12]),
            $"{theme}: merchant rows, excluded count and exact source render");
        var selected = window.MerchantList.Items.Cast<MerchantObservation>().Single(m => m.ItemId == 13006);
        window.MerchantList.SelectedItem = selected;
        var rows = window.MerchantList.ItemsSource;
        window.AcceptMerchant(capture with { ObservedAtUtc = capture.ObservedAtUtc.AddSeconds(2) }); await Layout();
        check(ReferenceEquals(rows, window.MerchantList.ItemsSource) && ReferenceEquals(selected, window.MerchantList.SelectedItem)
            && window.MerchantDetails.Text.Contains(capture.ObservedAtUtc.AddSeconds(2).ToLocalTime().ToString("HH:mm:ss")),
            $"{theme}: merchant polling preserves row objects and selection while updating details");
        foreach (string query in new[] { "Water", "13006", "Palatial", "General Supply" })
        {
            window.MerchantSearch.Text = query; await Layout();
            check(window.MerchantList.Items.Count == (query is "Water" or "13006" ? 1 : 3), $"{theme}: merchant filter supports {query}");
        }
        window.MerchantSearch.Text = "no such item"; await Layout();
        check(window.MerchantEmpty.IsVisible && window.MerchantEmpty.Text.Contains("No matching") && window.MerchantList.Items.Count == 0,
            $"{theme}: unmatched merchant search has a clear empty state");
        check(window.SelectedSnapshot() is MerchantHistorySnapshot { Items.Length: 3 }, $"{theme}: search does not narrow the exported history");
        window.MerchantSearch.Text = "";
        window.AcceptMerchant(capture with { IsOpen = false, Merchant = null, Items = [], ExcludedLimitedStock = 0 });
        window.UpdateSummary(); await Layout();
        check(window.MerchantList.Items.Count == 3 && window.ComparisonHint.Text.Contains("Open a merchant") && window.ExportControl.IsEnabled,
            $"{theme}: closing the game merchant retains observations and updates its state");
        window.merchantError = "Fixture unreadable list";
        // Exercise the normal status branch synchronously; no runtime timers or discovery are started.
        window.review = false;
        try { window.UpdateSummary(); }
        finally { window.review = true; }
        check(window.StatusText.Text.Contains("unavailable") && window.ExportControl.IsEnabled,
            $"{theme}: unreadable merchant list marks retained data stale while allowing history export");
        window.merchantError = null;
        window.AcceptMerchant(capture with { Items = Enumerable.Range(1, 200).Select(i => new MerchantItem(100000 + i, $"Fixture merchant item {i:000}")).ToArray() });
        await Layout();
        window.MerchantList.ScrollIntoView(window.MerchantList.Items[^1]); await Layout();
        check(window.MerchantList.ItemContainerGenerator.ContainerFromIndex(window.MerchantList.Items.Count - 1) is C.DataGridRow,
            $"{theme}: a long merchant history scrolls to its last row");
        check(window.MerchantDetails.TranslatePoint(new W.Point(0, window.MerchantDetails.ActualHeight), window.CollectorRoot).Y < window.CollectorRoot.ActualHeight,
            $"{theme}: merchant details remain within the viewer");
        window.history = new(); window.merchantCapture = null; window.merchantRowsFingerprint = null;
        window.FilterMerchants(); window.UpdateSummary();
        check(window.MerchantList.Items.Count == 0 && !window.ExportControl.IsEnabled, $"{theme}: resetting the viewer clears merchant rows and export");
    }
}
