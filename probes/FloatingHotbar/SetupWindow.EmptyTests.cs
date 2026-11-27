namespace TeamBar;

public partial class SetupWindow
{
    private void EmptyApplySelfTest(Action<bool, string> check)
    {
        check(!HasButtonDrafts, "Empty apply fixture must have no bar or standalone buttons.");
        foreach (var client in clients) client.Included = false;
        check(ApplyControl.IsEnabled, "Deleting every button must allow Apply without selected clients.");
        var expected = CurrentTeam();
        var originalClear = clearHotkeys;
        int clears = 0;
        clearHotkeys = profile =>
        {
            check(profile == profilePath && TeamEditing.Load(profile).Buttons.Length == 0,
                "Empty apply must persist its profile before clearing the current hotkeys.");
            clears++;
            return Task.FromResult(true); // The fixture never opens a runtime pipe or controller.
        };
        try
        {
            var apply = OpenBar();
            while (!apply.IsCompleted) Settle();
            apply.GetAwaiter().GetResult();
            var saved = TeamEditing.Load(profilePath);
            check(clears == 1 && saved.Buttons.Length == 0 && saved.Characters.SequenceEqual(expected.Characters)
                && saved.Personas.SequenceEqual(expected.Personas),
                "Empty apply must clear hotkeys once and retain identity metadata without a controller or clients.");
            var reopened = new SetupWindow(profilePath, controllerPath, () => new([], []), userSettings: new MemoryUserSettingsStore());
            try
            {
                check(reopened.drafts.Count == 0 && reopened.standaloneDrafts.Count == 0,
                    "Reopening an intentional empty configuration must not restore defaults or archived buttons.");
            }
            finally { reopened.Close(); }
        }
        finally { clearHotkeys = originalClear; }
    }
}
