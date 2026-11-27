using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

internal sealed record ContributionChoice(string Id, string Name);

public partial class ContributionSettingsWindow : W.Window
{
    private readonly ContributionDestinations? destinations;
    private readonly ItemCollectorWindow collector;
    private readonly bool preview;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Draft> drafts = [];
    private sealed record Draft(bool Enabled, string Endpoint, string Key, ContributionDataSelection Data);
    private string selectedId = "eqtradeskills";
    private ContributionDelivery[] displayedRecent = [];
    private const string EqEndpoint = "https://eqtradeskills.com/api/contributions/v1/catalog";
    private bool closed, saving, initialized;
    private ContributionClient? Client => destinations?.All.FirstOrDefault(d => d.Id == selectedId)?.Client;
    private string DestinationName => selectedId == "eqtradeskills" ? "EQTradeskills" : "Generic API";

    internal ContributionSettingsWindow(ItemCollectorWindow owner, ContributionDestinations? contributionDestinations, bool reviewOnly)
    {
        preview = reviewOnly;
        collector = owner;
        destinations = preview ? null : contributionDestinations;
        InitializeComponent();
        Icon = AppBranding.WindowIcon;
        Owner = owner.DialogOwner;
        Resources.MergedDictionaries.Add(owner.Resources);
        DestinationChoice.ItemsSource = new[] { new ContributionChoice("eqtradeskills", "EQTradeskills"), new ContributionChoice("dkp", "Generic API") };
        DestinationChoice.SelectedValue = selectedId;
        initialized = true; LoadDestination();
        if (destinations is not null) destinations.Changed += Changed;
        Closed += (_, _) =>
        {
            closed = true; lifetime.Cancel(); KeyInput.Clear(); drafts.Clear();
            if (destinations is not null) destinations.Changed -= Changed;
        };
    }

