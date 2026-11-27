using System.Text.Json;
using W = System.Windows;
using M = System.Windows.Media;

namespace TeamBar;

internal static class UserSettingsTests
{
    internal static void Run(string path)
    {
        static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        if (File.Exists(path)) throw new IOException("Settings test output must be new.");
        var store = new LocalUserSettingsStore(path);
        Check(store.Load().Theme == AppearanceTheme.Dark && !File.Exists(path), "Missing preferences should default to dark without writing.");
        store.Save(new() { Theme = AppearanceTheme.Light });
        Check(new LocalUserSettingsStore(path).Load().Theme == AppearanceTheme.Light, "Theme was not remembered by a new store.");
        File.WriteAllText(path, "{\"version\":1,\"theme\":\"light\",\"stopLoopsOnEscape\":true,\"futurePreference\":{\"keep\":true}}");
        store.Save(new() { Theme = AppearanceTheme.Dark });
        Check(store.Load().Extra!["futurePreference"].GetProperty("keep").GetBoolean(), "Saving appearance dropped unrelated settings.");
        Check(!File.ReadAllText(path).Contains("stopLoopsOnEscape", StringComparison.OrdinalIgnoreCase), "Saving appearance retained an obsolete loop preference.");
        Check(!store.Load().SpellOverlaysLoaded, "Older settings should not start overlays until they have been opened.");
        store.SaveSpellOverlaysLoaded(true);
        var reopened = new LocalUserSettingsStore(path);
        Check(reopened.Load().SpellOverlaysLoaded, "Loaded spell overlays must survive a new settings store.");
        store.Save(new() { Theme = AppearanceTheme.Light });
        Check(reopened.Load().SpellOverlaysLoaded, "Saving an older settings draft must preserve overlay startup state.");
        reopened.SaveSpellOverlaysLoaded(false);
        Check(!store.Load().SpellOverlaysLoaded && store.Load().Theme == AppearanceTheme.Light
            && store.Load().Extra!["futurePreference"].GetProperty("keep").GetBoolean(),
            "Hiding overlays must preserve appearance and unknown settings.");

        foreach (bool memory in new[] { false, true })
        foreach (bool collection in new[] { false, true })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                version = 1, allowGameMemoryReading = memory, enableDataCollection = collection,
                spellOverlaysLoaded = true, futurePreference = new { keep = 42 }
            }));
            var migrated = store.Load();
            Check(migrated.EnableScreenDetection == memory && migrated.EnableSpellBarData == memory &&
                migrated.EnableDataCollection == (memory && collection), "Migration must retain each previously effective permission.");
            store.Save(new() { Theme = AppearanceTheme.Light });
            using var written = JsonDocument.Parse(File.ReadAllText(path));
            var saved = written.RootElement;
            Check(saved.GetProperty("version").GetInt32() == 2 && !saved.TryGetProperty("allowGameMemoryReading", out _) &&
                saved.GetProperty("enableScreenDetection").GetBoolean() == memory &&
                saved.GetProperty("enableSpellBarData").GetBoolean() == memory &&
                saved.GetProperty("enableDataCollection").GetBoolean() == (memory && collection) &&
                saved.GetProperty("spellOverlaysLoaded").GetBoolean() && saved.GetProperty("futurePreference").GetProperty("keep").GetInt32() == 42,
                "Saving appearance must persist the migrated permissions, preserve spell startup and unknown fields, and retire the old gate.");
        }

        File.WriteAllText(path, "{\"version\":2,\"enableScreenDetection\":false,\"enableSpellBarData\":false,\"enableDataCollection\":false}");
        var stale = reopened.Load();
        store.SaveScreenDetection(true);
        reopened.SaveDataCollection(true);
        store.SaveSpellBarData(true);
        reopened.Save(stale with { Theme = AppearanceTheme.Light });
        Check(store.Load() is { EnableScreenDetection: true, EnableSpellBarData: true, EnableDataCollection: true },
            "Two preference stores and a stale theme draft must preserve independently enabled features.");
        reopened.SaveScreenDetection(false);
        store.SaveDataCollection(false);
        Check(reopened.Load() is { EnableScreenDetection: false, EnableSpellBarData: true, EnableDataCollection: false },
            "Disabling features from different stores must preserve the remaining permission.");

        foreach (string invalid in new[] { "{", "{\"version\":3,\"theme\":\"dark\"}", "{\"version\":1,\"theme\":\"unknown\"}" })
        {
            File.WriteAllText(path, invalid); bool rejected = false;
            try { store.Save(new() { Theme = AppearanceTheme.Light }); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(path) == invalid, "Unreadable or newer preferences must not be overwritten.");
        }
        File.WriteAllText(path, "{\"version\":1,\"theme\":\"dark\"}");
        var preview = new MemoryUserSettingsStore(store.Load()); preview.Save(new() { Theme = AppearanceTheme.Light });
        FloatingThemeTests.Run(path + ".floating.json");
        Check(store.Load().Theme == AppearanceTheme.Dark, "Review-mode appearance should not modify persisted preferences.");
        foreach (var theme in Enum.GetValues<AppearanceTheme>())
        {
            var resources = new W.ResourceDictionary(); ThemePalette.Apply(resources, theme);
            foreach (var (foreground, background) in new[] { ("Text", "AppBackground"), ("Text", "Surface"), ("Muted", "Surface"), ("OnAccent", "Accent"), ("KeyText", "KeySurface"), ("WarningText", "WarningSurface"), ("Error", "AppBackground"), ("PreviewWarning", "PreviewSurface") })
            {
                var a = ((M.SolidColorBrush)resources[foreground]).Color; var b = ((M.SolidColorBrush)resources[background]).Color;
                double first = Luminance(a), second = Luminance(b), contrast = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                Check(contrast >= 4.5, $"{theme} {foreground}/{background} contrast is too low: {contrast:F2}.");
            }
        }
    }
    private static double Luminance(M.Color color)
    {
        static double Linear(byte channel) { double c = channel / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    internal sealed class FailingStore(UserSettings settings) : IUserSettingsStore
    {
        public UserSettings Load() => settings;
        public void Save(UserSettings value) => throw new IOException("Simulated settings write failure.");
        public void SaveSpellOverlaysLoaded(bool loaded) => throw new IOException("Simulated settings write failure.");
        public void SaveScreenDetection(bool enabled) => throw new IOException("Simulated settings write failure.");
        public void SaveSpellBarData(bool enabled) => throw new IOException("Simulated settings write failure.");
        public void SaveDataCollection(bool enabled) => throw new IOException("Simulated settings write failure.");


        public void SaveFloating(FloatingAppearance value, FloatingAppearance expected) => throw new IOException("Simulated settings write failure.");
    }
}
