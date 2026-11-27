using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamBar;

internal sealed record ContributionSource(string Client, string ClientChannel, string? Version = null,
    string? BuildSha256 = null, string? Decoder = null);
internal sealed record ContributionRecord(string Kind, int EntityId, string Fingerprint, JsonElement Payload, ContributionSource Source);
internal sealed record ContributionBatch(ContributionRecord[] Records, byte[] Body);
internal sealed record ContributionRejection(string Kind, int? EntityId, string Reason);

// This is deliberately a separate wire model. Adding a property to a capture or
// a game decoder cannot silently add character or process data to an upload.
internal static class ContributionPayload
{
    internal const int MaxRecords = 100, MaxBodyBytes = 2 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private sealed record ItemWire(int ItemId, string Name, CandidateWire CandidateDetails, ItemSpellReference[]? Effects = null);
    private sealed record CandidateWire(string LoreName, int IconId, uint EquipSlotsMask, uint ClassesMask, uint RacesMask,
        int RequiredLevel, int RecommendedLevel, bool Tradeskill, bool QuestItem, bool Droppable, bool Attuneable,
        bool Heirloom, int LoreGroup, int ArmorClass, int HitPoints, int Mana, int Endurance, int Damage,
        int Delay, int AugmentationType, int StackSize);
    private sealed record SpellWire(int SpellId, string Name, string RawLine, string? Detail = null);
    private sealed record IngredientWire(int Slot, int ItemId, int IconId, string? Name);
    private sealed record RecipeWire(int RecipeId, string Name, int Trivial, int ContainerType,
        int ContainerItemId, int RequiredSize, bool DetailsLoaded, IngredientWire[] IngredientSlots);

    // Preserve the existing Live queue identity, including acknowledged records.
    // A Beta patch is independent evidence even when its definition is unchanged.
    internal static string SourceScope(ContributionSource source)
    {
        source = NormalizeSource(source);
        return source.ClientChannel == "Live" ? "" : $"Beta:{source.BuildSha256}:";
    }
    internal static string RecordKey(ContributionRecord record) =>
        $"{SourceScope(record.Source)}{record.Kind}:{record.EntityId}:{record.Fingerprint}";

    internal static ContributionRecord[] Recipes(IEnumerable<LoadedRecipe> recipes, ContributionSource source,
        Action<ContributionRejection>? rejected = null)
    {
        source = NormalizeSource(source);
        var result = new List<ContributionRecord>();
        foreach (var recipe in recipes)
        {
            try
            {
                if (recipe is null || recipe.RecipeId is < 1 or > 10_000_000 || recipe.Trivial is < 0 or > 2000
                    || recipe.ContainerType is < 0 or > 1000 || recipe.ContainerItemId is < 0 or > 10_000_000
                    || recipe.RequiredSize is < 0 or > 100 || recipe.IngredientSlots is null || recipe.IngredientSlots.Length > 10)
                    throw new InvalidDataException("Recipe identity or observed fields are invalid.");
                var slots = new HashSet<int>();
                var ingredients = new List<IngredientWire>();
                foreach (var slot in recipe.IngredientSlots)
                {
                    if (slot is null || slot.Slot is < 1 or > 10 || !slots.Add(slot.Slot)
                        || slot.ItemId is < 0 or > 10_000_000 || slot.IconId is < 0 or > 10_000_000)
                        throw new InvalidDataException("Recipe ingredient slots are invalid.");
                    if (slot.ItemId == 0) continue;
                    if (!recipe.DetailsLoaded) throw new InvalidDataException("A recipe summary cannot claim loaded ingredients.");
                    ingredients.Add(new(slot.Slot, slot.ItemId, slot.IconId,
                        string.IsNullOrEmpty(slot.Name) ? null : Text(slot.Name, 255)));
                }
                result.Add(Record("recipe", recipe.RecipeId, new RecipeWire(recipe.RecipeId, Text(recipe.Name, 255),
                    recipe.Trivial, recipe.ContainerType, recipe.ContainerItemId, recipe.RequiredSize, recipe.DetailsLoaded,
                    ingredients.OrderBy(slot => slot.Slot).ToArray()), source));
            }
            catch (InvalidDataException error) { rejected?.Invoke(new("recipe", recipe?.RecipeId, error.Message)); }
        }
        return result.DistinctBy(record => record.Fingerprint).ToArray();
    }

