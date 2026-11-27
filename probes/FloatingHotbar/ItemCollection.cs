internal sealed record CollectionRow(int ItemId, string Name, string Change, ItemData[] Variants,
    string[] Slots, string[] PreviousSlots)
{
    public string Label => Name;
    public string Meta => $"{ItemId}  ·  {Slots.Length} location{(Slots.Length == 1 ? "" : "s")}";
    public string ChangeLabel => Change == "Unchanged" ? "" : Change;
    public bool IsChanged => Change != "Unchanged";
    public bool IsTradeskill => Variants.Any(v => v.CandidateDetails.Tradeskill);
}
internal static class ItemCollection
{
    internal static void EnsureComparable(InventoryCapture baseline, InventoryCapture current)
    {
        if (baseline.CharacterName != current.CharacterName || baseline.CharacterClass != current.CharacterClass
            || baseline.ClientChannel != current.ClientChannel || baseline.BuildSha256 != current.BuildSha256 || baseline.Decoder != current.Decoder)
            throw new InvalidDataException("Choose a new baseline for this character, channel and build.");
    }
    internal static CollectionRow[] Compare(InventoryCapture baseline, InventoryCapture current)
    {
        EnsureComparable(baseline, current);
        var before = baseline.Items.GroupBy(e => e.Item.ItemId).ToDictionary(g => g.Key, g => g.ToArray());
        var after = current.Items.GroupBy(e => e.Item.ItemId).ToDictionary(g => g.Key, g => g.ToArray());
        return before.Keys.Union(after.Keys).Select(id =>
        {
            var old = before.GetValueOrDefault(id) ?? [];
            var now = after.GetValueOrDefault(id) ?? [];
            var oldVariants = old.Select(e => e.Item).Distinct().ToHashSet();
            var variants = now.Select(e => e.Item).Distinct().ToArray();
            string[] oldSlots = old.Select(e => e.SlotPath).Order(StringComparer.Ordinal).ToArray();
            string[] slots = now.Select(e => e.SlotPath).Order(StringComparer.Ordinal).ToArray();
            string change = old.Length == 0 ? "Added" : now.Length == 0 ? "Removed"
                : !oldVariants.SetEquals(variants) ? "Properties changed"
                : old.Length != now.Length ? "Locations changed"
                : !oldSlots.SequenceEqual(slots) ? "Moved" : "Unchanged";
            var displayed = now.Length == 0 ? old.Select(e => e.Item).Distinct().ToArray() : variants;
            return new CollectionRow(id, displayed[0].Name, change, displayed, slots, oldSlots);
        }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ItemId).ToArray();
    }
    internal static string Location(string path)
    {
        var parts = path.Split('/');
        if (!int.TryParse(parts[0], out int slot)) return path;
        string[] worn = ["Charm", "Left ear", "Head", "Face", "Right ear", "Neck", "Shoulders", "Arms", "Back",
            "Left wrist", "Right wrist", "Ranged", "Hands", "Primary", "Secondary", "Left finger", "Right finger", "Chest", "Legs", "Feet", "Waist", "Power source", "Ammo"];
        string root = slot < worn.Length && slot >= 0 ? worn[slot] : slot is >= 23 and <= 34 ? $"Bag {slot - 22}" : slot == 35 ? "Cursor" : $"Slot {slot}";
        return root + string.Concat(parts.Skip(1).Select(p => int.TryParse(p, out int child) ? $" / {child + 1}" : $" / {p}"));
    }
}
