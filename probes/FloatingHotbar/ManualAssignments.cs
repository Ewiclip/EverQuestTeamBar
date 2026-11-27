using System.Text.Json;

internal static class ManualAssignments
{
    private sealed record Saved(int Version, DetectedClient[] Clients);
    internal static string PathFor(string profile) => profile + ".assignments.json";
    internal static void Save(string profile, DetectedClient[] clients)
    {
        Validate(clients);
        string path = Path.GetFullPath(PathFor(profile)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(1, clients), Program.Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static DetectedClient[] Load(string profile, Func<DetectedClient, DetectedClient>? verify = null)
    {
        string path = PathFor(profile); if (!File.Exists(path)) return [];
        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Program.Json);
        if (saved is null || saved.Version != 1) throw new InvalidDataException("Saved window assignments need a newer Team Bar version.");
        Validate(saved.Clients); verify ??= ClientSessions.Verify;
        var current = new List<DetectedClient>();
        foreach (var expected in saved.Clients)
            try { var actual = verify(expected); if (Identity.Same(expected, actual) && actual.UserAssigned) current.Add(actual); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return current.ToArray();
    }
    private static void Validate(DetectedClient[] clients)
    {
        if (clients.Length > 0) ClientSessions.ValidateAssignments(clients);
        if (clients.Any(c => !c.UserAssigned || c.Character.ClassId is < 1 or > 16 || c.Character.Name.Length is < 2 or > 30
            || !c.Character.Name.All(char.IsAsciiLetter))) throw new InvalidDataException("Only confirmed manual assignments may be saved here.");
    }
}
