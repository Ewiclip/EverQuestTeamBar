using System.Security.Cryptography;
namespace TeamBar;

internal enum SpellDockSide { Left, Right, Top, Bottom }
internal sealed record SpellDockGroup(string Id, string Name, SpellDockSide Side, string[] ButtonIds, double Scale = 1, int Columns = 1,
    ButtonPlacement[]? Placements = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ColumnNames { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string EditorName => string.IsNullOrWhiteSpace(Name) ? $"{Side} group · {Id[^Math.Min(6, Id.Length)..]}" : Name;
    internal ButtonPlacement[] Layout => Placements ?? ButtonColumns.Legacy(ButtonIds, Columns);
    internal SpellDockGroup WithLayout(ButtonPlacement[] entries) => this with { ButtonIds = entries.Select(e => e.ButtonId).ToArray(), Placements = entries };
}
internal sealed record SpellDockButton(string Id, string Label, string Revision, string Recipients, bool Available, string Hint, bool Visible = true, bool HasConflict = false);
internal sealed class InputRosterChangedException(string message) : IOException(message);
internal static class SpellDockRules
{
    internal static SavedButton[] LoadButtons()
    {
        return StandaloneButtons.Load(TeamBarApplication.DefaultProfile());
    }
    internal static void Validate(SpellDockGroup[]? groups)
    {
        if (groups is null) return;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || group.Id.Length > 64 || !ids.Add(group.Id)
                || group.Name is null or { Length: > 40 } || !Enum.IsDefined(group.Side)
                || !double.IsFinite(group.Scale) || group.Scale is < .5 or > 2 || group.Columns is < 1 or > ButtonColumns.MaxColumns
                || group.ButtonIds is null
                || group.ButtonIds.Any(id => id is null || id.Length is < 1 or > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                || group.ButtonIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.ButtonIds.Length)
                throw new InvalidDataException("Each docked group needs a unique ID, an edge, a size from 50–200%, and distinct saved buttons. Its optional name can be up to 40 characters.");
            ButtonColumns.Validate(group.Layout, group.Columns);
            ButtonColumns.ValidateNames(group.ColumnNames, group.Columns);
            if (!group.Layout.Select(e => e.ButtonId).Order().SequenceEqual(group.ButtonIds.Order()))
                throw new InvalidDataException("The group's column layout must reference exactly its saved buttons.");
        }
    }
    internal static string Revision(SavedButton button) => Convert.ToHexString(SHA256.HashData(button.Bytes));
    internal static string Recipients(RoutedTap[] taps, DetectedClient[] clients) => string.Join("|", taps.OrderBy(t => t.ProcessId)
        .Select(t => $"{t.ProcessId}:{clients.Single(c => c.ProcessId == t.ProcessId).StartedUtcTicks}:{clients.Single(c => c.ProcessId == t.ProcessId).Character.Class}:{t.Key}"));
    internal static SpellDockButton Describe(string id, SavedButton[] buttons, DetectedClient[] clients)
    {
        var saved = buttons.SingleOrDefault(b => b.Value.Id == id);
        if (saved is null) return new(id, "Missing button", "", "", false, "This saved standalone button is unavailable. Choose another in settings.");
        try
        {
            bool visible = StandaloneButtons.Visible(saved.Value, clients);
            var conflicts = StandaloneButtons.Conflicts(saved.Value, clients);
            if (conflicts.Length > 0) return new(id, saved.Value.Label, Revision(saved), "", false,
                string.Join("\n", conflicts.Select(conflict => conflict.Message)), visible, HasConflict: true);
            var taps = StandaloneButtons.Resolve(saved.Value, clients);
            return new(id, saved.Value.Label, Revision(saved), Recipients(taps, clients), visible && taps.Length > 0,
                !visible ? "Not available for the current personas" : taps.Length == 0 ? "No matching characters connected"
                : string.Join(" · ", taps.Select(t => $"{t.Label}: {t.Key}")), visible);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        { return new(id, saved.Value.Label, Revision(saved), "", false, error.Message); }
    }
    internal static DetectedClient[] RecipientsFor(SpellOverlayDefinition[] definitions, SavedButton[] buttons, DetectedClient[] clients)
    {
        var ids = definitions.SelectMany(d => d.DockGroups ?? []).SelectMany(g => g.ButtonIds).ToHashSet();
        var pids = buttons.Where(b => ids.Contains(b.Value.Id)).SelectMany(b => StandaloneButtons.Resolve(b.Value, clients))
            .Select(t => t.ProcessId).ToHashSet();
        return clients.Where(c => pids.Contains(c.ProcessId)).ToArray();
    }
    internal static RoutedTap[] Resolve(SpellOverlayLibrary library, SavedButton[] buttons, SpellClickRequest request,
        DetectedClient[] clients, Func<int, long?, TargetCapture> readTarget)
    {
        var definition = library.Overlays.SingleOrDefault(d => d.Id == request.OverlayId);
        if (definition is null || !definition.Enabled || !definition.Locked) throw new InvalidDataException("Save and lock this overlay before using its buttons.");
        var group = definition.DockGroups?.SingleOrDefault(g => g.Id == request.GroupId);
        if (request.ButtonId is null || group?.ButtonIds.Contains(request.ButtonId) != true) throw new InvalidDataException("This button group changed. Wait for the overlay to update.");
        var owner = clients.SingleOrDefault(c => c.Character.Name.Equals(definition.Character, StringComparison.OrdinalIgnoreCase));
        if (owner is null || !definition.MatchesPersona(owner.Character.Class) || owner.ProcessId != request.ProcessId
            || owner.StartedUtcTicks != request.Started || owner.Character.Class != request.Class)
            throw new InputRosterChangedException("The overlay's character or persona changed. Refreshing characters; click again when ready.");
        var current = readTarget(owner.ProcessId, owner.StartedUtcTicks);
        if (current.ProcessId != owner.ProcessId || current.ProcessStartedUtcTicks != owner.StartedUtcTicks || current.CharacterName != owner.Character.Name
            || current.CharacterClass != owner.Character.Class || current.Zone.Id != request.ZoneId
            || (current.Target?.SpawnId ?? 0) != request.TargetId || current.Target?.Name != request.TargetName)
            throw new InvalidDataException("Character, zone or target changed. Click again after the overlay updates.");
        var saved = buttons.SingleOrDefault(b => b.Value.Id == request.ButtonId);
        if (saved is null || Revision(saved) != request.ButtonRevision || !StandaloneButtons.Visible(saved.Value, clients))
            throw new InvalidDataException("The saved button or its persona visibility changed. Refresh the overlay before clicking.");
        var conflicts = StandaloneButtons.Conflicts(saved.Value, clients);
        if (conflicts.Length > 0) throw new InvalidDataException(string.Join("\n", conflicts.Select(conflict => conflict.Message)));
        var taps = StandaloneButtons.Resolve(saved.Value, clients);
        if (taps.Length == 0 || Recipients(taps, clients) != request.Recipients)
            throw new InputRosterChangedException("Button recipients changed or are not connected. Refreshing characters; click again when ready.");
        return taps;
    }
}
