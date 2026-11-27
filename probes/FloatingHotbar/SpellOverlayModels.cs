using System.Text.Json;
using System.Text.Json.Serialization;
namespace TeamBar;

internal enum SpellTargetKind { Npc, Player, Either }
internal enum SpellCasterKind { Mine, Anyone }
internal sealed record SpellAlternative(int SpellId, string Name, int? IconId = null);
internal sealed record OverlayDesktopPosition(int X, int Y);
internal sealed record SpellWatch(int SpellId, string Name, SpellTargetKind Target = SpellTargetKind.Npc,
    SpellCasterKind Caster = SpellCasterKind.Mine, bool ShowMissing = true, int? IconId = null, bool ExpandTriggered = true,
    string[]? PersonaClasses = null, string? ClickKey = null, SpellAlternative[]? Alternatives = null, string? Alias = null)
{
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias.Trim();
}
internal sealed record SpellOverlayDefinition(string Id, string Name, string Character, SpellWatch[] Spells,
    bool Enabled = true, double X = .75, double Y = .2, int Width = 390, double Opacity = .94,
    ScreenPosition[]? Screens = null, bool Locked = true, double Scale = 1, string? PersonaClass = null, SpellDockGroup[]? DockGroups = null,
    bool FollowCharacterWindow = true, OverlayDesktopPosition? FreePosition = null)
{
    public string CharacterPersona => PersonaClass is null ? Character : $"{Character} · {PersonaClass}";
    [JsonIgnore] public string EditorName => string.IsNullOrWhiteSpace(Name) ? $"Unnamed bar · {Id[^Math.Min(6, Id.Length)..]}" : Name;
    internal bool MatchesPersona(string? value) => PersonaClass is null || string.Equals(PersonaClass, value, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] internal bool HasClickMappings => Spells.Any(s => s.ClickKey is not null) || DockGroups?.Any(g => g.ButtonIds.Length > 0) == true;
}
internal sealed record SpellOverlayLibrary(int Version, SpellOverlayDefinition[] Overlays);
internal sealed record WatchedSpellRow(int Priority, SpellWatch Rule, string State, int? RemainingMilliseconds, int? IconId,
    WatchedSpellRow[]? Children = null, string? CasterName = null, string? MatchedSpellName = null)
{
    public int? DisplayPriority { get; init; }
    public string Name => Rule.DisplayName;
    public bool Missing => State == "Missing" || Children?.Any(c => c.Missing) == true;
    public string Time => Children is { } children ? children.Any(c => c.Missing) ? $"{children.Count(c => c.Missing)} missing"
        : children.Any(c => c.State != "Active") ? "Check caster" : $"{children.Length} active"
        : RemainingMilliseconds is not { } ms ? State : ms < 0 ? "No timer"
        : ms >= 3600000 ? $"{ms / 3600000}h {ms / 60000 % 60:00}m {ms / 1000 % 60:00}s" : $"{ms / 60000}:{ms / 1000 % 60:00}";
}
internal sealed record SpellOverlayState(string Target, string Message, WatchedSpellRow[] Rows);

