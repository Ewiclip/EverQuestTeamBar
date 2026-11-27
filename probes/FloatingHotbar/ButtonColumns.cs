namespace TeamBar;

internal sealed record ButtonPlacement(string ButtonId, int Column);
internal static class ButtonColumns
{
    internal const int MaxColumns = 6;
    internal static string[] Names(string[]? names, int columns) => Enumerable.Range(0, columns)
        .Select(i => names is not null && i < names.Length ? names[i] : $"Column {i + 1}").ToArray();
    internal static void ValidateNames(string[]? names, int columns)
    {
        if (names is not null && (names.Length != columns || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 40)))
            throw new InvalidDataException("Give each column a name of up to 40 characters.");
    }
    internal static ButtonPlacement[] RemoveEmpty(ButtonPlacement[] entries, int column) => entries.Any(e => e.Column == column)
        ? throw new InvalidOperationException("Move this column's buttons before removing it.")
        : entries.Select(e => e.Column > column ? e with { Column = e.Column - 1 } : e).ToArray();
    internal static ButtonPlacement[] Legacy(string[] ids, int columns) => ids.Select((id, i) => new ButtonPlacement(id, i % columns)).ToArray();
    internal static void Validate(ButtonPlacement[] entries, int columns)
    {
        if (columns is < 1 or > MaxColumns || entries is null || entries.Any(e => e is null || e.Column < 0 || e.Column >= columns
            || string.IsNullOrEmpty(e.ButtonId) || e.ButtonId.Length > 40 || !e.ButtonId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            || entries.Select(e => e.ButtonId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new InvalidDataException("Each button needs a unique reference and a valid column.");
    }
    internal static ButtonPlacement[] Resize(ButtonPlacement[] entries, int columns)
    {
        // Removed columns append to the final remaining column, without dropping buttons.
        return entries.Where(e => e.Column < columns)
            .Concat(entries.Where(e => e.Column >= columns).OrderBy(e => e.Column).Select(e => e with { Column = columns - 1 })).ToArray();
    }
    internal static ButtonPlacement[] Move(ButtonPlacement[] entries, string id, int column, string? before = null)
    {
        var moved = entries.Single(e => e.ButtonId == id) with { Column = column };
        var result = entries.Where(e => e.ButtonId != id).ToList();
        int index = before is null ? -1 : result.FindIndex(e => e.ButtonId == before && e.Column == column);
        if (index < 0) result.Add(moved); else result.Insert(index, moved);
        return result.ToArray();
    }
}
