using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TeamBar;

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
internal sealed class ButtonDraft(TeamButton value) : Observable
{
    public string Id { get; } = value.Id;
    private string label = value.Label;
    public string Label { get => label; set { label = value; Notify(); Notify(nameof(ManagementName)); } }
    public string ManagementName => OverlayButtonIcons.ManagementName(Label, Document?.Icon, Rules.FirstOrDefault()?.Key);
    private ButtonRule[] rules = value.Rules;
    internal StandaloneButton? Document { get; set; }
    internal byte[]? SavedBytes { get; set; }
    internal bool Dirty { get; set; }
    internal bool PersonaSpecific { get; set; }
    internal string? FollowCharacterId { get; set; }
    internal bool Floating { get; set; } = true;
    internal TargetSelector[] ShowFor { get; set; } = [];
    private string conflictMessage = "";
    public string ConflictMessage => conflictMessage;
    public bool HasConflict => conflictMessage.Length > 0;
    internal void SetConflicts(string message)
    {
        if (conflictMessage == message) return;
        conflictMessage = message; Notify(nameof(ConflictMessage)); Notify(nameof(HasConflict));
    }

    public ButtonRule[] Rules { get => rules; set { rules = value; Notify(nameof(Description)); Notify(nameof(ManagementName)); } }
    public string Description =>
        Rules.Length == 0 ? "Add a recipient to get started" : Document is null ? $"{Rules.Length} mapping{(Rules.Length == 1 ? "" : "s")} · runs once per click"
            : (Floating ? "Floating" : "Groups only") + (!PersonaSpecific ? " · always visible" : ShowFor.Length == 0 ? " · choose visibility" : $" · {ShowFor.Length} visibility condition{(ShowFor.Length == 1 ? "" : "s")}");
    internal void VisibilityChanged() => Notify(nameof(Description));
    public TeamButton Build() => new(Id, Label, Rules) { Extra = value.Extra };
}
internal sealed class ClientOption(DetectedClient client, bool included) : Observable
{
    public DetectedClient Client { get; private set; } = client;
    internal void Observe(DetectedClient value) { Client = value; Notify(nameof(Client)); Notify(nameof(Details)); }
    public string Name => Client.Character.Name;
    public string Details => Client.UserAssigned ? $"{Client.Character.Class} · Assigned by you" : $"{Client.Character.Class} · Level {Client.Character.Level}";
    public string Initial => Name[..1];
    private bool selected = included;
    public bool Included { get => selected; set { if (selected == value) return; selected = value; Notify(); } }
}
internal sealed record RecipientChoice(string Id, string Label, string Detail, TargetSelector Target);
internal sealed class RuleRow(RecipientChoice choice, string key) : Observable
{
    private RecipientChoice target = choice;
    public RecipientChoice Target { get => target; set { if (value is null || value == target) return; target = value; Notify(); } }
    private string keyValue = key.Length == 0 ? "" : SpellOverlayKey.Parse(key).Key;
    public string Key { get => keyValue; set { if (value is null || value == keyValue) return; keyValue = value; Notify(); } }
    private int modifiers = key.Length == 0 ? 0 : SpellOverlayKey.Parse(key).Modifiers;
    public int Modifiers { get => modifiers; set { if (value < 0 || value == modifiers) return; modifiers = value; Notify(); } }
    internal string Chord => Key.Length == 0 ? "" : SpellOverlayKey.Format(Key, Modifiers);
    private string conflictMessage = "";
    public string ConflictMessage => conflictMessage;
    public bool HasConflict => conflictMessage.Length > 0;
    internal void SetConflicts(string message)
    {
        if (conflictMessage == message) return;
        conflictMessage = message; Notify(nameof(ConflictMessage)); Notify(nameof(HasConflict));
    }
}
