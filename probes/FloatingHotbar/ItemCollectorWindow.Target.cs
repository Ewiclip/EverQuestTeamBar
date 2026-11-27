using System.Diagnostics;
namespace TeamBar;

public partial class ItemCollectorWindow
{
    private void SpellOverlays_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (review) return;
        try { SpellOverlayManagerWindow.Open(); } catch (Exception error) { Status(error.Message, true); }
    }
    private readonly System.Windows.Threading.DispatcherTimer targetTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TargetCapture? targetCapture;
    private string? targetError;
    private bool targetBusy;
    private double targetReadMilliseconds;
    private int targetReads;
    private readonly SpellIcons spellIcons = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<MyTargetSpellRow> mySpells = [];

    private Task ReadTargetNow()
    {
        if (review || !DataCollectionAccess.CanCollect || ClientChoice.SelectedItem is not DetectedClient client) return Task.CompletedTask;
        return ReadTarget(client, () => Task.Run(() => ReadCollection(CollectorBuild.ForClient(client),
            CollectorCapability.Target | CollectorCapability.TargetBuffs, () => TargetBuffDecoder.CaptureTarget(client.ProcessId, client.StartedUtcTicks))));
    }
    private async Task ReadTarget(DetectedClient client, Func<Task<TargetCapture>> captureTarget)
    {
        if (targetBusy || paused || closed) return;
        int version = generation; targetBusy = true;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var capture = await captureTarget();
            if (closed || version != generation) return;
            RequireCaptureSource(client, capture.ClientChannel, capture.BuildSha256);
            if (capture.CharacterName != client.Character.Name || capture.CharacterClass != client.Character.Class)
                throw new InvalidDataException("Character or persona changed. Refresh the character list.");
            targetReadMilliseconds = elapsed.Elapsed.TotalMilliseconds; targetReads++;
            AcceptTarget(capture);
            await ContributeSpells(capture, version);
            await ContributeSources(ContributionSources.Npc(capture), version);
        }
        catch (Exception error)
        {
            if (closed || version != generation) return;
            targetReadMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            targetError = error.Message;
            ShowTarget(); if (DataTabs.SelectedItem == TargetTab) UpdateSummary();
        }
        finally { targetBusy = false; }
    }
    private void AcceptTarget(TargetCapture capture)
    {
        targetCapture = capture; targetError = null;
        ShowTarget(); if (DataTabs.SelectedItem == TargetTab) UpdateSummary();
    }
    private void ResetTarget()
    {
        targetCapture = null; targetError = null; targetReads = 0; targetReadMilliseconds = 0;
        ShowTarget();
    }
    private void ShowTarget()
    {
        if (!initialized) return;
        var target = targetError is null ? targetCapture?.Target : null;
        TargetTitle.Text = targetError is not null ? "Target unavailable" : target?.Name
            ?? (targetCapture is null ? "Waiting for a target reading" : "No target selected");
        TargetType.Text = target?.Type ?? "—";
        TargetLevel.Text = target is null ? "—" : target.Level.ToString();
        TargetSpawn.Text = target is null ? "—" : target.SpawnId.ToString();
        TargetZone.Text = targetError is not null ? "—" : targetCapture?.Zone.Name ?? "—";
        TargetZoneDetail.Text = targetError is not null || targetCapture is null ? ""
            : $"{targetCapture.Zone.ShortName} · Zone {targetCapture.Zone.Id}";
        TargetTiming.Text = review ? "Saved snapshot preview" : targetReads == 0 ? "Waiting for the first successful read"
            : $"{targetReads:N0} successful reads · Last attempt {targetReadMilliseconds:N1} ms";
        var effects = targetError is null && targetCapture is not null ? MyTargetSpells.Select(targetCapture) : [];
        // Keep row instances and scroll position while only the countdown changes.
        if (!mySpells.Select(s => (s.Slot, s.SpellId)).SequenceEqual(effects.Select(s => (s.Slot, s.SpellId))))
        {
            mySpells.Clear();
            foreach (var effect in effects) mySpells.Add(new(effect, spellIcons));
        }
        else for (int i = 0; i < effects.Length; i++) mySpells[i].Update(effects[i]);
        MySpellList.ItemsSource ??= mySpells;
        string character = targetCapture?.CharacterName ?? (ClientChoice.SelectedItem as DetectedClient)?.Character.Name ?? "the selected character";
        MySpellSummary.Text = $"{mySpells.Count} spells cast by {character}";
        MySpellEmpty.Visibility = mySpells.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        MySpellEmpty.Text = targetError is not null ? "Target reading unavailable."
            : targetCapture is null ? "Waiting for the first reading…"
            : targetCapture.Target is null ? "Select a target in EverQuest to see your spells."
            : targetCapture.BuffsError is not null ? "Spell data is temporarily unavailable. Waiting for the next reading…"
            : targetCapture.Buffs?.Status == "waiting-for-target-data" || targetCapture.Buffs is null ? "Waiting for the game to load target spells…"
            : $"No spells attributed to {character} on this target.";
        MySpellNote.Text = paused ? "Paused · Timers show the last reading."
            : $"Only spells whose caster is {character}. Timers follow the game; unknown casters are hidden.";
    }
}
