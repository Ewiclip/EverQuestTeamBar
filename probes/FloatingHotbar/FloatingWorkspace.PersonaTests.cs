using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private static void PersonaRoutingSelfTest(Action<bool, string> check, LiveSetup original, string output)
    {
        var initial = original.Identities!.Single();
        var character = original.Team.Characters.Single();
        var team = original.Team with { Buttons = [new("persona", "Persona test",
            [new(new(CharacterId: character.Id), "1"), new(new(CharacterId: character.Id, Class: "Cleric"), "2"),
             new(new(PersonaId: original.Clients.Single().PersonaId), "5")])] };
        var button = new StandaloneButton(1, "standalone", "Class test", team.Characters, team.Personas,
            [new(new(CharacterId: character.Id), "3"), new(new(CharacterId: character.Id, Class: "Cleric"), "4")], [new(Class: "Cleric")]);
        using var buffer = new MemoryStream(); using var log = new StreamWriter(buffer);
        using var workspace = new FloatingWorkspace(original with { Team = team }, [new(output + ".persona-button", button, [])],
            output + ".persona-profile", log, new MemoryUserSettingsStore());
        var actual = initial;
        workspace.ReadIdentity = _ => actual;
        check(workspace.CheckIdentities() && workspace.surfaces[0].Taps[0].Single().Key == "5",
            "original persona-specific rule takes priority");
        foreach (var (classId, barKey, standaloneKey) in new[] { (2, "2", "4"), (12, "1", "3"), (10, "5", "3") })
        {
            actual = initial with { Character = initial.Character with { ClassId = classId } };
            check(!workspace.CheckIdentities() && !workspace.identityBlocked && workspace.CheckIdentities(),
                "verified class transition cancels its old gesture then becomes ready automatically: " + classId);
            check(workspace.surfaces[0].Taps[0].Single().Key == barKey && workspace.surfaces[1].Taps[0].Single().Key == standaloneKey,
                "team and standalone mappings use the detected persona including a previously unknown class: " + classId);
            check(workspace.surfaces[1].Visible == (classId == 2) && workspace.sequence == 0,
                "class-specific visibility follows persona changes without emitting input: " + classId);
        }
        var other = initial with { ProcessId = initial.ProcessId + 10, Window = initial.Window + 10, Character = new("Other", 2, 130) };
        var mixedTeam = TeamEditing.MergeDetected(team, [other]);
        using var mixed = new FloatingWorkspace(original with { Team = mixedTeam, Identities = [initial, other],
            Clients = TeamEditing.Bind(mixedTeam, [initial, other]) }, [], output + ".mixed-personas", log, new MemoryUserSettingsStore());
        bool loading = true;
        mixed.ReadIdentity = expected => expected.ProcessId == other.ProcessId
            ? loading ? throw new InvalidDataException("Fixture loading") : other
            : initial with { Character = initial.Character with { ClassId = 2 } };
        check(!mixed.CheckIdentities() && mixed.identityUnavailable && !mixed.identityBlocked,
            "a partially loaded team waits without permanently blocking a detected persona switch");
        loading = false; mixed.CheckIdentities();
        check(mixed.CheckIdentities() && mixed.surfaces.Single().Taps[0].Single().Key == "2" && mixed.sequence == 0,
            "the whole team resumes with new persona mappings when the remaining client finishes loading");
    }

    private void PersonaSpellSelfTest(Action<bool, string> check, SpellClickRequest request, int requester,
        SpellOverlayDefinition definition, TargetCapture capture)
    {
        T Complete<T>(Task<T> task) { EditionWait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
        var oldReader = ReadIdentity; var oldLibrary = ReadSpellLibrary; var oldTarget = ReadSpellTarget;
        var initial = live.Identities!.Single(); long before = sequence;
        string? session = Complete(SpellOverlayInputPipe.RequestNamed(new("status"), SpellPipeName))?.Session;
        var actual = initial with { Character = initial.Character with { ClassId = 2 } };
        ReadIdentity = _ => actual;
        ReadSpellTarget = (_, _) => capture with { CharacterClass = actual.Character.Class };
        ReadSpellLibrary = () => new(1, [definition with { Spells = [definition.Spells[0] with { PersonaClasses = ["Shaman"] }] }]);
        try
        {
            check(!CheckIdentities() && cancelInFlight && !identityBlocked && sequence == before,
                "switching personas cancels old input without discarding the existing host");
            EditionWait(() => !cancelInFlight && !busy);
            check(CheckIdentities() && sequence == before && spellWindows.ContainsKey(request.Window),
                "persona switch retains spell-window registration and sends no automatic tap");
            SpellClickRequest Click(string cls) => request with { Class = cls, Foreground = Native.GetForegroundWindow().ToInt64() };
            check(Complete(SpellOverlayInputPipe.RequestNamed(Click("Shaman"), SpellPipeName)) is { Ok: false } && sequence == before,
                "a spell click captured before the persona switch cannot use the new class");
            check(Complete(SpellOverlayInputPipe.RequestNamed(Click("Cleric"), SpellPipeName)) is { Ok: false } && sequence == before,
                "a spell limited to the previous persona cannot cast on the new persona");
            ReadSpellLibrary = () => new(1, [definition with { Spells = [definition.Spells[0] with { PersonaClasses = ["Cleric"] }] }]);
            check(Complete(HandleSpellRequest(Click("Cleric"), requester)).Ok && sequence == before + 1,
                "a fresh explicit spell click works on the new persona without refreshing or reapplying");
            EditionWait(() => !busy);
            actual = initial; CheckIdentities(); EditionWait(() => !cancelInFlight);
            var status = Complete(SpellOverlayInputPipe.RequestNamed(new("status"), SpellPipeName));
            check(status is { Ok: true } && status.Session == session && sequence == before + 1,
                "switching back keeps the original spell session and never replays the previous tap");
        }
        finally { ReadIdentity = oldReader; ReadSpellLibrary = oldLibrary; ReadSpellTarget = oldTarget; }
    }
}
