using System.Collections.ObjectModel;
using System.Windows.Threading;
using C = System.Windows.Controls;
using W = System.Windows;
namespace TeamBar;

public partial class GameViewLayoutWindow
{
    private sealed record WindowChoice(GameViewWindow? Key, string Label, string? Character = null)
    {
        public override string ToString() => Label;
    }

    private readonly Func<IEnumerable<string>> characterNames;
    private readonly Func<GameSession[]> listWindows;
    private readonly ObservableCollection<string> names = [];
    private readonly DispatcherTimer windowTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<GameViewWindow, int> windowNumbers = [];
    private WindowIdentification? identification;
    private GameSession[] openWindows = [];
    private WindowChoice[] liveChoices = [];
    internal GameViewSlotRow[] SlotRows { get; } = Enumerable.Range(1, 6).Select(i => new GameViewSlotRow(i)).ToArray();
    private static GameViewWindow? ChosenWindow(GameViewSlotRow row) => (row.WindowPicker.SelectedItem as WindowChoice)?.Key;

    private void CreateSlotRows()
    {
        foreach (var row in SlotRows)
        {
            SlotList.Children.Add(row);
            row.Character.ItemsSource = names;
            row.Character.AddHandler(C.Primitives.TextBoxBase.TextChangedEvent, new C.TextChangedEventHandler((_, _) =>
            {
                if (!updating) { RefreshSlotStatus(); DrawPreview(); }
            }));
            row.WindowPicker.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                if (row.WindowPicker.SelectedItem is WindowChoice { Character: { } name }) row.Character.Text = name;
                RefreshSlotStatus();
                DrawPreview();
            };
            row.Clear.Click += (_, _) =>
            {
                row.Character.Text = "";
                row.WindowPicker.SelectedIndex = 0;
                RefreshSlotStatus();
                DrawPreview();
            };
        }
    }

    private void RefreshSlotStatus()
    {
        foreach (var row in SlotRows)
        {
            var key = ChosenWindow(row);
            var name = row.Character.Text.Trim();
            row.Clear.IsEnabled = key is not null || name.Length > 0;
            if (!row.Clear.IsEnabled) row.Status.Text = "Empty";
            else if (key is not null && liveChoices.Any(c => c.Key == key)) row.Status.Text = "Window available";
            else
            {
                int matches = name.Length == 0 ? 0 : liveChoices.Count(c => name.Equals(c.Character, StringComparison.OrdinalIgnoreCase));
                row.Status.Text = matches switch
                {
                    1 => "Character online",
                    > 1 => "Choose the matching EQ window",
                    _ => name.Length > 0 ? "Waiting for character" : "Window offline"
                };
            }
        }
    }

    internal void RefreshWindows()
    {
        openWindows = listWindows().OrderBy(w => w.StartedUtcTicks).ThenBy(w => w.ProcessId).ToArray();
        var roster = knownClients();
        var availableNames = characterNames().Concat(roster.Select(c => c.Character.Name))
            .Concat(SavedLayouts.Items.OfType<GameViewLayout>().SelectMany(l => l.Characters))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in availableNames.Order(StringComparer.OrdinalIgnoreCase))
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);

        var choices = openWindows.Select(window =>
        {
            var key = GameViewWindow.From(window);
            if (!windowNumbers.TryGetValue(key, out int number)) windowNumbers.Add(key, number = windowNumbers.Count + 1);
            var match = roster.FirstOrDefault(key.Matches);
            string prefix = match is null ? $"Window {number}" : $"Window {number} · {match.Character.Name}";
            return new WindowChoice(key, $"{prefix} · PID {key.ProcessId}", match?.Character.Name);
        }).ToArray();
        WindowCount.Text = choices.Length switch
        {
            0 => "No open EQ windows",
            1 => "1 open EQ window",
            _ => $"{choices.Length} open EQ windows"
        };
        IdentifyButton.IsEnabled = choices.Length > 0;
        if (choices.SequenceEqual(liveChoices)) return;
        liveChoices = choices;
        updating = true;
        try
        {
            foreach (var row in SlotRows)
                SetWindowChoices(row, ChosenWindow(row));
        }
        finally { updating = false; }
        RefreshSlotStatus();
        DrawPreview();
    }

    private void SetWindowChoices(GameViewSlotRow row, GameViewWindow? selected)
    {
        var choices = new List<WindowChoice> { new(null, "Match by character name") };
        choices.AddRange(liveChoices);
        if (selected is not null && !choices.Any(c => c.Key == selected))
            choices.Add(new(selected, $"Closed window · PID {selected.ProcessId}"));
        row.WindowPicker.ItemsSource = choices;
        row.WindowPicker.SelectedItem = choices.First(c => c.Key == selected);
    }

    private void Identify_Click(object sender, W.RoutedEventArgs e) => Run(() =>
    {
        RefreshWindows();
        identification?.Dispose();
        var targets = openWindows.Select(w => new WindowIdentificationTarget(windowNumbers[GameViewWindow.From(w)], w)).ToArray();
        identification = new(targets, message =>
        {
            IdentifyStatus.Text = message;
            IdentifyStatus.Visibility = W.Visibility.Visible;
        }, frameReader: WindowIdentification.ReadScreenFrame, viewSurface: session => host.ActiveGameViewSession is { } view &&
            view.TryGetVisibleTile((nint)session.Window, out var owner, out var bounds) ? (owner.ToInt64(), bounds) : null);
    });
}
