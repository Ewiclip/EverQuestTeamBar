namespace TeamBar;

internal static class SpellOverlayMigration
{
    internal static SpellOverlayLibrary Upgrade(SpellOverlayLibrary library)
    {
        SpellOverlayStore.Validate(library);
        if (library.Version >= 2) return library;
        var overlays = new List<SpellOverlayDefinition>();
        var usedIds = library.Overlays.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in library.Overlays)
        {
            // Unrestricted legacy spells belonged to every persona. Copy them into
            // each class bar instead of silently narrowing their previous behavior.
            var classes = old.PersonaClass is { } assigned ? [assigned] : Identity.Classes.Where(name =>
                old.Spells.Length == 0 || old.Spells.Any(s => s.PersonaClasses is null
                    || s.PersonaClasses.Contains(name, StringComparer.OrdinalIgnoreCase))).ToArray();
            for (int i = 0; i < classes.Length; i++)
            {
                string name = classes[i], id = old.Id;
                if (i > 0)
                    do { id = Guid.NewGuid().ToString("N"); } while (!usedIds.Add(id));
                string label = string.IsNullOrWhiteSpace(old.Name) ? "" : old.Name == "Target spells" ? name + " Bar"
                    : classes.Length > 1 ? old.Name[..Math.Min(old.Name.Length, 60 - name.Length - 3)] + " · " + name : old.Name;
                overlays.Add(old with { Id = id, Name = label, PersonaClass = name,
                    Spells = old.Spells.Where(s => s.PersonaClasses is null || s.PersonaClasses.Contains(name, StringComparer.OrdinalIgnoreCase))
                        .Select(s => s with { PersonaClasses = null }).ToArray() });
            }
        }
        var upgraded = new SpellOverlayLibrary(2, overlays.ToArray());
        SpellOverlayStore.Validate(upgraded);
        return upgraded;
    }

    internal static byte[] Save(string path, SpellOverlayLibrary upgraded, byte[] original)
    {
        // Keep the exact original file for recovery; the normal optimistic save
        // still refuses to overwrite another editor's changes.
        string backup = path + ".before-character-class-bars.json";
        if (!File.Exists(backup))
        {
            using var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write);
            file.Write(original); file.Flush(true);
        }
        return SpellOverlayStore.Save(path, upgraded, original);
    }
}
