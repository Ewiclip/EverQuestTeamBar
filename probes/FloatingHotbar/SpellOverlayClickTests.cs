namespace TeamBar;

internal static class SpellOverlayClickTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        var (definition, capture) = SpellOverlayTests.Fixture();
        definition = definition with { Spells = [definition.Spells[0] with { ClickKey = "5", PersonaClasses = ["Shaman"] }] };
        var client = new DetectedClient(capture.ProcessId, capture.ProcessStartedUtcTicks, 123, 0, new(capture.CharacterName, 10, 130));
        var request = new SpellClickRequest("tap", OverlayId: definition.Id, SpellId: 100, ProcessId: client.ProcessId,
            Started: client.StartedUtcTicks, TargetId: capture.Target!.SpawnId, TargetName: capture.Target.Name,
            Class: client.Character.Class, ZoneId: capture.Zone.Id, Key: "5");
        var library = new SpellOverlayLibrary(1, [definition]);
        var other = client with { ProcessId = 99, Character = new("Other", 10, 130) };
        var routed = SpellOverlayClickRules.Resolve(library, request, [client, other], (_, _) => capture);
        check(routed.ProcessId == client.ProcessId && routed.Key == "5" && routed.Scan == Routing.Scan("5"), "spell click routes one key only to the overlay character");
        void Reject(SpellOverlayLibrary lib, SpellClickRequest input, DetectedClient[] clients, TargetCapture actual, string why)
        {
            bool rejected = false;
            try { SpellOverlayClickRules.Resolve(lib, input, clients, (_, _) => actual); } catch (InvalidDataException) { rejected = true; }
            check(rejected, why);
        }
        foreach (var invalid in new[] { definition with { Locked = false }, definition with { Enabled = false },
            definition with { Spells = [definition.Spells[0] with { ClickKey = null }] },
            definition with { Spells = [definition.Spells[0] with { ClickKey = "6" }] },
            definition with { Spells = [definition.Spells[0] with { PersonaClasses = ["Cleric"] }] } })
            Reject(new(1, [invalid]), request, [client], capture, "disabled, editing, stale key or wrong persona cannot cast: " + invalid.Spells[0].ClickKey + "/" + invalid.Locked + "/" + invalid.Enabled + "/" + invalid.Spells[0].PersonaClasses![0]);
        Reject(library, request, [client, client with { ProcessId = 99 }], capture, "duplicate character sessions cannot receive a spell click");
        Reject(library, request with { ProcessId = 99 }, [client], capture, "process replacement cancels a spell click");
        Reject(library, request with { Started = 999 }, [client], capture, "PID reuse cancels a spell click");
        foreach (var actual in new[] { capture with { Target = null }, capture with { CharacterName = "Other" }, capture with { CharacterClass = "Cleric" },
            capture with { Target = capture.Target with { SpawnId = 999 } }, capture with { Target = capture.Target with { RawType = 2 } },
            capture with { Zone = capture.Zone with { Id = 99 } }, capture with { Target = capture.Target with { RawType = 0 } } })
            Reject(library, request, [client], actual, "fresh target/identity/zone validation rejects changed observation: " + actual.Target?.SpawnId + "/" + actual.Target?.RawType + "/" + actual.CharacterClass + "/" + actual.Zone.Id + "/" + actual.CharacterName);
        foreach (int modifiers in Enumerable.Range(0, 8))
        {
            string key = SpellOverlayKey.Format("6", modifiers);
            var chordLibrary = new SpellOverlayLibrary(1, [definition with { Spells = [definition.Spells[0] with { ClickKey = key }] }]);
            SpellOverlayStore.Validate(chordLibrary);
            var chord = SpellOverlayClickRules.Resolve(chordLibrary, request with { Key = key }, [client, other], (_, _) => capture);
            check(chord.ProcessId == client.ProcessId && chord.Scan == Routing.Scan("6") && chord.Modifiers == modifiers,
                "configured chord resolves to one character: " + key);
        }
        foreach (string key in new[] { "[", "]", "Ctrl+[", "Alt+]", "Shift+A", "Ctrl+F12", "Alt+NumpadAdd", "Space",
            "Up", "Ctrl+Down", "Shift+Left", "Alt+Right", "Ctrl+Home", "End", "PageUp", "Ctrl+Shift+PageDown", "Insert", "Delete", "Alt+NumpadEnter", "Ctrl+NumpadDivide" })
        {
            var chordLibrary = new SpellOverlayLibrary(1, [definition with { Spells = [definition.Spells[0] with { ClickKey = key }] }]);
            SpellOverlayStore.Validate(chordLibrary);
            var chord = SpellOverlayClickRules.Resolve(chordLibrary, request with { Key = key }, [client, other], (_, _) => capture);
            var parsed = SpellOverlayKey.Parse(key);
            check(chord.ProcessId == client.ProcessId && chord.Scan == parsed.Scan && chord.Modifiers == parsed.Modifiers,
                "expanded spell key resolves to one character: " + key);
        }
        foreach (string key in new[] { "Win+5", "Ctrl+Ctrl+5", "Alt+", "", "F13", "55", "Ctrl++", "CapsLock" })
        {
            bool rejected = false;
            try { SpellOverlayStore.Validate(new(1, [definition with { Spells = [definition.Spells[0] with { ClickKey = key }] }])); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "unsupported spell-click key rejected: " + key);
        }
        string path = output + ".click-library.json";
        var bytes = SpellOverlayStore.Save(path, library, null);
        check(SpellOverlayStore.Load(path).Library.Overlays[0].Spells[0].ClickKey == "5", "spell click key persists with the watched spell");
        SpellOverlayStore.Save(path, new(1, [definition with { Spells = [definition.Spells[0] with { ClickKey = null }] }]), bytes);
        check(SpellOverlayStore.Load(path).Library.Overlays[0].Spells[0].ClickKey is null, "removing the mapping restores display-only behavior");
        FloatingWorkspace.TestSpellClicks(check, output);
    }
}
