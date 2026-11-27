using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TeamBar;

internal enum AppearanceTheme { Dark, Light }
internal sealed record UserSettings
{
    public int Version { get; init; } = 2;
    public AppearanceTheme Theme { get; init; } = AppearanceTheme.Dark;
    public FloatingAppearance Floating { get; init; } = new();
    public bool SpellOverlaysLoaded { get; init; }
    public bool EnableScreenDetection { get; init; }
    public bool EnableSpellBarData { get; init; }
    public bool EnableDataCollection { get; init; }
    internal bool IsEnabled(MemoryFeature feature) => feature switch
    {
        MemoryFeature.ScreenDetection => EnableScreenDetection,
        MemoryFeature.SpellBarData => EnableSpellBarData,
        MemoryFeature.DataCollection => EnableDataCollection,
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
    };
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconGameDirectory { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenGameDirectory { get; init; }


    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

// Personal preferences are independent of team profiles; an account-backed store can implement this later.
internal interface IUserSettingsStore
{
    UserSettings Load();
    void Save(UserSettings settings);


    void SaveFloating(FloatingAppearance appearance, FloatingAppearance expected);
    void SaveSpellOverlaysLoaded(bool loaded);
    void SaveScreenDetection(bool enabled);
    void SaveSpellBarData(bool enabled);
    void SaveDataCollection(bool enabled);
}

internal sealed class LocalUserSettingsStore(string path) : IUserSettingsStore
{
    internal static string DefaultPath => Path.Combine(InstalledAppData.DataDirectory, "user-settings.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AppearanceTheme>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    internal static void Validate(UserSettings settings)
    {
        if (settings.Version != 2 || !Enum.IsDefined(settings.Theme)) throw new InvalidDataException("This preferences file needs a newer version of Team Bar.");
        FloatingThemes.Validate(settings.Floating);
    }
    public UserSettings Load()
    {
        if (!File.Exists(path)) return new();
        var json = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new InvalidDataException("The preferences file must contain a settings object.");
        // Version 1 used one gate for detection and spells, and required that same
        // gate in addition to the collection checkbox before collection could read.
        if (!json.TryGetPropertyValue("version", out var version) || version is JsonValue number && number.TryGetValue<int>(out int v) && v == 1)
        {
            bool memory = json["allowGameMemoryReading"]?.Deserialize<bool>() ?? false;
            bool collection = json["enableDataCollection"]?.Deserialize<bool>() ?? false;
            json["version"] = 2;
            json["enableScreenDetection"] = memory;
            json["enableSpellBarData"] = memory;
            json["enableDataCollection"] = memory && collection;
        }
        json.Remove("allowGameMemoryReading");
        var settings = json.Deserialize<UserSettings>(Options)!;
        Validate(settings);
        return settings with { Extra = settings.Extra?.Where(item => !item.Key.Equals("stopLoopsOnEscape", StringComparison.OrdinalIgnoreCase)
            && !item.Key.Equals("pauseLoopsOnEscape", StringComparison.OrdinalIgnoreCase)).ToDictionary(item => item.Key, item => item.Value) };
    }
    public void Save(UserSettings settings) => Update(latest => latest with { Theme = settings.Theme });
    public void SaveSpellOverlaysLoaded(bool loaded) => Update(latest => latest with { SpellOverlaysLoaded = loaded });
    public void SaveScreenDetection(bool enabled) => Update(latest => latest with { EnableScreenDetection = enabled });
    public void SaveSpellBarData(bool enabled) => Update(latest => latest with { EnableSpellBarData = enabled });
    public void SaveDataCollection(bool enabled) => Update(latest => latest with { EnableDataCollection = enabled });
    internal void SaveIconDirectory(string directory) => Update(latest => latest with { IconGameDirectory = directory });
    internal void SaveScreenDirectory(string directory) => Update(latest => latest with { ScreenGameDirectory = ScreenInstallation.Validate(directory) });


    public void SaveFloating(FloatingAppearance appearance, FloatingAppearance expected) => Update(latest =>
    {
        if (!SameFloating(latest.Floating, expected)) throw new IOException("Floating preferences changed in another window. Reopen Settings to load the latest version.");
        return latest with { Floating = appearance };
    });
    internal static bool SameFloating(FloatingAppearance a, FloatingAppearance b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private void Update(Func<UserSettings, UserSettings> update)
    {
        string destination = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var fileLock = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Preserve fields a future version may add, including changes saved by another open window.
        var settings = update(Load()); Validate(settings);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, settings, Options); stream.Flush(true); }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class MemoryUserSettingsStore(UserSettings? initial = null) : IUserSettingsStore
{
    private UserSettings value = initial ?? new();
    public UserSettings Load() => value;
    public void Save(UserSettings settings) { LocalUserSettingsStore.Validate(settings); value = value with { Theme = settings.Theme }; }
    public void SaveSpellOverlaysLoaded(bool loaded) => value = value with { SpellOverlaysLoaded = loaded };
    public void SaveScreenDetection(bool enabled) => value = value with { EnableScreenDetection = enabled };
    public void SaveSpellBarData(bool enabled) => value = value with { EnableSpellBarData = enabled };
    public void SaveDataCollection(bool enabled) => value = value with { EnableDataCollection = enabled };


    public void SaveFloating(FloatingAppearance appearance, FloatingAppearance expected)
    {
        if (!LocalUserSettingsStore.SameFloating(value.Floating, expected)) throw new IOException("Floating preferences changed in another window. Reopen Settings to load the latest version.");
        FloatingThemes.Validate(appearance); value = value with { Floating = appearance };
    }
}
