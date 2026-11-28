using System.Text.Json.Serialization;

namespace TeamBar;

internal sealed record ButtonPlacement(string ButtonId, int Column,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Row = null);
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
        if (entries.Any(e => e.Row.HasValue)) _ = PositionRows(entries);
    }
    internal static ButtonPlacement[] PositionRows(ButtonPlacement[] entries)
    {
        var next = new int[MaxColumns];
        return entries.Select(entry =>
        {
            int row = entry.Row ?? next[entry.Column];
            // Leave room for tall layouts without allowing a corrupt coordinate to allocate millions of UI rows.
            if (row < next[entry.Column] || row == int.MaxValue || row > entries.LongLength + 4096)
                throw new InvalidDataException("Button rows in each column must be distinct, ordered, and within the layout.");
            next[entry.Column] = row + 1;
            return entry with { Row = row };
        }).ToArray();
    }

    internal static ButtonPlacement[] Remove(ButtonPlacement[] entries, string id, bool keepRows = false)
    {
        bool positioned = entries.Any(e => e.Row.HasValue);
        if (!positioned && keepRows)
        {
            var removed = entries.SingleOrDefault(e => e.ButtonId == id);
            // Removing the last entry of a dense column doesn't create a gap.
            positioned = removed is not null && entries.Last(e => e.Column == removed.Column) != removed;
        }
        return (positioned ? PositionRows(entries) : entries).Where(e => e.ButtonId != id).ToArray();
    }

    internal static ButtonPlacement[] Resize(ButtonPlacement[] entries, int columns)
    {
        // Removed columns append to the final remaining column, without dropping buttons.
        if (entries.Any(e => e.Row.HasValue))
        {
            var positioned = PositionRows(entries);
            var kept = positioned.Where(e => e.Column < columns).ToList();
            int nextRow = kept.LastOrDefault(e => e.Column == columns - 1)?.Row + 1 ?? 0;
            foreach (var moved in positioned.Where(e => e.Column >= columns).OrderBy(e => e.Column))
                kept.Add(moved with { Column = columns - 1, Row = nextRow++ });
            return kept.ToArray();
        }
        return entries.Where(e => e.Column < columns)
            .Concat(entries.Where(e => e.Column >= columns).OrderBy(e => e.Column).Select(e => e with { Column = columns - 1 })).ToArray();
    }
    internal static ButtonPlacement[] Move(ButtonPlacement[] entries, string id, int column, string? before = null, int? row = null,
        bool keepSourceRows = false)
    {
        var moved = entries.Single(e => e.ButtonId == id);
        if (before == id && column == moved.Column && !row.HasValue) return entries;
        if (row is null && column == moved.Column && entries.Any(e => e.Row.HasValue))
        {
            // A reorder within a sparse column rearranges buttons among its existing slots.
            var positioned = PositionRows(entries);
            var slots = new Queue<int?>(positioned.Where(e => e.Column == column).Select(e => e.Row));
            var reordered = InsertOrdered(positioned.Where(e => e.ButtonId != id), moved, before);
            return reordered.Select(e => e.Column == column ? e with { Row = slots.Dequeue() } : e).ToArray();
        }
        return Insert(Remove(entries, id, keepRows: row.HasValue || (keepSourceRows && column != moved.Column)),
            moved with { Column = column, Row = row }, before);
    }
    internal static ButtonPlacement[] Insert(IEnumerable<ButtonPlacement> entries, ButtonPlacement placement, string? before)
    {
        var existing = entries as ButtonPlacement[] ?? entries.ToArray();
        if (placement.Row is null && !existing.Any(e => e.Row.HasValue)) return InsertOrdered(existing, placement, before);

        var positioned = PositionRows(existing);
        var column = positioned.Where(e => e.Column == placement.Column).ToArray();
        int row;
        if (placement.Row is int cell)
        {
            if (before is not null || cell < 0 || column.Any(e => e.Row == cell))
                throw new InvalidDataException("Choose an empty button slot.");
            row = cell;
            before = column.FirstOrDefault(e => e.Row > cell)?.ButtonId;
        }
        else if (column.FirstOrDefault(e => e.ButtonId == before) is { Row: int anchor })
        {
            var occupied = column.Select(e => e.Row!.Value).ToHashSet();
            if (anchor > 0 && !occupied.Contains(anchor - 1)) row = anchor - 1;
            else
            {
                row = anchor;
                int gap = anchor;
                while (occupied.Contains(gap)) gap++;
                positioned = positioned.Select(e => e.Column == placement.Column && e.Row >= anchor && e.Row < gap
                    ? e with { Row = e.Row + 1 } : e).ToArray();
            }
        }
        else row = column.LastOrDefault()?.Row + 1 ?? 0;
        return InsertOrdered(positioned, placement with { Row = row }, before);
    }

    private static ButtonPlacement[] InsertOrdered(IEnumerable<ButtonPlacement> entries, ButtonPlacement placement, string? before)
    {
        var result = entries.ToList();
        int index = before is null ? -1 : result.FindIndex(e => e.ButtonId == before && e.Column == placement.Column);
        if (index < 0) result.Add(placement); else result.Insert(index, placement);
        return result.ToArray();
    }
}
