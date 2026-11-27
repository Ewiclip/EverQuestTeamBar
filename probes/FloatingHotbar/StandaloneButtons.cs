using System.Text.Json;

// A button owns its mappings and visibility. It has no bar ID or bar membership.
internal sealed record StandaloneButton(int Version, string Id, string Label, CharacterProfile[] Characters,
    PersonaProfile[] Personas, ButtonRule[] Rules, TargetSelector[] ShowFor, string? FollowCharacterId = null, bool Floating = true
    )
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }
    internal string ManagementName => TeamBar.OverlayButtonIcons.ManagementName(Label, Icon, Rules.FirstOrDefault()?.Key);
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    internal TeamDefinition AsTeam() => new(1, Characters, Personas, [new(Id, Label, Rules
        )]);
}
internal sealed record SavedButton(string Path, StandaloneButton Value, byte[] Bytes);

internal static class StandaloneButtons
{
    internal static string DirectoryFor(string profile) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profile))!, "buttons");
    internal static string PathFor(string profile, string id)
    {
        if (id.Length is < 1 or > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new InvalidDataException("Invalid button ID.");
        return Path.Combine(DirectoryFor(profile), id + ".json");
    }
    internal static string[] DefinitionPaths(string profile)
    {
        string directory = DirectoryFor(profile);
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, "*.json").Where(p => !new[] { ".bar-position.json", ".screen-positions.json", ".windows.json" }
            .Any(suffix => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))).Order().ToArray();
    }
    internal static SavedButton[] Load(string profile)
    {
        var paths = DefinitionPaths(profile);
        var buttons = paths.Select(path =>
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (System.Text.Json.Nodes.JsonNode.Parse(bytes) is not System.Text.Json.Nodes.JsonObject)
                throw new InvalidDataException("Empty standalone button.");
            var value = JsonSerializer.Deserialize<StandaloneButton>(bytes, Program.Json);
            if (value is null) return null; // A legacy loop-control button has no one-tap action.
            Validate(value);
            if (!string.Equals(Path.GetFullPath(path), PathFor(profile, value.Id), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A standalone button's file name must match its ID.");
            return new SavedButton(path, value, bytes);
        }).OfType<SavedButton>().ToArray();
        return buttons;
    }
    internal static void Validate(StandaloneButton value)
    {
        if (value.Version != 1 || value.ShowFor is null || value.ShowFor.Length > 32) throw new InvalidDataException("Invalid standalone button visibility.");
        Routing.Validate(value.AsTeam(), [], requireClients: false);
        AnchorPositions.ValidateCharacter(value.FollowCharacterId, value.Characters);
        var selectors = new HashSet<string>();
        foreach (var selector in value.ShowFor)
        {
            if (selector is null || !selectors.Add(TeamEditing.SelectorKey(selector))) throw new InvalidDataException("Remove duplicate visibility conditions.");
            if (selector.PersonaId is not null)
            {
                var persona = value.Personas.SingleOrDefault(p => string.Equals(p.Id, selector.PersonaId, StringComparison.OrdinalIgnoreCase));
                if (persona?.Class is null || selector.CharacterId is not null || selector.Class is not null) throw new InvalidDataException("Visibility needs a class-specific persona.");
            }
            else if (selector.CharacterId is not null || selector.Class is null || !Identity.Classes.Any(c => TeamEditing.ClassKey(c) == TeamEditing.ClassKey(selector.Class)))
                throw new InvalidDataException("Choose a class or a named persona for visibility.");
        }
    }
    internal static bool Visible(StandaloneButton value, DetectedClient[] clients) => value.ShowFor.Length == 0 || value.ShowFor.Any(selector =>
    {
        if (selector.PersonaId is null) return clients.Any(c => TeamEditing.ClassKey(c.Character.Class) == TeamEditing.ClassKey(selector.Class!));
        var persona = value.Personas.Single(p => string.Equals(p.Id, selector.PersonaId, StringComparison.OrdinalIgnoreCase));
        var character = value.Characters.Single(c => string.Equals(c.Id, persona.CharacterId, StringComparison.OrdinalIgnoreCase));
        return clients.Any(c => string.Equals(c.Character.Name, character.Name, StringComparison.OrdinalIgnoreCase) && TeamEditing.ClassKey(c.Character.Class) == TeamEditing.ClassKey(persona.Class!));
    });
    internal static RoutedTap[] Resolve(StandaloneButton value, DetectedClient[] clients)
    {
        var team = TeamEditing.MergeDetected(value.AsTeam(), clients);
        return Routing.Resolve(team, value.Id, TeamEditing.Bind(team, clients));
    }
    internal static RoutingConflict[] Conflicts(StandaloneButton value, DetectedClient[] clients)
    {
        var team = TeamEditing.MergeDetected(value.AsTeam(), clients);
        return Routing.Conflicts(team, TeamEditing.Bind(team, clients));
    }
    internal static ButtonMapping Display(StandaloneButton value, DetectedClient[] clients)
    {
        var team = TeamEditing.MergeDetected(value.AsTeam(), clients);
        return Routing.DisplayButton(team, value.Id, TeamEditing.Bind(team, clients)) with { Icon = value.Icon };
    }
    internal static void CheckUnchanged(string path, byte[]? expected)
    {
        byte[]? actual = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (expected is null ? actual is not null : actual is null || !expected.SequenceEqual(actual))
            throw new IOException("This button changed in another editor. Reopen configuration before saving it.");
    }
    internal static byte[] Save(string path, StandaloneButton value, byte[]? expected)
    {
        Validate(value); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckUnchanged(path, expected); WindowLayouts.SaveJson(path, value); return File.ReadAllBytes(path);
    }
    internal static void Remove(string path, byte[]? expected)
    {
        if (expected is null) return;
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckUnchanged(path, expected);
        // Retain a recoverable copy when the user removes a saved button.
        string archive = Path.Combine(Path.GetDirectoryName(path)!, "removed"); Directory.CreateDirectory(archive);
        File.Move(path, Path.Combine(archive, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".json"));
    }
}
