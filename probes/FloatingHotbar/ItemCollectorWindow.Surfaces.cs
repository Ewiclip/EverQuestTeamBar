using C = System.Windows.Controls;
namespace TeamBar;

public partial class ItemCollectorWindow
{
    private DataCollectionHistory history = new();
    private InspectionCapture? inspectionCapture;
    private AdvancedLootCapture? lootCapture;
    private string? itemError, recipeError, inspectionError, lootError;

    private void AcceptInspection(InspectionCapture capture)
    { history.Accept(capture); inspectionCapture = capture; FilterInspections(); }
    private void AcceptLoot(AdvancedLootCapture capture)
    { history.Accept(capture); lootCapture = capture; FilterDrops(); }

    private void FilterInspections()
    {
        if (!initialized) return;
        string? selected = (InspectionList.SelectedItem as InspectedObservation)?.Key;
        string query = InspectionSearch.Text.Trim();
        var shown = history.Inspections.Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || i.ItemId.ToString().Contains(query, StringComparison.Ordinal)).ToArray();
        InspectionList.ItemsSource = shown;
        InspectionList.SelectedItem = shown.FirstOrDefault(i => i.Key == selected) ?? shown.FirstOrDefault();
        ShowInspection(InspectionList.SelectedItem as InspectedObservation);
    }
    private void ShowInspection(InspectedObservation? item)
    {
        InspectionTitle.Text = item?.Name ?? "No inspected items match";
        InspectionMeta.Text = item is null ? "Open an item link in EverQuest to collect its available properties."
            : $"Item {item.ItemId}\nFirst seen {item.FirstSeenUtc.ToLocalTime():g} · Last seen {item.LastSeenUtc.ToLocalTime():HH:mm:ss}\nRetained in this viewer session; not evidence of ownership.";
        if (item is null) { InspectionProperties.ItemsSource = null; return; }
        var properties = DescribeItems([item.Item]);
        if (item.Effects is not null)
        {
            properties.Insert(0, new("Spell effects", "Intrinsic item effects only; augments are separate items."));
            properties.InsertRange(1, item.Effects.Select(effect => new Detail(effect.Kind,
                $"{effect.Name ?? "Spell"} · ID {effect.SpellId}")));
        }
        InspectionProperties.ItemsSource = properties;
    }
    private void FilterDrops()
    {
        if (!initialized) return;
        string? selected = (DropList.SelectedItem as NpcDropObservation)?.Key;
        string query = DropSearch.Text.Trim();
        var shown = history.Drops.Where(d => query.Length == 0 || d.NpcName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || d.ItemName.Contains(query, StringComparison.OrdinalIgnoreCase) || d.ZoneLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || d.ItemId.ToString().Contains(query, StringComparison.Ordinal)).ToArray();
        DropList.ItemsSource = shown;
        DropList.SelectedItem = shown.FirstOrDefault(d => d.Key == selected) ?? shown.FirstOrDefault();
        ShowDrop(DropList.SelectedItem as NpcDropObservation);
    }
    private void ShowDrop(NpcDropObservation? drop)
    {
        DropDetails.Text = drop is null ? "Loot entries appear here as the selected character receives them."
            : $"Item {drop.ItemId} · {drop.SourceLabel} · Entry {drop.LootId}\nFirst seen {drop.FirstSeenUtc.ToLocalTime():g} · Last seen {drop.LastSeenUtc.ToLocalTime():HH:mm:ss}\n"
                + (drop.Zone is not null ? "Zone and level matched by corpse ID, source name and corpse type."
                    : $"Zone and level unconfirmed. Observed while in {drop.ObservationZone?.Name ?? "an unknown zone"}; that alone does not prove where the drop originated.");
    }
    private void InspectionFilter_Changed(object sender, System.Windows.RoutedEventArgs e) => FilterInspections();
    private void Inspection_Changed(object sender, C.SelectionChangedEventArgs e) => ShowInspection(InspectionList.SelectedItem as InspectedObservation);
    private void DropFilter_Changed(object sender, System.Windows.RoutedEventArgs e) => FilterDrops();
    private void Drop_Changed(object sender, C.SelectionChangedEventArgs e) => ShowDrop(DropList.SelectedItem as NpcDropObservation);
    private void Tab_Changed(object sender, C.SelectionChangedEventArgs e)
    { if (initialized && ReferenceEquals(e.Source, DataTabs)) UpdateSummary(); }

    private object? SelectedSnapshot()
    {
        if (DataTabs.SelectedItem == GatheringTab) return gatheringSnapshot;
        if (DataTabs.SelectedItem == MerchantsTab) return merchantCapture is null || !history.HasMerchantSource ? null : history.MerchantSnapshot(merchantCapture.CharacterName);
        if (DataTabs.SelectedItem == TargetTab) return targetError is null && targetCapture is not null ? MyTargetSpells.Export(targetCapture) : null;
        if (DataTabs.SelectedItem == RecipesTab) return recipeSnapshot;
        if (DataTabs.SelectedItem == InspectedTab) return inspectionCapture is null || !history.HasInspectionSource ? null : history.InspectionSnapshot(inspectionCapture.CharacterName);
        if (DataTabs.SelectedItem == DropsTab) return lootCapture is null || !history.HasLootSource ? null : history.DropSnapshot(lootCapture.CharacterName);
        return current;
    }
    private void UpdateSummary()
    {
        if (!initialized) return;
        string? error; DateTimeOffset? time; string? character;
        if (DataTabs.SelectedItem == GatheringTab)
        {
            error = null; time = gathering.Count == 0 ? null : gathering.Values.Max(row => row.SeenAtUtc); character = (ClientChoice.SelectedItem as DetectedClient)?.Character.Name;
            Counts.Text = $"{gathering.Count:N0} observed item / zone / gathering associations";
            ComparisonHint.Text = "Success messages and verified zone context. Repeated successes do not measure rates. Character names and chat are not shared.";
        }
        else if (DataTabs.SelectedItem == MerchantsTab)
        {
            error = merchantError; time = merchantCapture?.ObservedAtUtc; character = merchantCapture?.CharacterName;
            Counts.Text = $"{history.Merchants.Length:N0} unlimited item / merchant / zone associations";
            ComparisonHint.Text = merchantCapture is { IsOpen: true, Merchant: { } merchant }
                ? $"{merchant.Name} · {merchantCapture.Items.Length:N0} unlimited items · {merchantCapture.ExcludedLimitedStock:N0} limited-stock entries excluded"
                : "Open a merchant to collect unlimited stock. Previous observations stay in this viewer session.";
        }
        else if (DataTabs.SelectedItem == TargetTab)
        {
            error = targetError; time = targetCapture?.ObservedAtUtc; character = targetCapture?.CharacterName;
            Counts.Text = "Current target · Polls every 500 ms";
            ComparisonHint.Text = "Your spells on the selected target, with game-reported timers. Refreshes twice per second.";
        }
        else if (DataTabs.SelectedItem == InspectedTab)
        {
            error = inspectionError; time = inspectionCapture?.ObservedAtUtc; character = inspectionCapture?.CharacterName;
            Counts.Text = $"{history.Inspections.Select(i => i.ItemId).Distinct().Count():N0} inspected items · {history.Inspections.Length:N0} property variants saved this session";
            ComparisonHint.Text = "Item links can provide properties even when you do not own the item. Cached window records may remain after closing.";
        }
        else if (DataTabs.SelectedItem == DropsTab)
        {
            error = lootError; time = lootCapture?.ObservedAtUtc; character = lootCapture?.CharacterName;
            Counts.Text = $"{history.Drops.Length:N0} drop associations · {history.Drops.Count(d => d.Level is not null):N0} with NPC level · {history.Drops.Count(d => d.Zone is null):N0} with unknown zone";
            ComparisonHint.Text = "Observations stay here after looting. This does not record every kill or measure drop rates.";
        }
        else if (DataTabs.SelectedItem == RecipesTab)
        {
            error = recipeError; time = recipeSnapshot?.ObservedAtUtc; character = recipeSnapshot?.CharacterName;
            Counts.Text = $"{recipeSnapshot?.Recipes.Length ?? 0:N0} recipe summaries · {recipeSnapshot?.Recipes.Count(r => r.DetailsLoaded) ?? 0:N0} with loaded ingredients";
            ComparisonHint.Text = recipeSnapshot?.IsOpen == false
                ? "Open a tradeskill container in EverQuest, then search or choose a favorite to load recipes."
                : recipeSnapshot is { Recipes.Length: 0 }
                    ? $"{recipeSnapshot.ContainerName ?? "Tradeskill window"} · No recipes loaded. Search or choose a favorite in the game."
                    : $"{recipeSnapshot?.ContainerName ?? "No container"} · Select recipes in EverQuest to load ingredients. Observations share automatically when Recipes is enabled in Contributions.";
        }
        else
        {
            error = itemError; time = current?.ObservedAtUtc; character = current?.CharacterName;
            Counts.Text = current is null ? "Waiting for inventory…" : $"{current.Items.Select(i => i.Item.ItemId).Distinct().Count():N0} distinct items · {current.Items.Length:N0} item locations · {rows.Count(r => r.IsChanged):N0} changes";
            ComparisonHint.Text = baseline is null ? "The first successful reading becomes your comparison baseline."
                : $"Compared with {baseline.ObservedAtUtc.ToLocalTime():HH:mm:ss}. Choose a new baseline to clear the change markers.";
        }
        string latest = time is null ? "No successful reading yet" : $"Last successful read {time.Value.ToLocalTime():g} ({time.Value.ToLocalTime():HH:mm:ss})";
        SourceState.Text = latest + (DataTabs.SelectedItem == DropsTab && lootCapture?.ContextError is { } contextError
            ? " · NPC context unavailable: " + contextError : "");
        string source = SelectedProvenance();
        if (source.Length > 0) SourceState.Text = source + " · " + SourceState.Text;
        if (review) Status($"SAVED SNAPSHOT · {character} · {latest}");
        else if (paused) Status($"Paused · {character ?? (ClientChoice.SelectedItem as DetectedClient)?.Character.Name ?? "Select a character"} · Showing the last reading");
        else if (error is not null) Status("Reading unavailable for this tab; retained data may be stale. " + error, true);
        else Status((time is null ? "Waiting" : "Reading") + $" · {character ?? (ClientChoice.SelectedItem as DetectedClient)?.Character.Name ?? "Select a character"}"
            + (source.Length == 0 ? "" : " · " + source) + " · Read only");
        BaselineControl.IsEnabled = DataTabs.SelectedItem == InventoryTab && current is not null && itemError is null;
        ExportControl.IsEnabled = SelectedSnapshot() is not null;
    }
}
