using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace TeamBar;

public partial class ItemCollectorWindow : W.Window
{
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private InventoryCapture? baseline, current;
    private CollectionRow[] rows = [];
    private bool busy, closed, paused, initialized, loadingClients, review;
    private int generation;
    private string? currentFingerprint;
    private RecipeSnapshot? recipeSnapshot;
    private string? recipeFingerprint;
    internal ItemCollectorWindow(bool reviewOnly = false)
    {
        review = reviewOnly;
        InitializeComponent();
        Icon = AppBranding.WindowIcon;
        AppearanceTheme theme = AppearanceTheme.Light;
        if (!review)
            try { theme = new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath).Load().Theme; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        ThemePalette.Apply(Resources, theme);
        initialized = true;
        timer.Tick += async (_, _) => await ReadNow();
        targetTimer.Tick += async (_, _) => await ReadTargetNow();
        Loaded += async (_, _) => await InitializeRuntime();
        Closed += (_, _) => { closed = true; generation++; timer.Stop(); targetTimer.Stop(); CloseContributions(); };
        ShowContributionStatus();
    }
    private bool runtimeInitialized;
    private W.FrameworkElement? settingsContent;
    private SetupWindow? settingsOwner;
    internal W.Window DialogOwner => (W.Window?)settingsOwner ?? this;
    internal W.FrameworkElement TakeSettingsContent(SetupWindow owner)
    {
        settingsOwner = owner;
        if (settingsContent is null)
        {
            settingsContent = (W.FrameworkElement)Content; Content = null;
            settingsContent.Resources = Resources;
            C.TextBlock.SetFontSize(settingsContent, 13);
            settingsContent.SetResourceReference(C.TextBlock.ForegroundProperty, "Text");
        }
        _ = InitializeRuntime(); return settingsContent;
    }
    internal void SetSettingsTheme(AppearanceTheme theme) => ThemePalette.Apply(Resources, theme);
    private async Task InitializeRuntime()
    {
        if (review || closed || runtimeInitialized || !DataCollectionAccess.CanCollect) return;
        runtimeInitialized = true;
        _ = InitializeContributions(); await Discover();
        if (!closed && DataCollectionAccess.CanCollect) { timer.Start(); targetTimer.Start(); }
    }
    private void Status(string value, bool error = false)
    { StatusText.Text = value; StatusText.SetResourceReference(C.TextBlock.ForegroundProperty, error ? "Error" : "Muted"); }
    private async Task Discover()
    {
        if (loadingClients || closed || !DataCollectionAccess.CanCollect) return;
        loadingClients = true; RefreshControl.IsEnabled = false;
        try
        {
            var previous = ClientChoice.SelectedItem as DetectedClient;
            var scan = await Task.Run(() => DataCollectionAccess.Read(() => CollectorIdentity.Discover()));
            if (closed || !DataCollectionAccess.CanCollect) return;
            ApplyDiscovery(scan, previous);
        }
        catch (Exception error)
        {
            if (!closed) { ShowDiscoveryErrors([error.Message]); Status(error.Message, true); }
        }
        finally { loadingClients = false; if (!closed) RefreshControl.IsEnabled = true; }
        await Task.WhenAll(ReadNow(), ReadTargetNow());
    }
    private void ApplyDiscovery(Discovery scan, DetectedClient? previous)
    {
        ClientChoice.ItemsSource = scan.Clients;
        ClientChoice.SelectedItem = scan.Clients.FirstOrDefault(c => previous is not null && Identity.Same(previous, c))
            ?? scan.Clients.FirstOrDefault();
        // Keep discovery failures independent of the selected client's status:
        // successful Live polling must not erase an unavailable Beta explanation.
        ShowDiscoveryErrors(scan.Errors);
        if (scan.Clients.Length == 0) Status(scan.Errors.FirstOrDefault() ?? "No supported clients found. Enter the game, then refresh.", true);
    }
    private void ShowDiscoveryErrors(string[] errors)
    {
        DiscoveryWarningText.Text = string.Join(Environment.NewLine, errors);
        DiscoveryWarning.Visibility = errors.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
    private async Task ReadNow()
    {
        if (busy || paused || closed || review || !DataCollectionAccess.CanCollect || ClientChoice.SelectedItem is not DetectedClient client) return;
        int version = generation; busy = true;
        try
        {
            var build = CollectorBuild.ForClient(client);
            var readings = await Task.Run(() =>
            {
                InventoryCapture? inventory = null; RecipeSnapshot? recipes = null;
                InspectionCapture? inspection = null; AdvancedLootCapture? loot = null;
                MerchantCapture? merchant = null;
                string? itemError = null, recipeError = null, inspectionError = null, lootError = null, merchantError = null;
                try { inventory = ReadCollection(build, CollectorCapability.Inventory, () => InventoryDecoder.Capture(client.ProcessId, client.StartedUtcTicks, build)); } catch (Exception e) { itemError = e.Message; }
                try { recipes = ReadCollection(build, CollectorCapability.Recipes, () => RecipeDecoder.Capture(client.ProcessId, client.StartedUtcTicks, build)); } catch (Exception e) { recipeError = e.Message; }
                try { inspection = ReadCollection(build, CollectorCapability.Inspection, () => InspectionDecoder.Capture(client.ProcessId, client.StartedUtcTicks, build)); } catch (Exception e) { inspectionError = e.Message; }
                try { loot = ReadCollection(build, CollectorCapability.AdvancedLoot, () => AdvancedLootDecoder.Capture(client.ProcessId, client.StartedUtcTicks)); } catch (Exception e) { lootError = e.Message; }
                try { merchant = ReadCollection(build, CollectorCapability.Merchants, () => MerchantDecoder.Capture(client.ProcessId, client.StartedUtcTicks, build)); } catch (Exception e) { merchantError = e.Message; }
                return (inventory, recipes, inspection, loot, merchant, itemError, recipeError, inspectionError, lootError, merchantError);
            });
            if (closed || version != generation || !DataCollectionAccess.CanCollect) return;
            if (readings.inventory is { } sourceInventory) RequireCaptureSource(client, sourceInventory.ClientChannel, sourceInventory.BuildSha256);
            if (readings.recipes is { } sourceRecipes) RequireCaptureSource(client, sourceRecipes.ClientChannel, sourceRecipes.BuildSha256);
            if (readings.inspection is { } sourceInspection) RequireCaptureSource(client, sourceInspection.ClientChannel, sourceInspection.BuildSha256);
            if (readings.loot is { } sourceLoot) RequireCaptureSource(client, sourceLoot.ClientChannel, sourceLoot.BuildSha256);
            if (readings.merchant is { } sourceMerchant) RequireCaptureSource(client, sourceMerchant.ClientChannel, sourceMerchant.BuildSha256);
            if (readings.inventory is { } capture && (capture.CharacterName != client.Character.Name || capture.CharacterClass != client.Character.Class)
                || readings.recipes is { } recipes && (recipes.CharacterName != client.Character.Name
                    || recipes.CharacterClass is { } recipeClass && recipeClass != client.Character.Class)
                || readings.inspection is { } inspection && (inspection.CharacterName != client.Character.Name || inspection.CharacterClass != client.Character.Class)
                || readings.loot is { } loot && loot.CharacterName != client.Character.Name
                || readings.merchant is { } merchant && (merchant.CharacterName != client.Character.Name || merchant.CharacterClass != client.Character.Class))
                throw new InvalidDataException("Character or persona changed. Refresh the character list before collecting again.");
            itemError = readings.itemError; recipeError = readings.recipeError;
            inspectionError = readings.inspectionError; lootError = readings.lootError;
            merchantError = readings.merchantError;
            if (readings.inventory is not null) Accept(readings.inventory);
            if (readings.recipes is not null) AcceptRecipes(readings.recipes);
            if (readings.inspection is not null) AcceptInspection(readings.inspection);
            if (readings.loot is not null) AcceptLoot(readings.loot);
            if (readings.merchant is not null) AcceptMerchant(readings.merchant);
            UpdateSummary();
            await ContributeItems(readings.inventory, readings.inspection, version, readings.recipes);
            if (readings.loot is { } observedLoot)
                await ContributeSources(ContributionSources.Drops(history.Drops,
                    CaptureSource(observedLoot.ClientChannel, observedLoot.BuildSha256, observedLoot.Decoder)), version);
            await ReadGathering(client, version);
        }
        catch (Exception error)
        {
            if (!closed && version == generation)
            {
                itemError = recipeError = inspectionError = lootError = merchantError = error.Message;
                UpdateSummary();
            }
        }
        finally { busy = false; }
    }
    private void Accept(InventoryCapture capture)
    {
        if (baseline is not null) ItemCollection.EnsureComparable(baseline, capture);
        current = capture; baseline ??= capture;
        string fingerprint = CollectorFormat.Hash(capture.Items);
        if (fingerprint != currentFingerprint)
        {
            rows = ItemCollection.Compare(baseline, capture); currentFingerprint = fingerprint; Filter();
        }
    }
    private void Filter()
    {
        if (!initialized) return;
        int? selectedId = (ItemList.SelectedItem as CollectionRow)?.ItemId;
        string query = Search.Text.Trim();
        var shown = rows.Where(r => (!ChangesOnly.IsChecked.GetValueOrDefault() || r.IsChanged)
            && (!TradeskillOnly.IsChecked.GetValueOrDefault() || r.IsTradeskill)
            && (query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.ItemId.ToString().Contains(query, StringComparison.Ordinal))).ToArray();
        ItemList.ItemsSource = shown;
        ItemList.SelectedItem = shown.FirstOrDefault(r => r.ItemId == selectedId) ?? shown.FirstOrDefault();
        if (shown.Length == 0) ShowItem(null);
    }
    private void ShowItem(CollectionRow? row)
    {
        if (row is null) { ItemTitle.Text = rows.Length == 0 ? "Waiting for items" : "No matching items"; ItemSubtitle.Text = ""; ItemLocations.Text = ""; Properties.ItemsSource = null; return; }
        ItemTitle.Text = row.Name; ItemSubtitle.Text = $"Item {row.ItemId} · {row.Change}";
        ItemLocations.Text = row.Slots.Length == 0 ? "No longer in this inventory." : string.Join("\n", row.Slots.Select(ItemCollection.Location));
        if (row.IsChanged && row.PreviousSlots.Length > 0) ItemLocations.Text += "\nPreviously: " + string.Join(", ", row.PreviousSlots.Select(ItemCollection.Location));
        Properties.ItemsSource = DescribeItems(row.Variants);
    }
    private static List<Detail> DescribeItems(ItemData[] variants)
    {
        var fields = new List<Detail>();
        void Add(string label, object value) => fields.Add(new(label, value.ToString() ?? ""));
        foreach (var variant in variants)
        {
            var d = variant.CandidateDetails;
            if (variants.Length > 1) Add("Definition variant", Array.IndexOf(variants, variant) + 1);
            Add("Lore name", d.LoreName); Add("Required / recommended", $"{d.RequiredLevel} / {d.RecommendedLevel}");
            Add("AC", d.ArmorClass); Add("Hit points", d.HitPoints); Add("Mana", d.Mana); Add("Endurance", d.Endurance);
            Add("Damage / delay", $"{d.Damage} / {d.Delay}"); Add("Tradeskill / quest", $"{d.Tradeskill} / {d.QuestItem}");
            Add("Droppable", d.Droppable); Add("Attuneable / heirloom", $"{d.Attuneable} / {d.Heirloom}");
            Add("Stack limit", d.StackSize); Add("Lore group", d.LoreGroup); Add("Icon ID", d.IconId);
            Add("Classes mask", $"0x{d.ClassesMask:X}"); Add("Races mask", $"0x{d.RacesMask:X}");
            Add("Equipment slots mask", $"0x{d.EquipSlotsMask:X}"); Add("Augmentation type", d.AugmentationType);
        }
        return fields;
    }
    private sealed record Detail(string Label, string Value);
    private void AcceptRecipes(RecipeSnapshot snapshot)
    {
        recipeSnapshot = snapshot;
        string fingerprint = CollectorFormat.Hash(snapshot with { ObservedAtUtc = default });
        if (fingerprint != recipeFingerprint) { recipeFingerprint = fingerprint; FilterRecipes(); }
    }
    private void FilterRecipes()
    {
        if (!initialized) return;
        int? previous = (RecipeList.SelectedItem as LoadedRecipe)?.RecipeId;
        string query = RecipeSearch.Text.Trim();
        var shown = (recipeSnapshot?.Recipes ?? []).Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.RecipeId.ToString().Contains(query, StringComparison.Ordinal)).ToArray();
        RecipeList.ItemsSource = shown;
        RecipeList.SelectedItem = shown.FirstOrDefault(r => r.RecipeId == previous) ?? shown.FirstOrDefault(r => r.RecipeId == recipeSnapshot?.SelectedRecipeId) ?? shown.FirstOrDefault();
        ShowRecipe(RecipeList.SelectedItem as LoadedRecipe);
    }
    private void ShowRecipe(LoadedRecipe? recipe)
    {
        RecipeTitle.Text = recipe?.Name ?? "No loaded recipes match";
        RecipeMeta.Text = recipe is null ? "Search in EverQuest to request recipes." : $"Recipe {recipe.RecipeId} · Trivial {recipe.Trivial}\nContainer item {recipe.ContainerItemId} · Container type {recipe.ContainerType}\n" + (recipe.RecipeId == recipeSnapshot?.SelectedRecipeId ? "Selected in EverQuest" : "Not currently selected in EverQuest");
        IngredientState.Text = recipe?.DetailsLoaded == true ? "Ingredient data received by the client:" : "Summary only. Select this recipe in EverQuest to request its ingredients.";
        IngredientList.ItemsSource = recipe?.IngredientSlots.Where(i => i.ItemId > 0).ToArray();
    }
    private void Recipe_Changed(object sender, C.SelectionChangedEventArgs e) => ShowRecipe(RecipeList.SelectedItem as LoadedRecipe);
    private void RecipeFilter_Changed(object sender, W.RoutedEventArgs e) => FilterRecipes();
    private void Item_Changed(object sender, C.SelectionChangedEventArgs e) => ShowItem(ItemList.SelectedItem as CollectionRow);
    private void Filter_Changed(object sender, W.RoutedEventArgs e) => Filter();
    private async void Client_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        generation++; baseline = current = null; currentFingerprint = null; rows = []; Filter();
        recipeSnapshot = null; recipeFingerprint = null; FilterRecipes();
        history = new(); inspectionCapture = null; lootCapture = null;
        merchantCapture = null; merchantRowsFingerprint = null;
        ResetTarget();
        ResetGathering();
        itemError = recipeError = inspectionError = lootError = merchantError = null;
        ApplyCollectorCapabilities(ClientChoice.SelectedItem as DetectedClient);
        FilterInspections(); FilterDrops(); FilterMerchants(); UpdateSummary();
        await Task.WhenAll(ReadNow(), ReadTargetNow());
    }
    private async void Refresh_Click(object sender, W.RoutedEventArgs e) => await Discover();
    private async void Pause_Click(object sender, W.RoutedEventArgs e)
    {
        paused = !paused; generation++; PauseControl.Content = paused ? "Resume reading" : "Pause reading";
        gatheringReader = new();
        ShowTarget();
        UpdateSummary(); if (!paused) await Task.WhenAll(ReadNow(), ReadTargetNow());
    }
    private void Baseline_Click(object sender, W.RoutedEventArgs e)
    { if (current is not null) { baseline = current; currentFingerprint = null; Accept(current); UpdateSummary(); } }
    private void Export_Click(object sender, W.RoutedEventArgs e)
    {
        object? snapshot = SelectedSnapshot();
        if (snapshot is null) return;
        string prefix = DataTabs.SelectedItem == MerchantsTab ? "merchants" : DataTabs.SelectedItem == GatheringTab ? "gathering" : DataTabs.SelectedItem == TargetTab ? "target" : DataTabs.SelectedItem == RecipesTab ? "recipes" : DataTabs.SelectedItem == InspectedTab ? "inspected-items" : DataTabs.SelectedItem == DropsTab ? "npc-drops" : "inventory";
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON snapshot (*.json)|*.json", FileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json", OverwritePrompt = true };
        if (dialog.ShowDialog(DialogOwner) != true) return;
        try { CollectorFormat.WriteNew(dialog.FileName, snapshot); SaveHint.Text = "Saved locally: " + dialog.FileName; }
        catch (Exception error) { Status("Snapshot was not saved: " + error.Message, true); }
    }
    internal static void Review(string capturePath, string imagePath, AppearanceTheme theme = AppearanceTheme.Light)
    {
        string json = File.ReadAllText(capturePath);
        using var document = JsonDocument.Parse(json);
        string? source = document.RootElement.TryGetProperty("sourceKind", out var kind) ? kind.GetString() : null;
        bool isRecipe = document.RootElement.TryGetProperty("scope", out _);
        var app = new W.Application(); var window = new ItemCollectorWindow(true); Exception? failed = null;
        ThemePalette.Apply(window.Resources, theme);
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                window.ClientChoice.DisplayMemberPath = "";
                window.ClientChoice.ItemsSource = new[] { document.RootElement.TryGetProperty("characterName", out var characterName) ? characterName.GetString() : "Saved sources" }; window.ClientChoice.SelectedIndex = 0;
                if (source == "item-display") { window.AcceptInspection(JsonSerializer.Deserialize<InspectionCapture>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.InspectedTab; }
                else if (source == "advanced-loot") { window.AcceptLoot(JsonSerializer.Deserialize<AdvancedLootCapture>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.DropsTab; }
                else if (source == "merchant-unlimited") { window.AcceptMerchant(JsonSerializer.Deserialize<MerchantCapture>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.MerchantsTab; }
                else if (source == "selected-target") { window.AcceptTarget(JsonSerializer.Deserialize<TargetCapture>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.TargetTab; }
                else if (source == "gathering-session") { window.AcceptGatheringSnapshot(JsonSerializer.Deserialize<GatheringHistorySnapshot>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.GatheringTab; }
                else if (isRecipe) { window.AcceptRecipes(JsonSerializer.Deserialize<RecipeSnapshot>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.RecipesTab; }
                else { window.Accept(JsonSerializer.Deserialize<InventoryCapture>(json, CollectorFormat.Json)!); window.DataTabs.SelectedItem = window.InventoryTab; }
                window.UpdateSummary();
                window.PauseControl.IsEnabled = window.RefreshControl.IsEnabled = window.ClientChoice.IsEnabled = false;
                // Let the selected tab create and bind its virtualized rows before rendering the review image.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.CollectorRoot.ActualWidth,
                    (int)window.CollectorRoot.ActualHeight, 96, 96, M.PixelFormats.Pbgra32);
                bitmap.Render(window.CollectorRoot); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = new FileStream(imagePath, FileMode.CreateNew); encoder.Save(output);
            }
            catch (Exception error) { failed = error; }
            finally { window.Close(); }
        };
        app.Run(window); if (failed is not null) throw failed;
    }
}
