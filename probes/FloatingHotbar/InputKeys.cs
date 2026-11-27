// Physical DirectInput scan offsets shared by all input editors and validators.
// Extended keys retain the high bit so navigation and numpad keys stay distinct.
// Keep the original number-row choices first so existing selection indices stay stable.
internal static class InputKeys
{
    private static readonly (string Name, int Scan)[] Keys =
    [
        ("1", 0x02), ("2", 0x03), ("3", 0x04), ("4", 0x05), ("5", 0x06), ("6", 0x07),
        ("7", 0x08), ("8", 0x09), ("9", 0x0a), ("0", 0x0b), ("-", 0x0c), ("=", 0x0d),
        ("A", 0x1e), ("B", 0x30), ("C", 0x2e), ("D", 0x20), ("E", 0x12), ("F", 0x21),
        ("G", 0x22), ("H", 0x23), ("I", 0x17), ("J", 0x24), ("K", 0x25), ("L", 0x26),
        ("M", 0x32), ("N", 0x31), ("O", 0x18), ("P", 0x19), ("Q", 0x10), ("R", 0x13),
        ("S", 0x1f), ("T", 0x14), ("U", 0x16), ("V", 0x2f), ("W", 0x11), ("X", 0x2d),
        ("Y", 0x15), ("Z", 0x2c),
        ("[", 0x1a), ("]", 0x1b), ("\\", 0x2b), (";", 0x27), ("'", 0x28), ("`", 0x29),
        (",", 0x33), (".", 0x34), ("/", 0x35),
        ("F1", 0x3b), ("F2", 0x3c), ("F3", 0x3d), ("F4", 0x3e), ("F5", 0x3f), ("F6", 0x40),
        ("F7", 0x41), ("F8", 0x42), ("F9", 0x43), ("F10", 0x44), ("F11", 0x57), ("F12", 0x58),
        ("Space", 0x39), ("Tab", 0x0f), ("Enter", 0x1c), ("Backspace", 0x0e), ("Escape", 0x01),
        ("Numpad0", 0x52), ("Numpad1", 0x4f), ("Numpad2", 0x50), ("Numpad3", 0x51), ("Numpad4", 0x4b),
        ("Numpad5", 0x4c), ("Numpad6", 0x4d), ("Numpad7", 0x47), ("Numpad8", 0x48), ("Numpad9", 0x49),
        ("NumpadDecimal", 0x53), ("NumpadAdd", 0x4e), ("NumpadSubtract", 0x4a), ("NumpadMultiply", 0x37),
        ("Up", 0xc8), ("Down", 0xd0), ("Left", 0xcb), ("Right", 0xcd), ("Home", 0xc7), ("End", 0xcf),
        ("PageUp", 0xc9), ("PageDown", 0xd1), ("Insert", 0xd2), ("Delete", 0xd3),
        ("NumpadEnter", 0x9c), ("NumpadDivide", 0xb5)
    ];
    private static readonly Dictionary<string, (string Name, int Scan)> ByName = Keys.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);
    internal static readonly string[] Names = Keys.Select(k => k.Name).ToArray();

    private static (string Name, int Scan) Find(string key) => key is not null && ByName.TryGetValue(key, out var found)
        ? found : throw new InvalidDataException("Choose a supported keyboard key.");
    internal static string Normalize(string key) => Find(key).Name;
    internal static int Scan(string key) => Find(key).Scan;
}
