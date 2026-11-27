using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace TeamBar;

// EQ clients sharing an installation also share its normal-window launch settings.
// A client can save its last placement after its HWND has already disappeared.
internal sealed class GameViewLaunchGuard(string journalPath, Func<int, long, bool> sessionAlive, string? allowedIni = null,
    Action? validateInstallation = null)
{
    private sealed record Claim(string Ini, int Pid, long Started, GameViewLaunchPosition Position);
    private List<Claim>? claims;
    private readonly Dictionary<string, (DateTime Written, long Length)> stamps = new(StringComparer.OrdinalIgnoreCase);
    internal string? LastError { get; private set; }
    internal static GameViewLaunchGuard Shared { get; } = new(
        Path.Combine(InstalledAppData.DataDirectory, ScreenInstallation.RecoveryName("game-view-launch-recovery", ScreenInstallation.Current)),
        ProcessStillRunning, ClientSessions.LaunchSettings,
        ScreenInstallation.Same(ScreenInstallation.Current, ScreenInstallation.Original) ? null : () => ScreenInstallation.Validate(ScreenInstallation.Current));

    private static bool ProcessStillRunning(int pid, long started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // A temporarily inaccessible process may still save its settings.
    }

    private bool Valid(Claim? c) => c?.Position is { } p && c.Pid > 0 && c.Started > 0 &&
        p.Width > 0 && p.Height > 0 && p.ParkedY < p.SafeY && Path.IsPathFullyQualified(c.Ini) &&
        string.Equals(Path.GetFileName(c.Ini), "eqclient.ini", StringComparison.OrdinalIgnoreCase) &&
        (allowedIni is null || string.Equals(c.Ini, allowedIni, StringComparison.OrdinalIgnoreCase));

    private List<Claim> Load()
    {
        if (claims is not null) return claims;
        List<Claim>? saved;
        try { using var stream = File.OpenRead(journalPath); saved = JsonSerializer.Deserialize<List<Claim>>(stream, Program.Json); }
        catch (FileNotFoundException) { return claims = []; }
        catch (DirectoryNotFoundException) { return claims = []; }
        catch (JsonException ex) { throw new InvalidDataException("The saved EQ launch recovery data could not be read.", ex); }
        if (saved is null || saved.Any(c => !Valid(c))) throw new InvalidDataException("Invalid EQ launch recovery data.");
        return claims = saved;
    }

    private void Save(List<Claim> next)
    {
        if (next.Count == 0) File.Delete(journalPath);
        else WindowLayouts.SaveJson(journalPath, next);
        claims = next;
    }

    internal void Record(string ini, int pid, long started, GameViewLaunchPosition position)
    {
        var claim = new Claim(Path.GetFullPath(ini), pid, started, position);
        if (!Valid(claim)) throw new InvalidDataException("Invalid EQ launch position.");
        var all = Load();
        if (all.Contains(claim)) return;
        Save([.. all, claim]);
        stamps.Remove(claim.Ini);
    }

    internal bool TryFind(string ini, Rectangle frame, Size client, out int safeTop)
    {
        var tops = Load().Where(c => string.Equals(c.Ini, ini, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Position).Where(p => p.X == frame.X && p.ParkedY == frame.Y &&
                p.Width == client.Width && p.Height == client.Height).Select(p => p.SafeY).Distinct().Take(2).ToArray();
        safeTop = tops.Length == 1 ? tops[0] : frame.Top;
        return tops.Length == 1;
    }

    internal void Poll(bool force = false)
    {
        LastError = null;
        try
        {
            var all = Load();
            if (all.Count == 0) return;
            validateInstallation?.Invoke();
            var finished = new HashSet<Claim>();
            foreach (var group in all.GroupBy(c => c.Ini, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var departed = group.Where(c => !sessionAlive(c.Pid, c.Started)).ToArray();
                    var file = new FileInfo(group.Key);
                    var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
                    if (!force && departed.Length == 0 && stamps.TryGetValue(group.Key, out var previous) && previous == stamp) continue;
                    EqLaunchPosition.Repair(group.Key, group.Select(c => c.Position).Distinct().ToArray());
                    stamps[group.Key] = stamp;
                    finished.UnionWith(departed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
            }
            if (finished.Count != 0) Save(all.Where(c => !finished.Contains(c)).ToList());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { LastError = ex.Message; }
    }
}
