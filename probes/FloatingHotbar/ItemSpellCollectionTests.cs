using System.Text;
using System.Text.Json;
namespace TeamBar;

internal static class ItemSpellCollectionTests
{
    internal static void Run(string output)
    {
        var tests = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); tests.Add(name); }
        var slots = new byte[7 * 0x64];
        BitConverter.GetBytes(20525).CopyTo(slots, 0);
        BitConverter.GetBytes(32015).CopyTo(slots, 3 * 0x64);
        Encoding.ASCII.GetBytes("Radiant Resurgence").CopyTo(slots, 3 * 0x64 + 0x20);
        var refs = InspectionDecoder.DecodeEffects(slots);
        Check(refs.Length == 2 && refs[1] == new ItemSpellReference("Focus", 32015, "Radiant Resurgence"), "intrinsic slots preserve display alias and numeric spell identity");
        // A child augment pointer/name cannot enter a fixed-size definition array.
        Check(!refs.Any(r => r.Kind == "Augment"), "item reference projection never traverses augment containers");
        BitConverter.GetBytes(-2).CopyTo(slots, 0);
        bool rejected = false; try { InspectionDecoder.DecodeEffects(slots); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "invalid spell IDs fail closed");
        string[] f = Enumerable.Repeat("0", 166).ToArray(); f[0] = "1"; f[1] = "Fixture"; f[165] = "1|125|155|180|100|0$2|134|130|5|100|0";
        string raw = string.Join('^', f);
        Check(InstalledSpellDescriptions.Render("Heals #1 to $1%; above level #2.", raw) == "Heals 155 to 180%; above level 130.", "native constant base/limit description placeholders resolve");
        foreach (string template in new[] { "Scales @1", "For %z", "Unknown #32", "Bad #100", "Syntax #1|a", "<b>Markup</b>" })
            Check(InstalledSpellDescriptions.Render(template, raw) is null, "unresolved template omitted: " + template);
        Check(InstalledSpellDescriptions.Render("Value #1", raw.Replace("|100|", "|101|")) is null, "level-dependent formulas are not guessed");
        var source = new ContributionSource("Team Bar", "Live");
        var details = new CandidateDetails("", 1, 0, 0, 0, 0, 0, false, false, true, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        var item = new ItemData(123, "Fixture item", details);
        var plain = ContributionPayload.Items([item], source).Single();
        var enriched = ContributionPayload.Items([item], source, effects: new Dictionary<int, ItemSpellReference[]> { [123] = refs }).Single();
        Check(!plain.Payload.TryGetProperty("effects", out _) && plain.Fingerprint != enriched.Fingerprint, "old wire identity remains stable; new intrinsic references are independently reviewed");
        Check(enriched.Payload.GetProperty("effects")[1].GetProperty("name").GetString() == "Radiant Resurgence", "wire retains item alias separately from spell definition name");
        Check(ContributionPayload.GenericV1(enriched).Fingerprint == plain.Fingerprint, "generic receiver retains its existing v1 item payload and queue identity");
        var records = ContributionPayload.Spells([1], new SpellNames(new Dictionary<int, string>(), null, null, RawLines: new Dictionary<int, string> { [1] = raw }),
            source, descriptions: new Dictionary<int, string> { [1] = "Resolved fixture description" });
        Check(records[0].Payload.GetProperty("detail").GetString() == "Resolved fixture description", "approved detail and original raw definition travel together");
        CollectorFormat.WriteNew(output, new { passed = true, tests, gameInputSent = false, processMemoryRead = false });
    }
    internal static void ExportCapture(string capturePath, string output)
    {
        var capture = JsonSerializer.Deserialize<InspectionCapture>(File.ReadAllText(capturePath), CollectorFormat.Json) ?? throw new InvalidDataException("Missing capture");
        var records = ContributionPayload.InspectedItems(capture);
        Directory.CreateDirectory(output);
        int index = 0;
        foreach (var batch in ContributionPayload.Batches(records))
            File.WriteAllBytes(Path.Combine(output, $"batch-{++index}.json"), batch.Body);
        CollectorFormat.WriteNew(Path.Combine(output, "records.json"), records);
    }
}