    private void Destination_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!initialized || saving || DestinationChoice.SelectedValue is not string next || next == selectedId) return;
        drafts[selectedId] = new(EnabledInput.IsChecked == true, EndpointInput.Text, KeyInput.Password, SelectedData());
        KeyInput.Clear(); selectedId = next; Issue.Text = ""; LoadDestination();
    }

    private void LoadDestination()
    {
        var settings = Client?.Settings;
        var draft = drafts.GetValueOrDefault(selectedId) ?? new(settings?.Enabled == true,
            string.IsNullOrEmpty(settings?.Endpoint) && selectedId == "eqtradeskills" ? EqEndpoint : settings?.Endpoint ?? "", "", settings?.Data ?? new());
        EndpointInput.Text = draft.Endpoint; EnabledInput.IsChecked = draft.Enabled; KeyInput.Password = draft.Key;
        ItemsInput.IsChecked = draft.Data.Items; SpellsInput.IsChecked = draft.Data.Spells; RecipesInput.IsChecked = draft.Data.Recipes;
        NpcsInput.IsChecked = draft.Data.Npcs; DropsInput.IsChecked = draft.Data.Drops; ForagedInput.IsChecked = draft.Data.Foraged; FishedInput.IsChecked = draft.Data.Fished;
        EndpointLabel.Content = DestinationName + " API endpoint";
        EndpointHint.Text = selectedId == "eqtradeskills" ? "Ready for EQTradeskills. Keep an existing staging address when testing. Get a key from Admin Console → Catalog Contributions → API accounts."
            : "Paste the full HTTPS endpoint. It receives the same Team Bar data and receipt format as EQTradeskills; it is not a generic webhook.";
        KeyHint.Text = settings?.HasApiKey == true ? "A key is saved for this website. Leave blank to keep it; enter a new key when changing its address."
            : "Paste the dedicated API key issued by this website.";
        if (preview)
        {
            PreviewNotice.Visibility = W.Visibility.Visible;
            EndpointInput.Text = selectedId == "dkp" ? "https://api.example.org/team-bar/observations" : EqEndpoint;
            EnabledInput.IsEnabled = EndpointInput.IsEnabled = KeyInput.IsEnabled = ItemsInput.IsEnabled = SpellsInput.IsEnabled = RecipesInput.IsEnabled = false;
            NpcsInput.IsEnabled = DropsInput.IsEnabled = ForagedInput.IsEnabled = FishedInput.IsEnabled = false;
            SaveControl.IsEnabled = ClearControl.IsEnabled = RetryControl.IsEnabled = false;
        }
        ShowStatus();
    }

    private void Changed()
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() => { if (!closed) ShowStatus(); });
    }

    private void ShowStatus()
    {
        RefreshRecent();
        if (preview)
        {
            UploadState.Text = DestinationName + " preview · 0 pending";
            UploadDetail.Text = "No settings were read. No key is stored and no requests are sent.";
            return;
        }
        var client = Client; var state = client?.Status;
        UploadState.Text = state is null ? DestinationName + " is unavailable" : $"{DestinationName} · {(state.IsSending ? "Uploading" : client!.Settings.Enabled ? "Enabled" : "Disabled")} · {state.PendingCount:N0} pending";
        UploadDetail.Text = state is null ? "Close this window and try again." : $"{state.Message}\n{(state.LastUploadAtUtc is { } at ? $"Last upload {at.ToLocalTime():g}" : "No uploads yet")}";
        RetryControl.IsEnabled = !saving && client?.Settings.Enabled == true && state is { IsSending: false, PendingCount: > 0 };
    }

    private void SetSaving(bool value)
    {
        saving = value; DestinationChoice.IsEnabled = !value;
        SaveControl.IsEnabled = ClearControl.IsEnabled = EnabledInput.IsEnabled = EndpointInput.IsEnabled = KeyInput.IsEnabled = ItemsInput.IsEnabled = SpellsInput.IsEnabled = RecipesInput.IsEnabled = !value && !preview;
        NpcsInput.IsEnabled = DropsInput.IsEnabled = ForagedInput.IsEnabled = FishedInput.IsEnabled = !value && !preview;
        ShowStatus();
    }

    internal async void Save_Click(object sender, W.RoutedEventArgs e)
    {
        var client = Client;
        if (preview || closed || saving || client is null) return;
        Issue.Text = ""; SetSaving(true);
        try
        {
            string key = KeyInput.Password;
            await client.ConfigureAsync(EnabledInput.IsChecked == true, EndpointInput.Text.Trim(), key.Length == 0 ? null : key, lifetime.Token, SelectedData());
            if (closed) return;
            collector.ContributionSettingsChanged();
            drafts.Remove(selectedId); KeyInput.Clear();
            EndpointInput.Text = client.Settings.Endpoint;
            KeyHint.Text = client.Settings.HasApiKey ? "A key is saved for this website. Leave this field blank to keep it." : "No API key is saved.";
            Issue.Text = DestinationName + " settings saved. Other website connections are unchanged.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) Issue.Text = error.Message; }
        finally { if (!closed) SetSaving(false); }
    }

    internal async void Clear_Click(object sender, W.RoutedEventArgs e)
    {
        var client = Client;
        if (preview || closed || saving || client is null) return;
        Issue.Text = ""; SetSaving(true);
        try
        {
            await client.ClearAsync(lifetime.Token);
            if (closed) return;
            collector.ContributionSettingsChanged();
            drafts.Remove(selectedId); KeyInput.Clear(); LoadDestination();
            KeyHint.Text = "No API key is saved.";
            Issue.Text = DestinationName + " disabled and its saved key removed. Its pending data stays isolated; other connections are unchanged.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) Issue.Text = error.Message; }
        finally { if (!closed) SetSaving(false); }
    }

    internal void Retry_Click(object sender, W.RoutedEventArgs e)
    {
        var client = Client;
        if (preview || closed || saving || client?.Settings.Enabled != true) return;
        client.Retry(); ShowStatus();
    }
    private void Close_Click(object sender, W.RoutedEventArgs e) => Close();
    private ContributionDataSelection SelectedData() => new(ItemsInput.IsChecked == true, SpellsInput.IsChecked == true, RecipesInput.IsChecked == true,
        NpcsInput.IsChecked == true, DropsInput.IsChecked == true, ForagedInput.IsChecked == true, FishedInput.IsChecked == true);
    private void RefreshRecent()
    {
        var recent = Client?.Recent ?? [];
        if (!ReferenceEquals(displayedRecent, recent))
        {
            var selected = RecentList.SelectedItem as ContributionDelivery;
            displayedRecent = recent; RecentList.ItemsSource = recent;
            RecentList.SelectedItem = recent.FirstOrDefault(entry => entry == selected);
        }
        NoRecent.Visibility = recent.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
    }
    private void Recent_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not ContributionDelivery entry)
        { ReceiptDetail.Text = "Select a record to inspect the data that was shared."; SharedPayload.Clear(); return; }
        ReceiptDetail.Text = $"{entry.Summary}\nReceipt: {entry.Receipt.Id} · Received {entry.ReceivedAtUtc.ToLocalTime():G}";
        SharedPayload.Text = System.Text.Json.JsonSerializer.Serialize(new { entry.Record.Source, Data = entry.Record.Payload },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    }
}
