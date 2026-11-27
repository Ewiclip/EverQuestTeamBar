using System.Globalization;
using System.Text.RegularExpressions;

// Descriptions are game text, not a reimplementation of spell combat formulas.
// Only direct, constant nonnegative #base/$limit placeholders are resolved.
// Scaled @values, duration/control directives and unknown syntax stay unresolved.
internal static class InstalledSpellDescriptions
{
    private static readonly object Sync = new();
    private static (string Path, long Length, DateTime Modified)? stamp;
    private static Dictionary<int, string> descriptions = [];
    internal static IReadOnlyDictionary<int, string> Read(SpellNames spells, string path)
    {
        lock (Sync)
        {
            var result = new Dictionary<int, string>();
            try
            {
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists || file.Length > 128 * 1024 * 1024) return result;
                var current = (file.FullName, file.Length, file.LastWriteTimeUtc);
                if (stamp != current)
                {
                    var loaded = new Dictionary<int, string>(); var duplicates = new HashSet<int>();
                    int count = 0, bytes = 0;
                    foreach (string line in File.ReadLines(file.FullName))
                    {
                        if (++count > 1_000_000 || line.Length > 65536) throw new InvalidDataException("Description file exceeds its bounds.");
                        var parts = line.Split('^');
                        if (!(parts.Length == 4 || parts.Length == 5 && parts[4] == "") || parts[1] != "6"
                            || !int.TryParse(parts[0], out int id) || id < 1 || parts[2].Length > 16000) continue;
                        if (loaded.TryGetValue(id, out var prior) && prior != parts[2]) duplicates.Add(id);
                        else if (!loaded.ContainsKey(id)) { loaded[id] = parts[2]; bytes += parts[2].Length * sizeof(char); }
                        if (bytes > 32 * 1024 * 1024) throw new InvalidDataException("Description cache exceeds its bounds.");
                    }
                    var after = new FileInfo(file.FullName);
                    if (after.Length != file.Length || after.LastWriteTimeUtc != file.LastWriteTimeUtc) throw new InvalidDataException("Description file changed.");
                    foreach (int id in duplicates) loaded.Remove(id);
                    descriptions = loaded; stamp = current;
                }
                foreach (var entry in spells.RawLines ?? new Dictionary<int, string>())
                {
                    var fields = entry.Value.Split('^');
                    if (fields.Length == 166 && int.TryParse(fields[85], out int id) && descriptions.TryGetValue(id, out var template)
                        && Render(template, entry.Value) is { } detail) result[entry.Key] = detail;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            { stamp = null; descriptions.Clear(); }
            return result;
        }
    }
    internal static string? Render(string template, string raw)
    {
        var spell = LocalSpellRecord.Parse(raw);
        if (spell is null || string.IsNullOrWhiteSpace(template) || template.Length > 16000
            || template.Any(c => char.IsControl(c) || c is '<' or '>' or '^' or '|' or '@' or '{' or '}')
            || Regex.IsMatch(template, @"%[A-Za-z0-9]")) return null;
        bool valid = true;
        string result = Regex.Replace(template, @"([#$])(\d{1,2})(?!\d)", match =>
        {
            int slot = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var effect = spell.Effects.SingleOrDefault(e => e.Slot == slot);
            int value = match.Groups[1].Value == "#" ? effect?.Base ?? -1 : effect?.Limit ?? -1;
            if (effect is null || effect.Formula != 100 || value < 0) { valid = false; return match.Value; }
            return value.ToString(CultureInfo.InvariantCulture);
        });
        return valid && !result.Contains('#') && !result.Contains('$') ? result.Trim() : null;
    }
}
