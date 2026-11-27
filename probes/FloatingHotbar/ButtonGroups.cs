using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBar;

internal sealed record ButtonGroup(string Id, string Name, ButtonPlacement[] Buttons, int Columns = 1, double Scale = 1,
    string? FollowCharacterId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? ColumnNames { get; init; }
    [JsonIgnore] public string ManagementName => string.IsNullOrWhiteSpace(Name) ? $"Unnamed group · {Id[^Math.Min(6, Id.Length)..]}" : Name;
    [JsonIgnore] public string Description => $"{Buttons.Length} saved buttons · {Columns} column{(Columns == 1 ? "" : "s")}";
}
internal static class ButtonGroups
{
    internal static string PositionPath(string profile, string id) => profile + ".group-" + id;
    internal static void Validate(ButtonGroup[]? groups, CharacterProfile[] characters)
    {
        if (groups is null) return;
        if (groups.Any(g => g is null) || groups.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Length)
            throw new InvalidDataException("Each button group needs a unique ID.");
        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.Id) || group.Id.Length > 40 || !group.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                || group.Name is null or { Length: > 40 } || !double.IsFinite(group.Scale) || group.Scale is < .5 or > 2)
                throw new InvalidDataException("Use a valid group ID, a title of up to 40 characters, and a size from 50–200%.");
            ButtonColumns.Validate(group.Buttons, group.Columns);
            ButtonColumns.ValidateNames(group.ColumnNames, group.Columns);
            AnchorPositions.ValidateCharacter(group.FollowCharacterId, characters);
        }
    }
    internal static void Migrate(string profile)
    {
        if (!File.Exists(profile)) return;
        using var guard = new FileStream(profile + ".library-migration.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var oldBytes = File.ReadAllBytes(profile);
        var old = JsonSerializer.Deserialize<TeamDefinition>(oldBytes, Program.Json) ?? throw new InvalidDataException("Empty team profile.");
        if (old.Buttons.Length == 0) return;
        var existing = StandaloneButtons.Load(profile);
        var converted = old.Buttons.Select(button =>
        {
            string id = "bar-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(button.Id)))[..20];
            return new StandaloneButton(1, id, button.Label, old.Characters, old.Personas, button.Rules, [], Floating: false) { Extra = button.Extra };
        }).ToArray();
        foreach (var button in converted)
        {
            StandaloneButtons.Validate(button);
            if (existing.SingleOrDefault(e => e.Value.Id == button.Id) is { } found
                && JsonSerializer.Serialize(found.Value, Program.Json) != JsonSerializer.Serialize(button, Program.Json))
                throw new IOException("A converted bar button already exists with different settings. The original bar was preserved.");
        }
        var group = new ButtonGroup("migrated-team-bar", "Team bar", ButtonColumns.Legacy(converted.Select(b => b.Id).ToArray(), converted.Length),
            converted.Length, FollowCharacterId: old.FollowCharacterId);
        if (old.Groups?.Any(g => g.Id == group.Id) == true) throw new IOException("The migration group already exists. The original profile was preserved.");
        var updated = old with { Buttons = [], Groups = [.. old.Groups ?? [], group], FollowCharacterId = null };
        Routing.Validate(updated, [], requireClients: false);
        string backup = Path.Combine(Path.GetDirectoryName(profile)!, "library-backups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "team.json"), oldBytes);
        foreach (var saved in existing) File.WriteAllBytes(Path.Combine(backup, Path.GetFileName(saved.Path)), saved.Bytes);
        foreach (string suffix in new[] { ".bar-position.json", ".screen-positions.json", ".windows.json" })
            if (File.Exists(profile + suffix)) File.Copy(profile + suffix, Path.Combine(backup, "team.json" + suffix));
        foreach (var button in converted)
            if (existing.All(e => e.Value.Id != button.Id)) StandaloneButtons.Save(StandaloneButtons.PathFor(profile, button.Id), button, null);
        foreach (string suffix in new[] { ".bar-position.json", ".screen-positions.json" })
        {
            string destination = PositionPath(profile, group.Id) + suffix;
            if (File.Exists(profile + suffix) && !File.Exists(destination)) File.Copy(profile + suffix, destination);
        }
        TeamEditing.SaveChecked(profile, updated, [], oldBytes);
    }
}