    internal static ContributionRecord[] Items(IEnumerable<ItemData> items, ContributionSource source,
        Action<ContributionRejection>? rejected = null, IReadOnlyDictionary<int, ItemSpellReference[]>? effects = null)
    {
        source = NormalizeSource(source);
        var result = new List<ContributionRecord>();
        foreach (var item in items)
        {
            try
            {
                if (item is null || item.ItemId < 1 || item.CandidateDetails is null)
                    throw new InvalidDataException("Item identity or candidate definition is missing.");
                var d = item.CandidateDetails;
                ItemSpellReference[]? references = effects?.GetValueOrDefault(item.ItemId);
                if (references is not null)
                {
                    string[] kinds = ["Click", "Proc", "Worn", "Focus", "Scroll", "Focus2", "Blessing"];
                    if (references.Length > 7 || references.Select(e => e.Kind).Distinct().Count() != references.Length
                        || references.Any(e => !kinds.Contains(e.Kind) || e.SpellId is < 1 or > 1_000_000))
                        throw new InvalidDataException("Item spell references are invalid.");
                    references = references.OrderBy(e => Array.IndexOf(kinds, e.Kind)).Select(e => e with { Name = e.Name is null ? null : Text(e.Name, 128) }).ToArray();
                }
                var payload = new ItemWire(item.ItemId, Text(item.Name, 255), new(Text(d.LoreName, 255, empty: true),
                    d.IconId, d.EquipSlotsMask, d.ClassesMask, d.RacesMask, d.RequiredLevel, d.RecommendedLevel,
                    d.Tradeskill, d.QuestItem, d.Droppable, d.Attuneable, d.Heirloom, d.LoreGroup, d.ArmorClass,
                    d.HitPoints, d.Mana, d.Endurance, d.Damage, d.Delay, d.AugmentationType, d.StackSize), references);
                result.Add(Record("item", item.ItemId, payload, source));
            }
            catch (InvalidDataException error) { rejected?.Invoke(new("item", item?.ItemId, error.Message)); }
        }
        return result.DistinctBy(record => record.Fingerprint).ToArray();
    }

    internal static ContributionRecord[] Spells(IEnumerable<int> observedIds, SpellNames spells, ContributionSource source,
        Action<ContributionRejection>? rejected = null, IReadOnlyDictionary<int, string>? descriptions = null)
    {
        source = NormalizeSource(source);
        var result = new List<ContributionRecord>();
        foreach (int id in observedIds.Distinct())
        {
            try
            {
                if (id < 1 || spells.Error is not null || spells.RawLines is null || !spells.RawLines.TryGetValue(id, out var raw))
                    throw new InvalidDataException("A supported installed spell definition is unavailable.");
                raw = Text(raw, 32000, preserve: true);
                var parsed = LocalSpellRecord.Parse(raw);
                if (parsed is null || parsed.Id != id) throw new InvalidDataException("The installed spell definition has an unsupported layout.");
                string name = Text(parsed.Name, 128);
                var fields = raw.Split('^');
                if (fields[0] != id.ToString(CultureInfo.InvariantCulture) || Trim(fields[1]) != name)
                    throw new InvalidDataException("The installed spell identity is not in the supported format.");
                foreach (string effect in fields[165].Split('$', StringSplitOptions.RemoveEmptyEntries))
                    foreach (string component in effect.Split('|'))
                    {
                        string number = Trim(component);
                        int start = number.Length > 0 && number[0] is '+' or '-' ? 1 : 0;
                        if (number.Length == start || number.Skip(start).Any(c => c is < '0' or > '9'))
                            throw new InvalidDataException("The installed spell effect layout is invalid.");
                    }
                string? detail = descriptions?.GetValueOrDefault(id);
                result.Add(Record("spell", id, new SpellWire(id, name, raw, detail is null ? null : Text(detail, 16000)), source));
            }
            catch (InvalidDataException error) { rejected?.Invoke(new("spell", id, error.Message)); }
        }
        return result.ToArray();
    }

    // File I/O belongs on the caller's background task. Only requested spell IDs
    // are returned, and the ordinary overlay catalog never retains raw lines.
    internal static ContributionRecord[] ObservedSpells(IEnumerable<int> observedIds, ContributionSource source,
        Action<ContributionRejection>? rejected = null, string? spellPath = null)
    {
        source = NormalizeSource(source);
        if (source.ClientChannel == "Beta" && string.IsNullOrWhiteSpace(spellPath))
            throw new InvalidDataException("Beta spell contributions require an explicit validated installation file. Live definitions are not a fallback.");
        int[] ids = observedIds.Distinct().ToArray();
        var spells = SpellNameCatalog.ReadObserved(ids, spellPath);
        string directory = Path.GetDirectoryName(spellPath ?? Path.Combine(CollectorBuild.Live.InstallationPath, "spells_us.txt"))!;
        return Spells(ids, spells, source, rejected, InstalledSpellDescriptions.Read(spells, Path.Combine(directory, "dbstr_us.txt")));
    }

