namespace TeamBar;

internal sealed partial class EqIconCatalog
{
    internal static EqIconCatalog FromNames(IEnumerable<string> available, IEnumerable<(string Key, string Name)> names)
    {
        var items = new Dictionary<int, HashSet<string>>();
        var spells = new Dictionary<int, HashSet<string>>();
        foreach (var (key, name) in names)
        {
            var parts = key.Split(':');
            AddName(parts[1] == "item" ? items : spells, int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), name);
        }
        return new(available, Sorted(items), Sorted(spells));
    }
}

internal static class EqIconCatalogTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string dir = Path.GetFullPath(output) + ".catalog";
        if (Directory.Exists(dir)) throw new IOException("The icon fixture requires a new output path.");
        Directory.CreateDirectory(dir);
        string spellFile = Path.Combine(dir, "spells_us.txt");
        string Spell(int id, string name, int icon)
        {
            var fields = Enumerable.Repeat("0", LocalSpellRecord.FieldCount).ToArray();
            fields[0] = id.ToString(); fields[1] = name; fields[LocalSpellRecord.IconColumn] = icon.ToString();
            return string.Join('^', fields);
        }
        File.WriteAllLines(spellFile, [Spell(1, "Summon Flask", 584), Spell(2, "Bristling Shield", 45),
            Spell(3, "Flask", 45), Spell(4, "Bristling Flask", 9), Spell(5, "Bristling Shield", 45),
            Spell(6, "Incorrect format", 45) + "^changed"]);
        string[] available = ["eq:spell:45", "eq:item:584", "eq:spell:584", "eq:item:973", "eq:item:2877", "eq:spell:9", "eq:spell:8800"];
        var catalog = EqIconCatalog.Load(dir, available);
        check(catalog.Search("584").Count == 2 && catalog.Search("eq:item:584").Single().Kind == "item"
            && catalog.Search("#584", "spell").Single().Key == "eq:spell:584", "Numeric icon IDs distinguish item and spell atlases.");
        using var itemNames = typeof(EqIconCatalog).Assembly.GetManifestResourceStream("TeamBar.ItemIconNames.tsv.gz");
        if (itemNames is null)
        {
            check(catalog.Search("", "item").All(item => item.NameCount == 0 && item.Name == $"Item icon {item.Id}") &&
                catalog.Search("water flask", "item").Count == 0, "Items remain selectable by ID when this build omits item names.");
        }
        else
        {
            using var compressed = new System.IO.Compression.GZipStream(itemNames, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(compressed);
            var example = reader.ReadLine()?.Split('\t', 2) ?? throw new InvalidDataException("The bundled item-name fixture is empty.");
            string key = "eq:item:" + example[0];
            check(EqIconCatalog.Load(dir, [key]).Search(example[1], "item").Single().Key == key,
                "A name from the bundled item index resolves to its numeric icon.");
        }
        var sharedName = EqIconCatalog.FromNames(available, [("eq:item:973", "Fixture Orb"), ("eq:item:2877", "Fixture Orb")]);
        check(sharedName.Search("fixture orb").Select(m => m.Key).SequenceEqual(["eq:item:973", "eq:item:2877"]),
            "Different item images sharing the same name remain selectable.");
        var matches = catalog.Search("flask", "spell");
        check(matches.Count == 3 && matches[0].Name == "Flask" && matches[0].Key == "eq:spell:45"
            && matches[0].NameCount == 2 && catalog.Search("Bristling Shield").Single().Name == "Bristling Shield",
            "All aliases for a local spell image are searchable, with exact names first and duplicate names counted once.");
        check(catalog.Search("Incorrect format").Count == 0 && catalog.Search("").Count == available.Length
            && catalog.Search("8800").Single().NameCount == 0, "Unrecognized spell layouts are skipped and art without a name remains available by ID.");
        File.AppendAllText(spellFile, Environment.NewLine + Spell(7, "Fresh Spell", 8800));
        check(EqIconCatalog.Load(dir, available).Search("Fresh Spell").Single().Id == 8800, "Updated installed spell data refreshes the name index.");
        var withoutSpells = EqIconCatalog.Load(Path.Combine(dir, "missing"), available);
        check(withoutSpells.Search("Fresh Spell").Count == 0 && withoutSpells.Search("", "item").SequenceEqual(catalog.Search("", "item")),
            "A different or missing game folder retains the item catalog and drops the previous folder's spell aliases.");
    }
}
