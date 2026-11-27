using System.Net.Http;
using W = System.Windows;

namespace TeamBar;

public partial class ContributionSettingsWindow
{
    private sealed class NoUploadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The settings fixture must not send any observations.");
    }
    internal static async Task TestDestinations(ItemCollectorWindow owner, Action<bool, string> check, string directory)
    {
        var manager = new ContributionDestinations(directory, _ => new NoUploadHandler());
        await manager.Ready;
        var window = new ContributionSettingsWindow(owner, manager, false);
        var unifiedHost = new W.Window { Width = 300, Height = 200, ShowInTaskbar = false, Title = "Sharing owner fixture" };
        string dkpKey = "dkpc_" + new string('c', 32) + "_" + new string('C', 43);
        string eqKey = "dkpc_" + new string('d', 32) + "_" + new string('D', 43);
        async Task Saved()
        {
            long deadline = Environment.TickCount64 + 5000;
            while (window.saving)
            {
                if (Environment.TickCount64 >= deadline) throw new InvalidOperationException("Destination settings fixture timed out.");
                await Task.Delay(10);
            }
        }
        try
        {
            window.Show();
            unifiedHost.Show(); window.Owner = unifiedHost;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            double originalHeight = window.Height, originalWidth = window.Width;
            window.Height = window.MinHeight; window.Width = window.MinWidth;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            double retryBottom = window.RetryControl.TranslatePoint(new W.Point(0, window.RetryControl.ActualHeight), window).Y;
            double saveTop = window.SaveControl.TranslatePoint(new W.Point(0, 0), window).Y;
            check(window.SettingsScroll.ViewportHeight > 100 && retryBottom < saveTop
                && saveTop + window.SaveControl.ActualHeight <= window.ActualHeight,
                "minimum-size contribution settings keeps status and actions visible above a usable scrolling form");
            window.KeyInput.BringIntoView();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            double keyTop = window.KeyInput.TranslatePoint(new W.Point(0, 0), window.SettingsScroll).Y;
            check(keyTop >= 0 && keyTop + window.KeyInput.ActualHeight <= window.SettingsScroll.ViewportHeight + 1,
                "minimum-size contribution settings can scroll to the credential field");
            window.Height = originalHeight; window.Width = originalWidth;
            window.SettingsScroll.ScrollToTop();
            check(window.DestinationChoice.SelectedValue as string == "eqtradeskills" && window.EndpointInput.Text == EqEndpoint,
                "EQTradeskills is the initial preset with its API endpoint filled in");
            window.DestinationChoice.SelectedValue = "dkp";
            window.EnabledInput.IsChecked = true;
            window.EndpointInput.Text = "https://dkp-settings.invalid"; window.KeyInput.Password = dkpKey;
            window.SpellsInput.IsChecked = window.RecipesInput.IsChecked = false;
            window.NpcsInput.IsChecked = window.ForagedInput.IsChecked = true;
            window.DestinationChoice.SelectedValue = "eqtradeskills";
            check(window.EndpointInput.Text == EqEndpoint && window.KeyInput.Password.Length == 0 && window.EnabledInput.IsChecked == false
                && window.SpellsInput.IsChecked == true && window.RecipesInput.IsChecked == true,
                "selecting a second website never copies the first website's key or settings");
            window.EndpointInput.Text = "https://tradeskills-settings.invalid"; window.KeyInput.Password = eqKey;
            window.ItemsInput.IsChecked = window.SpellsInput.IsChecked = false;
            window.DropsInput.IsChecked = window.FishedInput.IsChecked = true;
            window.DestinationChoice.SelectedValue = "dkp";
            check(window.EndpointInput.Text == "https://dkp-settings.invalid" && window.KeyInput.Password == dkpKey
                && window.EnabledInput.IsChecked == true && window.ItemsInput.IsChecked == true && window.RecipesInput.IsChecked == false
                && window.NpcsInput.IsChecked == true && window.ForagedInput.IsChecked == true && window.DropsInput.IsChecked == false,
                "switching websites preserves each unsaved key and data-selection draft independently");
            window.Save_Click(window.SaveControl, new()); await Saved();
            var dkp = manager.All.Single(d => d.Id == "dkp").Client;
            var eq = manager.All.Single(d => d.Id == "eqtradeskills").Client;
            check(dkp.Settings is { Enabled: true, HasApiKey: true } && eq.Settings is { Enabled: false, HasApiKey: false }
                && dkp.Settings.Data == new ContributionDataSelection(true, false, false, true, false, true, false)
                && window.KeyInput.Password.Length == 0 && window.Issue.Text.Contains("settings saved"),
                "Save works inside a unified settings host, changes only the selected destination, and clears its visible key");
            var dkpSettings = dkp.Settings;
            window.DestinationChoice.SelectedValue = "eqtradeskills";
            check(window.EndpointInput.Text == "https://tradeskills-settings.invalid" && window.KeyInput.Password == eqKey,
                "saving one website retains the other website's pending draft");
            window.EnabledInput.IsChecked = true; window.Save_Click(window.SaveControl, new()); await Saved();
            check(eq.Settings is { Enabled: true, HasApiKey: true } && eq.Settings.Data == new ContributionDataSelection(false, false, true, false, true, false, true)
                && ReferenceEquals(dkpSettings, dkp.Settings),
                "both website connections can be enabled with separate keys");
            window.Clear_Click(window.ClearControl, new()); await Saved();
            check(eq.Settings is { Enabled: false, HasApiKey: false } && ReferenceEquals(dkpSettings, dkp.Settings)
                && window.KeyInput.Password.Length == 0 && window.SelectedData() == eq.Settings.Data, "Disable and clear affects only the selected website and displays the cleared settings accurately");
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { itemId = 120780, name = "Example item · receipt preview", candidateDetails = new { tradeskill = true, requiredLevel = 125 } });
            var record = new ContributionRecord("item", 120780, ContributionPayload.Fingerprint(payload), payload, new("Synthetic UI fixture", "Live"));
            var history = new ContributionQueue(directory, dkp.Settings.Endpoint, dkp.Settings.AccountId);
            var sourceRecords = ContributionSourcesTests.Run(directory);
            history.Enqueue([record, .. sourceRecords]);
            while (history.Next() is { } batch)
                history.Acknowledge(batch, batch.Records.Select(row => new ContributionReceipt(row.Kind, row.EntityId, Guid.NewGuid().ToString("N"), "pending", false)).ToArray());
            await dkp.ConfigureAsync(false, dkp.Settings.Endpoint, null);
            window.DestinationChoice.SelectedValue = "dkp";
            window.SharingTabs.SelectedIndex = 1;
            window.RecentList.SelectedItem = dkp.Recent.First(row => row.Kind == "drop");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            check(window.RecentList.Items.Count == 5 && window.NoRecent.Visibility == W.Visibility.Collapsed
                && window.ReceiptDetail.Text.Contains("pending at receipt") && window.SharedPayload.Text.Contains("120780")
                && !window.SharedPayload.Text.Contains(dkpKey),
                "recent sharing includes item, NPC, drop, foraged and fished receipts with the exact submitted source payload and no credentials");
            foreach (var theme in new[] { AppearanceTheme.Dark, AppearanceTheme.Light })
            {
                ThemePalette.Apply(window.Resources, theme); window.UpdateLayout();
                GroupEditorTests.Screenshot((W.FrameworkElement)window.Content, Path.Combine(directory, "recent-sharing-" + theme.ToString().ToLowerInvariant() + ".png"));
            }
            window.DestinationChoice.SelectedValue = "eqtradeskills";
            check(window.RecentList.Items.Count == 0 && window.SharedPayload.Text.Length == 0 && window.NoRecent.Visibility == W.Visibility.Visible,
                "switching destinations clears the previous connection's recent payload and shows an honest empty state");
            window.SharingTabs.SelectedIndex = 0; window.SettingsScroll.ScrollToTop();
            foreach (var theme in new[] { AppearanceTheme.Dark, AppearanceTheme.Light })
            {
                ThemePalette.Apply(window.Resources, theme);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                GroupEditorTests.Screenshot((W.FrameworkElement)window.Content, Path.Combine(directory, "sharing-settings-" + theme.ToString().ToLowerInvariant() + ".png"));
            }
        }
        finally
        {
            window.Close(); unifiedHost.Close(); manager.Dispose(); await manager.Completion;
        }
        check(window.KeyInput.Password.Length == 0 && window.drafts.Count == 0,
            "closing contribution settings clears all unsaved credential drafts");
    }
}
