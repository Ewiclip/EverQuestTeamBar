namespace TeamBar;

internal sealed record SpellOverlayKey(string Key, int Scan, int Modifiers)
{
    internal static readonly string[] ModifierNames = ["None", "Shift", "Ctrl", "Ctrl + Shift", "Alt", "Shift + Alt", "Ctrl + Alt", "Ctrl + Shift + Alt"];
    internal static SpellOverlayKey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Choose a supported keyboard key.");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        int mask = 0;
        foreach (string part in parts[..^1])
        {
            int bit = part.ToLowerInvariant() switch { "shift" => 1, "ctrl" => 2, "alt" => 4, _ => 0 };
            if (bit == 0 || (mask & bit) != 0) throw new InvalidDataException("Use Shift, Ctrl or Alt once each with a supported keyboard key.");
            mask |= bit;
        }
        string key = InputKeys.Normalize(parts[^1]); return new(key, InputKeys.Scan(key), mask);
    }
    internal static string Format(string key, int modifiers)
    {
        if (modifiers is < 0 or > 7) throw new InvalidDataException("Choose valid keyboard modifiers.");
        key = InputKeys.Normalize(key);
        return modifiers == 0 ? key : ModifierNames[modifiers].Replace(" ", "") + "+" + key;
    }
}
