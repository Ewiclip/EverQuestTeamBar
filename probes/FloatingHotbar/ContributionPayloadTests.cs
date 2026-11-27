using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TeamBar;

internal static class ContributionPayloadTests
{
    internal static void Run(string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        string fixtureDirectory = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        var tests = new List<string>();
        var fixtures = new List<object>();
        var rejections = new List<ContributionRejection>();
        var source = new ContributionSource("TeamBar collector fixture", "Live", "1.0", new string('a', 64), "live-inventory-research-1");
        var details = new CandidateDetails("  Lore & <catalog>  ", int.MinValue, uint.MaxValue, uint.MaxValue, uint.MaxValue,
            int.MinValue, int.MaxValue, true, false, false, true, false, int.MinValue, int.MinValue, int.MaxValue,
            0, -1, int.MaxValue, 0, int.MinValue, 0);
        var item = new ItemData(42, "  Fixture Élan 雪 🛡  ", details);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void ExpectFailure(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Malformed contribution was not rejected.");
        }
        void Save(IEnumerable<ContributionRecord> records)
        {
            foreach (var batch in ContributionPayload.Batches(records))
            {
                Check(batch.Body.Length <= ContributionPayload.MaxBodyBytes && batch.Records.Length is > 0 and <= 100, "Batch bound failed.");
                Check(batch.Records.Select(record => (record.Kind, record.EntityId)).Distinct().Count() == batch.Records.Length, "Batch repeated an entity ID.");
                string name = $"batch-{fixtures.Count + 1:000}.json";
                File.WriteAllBytes(Path.Combine(fixtureDirectory, name), batch.Body);
                fixtures.Add(new { file = name, bytes = batch.Body.Length,
                    source = batch.Records[0].Source,
                    records = batch.Records.Select(record => new { record.Kind, record.EntityId, record.Fingerprint }).ToArray() });
            }
        }

        var inventory = new InventoryCapture(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Live", new string('a', 64),
            "live-inventory-research-1", "inventory", CollectorFormat.DetailStatus,
            "PRIVATE_CHARACTER_SENTINEL", "PRIVATE_CLASS_SENTINEL", [new("PRIVATE_SLOT_SENTINEL", item), new("PRIVATE_OTHER_SLOT", item)]);
        var inspection = new InspectionCapture(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Live", new string('a', 64),
            "live-item-display-1", "item-display", CollectorFormat.DetailStatus,
            "PRIVATE_INSPECTION_CHARACTER", "PRIVATE_INSPECTION_CLASS", [new(999, item)], "PRIVATE_COMPLETENESS_SENTINEL");
        var inventoryRecords = ContributionPayload.Items(inventory.Items.Select(entry => entry.Item), source, rejections.Add);
        var inspectionRecords = ContributionPayload.Items(inspection.Items.Select(entry => entry.Item), source with { Decoder = inspection.Decoder });
        Check(inventoryRecords.Length == 1 && inventoryRecords[0].Fingerprint == inspectionRecords.Single().Fingerprint, "Source/context affected content deduplication.");
        Check(inventoryRecords[0].Payload.GetProperty("name").GetString() == "Fixture Élan 雪 🛡", "Name normalization failed.");
        var variant = ContributionPayload.Items([item with { CandidateDetails = details with { Mana = 123 } }], source).Single();
        Check(variant.Fingerprint != inventoryRecords[0].Fingerprint, "Changed item did not produce a new fingerprint.");
        Check(ContributionPayload.Batches([inventoryRecords[0], variant]).Length == 2, "Changed variants of one ID were not split.");
        tests.Add("explicit inventory/inspection projection; private context absent; normalized source-independent fingerprints; changed variants preserved in separate batches");

        string validRaw = SpellLine(42, "Fixture spell", "", "1|0|-2147483648|2147483647|100|0");
        var spellSource = source with { Decoder = "installed-spells-us-166-1" };
        var spellNames = new SpellNames(new Dictionary<int, string> { [42] = "Fixture spell" }, new string('b', 64), null,
            RawLines: new Dictionary<int, string> { [42] = validRaw });
        var spells = ContributionPayload.Spells([42, 42], spellNames, spellSource, rejections.Add);
        Check(spells.Length == 1 && spells[0].Payload.GetProperty("rawLine").GetString() == validRaw, "Original spell definition changed.");
        Check(!spells[0].Payload.TryGetProperty("detail", out _), "Exporter manufactured spell detail.");
        Save([inventoryRecords[0], variant, inspectionRecords[0], spells[0]]);
        Save([inventoryRecords[0], spells[0] with { Source = source }]);
        tests.Add("observed spell-only export; original 166-field source retained; shared item/spell ID allowed; source-homogeneous batches");

        var invalidItems = new[] { item with { ItemId = 0 }, item with { Name = "private\nnewline" }, item with { Name = "\ud800" }, item with { Name = new string('a', 256) } };
        Check(ContributionPayload.Items(invalidItems, source, rejections.Add).Length == 0, "Invalid item text or identity escaped validation.");
        ExpectFailure(() => ContributionPayload.Items([item], source with { ClientChannel = "Test" }));
        ExpectFailure(() => ContributionPayload.Items([item], source with { BuildSha256 = "bad" }));
        var invalidSpellLines = new Dictionary<int, string>
        {
            [44] = "44^Unsupported layout", [45] = SpellLine(45, new string('a', 129)),
            [46] = SpellLine(46, "Control\tname"), [47] = SpellLine(47, "Oversized", new string('a', 32000)),
            [48] = "+" + SpellLine(48, "Invalid ID syntax"), [49] = SpellLine(49, "Duplicate effect slot", "", "1|0|0|0|100|0$1|0|0|0|100|0"),
        };
        Check(ContributionPayload.Spells(invalidSpellLines.Keys, spellNames with { RawLines = invalidSpellLines }, spellSource, rejections.Add).Length == 0,
            "Unsupported spell source escaped validation.");
        Check(rejections.Count == invalidItems.Length + invalidSpellLines.Count, "Rejected records were not inspectable.");
        tests.Add("invalid IDs, source, control text, unpaired Unicode, oversized names/raw records and malformed effects are skipped with reasons");

        var manyItems = Enumerable.Range(1000, 101).Select(id => item with { ItemId = id }).ToArray();
        var itemBatches = ContributionPayload.Batches(ContributionPayload.Items(manyItems, source));
        Check(itemBatches.Length == 2 && itemBatches[0].Records.Length == 100 && itemBatches[1].Records.Length == 1, "100-record boundary failed.");
        Save(ContributionPayload.Items(manyItems, source));
        int visited = 0;
        IEnumerable<ContributionRecord> Visit()
        {
            foreach (var record in ContributionPayload.Items(Enumerable.Range(2000, 250).Select(id => item with { ItemId = id }), source))
            { visited++; yield return record; }
        }
        Check(ContributionPayload.NextBatch(Visit())?.Records.Length == 100 && visited == 101, "NextBatch eagerly built the whole queue.");
        var longRaw = Enumerable.Range(10000, 28).ToDictionary(id => id, id => SpellLine(id, "Large fixture spell", new string('雪', 30000)));
        var largeRecords = ContributionPayload.Spells(longRaw.Keys, spellNames with { RawLines = longRaw }, spellSource);
        var byteBatches = ContributionPayload.Batches(largeRecords);
        Check(largeRecords.Length == 28 && byteBatches.Length > 1 && byteBatches.Sum(batch => batch.Records.Length) == 28,
            "Actual UTF-8 byte accounting dropped or oversized a record.");
        Save(largeRecords);
        tests.Add("100-record and exact UTF-8 2-MiB limits; Unicode byte accounting; efficient first-batch enumeration");

        string spellPath = Path.Combine(fixtureDirectory, "installed-spells-fixture.txt");
        File.WriteAllText(spellPath, validRaw + "\n" + SpellLine(43, "Unobserved spell") + "\n", new UTF8Encoding(false));
        var observed = ContributionPayload.ObservedSpells([42, 999], spellSource, spellPath: spellPath);
        Check(observed.Length == 1 && observed[0].EntityId == 42, "Installed lookup leaked unobserved spell definitions.");
        var cached = ContributionPayload.ObservedSpells([42], spellSource, spellPath: spellPath);
        Check(cached.Single().Fingerprint == observed.Single().Fingerprint, "Repeated installed lookup changed content.");
        File.WriteAllText(spellPath, SpellLine(42, "Fixture updated spell") + "\n", new UTF8Encoding(false));
        var changed = ContributionPayload.ObservedSpells([42], spellSource, spellPath: spellPath);
        Check(changed.Single().Fingerprint != observed.Single().Fingerprint, "Updated installed spell source stayed stale.");
        int[] cacheIds = Enumerable.Range(100000, 150).ToArray();
        File.WriteAllText(spellPath, string.Join('\n', cacheIds.Select(id => SpellLine(id, "Bounded cache fixture", new string('a', 31000)))), new UTF8Encoding(false));
        Check(SpellNameCatalog.ReadObserved([cacheIds[0]], spellPath).RawLines?.Count == 1, "Initial cached lookup failed.");
        var afterEviction = SpellNameCatalog.ReadObserved(cacheIds, spellPath);
        Check(afterEviction.RawLines?.Count == cacheIds.Length && afterEviction.RawLines.ContainsKey(cacheIds[0]),
            "Cache eviction dropped a requested definition from the current lookup.");
        Check(SpellNameCatalog.ReadObserved(Enumerable.Range(1, 1001), spellPath).Error is not null,
            "Oversized observed-ID lookup was not bounded.");
        Check(ContributionPayload.ObservedSpells([42], spellSource, spellPath: spellPath + ".missing").Length == 0,
            "Unavailable definitions did not fail closed.");
        tests.Add("injected installed-file lookup; only observed IDs returned; requested records survive cache eviction; lookup count bounded; file changes refreshed; missing file fails closed");

        var summaryRecipe = new LoadedRecipe(700001, "Fixture Élan recipe", 325, 19, 66626, 2, false,
            Enumerable.Range(1, 10).Select(slot => new RecipeSlot(slot, 0, 0, null)).ToArray());
        var detailRecipe = summaryRecipe with { DetailsLoaded = true, IngredientSlots =
            [new(3, 42, 7, "Fixture ingredient"), new(1, 42, 7, "Fixture ingredient"), new(10, 900002, 0, null)] };
        var recipeSummary = ContributionPayload.Recipes([summaryRecipe], source).Single();
        var recipeDetail = ContributionPayload.Recipes([detailRecipe], source).Single();
        Check(recipeSummary.Payload.GetProperty("ingredientSlots").GetArrayLength() == 0, "Summary fabricated ingredients.");
        Check(recipeDetail.Payload.GetProperty("ingredientSlots").EnumerateArray().Select(slot => slot.GetProperty("slot").GetInt32())
            .SequenceEqual([1, 3, 10]), "Ingredient slots were merged, invented, or not normalized.");
        Check(recipeDetail.Fingerprint != recipeSummary.Fingerprint, "Loaded ingredient details were deduplicated as a summary.");
        Check(!recipeDetail.Payload.TryGetProperty("resultItemId", out _) && !recipeDetail.Payload.TryGetProperty("yield", out _),
            "Recipe observations fabricated a result or yield.");
        Check(ContributionPayload.Batches([recipeSummary, recipeDetail]).Length == 2, "Recipe versions must be separate batches.");
        Save([recipeSummary, recipeDetail]);
        Save([inventoryRecords[0], spells[0] with { Source = source }, recipeDetail]);
        var badRecipes = new[]
        {
            summaryRecipe with { RecipeId = 0 }, summaryRecipe with { Trivial = 2001 }, summaryRecipe with { Name = "Bad\nname" },
            detailRecipe with { IngredientSlots = [new(1, 42, 7, "Valid"), new(1, 43, 7, "Duplicate slot")] },
            detailRecipe with { DetailsLoaded = false }, detailRecipe with { IngredientSlots = [new(11, 42, 0, null)] },
            detailRecipe with { IngredientSlots = [new(1, -1, 0, null)] }, detailRecipe with { IngredientSlots = [new(1, 42, -1, null)] },
            detailRecipe with { IngredientSlots = [new(1, 42, 0, new string('x', 256))] }, summaryRecipe with { RequiredSize = 101 },
            summaryRecipe with { ContainerType = 1001 }, summaryRecipe with { ContainerItemId = -1 },
        };
        Check(ContributionPayload.Recipes(badRecipes, source, rejections.Add).Length == 0, "Invalid recipe observation escaped validation.");
        Save(ContributionPayload.Recipes([summaryRecipe with { RecipeId = 700002, DetailsLoaded = true, IngredientSlots = [] }], source));
        tests.Add("partial recipe projection; stable slot ordering; repeated ingredients preserved; summaries and details distinct; no invented output/quantity; invalid recipe bounds rejected");

        var liveRecipeSummaryCapture = LiveRecipeDecoderTests.FixtureCapture(false);
        var liveRecipeDetailCapture = LiveRecipeDecoderTests.FixtureCapture(true);
        var currentRecipeSource = source with { ClientChannel = liveRecipeDetailCapture.ClientChannel,
            BuildSha256 = liveRecipeDetailCapture.BuildSha256, Decoder = liveRecipeDetailCapture.Decoder };
        var currentSummaries = ContributionPayload.Recipes(liveRecipeSummaryCapture.Recipes, currentRecipeSource);
        var currentDetails = ContributionPayload.Recipes(liveRecipeDetailCapture.Recipes, currentRecipeSource);
        Check(currentSummaries.Length == 2 && currentDetails.Length == 2
            && currentSummaries.All(r => r.Payload.GetProperty("ingredientSlots").GetArrayLength() == 0)
            && currentDetails.Single(r => r.EntityId == 10261).Payload.GetProperty("ingredientSlots").GetArrayLength() == 2,
            "Current Live decoded summaries and selected ingredients did not reach the sharing projection.");
        Check(currentDetails.All(r => r.Source.BuildSha256 == AdvancedLootMemoryLayout.NovemberBuildHash && r.Source.Decoder == LiveRecipeDecoder.Decoder),
            "Current recipe sharing lost its validated source provenance.");
        var recipeQueue = new ContributionQueue(Path.Combine(fixtureDirectory, "current-live-recipes"), "https://example.test/api/contributions/v1/catalog", "fixture");
        recipeQueue.Enqueue(currentSummaries); recipeQueue.Enqueue(currentSummaries); recipeQueue.Enqueue(currentDetails);
        Check(recipeQueue.Count == 3, "Recipe polls duplicated unchanged summaries or lost newly loaded ingredients.");
        Save(currentSummaries); Save(currentDetails);
        tests.Add("current Live recipe decoder feeds the existing wire schema and queue; unchanged summaries deduplicate while loaded ingredients remain a new observation");

        var betaSource = source with { ClientChannel = "Beta", BuildSha256 = new string('c', 64), Decoder = "synthetic-beta-fixture" };
        ExpectFailure(() => ContributionPayload.ObservedSpells([42], betaSource));
        ExpectFailure(() => ContributionPayload.ObservedSpells([42], betaSource, spellPath: " "));
        string betaSpellPath = Path.Combine(fixtureDirectory, "beta-spells-fixture.txt");
        File.WriteAllText(betaSpellPath, SpellLine(42, "Beta-specific fixture spell") + "\n", new UTF8Encoding(false));
        var betaSpells = ContributionPayload.ObservedSpells([42], betaSource, spellPath: betaSpellPath);
        Check(betaSpells.Single().Source.ClientChannel == "Beta" && betaSpells.Single().Payload.GetProperty("name").GetString() == "Beta-specific fixture spell",
            "Explicit Beta spell file did not preserve its definitions and source.");
        tests.Add("Beta observed-spell lookup requires an explicit installation file and never defaults to Live definitions");
        var betaItem = inventoryRecords[0] with { Source = betaSource };
        var nextBetaItem = betaItem with { Source = betaSource with { BuildSha256 = new string('d', 64) } };
        Check(ContributionPayload.Batches([inventoryRecords[0], betaItem, nextBetaItem, betaItem]).Length == 3,
            "Live/Beta or separate Beta builds collapsed into one observation.");
        Check(ContributionPayload.SourceScope(source) == "", "Legacy Live queue scope changed.");
        ExpectFailure(() => ContributionPayload.Items([item], betaSource with { BuildSha256 = null }));
        ExpectFailure(() => ContributionPayload.Items([item], betaSource with { Decoder = " " }));
        Save([betaItem, nextBetaItem]);
        Save([spells[0] with { Source = betaSource }, recipeDetail with { Source = betaSource }]);
        tests.Add("explicit Beta provenance; missing build/decoder rejected; Live and Beta builds independently batched without changing content fingerprints");

        Save(ContributionSourcesTests.Run(fixtureDirectory));
        tests.Add("NPC classification, matched-corpse drops, log success/zone attribution, privacy projection, deterministic source deduplication, upgrade opt-in and log-tail boundaries");
        string[] privateSentinels = ["PRIVATE_CHARACTER_SENTINEL", "PRIVATE_CLASS_SENTINEL", "PRIVATE_SLOT_SENTINEL", "PRIVATE_OTHER_SLOT",
            "PRIVATE_INSPECTION_CHARACTER", "PRIVATE_INSPECTION_CLASS", "PRIVATE_COMPLETENESS_SENTINEL"];
        foreach (string file in Directory.GetFiles(fixtureDirectory, "batch-*.json"))
        {
            string body = File.ReadAllText(file);
            Check(privateSentinels.All(value => !body.Contains(value, StringComparison.Ordinal)), "A personal capture field entered the upload.");
            using var document = JsonDocument.Parse(body);
            string[] allowed = ["schemaVersion", "source", .. ContributionKinds.All.Select(ContributionKinds.ArrayName)];
            Check(document.RootElement.EnumerateObject().All(property => allowed.Contains(property.Name)), "Unexpected envelope fields.");
        }
        File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(new { passed = true, tests, fixtures,
            fixtureDirectory, privateSentinels, rejectedRecords = rejections.Count, gameplayInput = false, networkRequests = false },
            new JsonSerializerOptions(ContributionPayload.Json) { WriteIndented = true }));
    }

    private static string SpellLine(int id, string name, string extra = "", string effects = "1|0|25|0|100|0")
    {
        var fields = Enumerable.Repeat("0", 166).ToArray();
        fields[0] = id.ToString(CultureInfo.InvariantCulture); fields[1] = name; fields[2] = extra;
        fields[12] = "2"; fields[30] = "5"; fields[36] = "1"; fields[75] = "0"; fields[132] = "1"; fields[133] = "1"; fields[165] = effects;
        return string.Join('^', fields);
    }
}
