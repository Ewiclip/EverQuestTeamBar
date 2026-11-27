namespace TeamBar;

public partial class ItemCollectorWindow
{
    private static void TestCollectorBuildSelection(ItemCollectorWindow window, Action<bool, string> check)
    {
        var live = new DetectedClient(1, 100, 11, 0, new("Fellwort", 16, 130));
        var beta = live with { ProcessId = 2, Window = 22, ClientChannel = "Beta", BuildSha256 = CollectorBuild.BetaBuildHash };
        window.ClientChoice.ItemsSource = new[] { live, beta };
        window.ClientChoice.SelectedItem = live;
        var fixture = new RecipeSnapshot(1, DateTimeOffset.UtcNow, "Fellwort", Identity.BuildHash,
            "loaded-tradeskill-window-results", "Fixture container", 1, 30,
            [new(30, "Patchwork Boots", 26, 1, 1, 0, true, [new(0, 1, 10, "Fixture ingredient")])], "Synthetic fixture");
        window.AcceptRecipes(fixture);
        window.ClientChoice.SelectedItem = beta;
        check(window.recipeSnapshot is null && window.current is null && window.targetCapture is null && window.history.Inspections.Length == 0,
            "switching same-name Live and Beta clients clears prior snapshots and history");
        check(live.CollectorLabel.Contains("Live") && beta.CollectorLabel.Contains("Beta")
            && window.SourceState.Text.Contains("Beta") && window.SourceState.Text.Contains(CollectorBuild.BetaBuildHash[..12]),
            "collector selection and status distinguish channel and exact build");
        check(window.InventoryTab.IsEnabled == CollectorBuild.Beta.Supports(CollectorCapability.Inventory)
            && window.InspectedTab.IsEnabled == CollectorBuild.Beta.Supports(CollectorCapability.Inspection)
            && window.DropsTab.IsEnabled == CollectorBuild.Beta.Supports(CollectorCapability.AdvancedLoot)
            && !window.MerchantsTab.IsEnabled
            && window.TargetTab.IsEnabled == CollectorBuild.Beta.Supports(CollectorCapability.Target | CollectorCapability.TargetBuffs)
            && window.CapabilityNotice.Text.Contains("will not be read")
            && window.RecipesTab.IsEnabled == CollectorBuild.Beta.Supports(CollectorCapability.Recipes),
            "Beta enables only independently validated surfaces and explains disabled tabs");
        int attempted = 0; bool rejected = false;
        try { ReadSupported(CollectorBuild.Beta, CollectorCapability.TargetBuffs, () => ++attempted); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && attempted == 0, "unsupported Beta target effects never call the Live capture function");
        rejected = false;
        try { ReadSupported(CollectorBuild.Beta, CollectorCapability.Merchants, () => ++attempted); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && attempted == 0, "unsupported Beta merchants never call the Live capture function");
        rejected = false;
        try { RequireCaptureSource(beta, "Live", Identity.BuildHash); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "collector rejects a Live-stamped result for the selected Beta client");
        RequireCaptureSource(beta, "Beta", CollectorBuild.BetaBuildHash);
        if (window.RecipesTab.IsEnabled)
        {
            window.DataTabs.SelectedItem = window.RecipesTab;
            window.AcceptRecipes(fixture with { ClientChannel = "Beta", BuildSha256 = CollectorBuild.BetaBuildHash, Decoder = "beta-recipe-window-2" });
            window.UpdateSummary();
            check(window.SelectedSnapshot() is RecipeSnapshot { ClientChannel: "Beta", BuildSha256: CollectorBuild.BetaBuildHash }
                && window.SourceState.Text.Contains("Beta"), "Beta recipe preview and saved snapshot retain channel and build provenance");
        }
        window.ClientChoice.SelectedItem = live;
        check(window.InventoryTab.IsEnabled && window.InspectedTab.IsEnabled && window.DropsTab.IsEnabled && window.TargetTab.IsEnabled
            && window.recipeSnapshot is null, "returning to Live restores supported tabs without retaining Beta snapshots");
        window.ClientChoice.ItemsSource = null;
    }
}