internal static class SpellOverlayRules
{
    internal static SpellOverlayState Evaluate(SpellOverlayDefinition definition, TargetCapture? capture, string? error = null,
        SpellOverlayCatalog? catalog = null)
    {
        if (error is not null) return new("Reading unavailable", error, []);
        if (capture is null) return new("Waiting for character", "Waiting for a verified reading", []);
        if (!string.Equals(capture.CharacterName, definition.Character, StringComparison.OrdinalIgnoreCase))
            return new("Character changed", "Waiting for the configured character", []);
        if (!definition.MatchesPersona(capture.CharacterClass))
            return new("Persona changed", "Waiting for the configured class", []);
        if (capture.Target is null) return new("No target", "Select a target in EverQuest", []);
        if (!MyTargetSpells.Matches(capture)) return new(capture.Target.Name, "Waiting for target spell data", []);
        var personaRules = definition.Spells.Select((r, i) => (Rule: r, Priority: i + 1)).Where(r =>
            r.Rule.PersonaClasses is null || r.Rule.PersonaClasses.Contains(capture.CharacterClass, StringComparer.OrdinalIgnoreCase)).ToArray();
        var rules = personaRules.Where(r => capture.Target.RawType is 0 or 1 && (r.Rule.Target == SpellTargetKind.Either
            || (r.Rule.Target == SpellTargetKind.Player ? capture.Target.RawType == 0 : capture.Target.RawType == 1))).ToArray();
        var rows = new List<WatchedSpellRow>();
        foreach (var (rule, priority) in rules)
        {
            if (rule.Alternatives is { Length: > 0 } alternatives)
            {
                var options = new[] { new SpellAlternative(rule.SpellId, rule.Name, rule.IconId) }.Concat(alternatives)
                    .Select(option => MatchOption(rule, priority, option)).ToArray();
                // The OR rule is satisfied by any complete option. Prefer the option
                // that remains complete longest, with saved option order breaking ties.
                var result = options.Where(r => r.State == "Active").OrderByDescending(Lifetime).FirstOrDefault()
                    ?? options.FirstOrDefault(r => r.State != "Missing") ?? options[0];
                if (result.State != "Missing" || rule.ShowMissing)
                    rows.Add(result with { Rule = rule, IconId = result.State == "Missing" ? rule.IconId : result.IconId });
                continue;
            }
            var plan = rule.ExpandTriggered ? catalog?.Resolve(rule.SpellId) : null;
            if (plan?.Error is { } problem) { rows.Add(new(priority, rule, problem, null, rule.IconId)); continue; }
            if (plan?.Children.Length > 0)
            {
                var children = plan.Children.Select(child => Match(rule with { SpellId = child.SpellIds[0], Name = child.Name,
                    IconId = child.Icon, Alias = null }, priority, child.SpellIds)).OfType<WatchedSpellRow>().ToArray();
                bool complete = children.Length == plan.Children.Length && children.All(c => c.State == "Active");
                if (children.Length > 0) rows.Add(new(priority, rule, "Group", null, rule.IconId, children,
                    complete ? Casters(children) : null, complete ? Names(children) : null));
            }
            else if (Match(rule, priority, catalog?.MatchingSpellIds(rule.SpellId) ?? [rule.SpellId]) is { } row) rows.Add(row);
        }
        WatchedSpellRow MatchOption(SpellWatch rule, int priority, SpellAlternative option)
        {
            var optionRule = rule with { SpellId = option.SpellId, Name = option.Name, IconId = option.IconId,
                Alternatives = null, ShowMissing = true, Alias = null };
            if (rule.ExpandTriggered && catalog is null)
                return new(priority, optionRule, "Spell data unavailable", null, option.IconId);
            var plan = rule.ExpandTriggered ? catalog?.Resolve(option.SpellId) : null;
            if (plan?.Error is { } problem) return new(priority, optionRule, problem, null, option.IconId);
            if (plan?.Children.Length > 0)
            {
                // Missing children must participate even when the top-level rule hides
                // missing rows; one present child cannot satisfy a compound option.
                var children = plan.Children.Select(child => MatchPresentOrMissing(optionRule with {
                    SpellId = child.SpellIds[0], Name = child.Name, IconId = child.Icon }, priority, child.SpellIds)).ToArray();
                if (children.All(c => c.State == "Active"))
                    return new(priority, optionRule, "Active", children.OrderBy(Lifetime).First().RemainingMilliseconds,
                        option.IconId, CasterName: Casters(children), MatchedSpellName: Names(children));
                var unresolved = children.FirstOrDefault(c => c.State is not ("Active" or "Missing"));
                return new(priority, optionRule, unresolved?.State ?? "Missing", null, option.IconId);
            }
            return MatchPresentOrMissing(optionRule, priority, catalog?.MatchingSpellIds(option.SpellId) ?? [option.SpellId]);
        }
        WatchedSpellRow? Match(SpellWatch rule, int priority, int[] spellIds)
        {
            var row = MatchPresentOrMissing(rule, priority, spellIds);
            return row.State == "Missing" && !rule.ShowMissing ? null : row;
        }
        WatchedSpellRow MatchPresentOrMissing(SpellWatch rule, int priority, int[] spellIds)
        {
            var candidates = capture.Buffs!.Effects.Where(e => spellIds.Contains(e.SpellId)).ToArray();
            var matching = candidates.Where(e => rule.Caster == SpellCasterKind.Anyone
                || string.Equals(e.CasterName, capture.CharacterName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.RemainingMilliseconds < 0 ? int.MaxValue : e.RemainingMilliseconds).FirstOrDefault();
            if (matching is not null) return new(priority, rule, "Active", matching.RemainingMilliseconds, matching.IconId ?? rule.IconId,
                CasterName: Known(matching.CasterName), MatchedSpellName: Known(matching.SpellName) ?? rule.Name);
            var unknown = candidates.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.CasterName));
            if (rule.Caster == SpellCasterKind.Mine && unknown is not null)
                return new(priority, rule, "Caster unknown", null, unknown.IconId ?? rule.IconId,
                    MatchedSpellName: Known(unknown.SpellName) ?? rule.Name);
            return new(priority, rule, "Missing", null, rule.IconId);
        }
        return new(capture.Target.Name, personaRules.Length == 0 ? $"No watched spells for your {capture.CharacterClass} persona"
            : rules.Length == 0 ? "No watched spells for this target type"
            : rows.Count == 0 ? "No watched spells currently present" : "Your configured spell order",
            rows.Select(row => row with { DisplayPriority = Array.FindIndex(rules, r => r.Priority == row.Priority) + 1 }).ToArray());
    }
    private static string? Known(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static long Lifetime(WatchedSpellRow row) => row.RemainingMilliseconds is { } ms ? ms < 0 ? long.MaxValue : ms : 0;
    private static string? Casters(WatchedSpellRow[] rows) => rows.Any(r => r.CasterName is null) ? null
        : string.Join(", ", rows.Select(r => r.CasterName).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string Names(WatchedSpellRow[] rows) =>
        string.Join(" + ", rows.Select(r => r.MatchedSpellName ?? r.Name).Distinct(StringComparer.OrdinalIgnoreCase));
}

internal static class SpellOverlayStore
{
    internal const int MaxAlternatives = 12;
    internal const int MaxOverlays = 128;
    internal static string DefaultPath => Path.Combine(Path.GetDirectoryName(LocalUserSettingsStore.DefaultPath)!, "spell-overlays.json");
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    internal static void Validate(SpellOverlayLibrary library)
    {
        if (library.Version is not (1 or 2 or 3) || library.Overlays is null || library.Overlays.Length > MaxOverlays)
            throw new InvalidDataException($"Use up to {MaxOverlays} spell bars.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in library.Overlays)
        {
            if (overlay is null || string.IsNullOrWhiteSpace(overlay.Id) || overlay.Id.Length > 64 || !ids.Add(overlay.Id)
                || overlay.Name is null or { Length: > 60 } || string.IsNullOrWhiteSpace(overlay.Character)
                || overlay.Character.Length > 30 || overlay.Spells is null || overlay.Spells.Length > 24
                || !ValidPosition(overlay.X, overlay.Y) || overlay.Width is < 260 or > 700
                || !double.IsFinite(overlay.Opacity) || overlay.Opacity is < .35 or > 1
                || !double.IsFinite(overlay.Scale) || overlay.Scale is < .5 or > 2)
                throw new InvalidDataException("Each overlay needs a character, up to 24 spells, and valid placement. Its optional name can be up to 60 characters.");
            if ((library.Version >= 2 || overlay.PersonaClass is not null)
                && !Identity.Classes.Contains(overlay.PersonaClass, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a class for each spell bar.");
            var spells = new HashSet<(int, SpellTargetKind)>();
            SpellDockRules.Validate(overlay.DockGroups);
            foreach (var rule in overlay.Spells)
            {
                if (library.Version >= 2 && rule?.PersonaClasses is not null)
                    throw new InvalidDataException("Class belongs to the spell bar, not individual spells.");
                if (rule is null || rule.SpellId is < 1 or > 1000000 || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 255
                    || !Enum.IsDefined(rule.Target) || !Enum.IsDefined(rule.Caster) || rule.IconId is < 0 or >= 9216
                    || rule.PersonaClasses is { } classes && (classes.Length is < 1 or > 16
                        || classes.Any(c => !Identity.Classes.Contains(c, StringComparer.OrdinalIgnoreCase))
                        || classes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != classes.Length)
                    || !spells.Add((rule.SpellId, rule.Target))) throw new InvalidDataException("Choose valid spells and at least one class for persona-specific rules; avoid duplicate spell/target entries.");
                if (rule.ClickKey is not null) SpellOverlayKey.Parse(rule.ClickKey);
                if (rule.Alias is { } alias && (alias.Length > 80 || alias.Any(char.IsControl)))
                    throw new InvalidDataException("Spell aliases may contain up to 80 characters on one line.");
                if (rule.Alternatives is { } alternatives)
                {
                    var optionIds = new HashSet<int> { rule.SpellId };
                    if (alternatives.Length > MaxAlternatives || alternatives.Any(option => option is null
                        || option.SpellId is < 1 or > 1000000 || string.IsNullOrWhiteSpace(option.Name) || option.Name.Length > 255
                        || option.IconId is < 0 or >= 9216 || !optionIds.Add(option.SpellId)))
                        throw new InvalidDataException($"Choose up to {MaxAlternatives} valid alternatives without duplicate spell IDs in a watched rule.");
                }
            }
            if (overlay.Screens is { } screens && (screens.Length > 64 || screens.Any(s => s is null || string.IsNullOrWhiteSpace(s.ScreenId)
                || s.ScreenId.Length > 512 || s.Position is null || !ValidPosition(s.Position.X, s.Position.Y))
                || screens.Select(s => s.ScreenId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != screens.Length))
                throw new InvalidDataException("Invalid monitor placement.");
            if (overlay.FreePosition is { } desktop && (desktop.X is < -100000 or > 100000 || desktop.Y is < -100000 or > 100000))
                throw new InvalidDataException("Invalid desktop placement.");
        }
    }
    private static bool ValidPosition(double x, double y) => double.IsFinite(x) && double.IsFinite(y) && x is >= 0 and <= 1 && y is >= 0 and <= 1;
    internal static (SpellOverlayLibrary Library, byte[]? Bytes) Load(string path)
    {
        if (!File.Exists(path)) return (new(1, []), null);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Overlay library is too large.");
        var bytes = File.ReadAllBytes(path);
        var library = JsonSerializer.Deserialize<SpellOverlayLibrary>(bytes, Json) ?? throw new InvalidDataException("Empty overlay library.");
        Validate(library); return (library, bytes);
    }
    internal static byte[] Save(string path, SpellOverlayLibrary library, byte[]? expected)
    {
        return WithEditLock(path, expected, () => WriteLocked(path, library));
    }
    internal static T WithEditLock<T>(string path, byte[]? expected, Func<T> edit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var actual = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (expected is null ? actual is not null : actual is null || !expected.SequenceEqual(actual))
            throw new IOException("The spell overlay changed in another editor. Reopen it before saving.");
        return edit();
    }
    internal static byte[] WriteLocked(string path, SpellOverlayLibrary library)
    {
        Validate(library);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library, Json);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew)) { stream.Write(bytes); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return bytes;
    }
}
