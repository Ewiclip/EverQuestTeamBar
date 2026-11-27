using System.ComponentModel;
using System.Windows.Media;
namespace TeamBar;

internal static class MyTargetSpells
{
    internal static bool Matches(TargetCapture capture) => capture.Buffs is { } b && capture.Target is not null
        && b.Target == capture.Target && b.Zone == capture.Zone && b.ProcessId == capture.ProcessId
        && b.ProcessStartedUtcTicks == capture.ProcessStartedUtcTicks && b.CharacterName == capture.CharacterName
        && b.CharacterClass == capture.CharacterClass && b.Status == "observed-target-window" && capture.BuffsError is null;
    internal static TargetEffect[] Select(TargetCapture capture) => !Matches(capture) ? [] : capture.Buffs!.Effects
        .Where(e => !string.IsNullOrEmpty(e.CasterName) && string.Equals(e.CasterName, capture.CharacterName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(e => e.Slot).ToArray();
    internal static TargetCapture Export(TargetCapture capture) => capture with
    {
        Buffs = capture.Buffs is null ? null : capture.Buffs with
        { Effects = Select(capture), Completeness = capture.Buffs.Completeness + " Export filtered to effects attributed to the observing character." }
    };
}

internal sealed class MyTargetSpellRow(TargetEffect effect, SpellIcons icons) : INotifyPropertyChanged
{
    private TargetEffect current = effect;
    public int Slot => current.Slot;
    public int SpellId => current.SpellId;
    public string Name => current.SpellName ?? $"Spell {current.SpellId}";
    public ImageSource? Icon { get; private set; } = icons.Get(effect.IconId);
    public string Remaining => current.RemainingMilliseconds < 0 ? "No timer" : current.RemainingMilliseconds >= 3600000
        ? $"{current.RemainingMilliseconds / 3600000}h {current.RemainingMilliseconds / 60000 % 60:00}m {current.RemainingMilliseconds / 1000 % 60:00}s"
        : $"{current.RemainingMilliseconds / 60000}:{current.RemainingMilliseconds / 1000 % 60:00}";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(TargetEffect next)
    {
        string oldName = Name, oldTime = Remaining; int? oldIcon = current.IconId;
        current = next;
        if (Name != oldName) PropertyChanged?.Invoke(this, new(nameof(Name)));
        if (Remaining != oldTime) PropertyChanged?.Invoke(this, new(nameof(Remaining)));
        if (oldIcon != current.IconId) { Icon = icons.Get(current.IconId); PropertyChanged?.Invoke(this, new(nameof(Icon))); }
    }
}
