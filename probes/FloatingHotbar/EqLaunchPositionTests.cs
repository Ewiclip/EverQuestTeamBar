using System.Text;

internal static class EqLaunchPositionTests
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        string file = Path.Combine(directory, "layout-launch-position.ini");
        const string original = "; café\r\n[dEFAults]\r\nMaXimiZed=0\nWINDOWEDMODEXOFFSET = 5160\r\nWindowedModeYOffset =  -31 \t\n" +
            "[VideoMode]\r\nWindowedWidth=1704\r\nWindowedHeight=1440\r\nFullscreen=0\r\nBorderless=1\n[Other]\nWindowedModeYOffset=-31";
        const string parked = "WindowedModeYOffset =  -31";
        var right = new GameViewLaunchPosition(5160, -31, 1704, 1440, 0);
        var left = right with { X = 3440 };
        foreach (Encoding encoding in new[] { Encoding.Latin1, Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            byte[] Encode(string value) => [.. encoding.Preamble, .. encoding.GetBytes(value)];
            File.WriteAllBytes(file, Encode(original));
            check(EqLaunchPosition.Repair(file, [left, right]) &&
                File.ReadAllBytes(file).SequenceEqual(Encode(original.Replace(parked, "WindowedModeYOffset =  0"))),
                $"Repair changes only the verified parked Y token in a {encoding.WebName} file.");
        }

        foreach (var (label, contents) in new[]
        {
            ("manual movement", original.Replace("5160", "5161")),
            ("manual vertical adjustment", original.Replace(parked, "WindowedModeYOffset=8")),
            ("a new client size", original.Replace("1704", "1700")),
            ("fullscreen", original.Replace("Fullscreen=0", "Fullscreen=1")),
            ("maximized", original.Replace("MaXimiZed=0", "Maximized=1")),
            ("a missing setting", original.Replace("WindowedHeight=1440", "; removed")),
            ("a duplicate offset", original.Replace(parked, "WindowedModeYOffset=-31\n" + parked)),
            ("a repeated placement section", original + "\n[defaults]"),
            ("an unreadable value", original.Replace("WindowedWidth=1704", "WindowedWidth=unknown"))
        })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            File.WriteAllBytes(file, bytes);
            check(EqLaunchPosition.Repair(file, [right]) && File.ReadAllBytes(file).SequenceEqual(bytes),
                $"Launch repair preserves the complete INI after {label}.");
        }
        var initial = Encoding.UTF8.GetBytes(original);
        File.WriteAllBytes(file, initial);
        check(EqLaunchPosition.Repair(file, [right, right with { SafeY = 12 }]) && File.ReadAllBytes(file).SequenceEqual(initial),
            "Conflicting recovery positions leave the shared game settings alone.");
        bool busy = false;
        using (var writer = File.Open(file, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            try { EqLaunchPosition.Repair(file, [right]); }
            catch (IOException) { busy = true; }
        }
        check(busy && File.ReadAllBytes(file).SequenceEqual(initial) && EqLaunchPosition.Repair(file, [right]),
            "A concurrent game writer keeps its settings intact and launch recovery can retry after it closes.");
        string absent = Path.Combine(directory, "missing-launch-position.ini");
        check(EqLaunchPosition.Repair(absent, [right]) && !File.Exists(absent), "Repair never creates an absent EQ configuration.");
    }
}
