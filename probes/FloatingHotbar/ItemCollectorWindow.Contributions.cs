using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

public partial class ItemCollectorWindow
{
    private ContributionDestinations? contributionClient;
    private ContributionSettingsWindow? contributionDialog;
    private readonly CancellationTokenSource contributionLifetime = new();
    private bool contributionStarting, contributionItemsBusy, contributionSpellsBusy;
    private string? contributionIssue;
    private int skippedItemDefinitions, skippedSpellDefinitions, skippedRecipeDefinitions;
    private readonly Dictionary<string, (ContributionSettings Settings, string Ids, DateTimeOffset At)> lastSpellContributions = [];

    private async Task InitializeContributions()
    {
        if (review || closed || !DataCollectionAccess.CanCollect || contributionClient is not null || contributionStarting) return;
        contributionStarting = true;
        ContributionsControl.IsEnabled = false;
        ShowContributionStatus();
        try
        {
            // Credential and queue storage initialization stays off the WPF thread.
            var client = await Task.Run(() => new ContributionDestinations());
            try { await client.Ready; }
            catch { client.Dispose(); throw; }
            if (closed) { client.Dispose(); return; }
            contributionClient = client;
            gatheringNeedsSharing = true;
            contributionClient.Changed += ContributionsChanged;
            contributionIssue = null;
        }
        catch { if (!closed) contributionIssue = "Contribution settings could not be loaded. Open Contributions to try again."; }
        finally
        {
            contributionStarting = false;
            if (!closed) { ContributionsControl.IsEnabled = true; ShowContributionStatus(); }
        }
    }

