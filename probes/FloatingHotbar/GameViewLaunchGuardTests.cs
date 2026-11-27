using TeamBar;

internal static class GameViewLaunchGuardTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "launch-guard")).FullName;
        string install = Directory.CreateDirectory(Path.Combine(directory, "game")).FullName;
        string journal = Path.Combine(directory, "guard.json");
        string ini = Path.Combine(install, "eqclient.ini");
        File.WriteAllBytes(Path.Combine(install, "eqgame.exe"), []);

        var left = new GameViewLaunchPosition(3440, -31, 1704, 1440, 0);
        var right = left with { X = 5160 };
        var client = new Size(1704, 1440);
        var frame = new Rectangle(left.X, left.ParkedY, 1720, 1479);
        var neighbor = new Rectangle(right.X, right.ParkedY, 1720, 1479);
        const int firstPid = 8401, secondPid = 8402;
        long firstStart = DateTime.UtcNow.Ticks, secondStart = firstStart + 1;
        var running = new HashSet<(int, long)> { (firstPid, firstStart), (secondPid, secondStart) };
        GameViewLaunchGuard Restart() => new(journal, (pid, start) => running.Contains((pid, start)));
        static string Settings(int x, int y, string other = "unchanged") =>
            $"[Defaults]\r\nWindowedModeXOffset={x}\r\nWindowedModeYOffset={y}\r\nMaximized=0\r\n" +
            $"[VideoMode]\r\nWindowedWidth=1704\r\nWindowedHeight=1440\r\nFullscreen=0\r\n[Other]\r\nOption={other}\r\n";

        File.WriteAllText(ini, Settings(left.X, left.SafeY));
        var guard = Restart();
        guard.Record(ini, firstPid, firstStart, left);
        guard.Record(ini, secondPid, secondStart, right);
        guard = Restart();
        guard.Poll(force: true);
        check(guard.TryFind(ini, frame, client, out int safeLeft) && safeLeft == left.SafeY &&
            guard.TryFind(ini, neighbor, client, out int safeRight) && safeRight == right.SafeY &&
            !guard.TryFind(ini, neighbor, new Size(client.Width - 1, client.Height), out _),
            "Restarting Layout Lab retains both exact parked window signatures for a shared EQ installation.");

        var legitimate = Settings(left.X + 40, left.ParkedY, "edited by the player");
        File.WriteAllText(ini, legitimate);
        guard.Poll(force: true);
        check(File.ReadAllText(ini) == legitimate, "A player's new launch placement and unrelated settings survive a layout scan.");

        File.WriteAllText(ini, Settings(right.X, right.ParkedY));
        guard.Poll(force: true);
        check(File.ReadAllText(ini) == Settings(right.X, right.SafeY), "Either character's last write to a shared EQ INI can be repaired.");
        var writeTime = File.GetLastWriteTimeUtc(ini);
        guard.Poll(force: true); // Observe the clean file at the timestamp the game will reuse.
        File.WriteAllText(ini, Settings(right.X, right.ParkedY));
        File.SetLastWriteTimeUtc(ini, writeTime);
        guard.Poll(force: true);
        check(File.ReadAllText(ini) == Settings(right.X, right.SafeY), "A forced scan also repairs a repeated game write with the same timestamp.");

        running.Remove((firstPid, firstStart));
        running.Add((firstPid, firstStart + 100)); // A different process now has this PID.
        File.WriteAllText(ini, Settings(left.X, left.ParkedY, "saved at exit"));
        using (File.Open(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            guard = Restart();
            guard.Poll(force: true);
            check(File.Exists(journal) && guard.TryFind(ini, frame, client, out _),
                "An INI held by its writer keeps the exited character's recovery record available for retry.");
        }
        guard = Restart();
        guard.Poll(force: true);
        check(File.ReadAllText(ini) == Settings(left.X, left.SafeY, "saved at exit") &&
            !guard.TryFind(ini, frame, client, out _) && guard.TryFind(ini, neighbor, client, out _) && File.Exists(journal),
            "Recovery after a crash repairs the exited game's final write and releases its record even if its PID was reused.");

        running.Remove((secondPid, secondStart));
        File.WriteAllText(ini, Settings(right.X, right.ParkedY, "last game closed"));
        guard.Poll(force: true);
        check(File.ReadAllText(ini) == Settings(right.X, right.SafeY, "last game closed") && !File.Exists(journal),
            "The last game closing leaves a reachable launch position and clears the completed recovery journal.");
    }
}
