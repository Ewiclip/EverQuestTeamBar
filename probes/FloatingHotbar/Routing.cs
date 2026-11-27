using System.Text.Json;

internal sealed record CharacterProfile(string Id, string Name);
internal sealed record PersonaProfile(string Id, string CharacterId, string? Class);
internal sealed record TargetSelector(string? CharacterId = null, string? Class = null, string? PersonaId = null);
internal sealed record ButtonRule(TargetSelector Target, string Key);
internal sealed record TeamButton(string Id, string Label, ButtonRule[] Rules)
{
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
internal sealed record TeamDefinition(int Version, CharacterProfile[] Characters, PersonaProfile[] Personas, TeamButton[] Buttons,
    string? FollowCharacterId = null, ButtonGroup[]? Groups = null)
{
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
internal sealed record ClientBinding(string PersonaId, int ProcessId, long StartedUtcTicks);
internal sealed record ClientFile(int Version, ClientBinding[] Clients, DetectedClient[]? Identities = null);
internal sealed record RoutedTap(int ProcessId, string PersonaId, string Label, string Key, int Scan, int Modifiers = 0);
internal sealed record LiveSetup(string Controller, TeamDefinition Team, ClientBinding[] Clients, DetectedClient[]? Identities = null)
{
    internal MemoryFeature IdentityFeature { get; init; } = MemoryFeature.ScreenDetection;
    internal string[] ExcludedCharacterNames { get; init; } = [];
    internal string[] AutoConnectCharacterNames { get; init; } = [];
    internal bool FloatingHotkeysCleared { get; init; }
    internal LiveSetup RememberOverlayRecipients(IEnumerable<SavedButton> buttons) => this with
    {
        AutoConnectCharacterNames = AutoConnectCharacterNames.Concat(buttons.SelectMany(b => b.Value.Characters).Select(c => c.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    };
}
internal sealed record RoutingConflict(string ButtonId, string PersonaId, int[] RuleIndices, string Message);

internal static class Routing
{
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string ClassKey(string? value) => string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
    private static bool Id(string? value) => value is { Length: > 0 and <= 40 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static int Scan(string key) => InputKeys.Scan(key);
    internal static void Validate(TeamDefinition team, ClientBinding[] clients, bool requireClients = true)
    {
        Check(team.Version == 1 && team.Characters is { Length: <= 12 } && team.Personas is { Length: <= 64 }
            && team.Buttons is { Length: <= 6 }, "Invalid team profile.");
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var character in team.Characters) Check(Id(character.Id) && characters.Add(character.Id) && character.Name is { Length: > 0 and <= 30 }, "Invalid or duplicate character.");
        AnchorPositions.ValidateCharacter(team.FollowCharacterId, team.Characters);
        ButtonGroups.Validate(team.Groups, team.Characters);
        var personas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var persona in team.Personas)
            Check(Id(persona.Id) && personas.Add(persona.Id) && characters.Contains(persona.CharacterId)
                && (persona.Class is null || persona.Class is { Length: > 0 and <= 24 }), "Invalid or duplicate persona.");
        var buttons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in team.Buttons)
        {


            Check(Id(button.Id) && buttons.Add(button.Id) && button.Label is { Length: <= 24 }
                && button.Rules is { Length: <= 24 } && (
                    button.Rules.Length > 0), "Invalid or duplicate button.");

            foreach (var rule in button.Rules)
            {
                TeamBar.SpellOverlayKey.Parse(rule.Key); var selector = rule.Target;
                Check(selector is not null && (selector.CharacterId is not null || selector.Class is not null || selector.PersonaId is not null), "A mapping needs a target.");
                Check(selector.CharacterId is null || characters.Contains(selector.CharacterId), "Unknown character in mapping.");
                Check(selector.Class is null || selector.Class is { Length: > 0 and <= 24 }, "Invalid class in mapping.");
                Check(selector.PersonaId is null || personas.Contains(selector.PersonaId) && selector.CharacterId is null && selector.Class is null, "A persona selector must stand alone.");
            }
            // Resolve all declared personas, including inactive ones, before saving.
            foreach (var persona in team.Personas) ResolveOne(team, button, new(persona.Id, 1, 1));
        }
        Check(clients.Length <= 6 && (!requireClients || clients.Length > 0), "Select one to six clients.");
        var pids = new HashSet<int>(); var activeCharacters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in clients)
        {
            Check(personas.Contains(client.PersonaId) && client.ProcessId > 0 && pids.Add(client.ProcessId) && client.StartedUtcTicks > 0, "Invalid or duplicate client binding.");
            var persona = team.Personas.Single(p => Equal(p.Id, client.PersonaId));
            Check(activeCharacters.Add(persona.CharacterId), "Only one active persona per character is allowed.");
        }
    }
    private static RoutedTap[] ResolveOne(TeamDefinition team, TeamButton button, ClientBinding client)
    {


        var persona = team.Personas.Single(p => Equal(p.Id, client.PersonaId));
        var matching = WinningRules(button, persona);
        if (matching.Length == 0) return [];
        string name = team.Characters.Single(c => Equal(c.Id, persona.CharacterId)).Name;
        string label = persona.Class is null ? name : $"{name} · {persona.Class}";
        // Equivalent selector rows are an intentional ordered key sequence.
        return matching.Select(entry =>
        {
            var chord = TeamBar.SpellOverlayKey.Parse(entry.Rule.Key);
            return new RoutedTap(client.ProcessId, persona.Id, label,
                TeamBar.SpellOverlayKey.Format(chord.Key, chord.Modifiers), chord.Scan, chord.Modifiers);
        }).ToArray();
    }
    private static (ButtonRule Rule, int Index)[] WinningRules(TeamButton button, PersonaProfile persona)
    {
        var matching = button.Rules.Select((rule, index) => (Rule: rule, Index: index)).Where(entry =>
        {
            var rule = entry.Rule;
            return
            (rule.Target.PersonaId is null || Equal(rule.Target.PersonaId, persona.Id)) &&
            (rule.Target.CharacterId is null || Equal(rule.Target.CharacterId, persona.CharacterId)) &&
            (rule.Target.Class is null || ClassKey(rule.Target.Class) == ClassKey(persona.Class));
        }).ToArray();
        if (matching.Length == 0) return [];
        static int Rank(ButtonRule rule) => rule.Target.PersonaId is not null || rule.Target.CharacterId is not null && rule.Target.Class is not null ? 3 : rule.Target.CharacterId is not null ? 2 : 1;
        int best = matching.Max(entry => Rank(entry.Rule));
        return matching.Where(entry => Rank(entry.Rule) == best).ToArray();
    }
    private static string SelectorIdentity(TargetSelector target) => target.PersonaId is { } persona ? "persona:" + persona.ToLowerInvariant()
        : "character:" + (target.CharacterId?.ToLowerInvariant() ?? "*") + ":class:" + (target.Class is null ? "*" : ClassKey(target.Class));
    internal static RoutingConflict[] Conflicts(TeamDefinition team, ClientBinding[]? clients = null)
    {
        var active = clients?.Select(client => client.PersonaId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<RoutingConflict>();
        foreach (var button in team.Buttons)
            foreach (var persona in team.Personas.Where(persona => active is null || active.Contains(persona.Id)))
            {
                var winning = WinningRules(button, persona);
                if (winning.Select(entry => SelectorIdentity(entry.Rule.Target)).Distinct(StringComparer.Ordinal).Count() <= 1) continue;
                string name = team.Characters.Single(character => Equal(character.Id, persona.CharacterId)).Name;
                string recipient = persona.Class is null ? name : name + " · " + persona.Class;
                conflicts.Add(new(button.Id, persona.Id, winning.Select(entry => entry.Index).ToArray(),
                    $"Conflicting selectors for {recipient}. Keep one recipient selector for this sequence. This button is disabled while that persona is connected."));
            }
        return conflicts.ToArray();
    }
    internal static RoutedTap[] Resolve(TeamDefinition team, string buttonId, ClientBinding[] clients)
    {
        var button = team.Buttons.Single(b => Equal(b.Id, buttonId));
        // Do not send a partial fan-out or fall back to a less specific route.
        if (Conflicts(team, clients).Any(conflict => Equal(conflict.ButtonId, buttonId))) return [];
        return clients.SelectMany(client => ResolveOne(team, button, client)).ToArray();
    }
    internal static ButtonMapping DisplayButton(TeamDefinition team, string buttonId, ClientBinding[] clients)
    {
        var button = team.Buttons.Single(button => Equal(button.Id, buttonId));
        var conflicts = Conflicts(team, clients).Where(conflict => Equal(conflict.ButtonId, buttonId)).ToArray();
        return new(button.Id, button.Label,
            Resolve(team, button.Id, clients).Select(tap => new TargetMapping(tap.Label, tap.Key))
                .DefaultIfEmpty(new("No matching clients", "—")).ToArray(),
            conflicts.Length == 0 ? null : string.Join("\n", conflicts.Select(conflict => conflict.Message)));
    }
    internal static BarProfile Display(LiveSetup live) => new(1, live.Team.Buttons.Select(button => DisplayButton(live.Team, button.Id, live.Clients)).ToArray());
#if !TEAMBAR_PUBLIC_RELEASE
    internal static void SelfTest()
    {
        InputKeysTests.Run();
        var team = new TeamDefinition(1, [new("runa", "Runa"), new("other", "Other")],
            [new("runa-cleric", "runa", "Cleric"), new("runa-sk", "runa", "Shadowknight"), new("other-cleric", "other", "Cleric")],
            [new("test", "Test", [new(new(Class: "Cleric"), "1"), new(new(CharacterId: "runa"), "2"), new(new(PersonaId: "runa-cleric"), "5")])]);
        ClientBinding[] clients = [new("runa-cleric", 11, 1), new("other-cleric", 22, 1)];
        Validate(team, clients); var a = Resolve(team, "test", clients);
        Check(a.Length == 2 && a[0].Key == "5" && a[1].Key == "1", "Persona override / class fan-out failed.");
        clients[0] = new("runa-sk", 11, 1); Validate(team, clients); a = Resolve(team, "test", clients);
        Check(a[0].Key == "2" && a[0].PersonaId == "runa-sk", "Persona switch / character fallback failed.");
        var ordered = team with { Buttons = [new("test", "Test", [new(new(Class: "Cleric"), "1"),
            new(new(CharacterId: "runa"), "Alt+2"), new(new(PersonaId: "runa-cleric"), "Ctrl+3"),
            new(new(CharacterId: "runa"), "Shift+4"), new(new(PersonaId: "runa-cleric"), "Ctrl+3")])] };
        Check(Resolve(ordered, "test", [new("runa-cleric", 11, 1)]).Select(t => t.Key).SequenceEqual(["Ctrl+3", "Ctrl+3"]),
            "Persona-specific sequences must replace general character/class rows and retain repeated keys.");
        Check(Resolve(ordered, "test", [new("runa-sk", 11, 1)]).Select(t => t.Key).SequenceEqual(["Alt+2", "Shift+4"]),
            "Character fallback sequences must preserve row order across a persona switch.");
        var conflict = team with { Buttons = [new("test", "Test", [new(new(Class: "Cleric"), "1"), new(new(Class: "Cleric"), "2")])] };
        Validate(conflict, clients);
        Check(Resolve(conflict, "test", clients).Select(t => t.Key).SequenceEqual(["1", "2"]), "Repeated recipient mappings must retain their configured order.");
        bool rejected = false; try { Validate(team, [new("runa-cleric", 11, 1), new("runa-sk", 22, 1)]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "One character cannot have two active personas.");
    }
#endif
}
