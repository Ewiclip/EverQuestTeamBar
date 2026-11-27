internal sealed record MerchantObservation(string Key, ObservedZone Zone, string MerchantName, int MerchantLevel,
    int ItemId, string ItemName, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc)
{
    public string ZoneLabel => Zone.Name;
    public string Stock => "Unlimited";
}
internal sealed record MerchantHistorySnapshot(int SchemaVersion, string SourceKind, string ClientChannel,
    string BuildSha256, string Decoder, MerchantObservation[] Items, string Completeness);

internal sealed partial class DataCollectionHistory
{
    private readonly Dictionary<string, MerchantObservation> merchants = [];
    private string? merchantDecoder;
    internal bool HasMerchantSource => source is not null && merchantDecoder is not null;
    internal MerchantObservation[] Merchants => merchants.Values.OrderBy(m => m.ZoneLabel, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.MerchantName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ItemName, StringComparer.OrdinalIgnoreCase).ToArray();

    internal void Accept(MerchantCapture capture)
    {
        if (capture.SourceKind != "merchant-unlimited" || capture.ExcludedLimitedStock < 0
            || !capture.IsOpen && (capture.Items.Length != 0 || capture.Merchant is not null)
            || capture.IsOpen && capture.Merchant is null
            || capture.Items.Any(i => i.ItemId <= 0 || string.IsNullOrWhiteSpace(i.Name)))
            throw new InvalidDataException("Invalid merchant observation.");
        AcceptSource(capture.ClientChannel, capture.BuildSha256, capture.CharacterName, capture.Decoder, merchantDecoder);
        merchantDecoder = capture.Decoder;
        if (capture.Merchant is not { } merchant) return;
        foreach (var item in capture.Items)
        {
            // Public source identity, not the character, current target or a session-scoped spawn ID.
            string key = CollectorFormat.Hash(new { capture.Zone.Id, capture.Zone.ShortName, merchant.Name, item.ItemId });
            var next = new MerchantObservation(key, capture.Zone, merchant.Name, merchant.Level, item.ItemId, item.Name,
                capture.ObservedAtUtc, capture.ObservedAtUtc);
            if (merchants.TryGetValue(key, out var old)) next = next with { FirstSeenUtc = old.FirstSeenUtc };
            merchants[key] = next;
        }
    }

    internal MerchantHistorySnapshot MerchantSnapshot(string character)
    {
        var captured = SnapshotSource(character, merchantDecoder);
        return new(1, "merchant-unlimited-session", captured.Channel, captured.Build, merchantDecoder!, Merchants,
            MerchantDecoder.Completeness + " Observations persist in this viewer session; repeated reads merge by zone, merchant name and item ID. Character names and process identifiers are omitted. Local export only; merchant sharing is not enabled.");
    }
}
