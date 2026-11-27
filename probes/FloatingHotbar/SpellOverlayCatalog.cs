using System.Text.RegularExpressions;
namespace TeamBar;

internal sealed record TriggeredSpellFamily(string Name, int[] SpellIds, int Icon);
internal sealed record TriggeredSpellPlan(TriggeredSpellFamily[] Children, string? Error = null);

internal sealed class SpellOverlayCatalog
{
    private readonly SpellNames source;
    private readonly Dictionary<int, LocalSpellRecord[]> groups;
    private readonly Dictionary<string, LocalSpellRecord[]> ungroupedNames;
    private readonly Dictionary<int, TriggeredSpellPlan> plans = [];
    private readonly Dictionary<int, int[]> rankFamilies = [];
    private static readonly TriggeredSpellPlan Ordinary = new([]);
    private const string NumberedBleedPrefix = "Languid Bite Bleed ";
    private static readonly Regex RomanRank = new(@"\AM{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal SpellOverlayCatalog(SpellNames source)
    {
        this.source = source;
        groups = (source.Records?.Values ?? []).Where(s => s.Group > 0).GroupBy(s => s.Group)
            .ToDictionary(g => g.Key, g => g.ToArray());
        ungroupedNames = (source.Records?.Values ?? []).Where(s => s.Group == 0)
            .GroupBy(s => RankName(s.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    internal bool SameRankFamily(int firstSpellId, int secondSpellId) =>
        firstSpellId == secondSpellId || MatchingSpellIds(firstSpellId).Contains(secondSpellId);

    internal int[] MatchingSpellIds(int spellId)
    {
        if (rankFamilies.TryGetValue(spellId, out var cached)) return cached;
        int[] Exact() => rankFamilies[spellId] = [spellId];
        if (source.Error is not null || source.Records is null || !source.Records.TryGetValue(spellId, out var spell)) return Exact();
        bool numberedBleed = NumberedBleedRank(spell.Name) > 0;
        // This effect numbers its upgrades I, II, ... XXVIII rather than Rk. I–III.
        // Require its installed group/rank metadata; other numbered spell lines
        // and editable display labels must not establish equivalence.
        if (numberedBleed && spell.Group <= 0) return Exact();
        string name = RankName(spell.Name);
        var members = spell.Group > 0 ? groups.GetValueOrDefault(spell.Group) : ungroupedNames.GetValueOrDefault(name);
        // Group IDs can span expansion lines or include cast-on-fade effects.
        // Only a terminal rank suffix can differ; editable/observed labels are
        // never evidence of equivalence. Groupless effects additionally rely on
        // a unique installed name for every variant in the same effect scope.
        var ranks = (members ?? []).Where(s => s.Group == spell.Group
            && (numberedBleed ? NumberedBleedRank(s.Name) > 0
                : NumberedBleedRank(s.Name) == 0 && RankName(s.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
            && s.Target == spell.Target && s.PlayerSpell == spell.PlayerSpell
            && (s.Duration > 0) == (spell.Duration > 0)).ToArray();
        if (ranks.Length < 1 || !numberedBleed && ranks.Length > 4
            || ranks.Any(s => numberedBleed ? s.Rank != NumberedBleedRank(s.Name) : s.Rank != 0 && s.Rank != NameRank(s.Name))
            || ranks.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ranks.Length)
            return Exact();
        int[] family = ranks.OrderBy(s => numberedBleed ? NumberedBleedRank(s.Name) : NameRank(s.Name)).ThenBy(s => s.Id).Select(s => s.Id).ToArray();
        foreach (int member in family) rankFamilies[member] = family;
        return family;
    }
    internal TriggeredSpellPlan Resolve(int spellId)
    {
        if (plans.TryGetValue(spellId, out var cached)) return cached;
        var plan = Build(spellId); plans[spellId] = plan; return plan;
    }
    private TriggeredSpellPlan Build(int spellId)
    {
        if (source.Error is not null || source.Records is null || !source.Records.TryGetValue(spellId, out var parent))
            return new([], "Spell data unavailable");
        // Persistent buffs containing triggers should still be tracked as buffs themselves.
        var triggers = parent.Effects.Where(e => e.Attribute == 470 && e.Base == 100).OrderBy(e => e.Slot).ToArray();
        if (triggers.Length == 0 || parent.Duration > 1) return Ordinary;
        // Only verified direct, single-target, guaranteed group casts are requirements. Never recurse into procs.
        if (parent.Target != 5 || triggers.Length > 12 || triggers.Any(e => e.Limit <= 0 || e.Maximum != 0 || e.Formula != 100)
            || parent.Effects.Any(e => e.Attribute != 470 && e.Attribute != 374 && !(e.Attribute == 10 && e.Base == 0)))
            return new([], "Triggered effects need review");
        var children = new List<TriggeredSpellFamily>();
        foreach (int group in triggers.Select(e => e.Limit).Distinct())
        {
            if (!groups.TryGetValue(group, out var members)) return new([], "Triggered spell group unavailable");
            // Some rank groups also contain their cast-on-fade spells (e.g. Nectar -> Vengeance).
            var followups = members.SelectMany(s => s.Effects).Where(e => e.Attribute == 289).Select(e => e.Base).ToHashSet();
            var ranks = members.Where(s => !followups.Contains(s.Id) && s.PlayerSpell && s.Duration > 0 && s.Target == 5)
                .OrderBy(s => s.Rank).ThenBy(s => s.Id).ToArray();
            if (ranks.Length == 0 || ranks.Length > 4 || ranks.Any(s => s.Rank is not (1 or 5 or 10) || s.Rank != NameRank(s.Name)
                || s.Effects.Any(e => e.Attribute is 470 or 469)) || ranks.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ranks.Length
                || ranks.Select(s => RankName(s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                return new([], "Triggered spell group needs review");
            children.Add(new(RankName(ranks[0].Name), ranks.Select(s => s.Id).ToArray(), ranks[0].Icon));
        }
        return new(children.ToArray());
    }
    private static string RankName(string name)
    {
        foreach (string suffix in new[] { " Rk. III", " Rk. II", " Rk. I" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }
    private static int NumberedBleedRank(string name)
    {
        if (!name.StartsWith(NumberedBleedPrefix, StringComparison.OrdinalIgnoreCase)) return 0;
        string numeral = name[NumberedBleedPrefix.Length..];
        if (numeral.Length == 0 || !RomanRank.IsMatch(numeral)) return 0;
        int total = 0, previous = 0;
        for (int i = numeral.Length - 1; i >= 0; i--)
        {
            int value = char.ToUpperInvariant(numeral[i]) switch
            { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
            total += value < previous ? -value : value;
            previous = value;
        }
        return total;
    }
    private static int NameRank(string name) => name.EndsWith(" Rk. III", StringComparison.OrdinalIgnoreCase) ? 10
        : name.EndsWith(" Rk. II", StringComparison.OrdinalIgnoreCase) ? 5 : 1;
}
