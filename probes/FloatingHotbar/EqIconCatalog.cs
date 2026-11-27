using System.Globalization;
using System.IO.Compression;

namespace TeamBar;

internal sealed record EqIconSearchResult(string Key, string Kind, int Id, string Name, int NameCount);

internal sealed partial class EqIconCatalog
{
    private sealed record Entry(EqIconSearchResult Result, string[] Aliases);
    private readonly Entry[] entries;
    private static readonly Lazy<Dictionary<int, string[]>> ItemNames = new(ReadItemNames);
    private static readonly object SpellLock = new();
    private static (string Path, long Size, DateTime Modified) cachedStamp;
    private static Dictionary<int, string[]> cachedSpells = [];

    private EqIconCatalog(IEnumerable<string> available, Dictionary<int, string[]> items, Dictionary<int, string[]> spells)
    {
        var listed = new List<Entry>();
        foreach (string key in available.Distinct(StringComparer.Ordinal))
        {
            var parts = key.Split(':');
            if (parts.Length != 3 || parts[0] != "eq" || parts[1] is not ("item" or "spell")
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int id)) continue;
            string kind = parts[1];
            string[] aliases = (kind == "item" ? items : spells).GetValueOrDefault(id) ?? [];
            string label = aliases.FirstOrDefault() ?? $"{(kind == "item" ? "Item" : "Spell")} icon {id}";
            listed.Add(new(new(key, kind, id, label, aliases.Length), aliases));
        }
        entries = listed.OrderBy(e => e.Result.Kind == "item").ThenBy(e => e.Result.Id).ToArray();
    }

    internal static EqIconCatalog Load(string gameDirectory, IEnumerable<string> available) =>
        new(available, ItemNames.Value, SpellNames(gameDirectory));

    internal IReadOnlyList<EqIconSearchResult> Search(string query, string? kind = null)
    {
        string term = query.Trim();
        IEnumerable<Entry> scope = entries;
        if (kind is not null) scope = scope.Where(e => e.Result.Kind == kind);
        if (term.Length == 0) return scope.Select(e => e.Result).ToArray();
        foreach (string category in new[] { "item", "spell" })
        {
            string prefix = category + ":";
            if (term.StartsWith("eq:" + prefix, StringComparison.OrdinalIgnoreCase)) term = term[3..];
            if (!term.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            scope = scope.Where(e => e.Result.Kind == category);
            term = term[prefix.Length..].Trim();
            break;
        }
        if (int.TryParse(term.AsSpan().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            return scope.Where(e => e.Result.Id == number).Select(e => e.Result).ToArray();
        if (term.Length == 0) return scope.Select(e => e.Result).ToArray();

        var matches = new List<(int Rank, EqIconSearchResult Result)>();
        foreach (var entry in scope)
        {
            int best = int.MaxValue;
            string? matched = null;
            foreach (string alias in entry.Aliases)
            {
                int index = alias.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                int rank = index == 0 ? (alias.Length == term.Length ? 0 : 1)
                    : char.IsLetterOrDigit(alias[index - 1]) ? 3 : 2;
                if (rank >= best) continue;
                best = rank;
                matched = alias;
                if (best == 0) break;
            }
            if (matched is not null) matches.Add((best, entry.Result with { Name = matched }));
        }
        return matches.OrderBy(m => m.Rank).ThenBy(m => m.Result.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Result.Kind, StringComparer.Ordinal).ThenBy(m => m.Result.Id).Select(m => m.Result).ToArray();
    }

    private static void AddName(Dictionary<int, HashSet<string>> index, int id, string name)
    {
        if (!index.TryGetValue(id, out var names)) index[id] = names = new(StringComparer.OrdinalIgnoreCase);
        names.Add(name);
    }

    private static Dictionary<int, string[]> Sorted(Dictionary<int, HashSet<string>> index) =>
        index.ToDictionary(p => p.Key, p => p.Value.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());

    private static Dictionary<int, string[]> ReadItemNames()
    {
        using var file = typeof(EqIconCatalog).Assembly.GetManifestResourceStream("TeamBar.ItemIconNames.tsv.gz");
        if (file is null) return [];
        var index = new Dictionary<int, HashSet<string>>();
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(compressed);
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab < 1 || !int.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                throw new InvalidDataException("Invalid item icon index.");
            AddName(index, id, line[(tab + 1)..]);
        }
        return Sorted(index);
    }

    private static Dictionary<int, string[]> SpellNames(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return [];
        lock (SpellLock)
        {
            try
            {
                var file = new FileInfo(Path.Combine(gameDirectory, "spells_us.txt"));
                if (!file.Exists || file.Length > 128 * 1024 * 1024) return [];
                var stamp = (file.FullName, file.Length, file.LastWriteTimeUtc);
                if (stamp == cachedStamp) return cachedSpells;
                var index = new Dictionary<int, HashSet<string>>();
                using var text = file.OpenText();
                Span<Range> fields = stackalloc Range[LocalSpellRecord.FieldCount + 1];
                int count = 0;
                while (text.ReadLine() is { } line)
                {
                    if (++count > 250000 || line.Length > 65536) return [];
                    var row = line.AsSpan();
                    if (row.Split(fields, '^') != LocalSpellRecord.FieldCount) continue;
                    var name = row[fields[1]];
                    if (name.Length is < 1 or > 255
                        || !int.TryParse(row[fields[0]], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0
                        || !int.TryParse(row[fields[LocalSpellRecord.IconColumn]], NumberStyles.None, CultureInfo.InvariantCulture, out int icon)) continue;
                    AddName(index, icon, name.ToString());
                }
                file.Refresh();
                if (!file.Exists || stamp != (file.FullName, file.Length, file.LastWriteTimeUtc)) return [];
                cachedSpells = Sorted(index);
                cachedStamp = stamp;
                return cachedSpells;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return [];
            }
        }
    }
}
