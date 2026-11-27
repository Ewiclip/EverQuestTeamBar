using System.Text.Json;
internal static class ItemCollectionTests
{
    internal static void Run(string output)
    {
        object decoderTests = CollectorTests.Run();
        var detail = new CandidateDetails("Example", 10, 1, 65535, 65535, 0, 0, true, false, true, false, false, 0, 1, 2, 3, 4, 0, 0, 0, 20);
        var item = new ItemData(123, "Example", detail);
        var before = new InventoryCapture(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Live", Identity.BuildHash,
            CollectorFormat.Decoder, "inventory", CollectorFormat.DetailStatus, "Test", "Cleric", [new("23/0", item)]);
        int comparisonTests = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Item comparison test failed."); comparisonTests++; }
        var moved = before with { Items = [new("24/0", item)] };
        Check(ItemCollection.Compare(before, before).Single().Change == "Unchanged");
        Check(ItemCollection.Compare(before, moved).Single().Change == "Moved");
        Check(ItemCollection.Compare(before, before with { Items = [] }).Single().Change == "Removed");
        Check(ItemCollection.Compare(before with { Items = [] }, before).Single().Change == "Added");
        Check(ItemCollection.Compare(before, before with { Items = [new("23/0", item), new("23/1", item)] }).Single().Change == "Locations changed");
        Check(ItemCollection.Compare(before, before with { Items = [new("23/0", item with { CandidateDetails = detail with { HitPoints = 20 } })] }).Single().Change == "Properties changed");
        bool rejected = false; try { ItemCollection.Compare(before, before with { CharacterName = "Other" }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && ItemCollection.Location("24/0") == "Bag 2 / 1");
        var beta = before with { ClientChannel = "Beta", BuildSha256 = CollectorBuild.BetaBuildHash, Decoder = "beta-inventory-fixture" };
        Check(ItemCollection.Compare(beta, beta).Single().Change == "Unchanged");
        foreach (var different in new[] { beta, before with { BuildSha256 = new string('a', 64) }, before with { Decoder = "different-decoder" } })
        {
            rejected = false;
            try { ItemCollection.Compare(before, different); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
        }
        var historyTests = DataCollectionHistoryTests.Run();
        var merchantHistoryTests = MerchantHistoryTests.Run();
        CollectorFormat.WriteNew(output, new { passed = true, decoderTests, comparisonTests, historyTests, merchantHistoryTests, gameInput = false });
    }
}
