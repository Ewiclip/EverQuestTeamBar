using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using System.Collections.ObjectModel;
namespace TeamBar;
public partial class WindowAssignments : W.Window
{
    private readonly ObservableCollection<AssignmentRow> rows = [];
    private readonly W.Threading.DispatcherTimer windowTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CharacterProfile[] characters;
    private readonly Func<GameSession[]> listWindows;
    private int nextNumber = 1;
    private readonly Func<DetectedClient, DetectedClient> verify;
    private WindowIdentification? identification;
    internal DetectedClient[]? Result { get; private set; }
    internal WindowAssignments(SetupWindow owner, TeamDefinition team, GameSession[] windows, DetectedClient[] assigned,
        Func<DetectedClient, DetectedClient>? verifier = null, Func<GameSession[]>? windowProvider = null)
    {
        Owner = owner; Resources.MergedDictionaries.Add(owner.Resources); InitializeComponent();
        Icon = AppBranding.WindowIcon;
        verify = verifier ?? ClientSessions.Verify;
        characters = team.Characters; listWindows = windowProvider ?? ClientSessions.ListWindows;
        foreach (var row in windows.Select(NewRow))
        {
            rows.Add(row);
            var current = assigned.SingleOrDefault(c => c.ProcessId == row.Window.ProcessId && c.StartedUtcTicks == row.Window.StartedUtcTicks && c.Window == row.Window.Window);
            if (current is null) continue;
            row.Name = current.Character.Name; row.Class = current.Character.Class;
        }
        WindowRows.ItemsSource = rows;
        foreach (var row in rows) Watch(row);
        IdentifyWindowsButton.IsEnabled = rows.Count > 0;
        Loaded += (_, _) => windowTimer.Start();
        Closed += (_, _) =>
        {
            windowTimer.Stop(); windowTimer.Tick -= PollWindows;
            identification?.Dispose(); identification = null;
        };
        AutomaticChoice.IsEnabled = GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection); AutomaticChoice.Opacity = GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection) ? 1 : .42;
        AutomaticChoice.ToolTip = GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection) ? null : GameDataAccess.DisabledReason(MemoryFeature.ScreenDetection);
        UpdateValidation();
        if (rows.Count == 0) WindowHint.Text = "Waiting for an EQ window. New windows appear here automatically.";
        windowTimer.Tick += PollWindows;
    }
    private void PollWindows(object? sender, EventArgs e)
    {
        try { RefreshWindows(onlyChanges: true); }
        catch (Exception error) { IdentifyStatus.Text = "Could not refresh windows: " + error.Message; }
    }
    private AssignmentRow NewRow(GameSession window) => new(window, nextNumber++, characters);
    private void Watch(AssignmentRow row) => row.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is not (nameof(AssignmentRow.Name) or nameof(AssignmentRow.Class))) return;
        Confirmed.IsChecked = false; UpdateValidation();
    };
    internal void RefreshWindows(bool onlyChanges = false)
    {
        // Read the complete scan before replacing any rows, so a scan failure
        // leaves both the user's draft and the visible window numbers intact.
        var windows = listWindows();
        var next = windows.Select(window =>
        {
            var row = rows.FirstOrDefault(r => WindowIdentification.SameSession(r.Window, window));
            if (row is not null) { row.UpdateWindow(window); return row; }
            row = NewRow(window); Watch(row); return row;
        }).ToArray();
        var closed = rows.Except(next).ToArray();
        var opened = next.Except(rows).ToArray();
        bool changed = closed.Length + opened.Length > 0;
        if (onlyChanges && !changed) return;
        if (changed)
        {
            identification?.Dispose(); identification = null;
            Confirmed.IsChecked = false;
            foreach (var row in closed) rows.Remove(row);
            foreach (var row in opened) rows.Add(row);
        }
        // Preserve existing row controls, editing focus and caret on background scans.
        if (!onlyChanges)
            for (int i = 0; i < next.Length; i++)
                if (rows[i] != next[i]) rows.Move(rows.IndexOf(next[i]), i);
        int removed = closed.Count(row => !string.IsNullOrWhiteSpace(row.Name));
        IdentifyStatus.Text = removed > 0 ? $"{removed} assigned window(s) closed or changed. Remaining entries were kept; assign new windows before continuing."
            : changed ? "Windows updated. Existing entries and numbers were kept. Match any new windows, then confirm below."
            : "Windows are up to date. Your entries and window numbers were kept.";
        WindowHint.Text = rows.Count == 0 ? "Waiting for an EQ window. New windows appear here automatically."
            : "Choose a saved character or type a new name, then select the current class. Leave unused windows blank.";
        IdentifyWindowsButton.IsEnabled = rows.Count > 0;
        UpdateValidation();
    }
    private void RefreshWindows_Click(object sender, W.RoutedEventArgs e)
    {
        try { RefreshWindows(); }
        catch (Exception error) { IdentifyStatus.Text = "Could not refresh windows: " + error.Message; }
    }
    private void Confirmed_Changed(object sender, W.RoutedEventArgs e) { if (UseAssignmentsButton is not null) UpdateValidation(); }
    private void UpdateValidation()
    {
        var chosen = rows.Where(row => !string.IsNullOrWhiteSpace(row.Name)).ToArray();
        foreach (var row in rows)
        {
            string name = row.Name.Trim();
            row.Problem = name.Length == 0 ? "" : name.Length is < 2 or > 30 || !name.All(char.IsAsciiLetter) ? "Use 2–30 letters for the character name."
                : !Identity.Classes.Contains(row.Class ?? "") ? "Choose this character’s current class."
                : chosen.Count(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) > 1 ? "This character is assigned to more than one window." : "";
        }
        bool valid = chosen.Length is > 0 and <= 6 && rows.All(row => row.Problem.Length == 0);
        UseAssignmentsButton.IsEnabled = valid && Confirmed.IsChecked == true;
        Feedback.Text = chosen.Length == 0 ? "Start by entering a character name and current class in a window above."
            : chosen.Length > 6 ? "Select up to six characters. Leave the other windows blank."
            : !valid ? "Review the highlighted entries above. Your entries stay here while you fix them."
            : $"{chosen.Length} character{(chosen.Length == 1 ? "" : "s")} ready · {rows.Count - chosen.Length} window(s) unused."
                + (Confirmed.IsChecked == true ? "" : " Confirm that these match the game to continue.");
        Feedback.SetResourceReference(C.TextBlock.ForegroundProperty, chosen.Length > 0 && !valid ? "Error" : "Muted");
    }
    internal DetectedClient[] BuildAssignments()
    {
        if (Confirmed.IsChecked != true) throw new InvalidDataException("Check your characters and classes, then confirm the assignments.");
        var result = rows.Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => ClientSessions.Assign(r.Window, r.Name.Trim(), r.Class ?? "")).ToArray();
        ClientSessions.ValidateAssignments(result);
        foreach (var client in result) if (!Identity.Same(client, verify(client))) throw new InvalidDataException("A window changed. Reopen window assignment.");
        return result;
    }
    private void Use_Click(object sender, W.RoutedEventArgs e)
    {
        try { Result = BuildAssignments(); Close(); }
        catch (Exception error) { Feedback.Text = error.Message; Feedback.SetResourceReference(C.TextBlock.ForegroundProperty, "Error"); }
    }
    private void Automatic_Click(object sender, W.RoutedEventArgs e) { if (!GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection)) return; Result = []; Close(); }
    private void Cancel_Click(object sender, W.RoutedEventArgs e) => Close();
    private void IdentifyWindows_Click(object sender, W.RoutedEventArgs e) => Identify(rows.ToArray(), reveal: false);
    private void IdentifyWindow_Click(object sender, W.RoutedEventArgs e)
    {
        if (sender is C.Button { DataContext: AssignmentRow row }) Identify([row], reveal: true);
    }
    private void Identify(AssignmentRow[] selectedRows, bool reveal)
    {
        identification?.Dispose(); identification = null;
        try
        {
            if (reveal) WindowIdentification.Reveal(selectedRows.Single().Window, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            identification = new(selectedRows.Select(row => new WindowIdentificationTarget(row.Number, row.Window)).ToArray(), text => IdentifyStatus.Text = text);
        }
        catch (Exception error) { IdentifyStatus.Text = error.Message; }
    }
    internal void Select(int index, string name, string cls)
    { rows[index].Name = name; rows[index].Class = cls; }
    internal void Confirm() => Confirmed.IsChecked = true;
    internal void Export(string path)
    {
        UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)AssignmentRoot.ActualWidth, (int)AssignmentRoot.ActualHeight, 96, 96, M.PixelFormats.Pbgra32);
        bitmap.Render(AssignmentRoot); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew); encoder.Save(file);
    }
    private sealed class AssignmentRow(GameSession window, int number, CharacterProfile[] characters) : Observable
    {
        public GameSession Window { get; private set; } = window;
        public int Number { get; } = number;
        public string Heading => $"Window {Number}";
        public string Label => Window.Label;
        public string ProcessLabel => $"Process {Window.ProcessId}";
        internal void UpdateWindow(GameSession value) { if (value == Window) return; Window = value; Notify(nameof(Label)); }
        public string[] Characters { get; } = ["", .. characters.Select(c => c.Name)];
        public string[] Classes => Identity.Classes;
        private string name = "";
        public string Name { get => name; set { if (name == value) return; name = value; Notify(); } }
        private string? cls;
        public string? Class { get => cls; set { if (cls == value) return; cls = value; Notify(); } }
        private string problem = "";
        public string Problem { get => problem; set { if (problem == value) return; problem = value; Notify(); } }
    }
}
