using System.Text.Json;

internal static class TeamEditing
{
    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static string ClassKey(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
    internal static TeamDefinition IncludeButtonRecipients(TeamDefinition team, StandaloneButton button)
    {
        // Button files can retain offline characters and personas that aren't in team.json.
        var characters = team.Characters.ToList();
        var personas = team.Personas.ToList();
        foreach (var character in button.Characters)
        {
            var found = characters.FirstOrDefault(c => Eq(c.Id, character.Id));
            if (found is null) characters.Add(character);
            else if (found != character) throw new InvalidDataException("A standalone button has a conflicting character ID.");
        }
        foreach (var persona in button.Personas)
        {
            var found = personas.FirstOrDefault(p => Eq(p.Id, persona.Id));
            if (found is null) personas.Add(persona);
            else if (found != persona) throw new InvalidDataException("A standalone button has a conflicting persona ID.");
        }
        return team with { Characters = characters.ToArray(), Personas = personas.ToArray() };
    }
    internal static TeamDefinition MergeDetected(TeamDefinition team, DetectedClient[] clients)
    {
        var characters = team.Characters.ToList(); var personas = team.Personas.ToList();
        foreach (var client in clients)
        {
            var c = characters.SingleOrDefault(c => Eq(c.Name, client.Character.Name));
            if (c is null)
            {
                string root = client.Character.Name.ToLowerInvariant(), id = root; int suffix = 2;
                while (characters.Any(c => Eq(c.Id, id))) id = root + "-" + suffix++;
                c = new(id, client.Character.Name); characters.Add(c);
            }
            if (!personas.Any(p => Eq(p.CharacterId, c.Id) && p.Class is not null && ClassKey(p.Class) == ClassKey(client.Character.Class)))
            {
                string root = c.Id + "-" + ClassKey(client.Character.Class), id = root; int suffix = 2;
                while (personas.Any(p => Eq(p.Id, id))) id = root + "-" + suffix++;
                personas.Add(new(id, c.Id, client.Character.Class));
            }
        }
        return team with { Characters = characters.ToArray(), Personas = personas.ToArray() };
    }
    internal static ClientBinding[] Bind(TeamDefinition team, DetectedClient[] clients) => clients.Select(client =>
    {
        var character = team.Characters.Single(c => Eq(c.Name, client.Character.Name));
        var persona = team.Personas.First(p => Eq(p.CharacterId, character.Id) && p.Class is not null && ClassKey(p.Class) == ClassKey(client.Character.Class));
        return new ClientBinding(persona.Id, client.ProcessId, client.StartedUtcTicks);
    }).ToArray();
    internal static void Save(string path, TeamDefinition team, ClientBinding[] clients)
        => SaveCore(path, team, clients, null, false);
    internal static byte[] SaveChecked(string path, TeamDefinition team, ClientBinding[] clients, byte[]? expected)
        => SaveCore(path, team, clients, expected, true);
    private static byte[] SaveCore(string path, TeamDefinition team, ClientBinding[] clients, byte[]? expected, bool checkExisting)
    {
        Routing.Validate(team, clients, requireClients: false);
        string destination = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var guard = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (checkExisting)
        {
            byte[]? actual = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
            if (expected is null ? actual is not null : actual is null || !expected.SequenceEqual(actual))
                throw new IOException("The profile changed in another editor. Reopen configuration before saving it.");
        }
        // Compare and replace under the same lock. Return the bytes we wrote, not
        // a later reread that could accidentally adopt another editor's version.
        var options = BuildEdition.JsonOptions(); options.WriteIndented = true; options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(team, options);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return bytes;
    }
    internal static TeamDefinition Load(string path) => JsonSerializer.Deserialize<TeamDefinition>(File.ReadAllText(path), Program.Json) ?? throw new InvalidDataException("Empty team profile.");
    internal static string SelectorKey(TargetSelector target) => JsonSerializer.Serialize(target);
    internal static string SelectorLabel(TeamDefinition team, TargetSelector target)
    {
        if (target.PersonaId is not null)
        {
            var p = team.Personas.Single(p => Eq(p.Id, target.PersonaId));
            string n = team.Characters.Single(c => Eq(c.Id, p.CharacterId)).Name;
            return p.Class is null ? n + " — saved profile" : n + " — " + p.Class;
        }
        if (target.CharacterId is not null)
        {
            string n = team.Characters.Single(c => Eq(c.Id, target.CharacterId)).Name;
            return target.Class is null ? n + " — any class" : n + " — " + target.Class;
        }
        return "All " + target.Class + " characters";
    }
}
