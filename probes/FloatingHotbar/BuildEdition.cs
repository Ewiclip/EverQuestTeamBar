using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal static class BuildEdition
{
    internal const string Id = "team-bar", Label = "Team Bar";
    internal const string ReadyHint = "Ready · one tap per click";
    internal static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new OneTapProfileConverter());
        return options;
    }
}

// Legacy files may still contain repeats. Import them as one-tap mappings and
// discard loop controls before deserializing: no runtime model can repeat input.
internal sealed class OneTapProfileConverter : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type == typeof(TeamDefinition) || type == typeof(TeamButton)
        || type == typeof(StandaloneButton) || type == typeof(BarProfile) || type == typeof(ButtonMapping);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type))!;

    private sealed class Converter<T> : JsonConverter<T>
    {
        private static JsonSerializerOptions Unwrapped(JsonSerializerOptions options)
        {
            var copy = new JsonSerializerOptions(options);
            for (int i = copy.Converters.Count - 1; i >= 0; i--)
                if (copy.Converters[i] is OneTapProfileConverter) copy.Converters.RemoveAt(i);
            return copy;
        }
        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader)?.AsObject() ?? throw new JsonException("Empty profile.");
            if (!Normalize(node, type)) return default;
            return node.Deserialize<T>(Unwrapped(options));
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var node = JsonSerializer.SerializeToNode(value, Unwrapped(options))!.AsObject();
            if (Normalize(node, typeof(T))) node.WriteTo(writer, options);
            else writer.WriteNullValue();
        }
    }
    internal static bool Normalize(JsonObject node, Type type, List<string>? changes = null)
    {
        if (type == typeof(TeamDefinition) || type == typeof(BarProfile))
        {
            foreach (string key in Keys(node, "buttons"))
            {
                if (node[key] is not JsonArray buttons) throw new JsonException("A profile needs a buttons array.");
                for (int i = buttons.Count - 1; i >= 0; i--)
                    if (!NormalizeButton(buttons[i]?.AsObject() ?? throw new JsonException("Empty button."), changes)) buttons.RemoveAt(i);
            }
            return true;
        }
        return NormalizeButton(node, changes);
    }
    private static bool NormalizeButton(JsonObject button, List<string>? changes)
    {
        string label = button[Keys(button, "label").FirstOrDefault() ?? "label"]?.ToString() ?? "Unnamed button";
        bool control = new[] { "stopAllLoops", "pauseAllLoops", "resumeAllLoops" }.SelectMany(name => Keys(button, name))
            .Any(key => button[key] is not null && button[key]!.GetValueKind() != JsonValueKind.False);
        if (control) { changes?.Add("Omitted loop control: " + label); return false; }
        if (Keys(button, "repeat").Any(key => button[key] is not null)) changes?.Add("Converted to one tap per click: " + label);
        foreach (string key in new[] { "repeat", "stopAllLoops", "pauseAllLoops", "resumeAllLoops" }.SelectMany(name => Keys(button, name)).ToArray())
            button.Remove(key);
        return true;
    }
    internal static string[] Keys(JsonObject node, string name) => node.Select(p => p.Key).Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
}
