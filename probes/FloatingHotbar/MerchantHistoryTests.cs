using System.Text.Json;

internal static class MerchantHistoryTests
{
    internal static string[] Run()
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        void Reject(Action action, string name)
        { try { action(); } catch (InvalidDataException) { checks.Add(name); return; } throw new InvalidOperationException(name); }
        var at = new DateTimeOffset(2026, 11, 9, 19, 0, 0, TimeSpan.Zero);
        var capture = new MerchantCapture(1, at, "Live", AdvancedLootMemoryLayout.NovemberBuildHash,
            MerchantDecoder.Decoder, "merchant-unlimited", "Private character", "Shaman", new(151, "bazaar", "The Bazaar"),
            true, new("Supply Merchant", 70), [new(13006, "Water Flask"), new(13005, "Iron Ration")], 55, MerchantDecoder.Completeness);
        var history = new DataCollectionHistory(); history.Accept(capture);
        history.Accept(capture with { ObservedAtUtc = at.AddSeconds(2) });
        Check(history.Merchants.Length == 2 && history.Merchants.All(m => m.FirstSeenUtc == at && m.LastSeenUtc == at.AddSeconds(2)),
            "repeated merchant polls merge without duplicating items");
        history.Accept(capture with { IsOpen = false, Merchant = null, Items = [], ExcludedLimitedStock = 0 });
        Check(history.Merchants.Length == 2, "closing merchant retains observed sources");
        history.Accept(capture with { Items = [] });
        Check(history.Merchants.Length == 2, "empty or filtered purchase lists cannot remove observed sources");
        history.Accept(capture with { Merchant = new("Another Merchant", 10) });
        history.Accept(capture with { Zone = new(383, "freeportwest", "West Freeport") });
        Check(history.Merchants.Length == 6, "same item from different merchants or zones stays separate");
        string json = JsonSerializer.Serialize(history.MerchantSnapshot(capture.CharacterName), CollectorFormat.Json);
        Check(!json.Contains("Private character") && !json.Contains("Shaman") && !json.Contains("processId") && !json.Contains("excludedLimitedStock")
            && json.Contains("Unlimited") && json.Contains(AdvancedLootMemoryLayout.NovemberBuildHash),
            "merchant export contains public source data and provenance without observer identity or finite-stock counts");
        Reject(() => history.Accept(capture with { CharacterName = "Other" }), "another character cannot mix into merchant history");
        Reject(() => history.Accept(capture with { ClientChannel = "Beta" }), "another channel cannot mix into merchant history");
        Reject(() => history.Accept(capture with { BuildSha256 = Identity.BuildHash }), "another build cannot mix into merchant history");
        Reject(() => history.Accept(capture with { Decoder = "another" }), "another decoder cannot mix into merchant history");
        Reject(() => history.Accept(capture with { IsOpen = false }), "closed capture with cached merchant items rejects");
        Reject(() => history.Accept(capture with { Merchant = null }), "open capture without merchant rejects");
        Reject(() => history.MerchantSnapshot("Other"), "merchant export requires the captured source");
        Check(json == JsonSerializer.Serialize(history.MerchantSnapshot(capture.CharacterName), CollectorFormat.Json), "rejected captures leave merchant history unchanged");
        Reject(() => new DataCollectionHistory().MerchantSnapshot(capture.CharacterName), "empty history cannot invent merchant provenance");
        return checks.ToArray();
    }
}