    private void ContributionsChanged()
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() => { if (!closed) ShowContributionStatus(); });
    }

    private void ShowContributionStatus()
    {
        if (!initialized || closed) return;
        if (review)
        {
            CollectorBrand.Text = "TEAM BAR · PREVIEW";
            ContributionState.Text = "Preview only · Contribution settings and uploads are disabled.";
            RetryContributionsControl.IsEnabled = false;
            return;
        }
        var destinations = contributionClient?.All ?? [];
        bool enabled = destinations.Any(d => d.Client.Settings.Enabled);
        CollectorBrand.Text = enabled ? "TEAM BAR · CONTRIBUTING" : "TEAM BAR · LOCAL ONLY";
        ContributionState.Text = contributionIssue ?? (destinations.Length == 0 ? contributionStarting ? "Loading contribution settings…" : "Contributions are off. Collection stays on this computer."
            : string.Join("\n", destinations.Select(d => $"{d.Name}: {(d.Client.Settings.Enabled ? "on" : "off")} · {d.Client.Status.PendingCount:N0} pending · {d.Client.Status.Message}")));
        string skipped = SkippedDefinitionsMessage(skippedItemDefinitions, skippedSpellDefinitions, skippedRecipeDefinitions);
        if (enabled && skipped.Length > 0) ContributionState.Text += $"\n{skipped}";
        ContributionState.SetResourceReference(C.TextBlock.ForegroundProperty,
            contributionIssue is not null || destinations.Any(d => d.Client.Status.NeedsAttention) ? "Error" : enabled && skipped.Length > 0 ? "WarningText" : "Muted");
        RetryContributionsControl.IsEnabled = destinations.Any(d => d.Client.Settings.Enabled && d.Client.Status is { IsSending: false, PendingCount: > 0 });
    }

    private async void Contributions_Click(object sender, W.RoutedEventArgs e)
    {
        if (closed) return;
        if (contributionDialog is not null) { contributionDialog.Activate(); return; }
        if (!review) await InitializeContributions();
        if (closed) return;
        if (!review && contributionClient is null) { ShowContributionStatus(); return; }
        contributionDialog = new ContributionSettingsWindow(this, contributionClient, review);
        try { contributionDialog.ShowDialog(); }
        finally
        {
            contributionDialog = null;
            ContributionSettingsChanged();
        }
    }

    internal void ContributionSettingsChanged()
    {
        gatheringNeedsSharing = true;
        lastSpellContributions.Clear();
        skippedItemDefinitions = skippedSpellDefinitions = skippedRecipeDefinitions = 0;
        contributionIssue = null;
        if (!closed) ShowContributionStatus();
    }

    private void RetryContributions_Click(object sender, W.RoutedEventArgs e)
    {
        if (review || closed || contributionClient is null) return;
        contributionIssue = null;
        contributionClient.Retry();
        ShowContributionStatus();
    }

    private bool CanContribute(int version, ContributionDestinations? client, ContributionRoute[] routes) =>
        !review && !closed && !paused && DataCollectionAccess.CanCollect && version == generation
        && client is not null && ReferenceEquals(client, contributionClient) && routes.Any(client.IsCurrent);

    private async Task ContributeSources(ContributionRecord[] records, int version)
    {
        var client = contributionClient;
        var routes = client?.Snapshot() ?? [];
        if (records.Length == 0 || !CanContribute(version, client, routes)) return;
        try
        {
            var failures = await client!.Enqueue(records, routes, contributionLifetime.Token);
            if (CanContribute(version, client, routes) && failures.Length > 0)
            { contributionIssue = string.Join(", ", failures) + ": source observations could not be queued."; ShowContributionStatus(); }
        }
        catch (OperationCanceledException) { }
    }

    private static ContributionSource CaptureSource(string channel, string build, string decoder) =>
        new("Team Bar", channel, Version: "1", BuildSha256: build, Decoder: decoder);

    private static string SkippedDefinitionsMessage(int items, int spells, int recipes = 0) => items + spells + recipes == 0 ? ""
        : $"Unsupported definitions skipped in the latest observations: {items:N0} item(s), {spells:N0} spell(s), {recipes:N0} recipe(s).";

    // The same gate applies before export and again after background work. Tests
    // can exercise character changes and cancellation without storage or HTTP.
    private static async Task<bool> QueueCurrentContributions(Func<bool> isCurrent,
        Func<CancellationToken, Task<ContributionRecord[]>> export,
        Func<ContributionRecord[], CancellationToken, Task> enqueue, CancellationToken token)
    {
        if (!isCurrent()) return false;
        var records = await export(token);
        token.ThrowIfCancellationRequested();
        if (!isCurrent()) return false;
        await enqueue(records, token);
        return true;
    }

    private async Task ContributeItems(InventoryCapture? inventory, InspectionCapture? inspection, int version, RecipeSnapshot? recipes = null)
    {
        var client = contributionClient;
        var routes = client?.Snapshot() ?? [];
        if (contributionItemsBusy || !CanContribute(version, client, routes) || inventory is null && inspection is null && recipes is null) return;
        contributionItemsBusy = true;
        try
        {
            int skipped = 0, skippedRecipes = 0, skippedInspectionSpells = 0; string[] failed = [];
            bool queued = await QueueCurrentContributions(() => CanContribute(version, client, routes), cancel => Task.Run(() =>
            {
                var observations = new List<ContributionRecord>();
                if (inventory is not null) observations.AddRange(ContributionPayload.Items(inventory.Items.Select(entry => entry.Item),
                    CaptureSource(inventory.ClientChannel, inventory.BuildSha256, inventory.Decoder), _ => skipped++));
                if (inspection is not null) observations.AddRange(ContributionPayload.InspectedItems(inspection,
                    rejection => { if (rejection.Kind == "item") skipped++; else if (rejection.Kind == "spell") skippedInspectionSpells++; }));
                if (recipes is not null) observations.AddRange(ContributionPayload.Recipes(recipes.Recipes,
                    CaptureSource(recipes.ClientChannel, recipes.BuildSha256, recipes.Decoder), _ => skippedRecipes++));
                return observations.ToArray();
            }, cancel), async (records, cancel) => { failed = await client!.Enqueue(records, routes, cancel); }, contributionLifetime.Token);
            if (queued && CanContribute(version, client, routes))
            {
                skippedItemDefinitions = skipped;
                skippedRecipeDefinitions = skippedRecipes;
                if (inspection is not null) skippedSpellDefinitions = skippedInspectionSpells;
                contributionIssue = failed.Length == 0 ? null : string.Join(", ", failed) + ": observations could not be queued. Other destinations continue independently.";
                ShowContributionStatus();
            }
        }
        catch (OperationCanceledException) { }
        catch { if (CanContribute(version, client, routes)) { contributionIssue = "Some item or recipe observations could not be queued. Collection will try again."; ShowContributionStatus(); } }
        finally { contributionItemsBusy = false; }
    }

    private async Task ContributeSpells(TargetCapture capture, int version)
    {
        var client = contributionClient;
        var routes = client?.Snapshot() ?? [];
        if (contributionSpellsBusy || !CanContribute(version, client, routes) || !MyTargetSpells.Matches(capture)) return;
        // Memory collection currently verifies only Live. A future channel must supply
        // its own validated installation before observed IDs can resolve definitions.
        if (capture.ClientChannel != "Live")
        {
            contributionIssue = "Spell contributions need a validated installation for this client channel. No Live definitions were substituted.";
            ShowContributionStatus(); return;
        }
        // Only IDs cross into the exporter. Target names, casters and remaining
        // durations never become contribution data. Definitions come from the
        // installed spell file, without another game-memory read.
        int[] ids = capture.Buffs!.Effects.Select(effect => effect.SpellId).Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0) return;
        string fingerprint = capture.ClientChannel + ":" + capture.BuildSha256 + ":" + string.Join(',', ids);
        routes = routes.Where(route => !lastSpellContributions.TryGetValue(route.Destination.Id, out var prior)
            || !ReferenceEquals(prior.Settings, route.Settings) || prior.Ids != fingerprint
            || DateTimeOffset.UtcNow - prior.At >= TimeSpan.FromSeconds(30)).ToArray();
        if (routes.Length == 0) return;
        contributionSpellsBusy = true;
        try
        {
            int skipped = 0; string[] failed = [];
            bool queued = await QueueCurrentContributions(() => CanContribute(version, client, routes), cancel => Task.Run(() => ContributionPayload.ObservedSpells(ids,
                CaptureSource(capture.ClientChannel, capture.BuildSha256, "live-installed-spells-166"), _ => skipped++), cancel),
                async (records, cancel) => { failed = await client!.Enqueue(records, routes, cancel); }, contributionLifetime.Token);
            if (!queued || !CanContribute(version, client, routes)) return;
            skippedSpellDefinitions = skipped;
            contributionIssue = failed.Length == 0 ? null : string.Join(", ", failed) + ": spell definitions could not be queued. Other destinations continue independently.";
            foreach (var route in routes.Where(route => client!.IsCurrent(route) && !failed.Contains(route.Destination.Name)))
                lastSpellContributions[route.Destination.Id] = (route.Settings, fingerprint, DateTimeOffset.UtcNow);
            ShowContributionStatus();
        }
        catch (OperationCanceledException) { }
        catch { if (CanContribute(version, client, routes)) { contributionIssue = "Some spell definitions could not be queued. Collection will try again."; ShowContributionStatus(); } }
        finally { contributionSpellsBusy = false; }
    }

    private void CloseContributions()
    {
        contributionLifetime.Cancel();
        contributionDialog?.Close();
        if (contributionClient is { } client)
        {
            client.Changed -= ContributionsChanged;
            client.Dispose();
            contributionClient = null;
        }
    }
}
