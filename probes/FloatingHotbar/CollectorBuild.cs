[Flags]
internal enum CollectorCapability
{
    None = 0, Identity = 1, Inventory = 2, Recipes = 4, Inspection = 8,
    Target = 16, TargetBuffs = 32, AdvancedLoot = 64, Merchants = 128
}

// This registry grants passive decoder capabilities only. It is deliberately
// separate from Identity.Read/Discover and the native Live-only input policy.
internal sealed class CollectorBuild
{
    internal const string BetaBuildHash = "2d97f027f290568514a79990ba4ca2e7efe47b1d9fee690badf288aa218f72ac";
    internal static readonly CollectorBuild Live = new("Live", MemoryReader.LiveExecutablePath, Identity.BuildHash,
        Identity.LiveLayout, CollectorCapability.Identity | CollectorCapability.Inventory | CollectorCapability.Recipes
            | CollectorCapability.Inspection | CollectorCapability.Target | CollectorCapability.TargetBuffs | CollectorCapability.AdvancedLoot);
    // Exact 2d97... identity verified from static code and repeated, bounded
    // observations from a level 130 Berserker. Passive surfaces have separate layouts;
    // targets, effects and advanced loot remain disabled pending validation.
    internal static readonly CollectorBuild Beta = new("Beta", @"C:\Everquest-Beta\eqgame.exe", BetaBuildHash,
        new(0xf351d8, 0xf37a78, 0xfb6530, 0x5e4, 0x2890, 0x135, 0xb4, 0x2fe8, 0x2f58, 0x2f50, 0xfbc, 0x17cc, 0x17e8),
        CollectorCapability.Identity | CollectorCapability.Recipes | CollectorCapability.Inventory | CollectorCapability.Inspection);

    internal string Channel { get; }
    internal string ExecutablePath { get; }
    internal string InstallationPath => Path.GetDirectoryName(ExecutablePath)!;
    internal string BuildSha256 { get; }
    private readonly IdentityLayout identityLayout;
    internal bool UsesResolvedIdentity { get; private init; }
    internal IdentityLayout IdentityLayout => UsesResolvedIdentity
        ? throw new InvalidDataException("This build requires its resolved identity layout.") : identityLayout;
    internal CollectorCapability Capabilities { get; }
    private CollectorBuild(string channel, string path, string hash, IdentityLayout identity, CollectorCapability capabilities)
    { Channel = channel; ExecutablePath = path; BuildSha256 = hash; identityLayout = identity; Capabilities = capabilities; }

    internal bool Supports(CollectorCapability capability) => capability != CollectorCapability.None && (Capabilities & capability) == capability;
    internal void Require(CollectorCapability capability)
    {
        if (!Supports(capability)) throw new InvalidDataException($"{Channel} {capability} collection is not validated for this build. Nothing was read.");
    }
    internal static CollectorBuild ForPath(string path)
    {
        if (MemoryReader.MatchesExecutablePath(path, Live.ExecutablePath)) return Live;
        if (MemoryReader.MatchesExecutablePath(path, Beta.ExecutablePath)) return Beta;
        throw new InvalidDataException("This installation has no registered passive collector build.");
    }
    internal static CollectorBuild ForClient(DetectedClient client)
    {
        var build = client.ClientChannel switch { "Live" => Live, "Beta" => Beta, _ => null };
        if (build == Live && !client.UserAssigned && client.BuildSha256 != Live.BuildSha256)
        {
            var resolved = IdentityResolver.FileLayout(Live.ExecutablePath);
            if (client.BuildSha256 == resolved.Hash) build = ResolvedLive(resolved);
        }
        if (build is null || client.BuildSha256 != build.BuildSha256 || client.UserAssigned)
            throw new InvalidDataException("This client has no validated passive collector identity. Refresh the character list.");
        return build;
    }
    private static readonly object ResolvedLock = new();
    private static IdentityResolver.Resolved? resolvedImage;
    private static CollectorBuild? resolvedLive;
    private static CollectorBuild ResolvedLive(IdentityResolver.Resolved image)
    {
        lock (ResolvedLock)
        {
            if (ReferenceEquals(image, resolvedImage)) return resolvedLive!;
            var capabilities = CollectorCapability.Identity;
            try
            {
                var targets = TargetLayoutResolver.FileLayout(Live.ExecutablePath);
                if (ReferenceEquals(targets.Image, image))
                {
                    capabilities |= CollectorCapability.Target | CollectorCapability.TargetBuffs;
                    if (AdvancedLootMemoryLayout.SupportsHash(image.Hash)) capabilities |= CollectorCapability.AdvancedLoot;
                    if (image.Hash == AdvancedLootMemoryLayout.NovemberBuildHash)
                        capabilities |= CollectorCapability.Inspection | CollectorCapability.Merchants | CollectorCapability.Recipes;
                }
            }
            catch (InvalidDataException) { /* Unresolved surfaces stay disabled; identity can still be displayed. */ }
            resolvedLive = new("Live", Live.ExecutablePath, image.Hash, Live.identityLayout, capabilities) { UsesResolvedIdentity = true };
            resolvedImage = image;
            return resolvedLive;
        }
    }
    internal MemoryReader Open(int pid, long? expectedStartedUtcTicks, CollectorCapability capability)
    {
        Require(capability);
        var memory = new MemoryReader(pid, ExecutablePath, MemoryFeature.DataCollection);
        try
        {
            if (expectedStartedUtcTicks is not null && memory.StartedUtcTicks != expectedStartedUtcTicks)
                throw new InvalidDataException("The selected client restarted. Refresh the character list.");
            Identity.VerifyFile(memory.Path, BuildSha256);
            return memory;
        }
        catch { memory.Dispose(); throw; }
    }
}
