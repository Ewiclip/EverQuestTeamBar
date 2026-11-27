using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class ItemCollectorWindow
{
    private static T ReadCollection<T>(CollectorBuild build, CollectorCapability capability, Func<T> capture) =>
        DataCollectionAccess.Read(() => ReadSupported(build, capability, capture));
    private static T ReadSupported<T>(CollectorBuild build, CollectorCapability capability, Func<T> capture)
    { build.Require(capability); return capture(); }

    private static void RequireCaptureSource(DetectedClient client, string channel, string hash)
    {
        if (channel != client.ClientChannel || hash != client.BuildSha256)
            throw new InvalidDataException("The capture does not match the selected channel and build. Nothing was accepted.");
    }
    private void ApplyCollectorCapabilities(DetectedClient? client)
    {
        CollectorBuild? build = client is null ? null : CollectorBuild.ForClient(client);
        (C.TabItem Tab, CollectorCapability Capability, string Name)[] tabs =
        [
            (InventoryTab, CollectorCapability.Inventory, "Inventory"), (RecipesTab, CollectorCapability.Recipes, "Recipes"),
            (InspectedTab, CollectorCapability.Inspection, "Inspected items"), (DropsTab, CollectorCapability.AdvancedLoot, "NPC drops"),
            (MerchantsTab, CollectorCapability.Merchants, "Merchants"),
            (TargetTab, CollectorCapability.Target | CollectorCapability.TargetBuffs, "Target spells"),
            (GatheringTab, CollectorCapability.Target, "Foraging / fishing zone context")
        ];
        foreach (var (tab, capability, name) in tabs)
        {
            tab.IsEnabled = build is null || build.Supports(capability);
            tab.Opacity = tab.IsEnabled ? 1 : 0.45;
            tab.ToolTip = tab.IsEnabled ? null : $"{name} is not yet validated for this {build!.Channel} build. No game data is read for this tab.";
            C.ToolTipService.SetShowOnDisabled(tab, true);
        }
        if (DataTabs.SelectedItem is C.TabItem { IsEnabled: false })
            DataTabs.SelectedItem = tabs.FirstOrDefault(t => t.Tab.IsEnabled).Tab;
        string[] unavailable = tabs.Where(t => !t.Tab.IsEnabled).Select(t => t.Name.ToLowerInvariant()).ToArray();
        CapabilityNotice.Visibility = unavailable.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        CapabilityNotice.Text = unavailable.Length == 0 ? "" : $"{build!.Channel} collection: {string.Join(", ", unavailable)} are not yet validated and will not be read."
            + (tabs.All(t => !t.Tab.IsEnabled) ? " Only character identity is available for this build." : "");
    }
    private string SelectedProvenance()
    {
        (string? Channel, string? Hash) source = DataTabs.SelectedItem == GatheringTab ? (gatheringSnapshot?.ClientChannel, gatheringSnapshot?.BuildSha256)
            : DataTabs.SelectedItem == RecipesTab ? (recipeSnapshot?.ClientChannel, recipeSnapshot?.BuildSha256)
            : DataTabs.SelectedItem == TargetTab ? (targetCapture?.ClientChannel, targetCapture?.BuildSha256)
            : DataTabs.SelectedItem == InspectedTab ? (inspectionCapture?.ClientChannel, inspectionCapture?.BuildSha256)
            : DataTabs.SelectedItem == DropsTab ? (lootCapture?.ClientChannel, lootCapture?.BuildSha256)
            : DataTabs.SelectedItem == MerchantsTab ? (merchantCapture?.ClientChannel, merchantCapture?.BuildSha256)
            : (current?.ClientChannel, current?.BuildSha256);
        if (source.Channel is null && ClientChoice.SelectedItem is DetectedClient client)
            source = (client.ClientChannel, client.BuildSha256);
        return source.Channel is null ? "" : source.Channel + (source.Hash is null ? "" : " · Build " + source.Hash[..Math.Min(12, source.Hash.Length)]);
    }
}
