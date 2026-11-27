using System.Globalization;
using System.Text;

internal sealed record GameViewLaunchPosition(int X, int ParkedY, int Width, int Height, int SafeY);

internal static class EqLaunchPosition
{
    private static readonly HashSet<string> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Defaults.WindowedModeXOffset", "Defaults.WindowedModeYOffset", "Defaults.Maximized",
        "VideoMode.WindowedWidth", "VideoMode.WindowedHeight", "VideoMode.Fullscreen"
    };

    internal static bool Repair(string iniPath, IReadOnlyList<GameViewLaunchPosition> signatures)
    {
        if (signatures.Count == 0) return true;
        const FileShare readOrReplace = FileShare.Read | FileShare.Delete;
        FileStream original;
        try { original = File.Open(iniPath, FileMode.Open, FileAccess.Read, readOrReplace); }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        using (original)
        {
            var bytes = new byte[checked((int)original.Length)];
            original.ReadExactly(bytes);
            if (!TryRepair(bytes, signatures, out var repaired)) return true;

            // Keep ordinary writers excluded from the read through the replacement.
            // If another application has replaced the file by name while staging,
            // compare that version under the same sharing rules before committing.
            var temp = iniPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool created = false;
            try
            {
                using (var staging = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    staging.Write(repaired);
                    staging.Flush(flushToDisk: true);
                }
                using var current = File.Open(iniPath, FileMode.Open, FileAccess.Read, readOrReplace);
                if (current.Length != bytes.Length) throw new IOException("EQ changed its launch settings during recovery.");
                var latest = new byte[bytes.Length];
                current.ReadExactly(latest);
                if (!latest.AsSpan().SequenceEqual(bytes)) throw new IOException("EQ changed its launch settings during recovery.");
                File.Replace(temp, iniPath, null);
                return true;
            }
            finally
            {
                // A storage error during replacement can leave only the staged
                // file. Retain that complete copy if the original name is gone.
                if (created && File.Exists(iniPath)) File.Delete(temp);
            }
        }
    }

    private static bool TryRepair(byte[] bytes, IReadOnlyList<GameViewLaunchPosition> signatures, out byte[] repaired)
    {
        repaired = [];
        // All relevant INI syntax is ASCII. A byte-for-byte Latin-1 view lets us
        // parse ANSI and UTF-8 files without guessing how unrelated text is encoded.
        Encoding encoding = Encoding.Latin1;
        int bom = 0;
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) bom = 3;
        else if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) { encoding = Encoding.Unicode; bom = 2; }
        else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) { encoding = Encoding.BigEndianUnicode; bom = 2; }
        var text = encoding.GetString(bytes, bom, bytes.Length - bom);
        if (text.Contains('\0')) return false;

        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, (int Number, int Start, int Length)>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.StartsWith("["))
            {
                section = line.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase) ? "Defaults" :
                    line.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase) ? "VideoMode" : null;
                if (section is not null && !sections.Add(section)) return false;
                continue;
            }
            if (section is null) continue;
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            string key = section + "." + line[..equals].Trim().ToString();
            if (!Settings.Contains(key)) continue;
            var value = line[(equals + 1)..].Trim();
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) ||
                !text.AsSpan().Overlaps(value, out int start) || !values.TryAdd(key, (number, start, value.Length)))
                return false;
        }
        if (values.Count != Settings.Count || values["Defaults.Maximized"].Number != 0 || values["VideoMode.Fullscreen"].Number != 0)
            return false;
        var y = values["Defaults.WindowedModeYOffset"];
        var safeValues = signatures.Where(p => p.X == values["Defaults.WindowedModeXOffset"].Number && p.ParkedY == y.Number &&
            p.Width == values["VideoMode.WindowedWidth"].Number && p.Height == values["VideoMode.WindowedHeight"].Number)
            .Select(p => p.SafeY).Distinct().Take(2).ToArray();
        if (safeValues.Length != 1 || safeValues[0] <= y.Number) return false;

        // The matched value consists exclusively of ASCII signs and digits. In
        // UTF-16 files each code unit before it occupies exactly two bytes.
        int unit = encoding.IsSingleByte ? 1 : 2;
        int offset = bom + y.Start * unit;
        int end = offset + y.Length * unit;
        var replacement = encoding.GetBytes(safeValues[0].ToString(CultureInfo.InvariantCulture));
        repaired = new byte[bytes.Length + replacement.Length - (end - offset)];
        bytes.AsSpan(0, offset).CopyTo(repaired);
        replacement.CopyTo(repaired, offset);
        bytes.AsSpan(end).CopyTo(repaired.AsSpan(offset + replacement.Length));
        return true;
    }
}
