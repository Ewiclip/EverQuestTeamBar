namespace TeamBar;

internal static class SpellOverlayClickRules
{
    internal static RoutedTap Resolve(SpellOverlayLibrary library, SpellClickRequest request, DetectedClient[] clients,
        Func<int, long?, TargetCapture> readTarget)
    {
        var definition = library.Overlays.SingleOrDefault(d => d.Id == request.OverlayId);
        if (definition is null || !definition.Enabled || !definition.Locked) throw new InvalidDataException("Save and lock this overlay before casting.");
        var rule = definition.Spells.SingleOrDefault(s => s.SpellId == request.SpellId && s.Target == request.Target);
        if (rule?.ClickKey is null || rule.ClickKey != request.Key) throw new InvalidDataException("Spell key changed. Wait for the overlay to update, then click again.");
        var matches = clients.Where(c => c.Character.Name.Equals(definition.Character, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The configured character is not uniquely connected.");
        var client = matches[0];
        if (!definition.MatchesPersona(client.Character.Class))
            throw new InvalidDataException("This bar belongs to a different persona. Wait for the matching bar.");
        if (client.ProcessId != request.ProcessId || client.StartedUtcTicks != request.Started || client.Character.Class != request.Class)
            throw new InvalidDataException("Character or persona changed. Wait for the overlay to update, then click again.");
        if (rule.PersonaClasses is not null && !rule.PersonaClasses.Contains(client.Character.Class, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("This spell is not configured for the current persona.");
        var current = readTarget(client.ProcessId, client.StartedUtcTicks);
        if (current.ProcessId != client.ProcessId || current.ProcessStartedUtcTicks != client.StartedUtcTicks
            || current.CharacterName != client.Character.Name || current.CharacterClass != client.Character.Class
            || current.Target is not { RawType: 0 or 1 } target || target.SpawnId != request.TargetId || target.Name != request.TargetName
            || current.Zone.Id != request.ZoneId || (rule.Target != SpellTargetKind.Either && target.RawType != (rule.Target == SpellTargetKind.Player ? 0 : 1)))
            throw new InvalidDataException("Target changed. Wait for the overlay to update, then click again.");
        var chord = SpellOverlayKey.Parse(rule.ClickKey);
        return new(client.ProcessId, "spell-overlay", client.Character.Name, rule.ClickKey, chord.Scan, chord.Modifiers);
    }
}