    internal static ContributionRecord[] InspectedItems(InspectionCapture inspection, Action<ContributionRejection>? rejected = null)
    {
        var build = inspection.ClientChannel == "Live" ? CollectorBuild.Live : CollectorBuild.Beta;
        if (inspection.ClientChannel is not ("Live" or "Beta")) throw new InvalidDataException("Unknown inspection channel.");
        Identity.VerifyFile(build.ExecutablePath, inspection.BuildSha256);
        var source = new ContributionSource("Team Bar", inspection.ClientChannel, "1", inspection.BuildSha256, inspection.Decoder);
        var result = new List<ContributionRecord>();
        // Per-window records avoid conflating variants of the same evolving item.
        foreach (var entry in inspection.Items)
            result.AddRange(Items([entry.Item], source, rejected, entry.Effects is null ? null
                : new Dictionary<int, ItemSpellReference[]> { [entry.Item.ItemId] = entry.Effects }));
        int[] ids = inspection.Items.SelectMany(e => e.Effects ?? []).Select(e => e.SpellId).Distinct().ToArray();
        result.AddRange(ObservedSpells(ids, source with { Decoder = inspection.ClientChannel.ToLowerInvariant() + "-installed-spells-description-1" }, rejected,
            Path.Combine(build.InstallationPath, "spells_us.txt")));
        return result.ToArray();
    }

    internal static ContributionBatch[] Batches(IEnumerable<ContributionRecord> records) => EnumerateBatches(records).ToArray();
    // Generic API retains its existing v1 item contract until those receivers
    // support intrinsic references. Spell detail is already part of that contract.
    internal static ContributionRecord GenericV1(ContributionRecord record) => record.Kind == "item" && record.Payload.TryGetProperty("effects", out _)
        ? Record(record.Kind, record.EntityId, record.Payload.EnumerateObject().Where(p => p.Name != "effects")
            .ToDictionary(p => p.Name, p => p.Value.Clone()), record.Source) : record;
    internal static ContributionBatch? NextBatch(IEnumerable<ContributionRecord> records) => EnumerateBatches(records).FirstOrDefault();

    private static IEnumerable<ContributionBatch> EnumerateBatches(IEnumerable<ContributionRecord> records)
    {
        var selected = new List<ContributionRecord>();
        var identities = new HashSet<(string Kind, int Id)>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        ContributionSource? source = null;
        int bytes = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        ContributionBatch Complete()
        {
            byte[] body = Serialize(source!, selected);
            if (body.Length > MaxBodyBytes) throw new InvalidDataException("Contribution batch exceeds its byte limit.");
            return new(selected.ToArray(), body);
        }
        foreach (var record in records)
        {
            ContributionSource nextSource = NormalizeSource(record.Source);
            if (!ContributionKinds.All.Contains(record.Kind) || record.EntityId < 1 || record.Payload.ValueKind != JsonValueKind.Object
                || (ContributionKinds.IsSource(record.Kind) ? record.EntityId != ContributionKinds.SourceId(record.Payload)
                    : !record.Payload.TryGetProperty(record.Kind + "Id", out var id) || !id.TryGetInt32(out int payloadId) || payloadId != record.EntityId)
                || record.Fingerprint != Fingerprint(record.Payload))
                throw new InvalidDataException("Contribution record identity is invalid.");
            if (!fingerprints.Add(RecordKey(record))) continue;
            int payloadBytes = JsonSerializer.SerializeToUtf8Bytes(record.Payload, Json).Length;
            int optionalArrayBytes = record.Kind is "item" or "spell" ? 0 : ContributionKinds.ArrayName(record.Kind).Length + 6;
            int overhead = counts.GetValueOrDefault(record.Kind) > 0 ? 1 : optionalArrayBytes;
            if (selected.Count > 0 && (source != nextSource || selected.Count == MaxRecords
                || identities.Contains((record.Kind, record.EntityId)) || bytes + payloadBytes + overhead > MaxBodyBytes))
            {
                yield return Complete();
                selected.Clear(); identities.Clear(); counts.Clear(); source = null;
                overhead = optionalArrayBytes;
            }
            if (source is null) { source = nextSource; bytes = Serialize(source, []).Length; }
            bytes += payloadBytes + overhead;
            if (bytes > MaxBodyBytes) throw new InvalidDataException("A contribution record exceeds its byte limit.");
            selected.Add(record); identities.Add((record.Kind, record.EntityId));
            counts[record.Kind] = counts.GetValueOrDefault(record.Kind) + 1;
        }
        if (selected.Count > 0) yield return Complete();
    }
    private static byte[] Serialize(ContributionSource source, IEnumerable<ContributionRecord> records)
    {
        var all = records.ToArray();
        var body = new Dictionary<string, object> { ["schemaVersion"] = 1, ["source"] = source };
        foreach (string kind in ContributionKinds.All)
        {
            var rows = all.Where(record => record.Kind == kind).Select(record => record.Payload).ToArray();
            if (rows.Length > 0 || kind is "item" or "spell") body[ContributionKinds.ArrayName(kind)] = rows;
        }
        return JsonSerializer.SerializeToUtf8Bytes(body, Json);
    }

