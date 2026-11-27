using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record GatheringObservation(string Kind, string Name, ObservedZone Zone, DateTimeOffset SeenAtUtc)
{
    public string Method => Kind == "foraged" ? "Foraged" : "Fished";
    public string ZoneName => Zone.Name;
    public string Seen => SeenAtUtc.ToLocalTime().ToString("g");
}
internal sealed record GatheringHistorySnapshot(int SchemaVersion, string SourceKind, string ClientChannel,
    string BuildSha256, string Decoder, GatheringObservation[] Observations);

// Tail only new lines. No chat, character, path, timestamps or failure counts
// cross into the contribution wire model. Inventory changes never imply a source.
internal sealed class GatheringLogReader
{
    private string? activePath;
    private DateTime created;
    private long offset;
    private ObservedZone? previousZone;
    private DateTimeOffset previousRead;
    private bool waitingForZone;
    private string? requiredZoneName;
    internal string Status { get; private set; } = "Waiting for a verified zone and a character log.";
    private static readonly Regex Success = new(@"\A(?:You have scrounged up (?<foraged>[^\r\n]+)\.|You caught (?<fished>[^\r\n]+)!)\z", RegexOptions.CultureInvariant);
    internal static bool MatchesClientLog(string path, string installation, string character) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Path.Combine(installation, "Logs")), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path).StartsWith("eqlog_" + character + "_", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
    internal static (string Kind, string Name)? ParseSuccess(string text)
    {
        if (text.Length > 320) return null;
        var match = Success.Match(text);
        if (!match.Success) return null;
        string kind = match.Groups["foraged"].Success ? "foraged" : "fished";
        string name = match.Groups[kind].Value.Trim();
        if (name.Length is < 1 or > 255 || name.Any(c => char.IsControl(c) || c == '\ufffd')
            || name.Equals("nothing", StringComparison.OrdinalIgnoreCase)) return null;
        return (kind, name);
    }
    internal GatheringObservation[] Poll(string path, ObservedZone zone, DateTimeOffset now)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var creation = File.GetCreationTimeUtc(path);
        bool reset = activePath != path || creation != created || file.Length < offset || now - previousRead > TimeSpan.FromSeconds(15);
        bool stable = !reset && previousZone == zone;
        if (waitingForZone)
        {
            stable = false;
            if (requiredZoneName is not null && requiredZoneName.Equals(zone.Name, StringComparison.OrdinalIgnoreCase))
            { waitingForZone = false; requiredZoneName = null; }
        }
        previousZone = zone; previousRead = now;
        if (reset || file.Length - offset > 262144)
        {
            activePath = path; created = creation; offset = file.Length;
            waitingForZone = false; requiredZoneName = null;
            Status = "Watching new successes. Enable game logging with /log on; old log entries are not imported.";
            return [];
        }
        file.Position = offset;
        var bytes = new byte[checked((int)(file.Length - offset))];
        int count = 0;
        while (count < bytes.Length) { int read = file.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
        if (count == 0) return [];
        int last = Array.LastIndexOf(bytes, (byte)'\n', count - 1, count);
        if (last < 0) { if (!stable) offset = file.Length; return []; }
        offset += last + 1;
        var result = new List<GatheringObservation>();
        string[] lines = Encoding.UTF8.GetString(bytes, 0, last + 1).Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            int end = line.IndexOf("] ", StringComparison.Ordinal);
            if (!line.StartsWith('[') || end is < 20 or > 35) continue;
            string message = line[(end + 2)..];
            if (message.StartsWith("You have entered ", StringComparison.Ordinal) || message == "LOADING, PLEASE WAIT...")
            {
                stable = false; waitingForZone = true;
                requiredZoneName = message.StartsWith("You have entered ", StringComparison.Ordinal) ? message[17..].TrimEnd('.') : null;
                continue;
            }
            if (!stable || ParseSuccess(message) is not { } success) continue;
            if (!DateTimeOffset.TryParseExact(line[1..end], ["ddd MMM dd HH:mm:ss yyyy", "ddd MMM  d HH:mm:ss yyyy", "ddd MMM d HH:mm:ss yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) || now - at > TimeSpan.FromSeconds(15) || at > now.AddSeconds(2)) continue;
            result.Add(new(success.Kind, success.Name, zone, at.ToUniversalTime()));
        }
        // Discard any unfinished line if its zone interval was uncertain. It
        // must not become attributed to a different zone when completed later.
        if (!stable) offset = file.Length;
        Status = waitingForZone ? "Waiting for the logged zone transition to match a fresh game reading."
            : "Watching new forage and fishing successes; zone verified. Only source associations are shared.";
        return result.ToArray();
    }
}
