internal static class DataCollectionHistoryTests
{
    internal static string[] Run()
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, name);
        }
        var detail = new CandidateDetails("Fixture", 10, 1, 65535, 65535, 0, 0, true, false, true, false, false, 0, 1, 2, 3, 4, 0, 0, 0, 20);
        var at = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var inspection = new InspectionCapture(1, Guid.NewGuid(), at, "Beta", CollectorBuild.BetaBuildHash, "beta-inspection-fixture",
            "item-display", CollectorFormat.DetailStatus, "Fellwort", "Berserker", [new(0, new(42, "Fixture item", detail))], "Synthetic fixture");
        var history = new DataCollectionHistory();
        history.Accept(inspection);
        history.Accept(inspection with { ObservedAtUtc = at.AddSeconds(2) });
        var inspected = history.InspectionSnapshot("Fellwort");
        Check(inspected.ClientChannel == "Beta" && inspected.BuildSha256 == CollectorBuild.BetaBuildHash
            && inspected.Decoder == inspection.Decoder && inspected.Items.Length == 1 && inspected.Items[0].LastSeenUtc == at.AddSeconds(2),
            "inspection history merges repeated observations while preserving actual Beta provenance");
        var loot = new AdvancedLootCapture(1, Guid.NewGuid(), at, "Beta", CollectorBuild.BetaBuildHash, "beta-loot-fixture", "advanced-loot", "Fellwort",
            [new("shared", 42, "Fixture item", 10, 1, [new(2, "Fixture NPC", 3, "fixture")])], "Synthetic fixture");
        history.Accept(loot);
        history.Accept(loot with { ObservedAtUtc = at.AddSeconds(2), Items = [loot.Items[0] with { Sources = [loot.Items[0].Sources[0] with { StackCount = 1 }] }] });
        var drops = history.DropSnapshot("Fellwort");
        Check(drops.ClientChannel == "Beta" && drops.BuildSha256 == CollectorBuild.BetaBuildHash && drops.Decoder == loot.Decoder
            && drops.Drops.Length == 1 && drops.Drops[0].Quantity == 3,
            "drop history preserves source provenance and does not count repeated polls as new drops");
        Reject(() => history.Accept(inspection with { ClientChannel = "Live", BuildSha256 = Identity.BuildHash }),
            "same-name Live inspection cannot enter Beta history");
        Reject(() => history.Accept(loot with { ClientChannel = "Live", BuildSha256 = Identity.BuildHash }),
            "same-name Live loot cannot enter Beta history");
        Reject(() => history.Accept(inspection with { BuildSha256 = new string('a', 64) }), "a different Beta build requires new history");
        Reject(() => history.Accept(inspection with { Decoder = "different-decoder" }), "decoder revisions cannot be silently merged into one inspection export");
        Reject(() => history.Accept(loot with { CharacterName = "Other" }), "another character cannot enter retained history");
        Reject(() => history.InspectionSnapshot("Other"), "export cannot relabel the captured character");
        Reject(() => new DataCollectionHistory().InspectionSnapshot("Fellwort"), "empty history cannot invent Live provenance");
        Check(history.Inspections.Length == 1 && history.Drops.Length == 1
            && CollectorFormat.Hash(history.InspectionSnapshot("Fellwort")) == CollectorFormat.Hash(inspected),
            "rejected mixed sources leave existing observations unchanged");
        var liveHistory = new DataCollectionHistory();
        var liveInspection = inspection with { ClientChannel = "Live", BuildSha256 = Identity.BuildHash, Decoder = InspectionDecoder.Decoder };
        liveHistory.Accept(liveInspection);
        Check(liveHistory.InspectionSnapshot("Fellwort") is { ClientChannel: "Live", BuildSha256: Identity.BuildHash },
            "Live history continues to export its original source explicitly");
        var freeport = new ObservedZone(383, "freeportwest", "West Freeport");
        var rat = new LootSource(11226, "a large rat", 1, "direct-loot-entry-source-record");
        var secondRat = rat with { CorpseId = 13864 };
        var pawn = new LootSource(27515, "a Deathfist pawn", 1, "direct-loot-entry-source-record");
        var novemberLoot = new AdvancedLootCapture(1, Guid.NewGuid(), at, "Live", AdvancedLootMemoryLayout.NovemberBuildHash,
            AdvancedLootDecoder.Decoder, "advanced-loot", "Fixture", [new("shared", 13065, "Rat Foot", 743, 1, [rat]),
                new("shared", 97857, "Raw Stiff Hide", 555, 2, [rat]), new("shared", 13065, "Rat Foot", 743, 3, [secondRat]),
                new("shared", 34211, "Coarse Silk", 782, 4, [pawn])], "Synthetic regression from confirmed live layout",
            new(freeport, [new(rat.CorpseId, rat.Name, 1, freeport), new(secondRat.CorpseId, secondRat.Name, 1, freeport),
                new(pawn.CorpseId, pawn.Name, 2, freeport)]));
        var sourceHistory = new DataCollectionHistory(); sourceHistory.Accept(novemberLoot);
        sourceHistory.Accept(novemberLoot with { ObservedAtUtc = at.AddSeconds(2) });
        Check(sourceHistory.Drops.Length == 4 && sourceHistory.Drops.All(d => d.Zone == freeport && d.Quantity == 1)
            && sourceHistory.Drops.Count(d => d.ItemName == "Rat Foot") == 2,
            "confirmed November layout retains two distinct Rat Foot entries without inflating repeated polls");
        var sharingSource = new TeamBar.ContributionSource("Fixture", "Live", "1", novemberLoot.BuildSha256, novemberLoot.Decoder);
        var sharedDrops = TeamBar.ContributionSources.Drops(sourceHistory.Drops, sharingSource);
        Check(sharedDrops.Length == 3 && sharedDrops.Count(d => d.Payload.GetProperty("name").GetString() == "Rat Foot") == 1,
            "four observed loot entries become three distinct public item/NPC/zone associations");
        sourceHistory.Accept(novemberLoot with { ObservedAtUtc = at.AddSeconds(4), Context = new(freeport, []) });
        Check(sourceHistory.Drops.Length == 4 && sourceHistory.Drops.All(d => d.Zone == freeport && d.Level is not null),
            "a disappearing corpse does not erase already verified source context from the same loot entry");
        sourceHistory.Accept(novemberLoot with { ObservedAtUtc = at.AddSeconds(6), Items = [] });
        Check(sourceHistory.Drops.Length == 4, "looted entries remain visible in the current collection session");
        var unmatched = new DataCollectionHistory(); unmatched.Accept(novemberLoot with { Context = new(freeport, []) });
        Check(unmatched.Drops.Length == 4 && TeamBar.ContributionSources.Drops(unmatched.Drops, sharingSource).Length == 0,
            "unmatched drops remain inspectable but cannot be shared with an inferred zone");
        var effectsHistory = new DataCollectionHistory();
        var withEffects = inspection with { Items = [inspection.Items[0] with { Effects = [new("Focus", 32015, "Radiant Resurgence")] }] };
        effectsHistory.Accept(withEffects);
        effectsHistory.Accept(withEffects with { ObservedAtUtc = at.AddSeconds(1) });
        Check(effectsHistory.InspectionSnapshot("Fellwort").Items.Single().Effects!.Single().SpellId == 32015,
            "inspection history and export retain intrinsic spell identity and alias");
        return checks.ToArray();
    }
}
