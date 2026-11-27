using System.Security.Cryptography;

internal sealed record SpellNames(IReadOnlyDictionary<int, string> Names, string? Sha256, string? Error,
    IReadOnlyDictionary<int, LocalSpellRecord>? Records = null, IReadOnlyDictionary<int, string>? RawLines = null);
internal sealed record LocalSpellEffect(int Slot, int Attribute, int Base, int Limit, int Formula, int Maximum);
internal sealed record LocalSpellRecord(int Id, string Name, int Duration, int Target, int Icon, int Group, int Rank,
    bool PlayerSpell, LocalSpellEffect[] Effects)
{
    internal const int FieldCount = 166, IconColumn = 75;
    // Live's current 166-column format. Unknown layouts retain names, but never produce inferred relationships.
    internal static LocalSpellRecord? Parse(string line)
    {
        var f = line.Split('^');
        if (f.Length != FieldCount || f[1].Length is < 1 or > 255) return null;
        int[] columns = [0, 12, 30, IconColumn, 132, 133, .. Enumerable.Range(36, 16)];
        var values = new Dictionary<int, int>();
        foreach (int column in columns)
        {
            if (!int.TryParse(f[column], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value)) return null;
            values[column] = value;
        }
        if (values[0] is < 1 or > 1000000 || values[IconColumn] is < 0 or >= 9216 || values[132] < 0) return null;
        var effects = new List<LocalSpellEffect>();
        foreach (string part in f[165].Split('$', StringSplitOptions.RemoveEmptyEntries))
        {
            if (effects.Count >= 32) return null;
            var numbers = part.Split('|'); var parsed = new int[6];
            if (numbers.Length != 6) return null;
            for (int i = 0; i < 6; i++) if (!int.TryParse(numbers[i], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out parsed[i])) return null;
            if (parsed[0] is < 1 or > 32 || effects.Any(e => e.Slot == parsed[0])) return null;
            effects.Add(new(parsed[0], parsed[1], parsed[2], parsed[3], parsed[4], parsed[5]));
        }
        return new(values[0], f[1], values[12], values[30], values[IconColumn], values[132], values[133],
            Enumerable.Range(36, 16).Any(c => values[c] is > 0 and < 255), effects.ToArray());
    }
}
internal static class SpellNameCatalog
{
    private const string Path = @"C:\EverQuest-Live\spells_us.txt";
    private static readonly object Sync = new();
    private static (long Length, DateTime Modified)? stamp;
    private static SpellNames? cached;
    // Raw source is needed only for spells actually observed by the collector.
    // Keep this separate from the ordinary name/overlay cache and bound both
    // positive and missing lookups so each timer tick does not rescan the file.
    private const int ObservedLimit = 1000, RawCacheBytes = 8 * 1024 * 1024;
    private static (string Path, long Length, DateTime Modified)? observedStamp;
    private static string? observedHash;
    private static readonly Dictionary<int, string?> observedLines = [];
    private static readonly Queue<int> observedOrder = [];
    private static int observedBytes;