    private static ContributionRecord Record<T>(string kind, int id, T value, ContributionSource source)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(value, Json);
        return new(kind, id, Fingerprint(payload), payload, source);
    }

    // Sort all object keys; source, inventory locations, client identity, and
    // observation time are intentionally absent from the content fingerprint.
    internal static string Fingerprint(JsonElement payload)
    {
        // Match JSON.stringify's string representation, including literal UTF-8
        // supplementary characters. Utf8JsonWriter still escapes those characters
        // with UnsafeRelaxedJsonEscaping, producing a different server hash.
        var canonical = new StringBuilder();
        void String(string value)
        {
            canonical.Append('"');
            foreach (char c in value)
                switch (c)
                {
                    case '"': canonical.Append("\\\""); break;
                    case '\\': canonical.Append("\\\\"); break;
                    case '\b': canonical.Append("\\b"); break;
                    case '\t': canonical.Append("\\t"); break;
                    case '\n': canonical.Append("\\n"); break;
                    case '\f': canonical.Append("\\f"); break;
                    case '\r': canonical.Append("\\r"); break;
                    default:
                        if (c < ' ') canonical.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else canonical.Append(c);
                        break;
                }
            canonical.Append('"');
        }
        void Write(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    canonical.Append('{');
                    bool first = true;
                    foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        if (!first) canonical.Append(',');
                        first = false; String(property.Name); canonical.Append(':'); Write(property.Value);
                    }
                    canonical.Append('}'); break;
                case JsonValueKind.Array:
                    canonical.Append('[');
                    bool firstElement = true;
                    foreach (var child in value.EnumerateArray())
                    { if (!firstElement) canonical.Append(','); firstElement = false; Write(child); }
                    canonical.Append(']'); break;
                case JsonValueKind.String: String(value.GetString()!); break;
                default: canonical.Append(value.GetRawText()); break;
            }
        }
        Write(payload);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    // Queue v1 used Utf8JsonWriter's Unicode escaping. Retain this solely to
    // recognize existing pending records and acknowledgement history on upgrade.
    internal static string LegacyFingerprint(JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void Write(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value); }
                    writer.WriteEndObject();
                }
                else if (value.ValueKind == JsonValueKind.Array)
                { writer.WriteStartArray(); foreach (var child in value.EnumerateArray()) Write(child); writer.WriteEndArray(); }
                else value.WriteTo(writer);
            }
            Write(payload);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static ContributionSource NormalizeSource(ContributionSource source)
    {
        if (source is null || source.ClientChannel is not ("Live" or "Beta"))
            throw new InvalidDataException("Only explicitly identified Live or Beta observations can contribute.");
        string? hash = source.BuildSha256;
        if (hash is not null && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("The contribution build hash is invalid.");
        if (source.ClientChannel == "Beta" && (hash is null || string.IsNullOrWhiteSpace(source.Decoder)))
            throw new InvalidDataException("Beta contributions require a client build hash and decoder identity.");
        return new(Text(source.Client, 100), source.ClientChannel, source.Version is null ? null : Text(source.Version, 80),
            hash?.ToLowerInvariant(), source.Decoder is null ? null : Text(source.Decoder, 100));
    }

    private static string Text(string value, int maximum, bool empty = false, bool preserve = false)
    {
        if (value is null || value.Length > maximum) throw new InvalidDataException("Contribution text exceeds its supported length.");
        for (int index = 0; index < value.Length; index++)
        {
            char c = value[index];
            if (c < ' ' || c is '\u007f' or '\u2028' or '\u2029') throw new InvalidDataException("Contribution text contains a control character.");
            if (char.IsHighSurrogate(c))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw new InvalidDataException("Contribution text contains invalid Unicode.");
            }
            else if (char.IsLowSurrogate(c)) throw new InvalidDataException("Contribution text contains invalid Unicode.");
        }
        string result = preserve ? value : Trim(value);
        if (!empty && Trim(result).Length == 0) throw new InvalidDataException("Contribution text is empty.");
        return result;
    }

    // Match JavaScript String.trim, including BOM but excluding U+0085.
    private static string Trim(string value) => value.Trim(' ', '\t', '\n', '\r', '\v', '\f', '\u00a0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
        '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff');
}