    internal static SpellNames ReadObserved(IEnumerable<int> observedIds, string? filePath = null)
    {
        lock (Sync)
        {
            try
            {
                int[] ids = observedIds.Where(id => id > 0).Distinct().Take(ObservedLimit + 1).ToArray();
                if (ids.Length > ObservedLimit) throw new InvalidDataException("Too many observed spells in one lookup.");
                if (ids.Length == 0) return new(new Dictionary<int, string>(), null, null, RawLines: new Dictionary<int, string>());
                string fullPath = System.IO.Path.GetFullPath(filePath ?? Path);
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length > 128 * 1024 * 1024) throw new InvalidDataException("Installed spell definitions are unavailable or too large.");
                var current = (fullPath, info.Length, info.LastWriteTimeUtc);
                if (observedStamp != current)
                {
                    observedStamp = null; observedHash = null;
                    observedLines.Clear(); observedOrder.Clear(); observedBytes = 0;
                }
                var missing = ids.Where(id => !observedLines.ContainsKey(id)).ToHashSet();
                var found = ids.Where(observedLines.ContainsKey).ToDictionary(id => id, id => observedLines[id]);
                if (missing.Count > 0)
                {
                    using var file = File.OpenRead(fullPath);
                    if (observedHash is null) { observedHash = Convert.ToHexStringLower(SHA256.HashData(file)); file.Position = 0; }
                    using var text = new StreamReader(file); int count = 0;
                    while (text.ReadLine() is { } line)
                    {
                        if (++count > 250000 || line.Length > 65536) throw new InvalidDataException("Installed spell data exceeds its bounds.");
                        int separator = line.IndexOf('^');
                        if (separator < 1 || !int.TryParse(line.AsSpan(0, separator), out int id) || !missing.Contains(id)) continue;
                        found[id] = line.Length <= 32000 && LocalSpellRecord.Parse(line) is not null ? line : null;
                    }
                    var after = new FileInfo(fullPath);
                    if (!after.Exists || after.Length != info.Length || after.LastWriteTimeUtc != info.LastWriteTimeUtc)
                        throw new InvalidDataException("Installed spell definitions changed during collection.");
                    observedStamp = current;
                    foreach (int id in missing)
                    {
                        string? line = found.GetValueOrDefault(id);
                        while (observedOrder.Count > 0 && (observedLines.Count >= ObservedLimit
                            || observedBytes + (line?.Length * sizeof(char) ?? 0) > RawCacheBytes))
                        {
                            int old = observedOrder.Dequeue();
                            observedBytes -= observedLines[old]?.Length * sizeof(char) ?? 0; observedLines.Remove(old);
                        }
                        observedLines[id] = line; observedOrder.Enqueue(id); observedBytes += line?.Length * sizeof(char) ?? 0;
                    }
                }
                var names = new Dictionary<int, string>();
                var records = new Dictionary<int, LocalSpellRecord>();
                var raw = new Dictionary<int, string>();
                foreach (int id in ids)
                {
                    // A very large lookup may evict its first lines from the
                    // cache. The current result still includes the full request.
                    string? line = found.TryGetValue(id, out var freshlyRead) ? freshlyRead : observedLines.GetValueOrDefault(id);
                    if (line is not null && LocalSpellRecord.Parse(line) is { } record)
                    { names[id] = record.Name; records[id] = record; raw[id] = line; }
                }
                return new(names, observedHash, null, records, raw);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                observedStamp = null; observedHash = null; observedLines.Clear(); observedOrder.Clear(); observedBytes = 0;
                return new(new Dictionary<int, string>(), null, "Installed spell definitions could not be read safely.");
            }
        }
    }
    internal static SpellNames Read()
    {
        lock (Sync)
        {
            try
            {
                var fileInfo = new FileInfo(Path);
                var current = fileInfo.Exists ? (fileInfo.Length, fileInfo.LastWriteTimeUtc) : (-1L, DateTime.MinValue);
                if (cached is not null && current == stamp) return cached;
                var names = new Dictionary<int, string>();
                var records = new Dictionary<int, LocalSpellRecord>();
                using var file = File.OpenRead(Path);
                if (file.Length > 128 * 1024 * 1024) throw new InvalidDataException("Spell-name file exceeds its bound.");
                string hash = Convert.ToHexStringLower(SHA256.HashData(file)); file.Position = 0;
                using var text = new StreamReader(file); int lines = 0;
                while (text.ReadLine() is { } line)
                {
                    if (++lines > 250000 || line.Length > 65536) throw new InvalidDataException("Spell-name data exceeds its bounds.");
                    int first = line.IndexOf('^'), second = first < 0 ? -1 : line.IndexOf('^', first + 1);
                    if (first > 0 && second > first + 1 && second - first <= 256 && int.TryParse(line.AsSpan(0, first), out int id))
                        names[id] = line[(first + 1)..second];
                    if (LocalSpellRecord.Parse(line) is { } record) records[record.Id] = record;
                }
                cached = new(names, hash, null, records); stamp = current;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { cached = new(new Dictionary<int, string>(), null, e.Message); stamp = null; }
            return cached;
        }
    }
}
