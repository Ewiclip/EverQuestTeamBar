using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed record TargetMapping(string Character, string Key);
internal sealed record ButtonMapping(string Id, string Label, TargetMapping[] Targets,
    [property: System.Text.Json.Serialization.JsonIgnore] string? DisabledReason = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string? Icon { get; init; }
}
internal sealed record BarProfile(int Version, ButtonMapping[] Buttons);

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = BuildEdition.JsonOptions();
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Installer verification is deliberately before settings, UI, native
            // discovery, and single-instance activation. It must not start input.
            if (args.Length == 2 && args[0] == "--installation-health-check")
                return TeamBar.InstallationHealthCheck.Run(args[1]);
#if !TEAMBAR_PUBLIC_RELEASE
            // These suites use only isolated fixture state, before even reading
            // real user preferences or setting up any UI/input service.
            if (args.Length == 2 && args[0] == "--game-view-layout-test") { GameViewLayoutTests.Run(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--game-view-renderer-fixture")
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TeamBar.GameViewRuntimeTests.RunRendererFixture(new(int.Parse(args[1]), int.Parse(args[2])));
                return 0;
            }
            if (args.Length == 2 && args[0] is "--game-view-runtime-test" or "--game-view-multiscreen-test" or "--game-view-editor-test" or "--game-view-switch-matrix-test")
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args[0] == "--game-view-runtime-test") TeamBar.GameViewRuntimeTests.Run(args[1]);
                else if (args[0] == "--game-view-multiscreen-test") TeamBar.GameViewRuntimeTests.Run(args[1], multiScreenOnly: true);
                else if (args[0] == "--game-view-switch-matrix-test") TeamBar.GameViewRuntimeTests.RunSwitchMatrix(args[1]);
                else TeamBar.GameViewEditorTests.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--native-path-test") { TeamBar.NativePathTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--button-icon-test") { TeamBar.ButtonIconTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--release-update-test") { TeamBar.ReleaseUpdateTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--profile-migration-test") { TeamBar.ProfileMigrationTests.Run(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--release-update-trust-test") { new TeamBar.ReleaseUpdateTrust().VerifyInstaller(args[1], args[2]); return 0; }
#endif
            // Software drawing keeps scrolling and drag feedback responsive with
            // live overlays. Apply before creating any WPF window; this changes
            // only Team Bar's renderer, never the game's. The environment override
            // is reserved for an explicit diagnostic comparison.
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                Environment.GetEnvironmentVariable("TEAMBAR_UI_RENDER_MODE") == "Default"
                    ? System.Windows.Interop.RenderMode.Default : System.Windows.Interop.RenderMode.SoftwareOnly;
            TeamBar.GameDataAccess.Initialize(new TeamBar.LocalUserSettingsStore(TeamBar.LocalUserSettingsStore.DefaultPath));
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (TeamBar.TeamBarApplication.IsLaunch(args)) return TeamBar.TeamBarApplication.Run(args);
#if TEAMBAR_PUBLIC_RELEASE
            throw new ArgumentException("Unsupported Team Bar launch option.");
#else
            if (args.Length == 1 && args[0] == "--spell-input-host") { return TeamBar.TeamBarApplication.Run(["--spell-overlays"]); }
            if (args.Length == 2 && args[0] == "--spell-overlay-test") { TeamBar.SpellOverlayTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--pipe-disconnect-test") { TeamBar.PipeDisconnectTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--spell-overlay-ui-test")
            {
                var checks = new List<string>(); TeamBar.SpellOverlayManagerWindow.TestEditor(checks);
                CollectorFormat.WriteNew(args[1], new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false, processMemoryWritten = false });
                return 0;
            }
            if (args.Length == 3 && args[0] == "--spell-overlay-review") { TeamBar.SpellOverlayManagerWindow.Review(args[1], Enum.Parse<TeamBar.AppearanceTheme>(args[2], true)); return 0; }
            if (args.Length == 4 && args[0] == "--spell-overlay-review" && args[3] == "interactive")
            { TeamBar.SpellOverlayManagerWindow.Review(args[1], Enum.Parse<TeamBar.AppearanceTheme>(args[2], true), true); return 0; }
            if (args.Length == 2 && args[0] == "--items-test") { ItemCollectionTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--merchant-test") { CollectorFormat.WriteNew(args[1], MerchantDecoderTests.Run()); return 0; }
            if (args.Length == 2 && args[0] == "--collector-identity-test") { CollectorIdentityTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--beta-recipe-test") { BetaRecipeTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--live-recipe-test") { CollectorFormat.WriteNew(args[1], LiveRecipeDecoderTests.Run()); return 0; }
            if (args.Length == 2 && args[0] == "--item-build-test") { CollectorFormat.WriteNew(args[1], ItemBuildDecoderTests.Run()); return 0; }
            if (args.Length == 2 && args[0] == "--item-spells-test") { TeamBar.ItemSpellCollectionTests.Run(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--inspection-export-fixture") { TeamBar.ItemSpellCollectionTests.ExportCapture(args[1], args[2]); return 0; }
            if (args.Length == 2 && args[0] == "--collector-identity-scan") { CollectorFormat.WriteNew(args[1], CollectorIdentity.Discover()); return 0; }
            if (args.Length == 2 && args[0] == "--items-ui-test") { TeamBar.ItemCollectorWindow.RunUiTests(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--contribution-payload-test") { TeamBar.ContributionPayloadTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--contribution-client-test") { TeamBar.ContributionClientTests.Run(args[1]).GetAwaiter().GetResult(); return 0; }
            if (args.Length == 4 && args[0] == "--beta-merchant-import-fixture") { TeamBar.BetaMerchantImportFixture.Capture(int.Parse(args[1]), args[2], args[3]); return 0; }
            if (args.Length == 2 && args[0] == "--beta-merchant-import-test") { TeamBar.BetaMerchantImportFixtureTests.Run(args[1]); return 0; }
            if (args.Length is 3 or 4 && args[0] == "--items-review") { TeamBar.ItemCollectorWindow.Review(args[1], args[2], args.Length == 4 ? Enum.Parse<TeamBar.AppearanceTheme>(args[3], true) : TeamBar.AppearanceTheme.Light); return 0; }
            if (args.Length == 2 && args[0] is "--single-version-test" or "--edition-test") { EditionTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] is "--prepare-profile" or "--prepare-edition") { Console.WriteLine(EditionProfiles.Prepare(args[1])); return 0; }
            if (args.Length == 2 && args[0] == "--preview-setup") { new System.Windows.Application().Run(new TeamBar.SetupWindow(args[1], "review-only-controller.exe", reviewOnly: true)); return 0; }
            if (args.Length == 3 && args[0] == "--ui-review") { TeamBar.SetupWindow.Review(args[1], args[2]); return 0; }
            if (args.Length == 2 && args[0] == "--identity-scan")
            {
                using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(output, Identity.Discover(), new JsonSerializerOptions { WriteIndented = true }); return 0;
            }
            if (args.Length == 3 && args[0] == "--identity-layout-report") { CollectorFormat.WriteNew(args[2], IdentityResolver.Describe(args[1])); return 0; }
            if (args.Length == 3 && args[0] == "--target-layout-report") { CollectorFormat.WriteNew(args[2], TargetLayoutResolver.Describe(args[1])); return 0; }
            if (args.Length == 3 && args[0] == "--target-resolver-test") { TargetResolverTests.Run(args[1], args[2]); return 0; }
            if (args.Length == 3 && args[0] == "--target-capture") { CollectorFormat.WriteNew(args[2], TargetBuffDecoder.CaptureTarget(int.Parse(args[1]))); return 0; }
            if (args.Length == 2 && args[0] == "--target-buffs-test") { CollectorFormat.WriteNew(args[1], TargetBuffDecoderTests.Run()); return 0; }
            if (args.Length == 2 && args[0] == "--surface-test") { CollectorFormat.WriteNew(args[1], SurfaceDecoderTests.Run()); return 0; }
            if (args.Length == 2 && args[0] == "--identity-resolver-test") { IdentityResolverTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--editor-test") { EditorTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--settings-test") { TeamBar.SettingsTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--privacy-test") { TeamBar.PrivacyTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--window-identification-test") { TeamBar.WindowIdentificationTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--usability-test") { TeamBar.UsabilityTests.Run(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--library-panel-test") { TeamBar.LibraryPanelTests.Run(args[1], args[2]); return 0; }
            if (args.Length == 2 && args[0] == "--slide-preview") { TeamBar.SlidePreview.Run(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--interaction-performance-test") { TeamBar.InteractionPerformanceTests.Run(args[1], args[2] == "software", args[2] == "interactive"); return 0; }
            if (args.Length == 2 && args[0] == "--button-groups-test") { ButtonGroupTests.Run(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--group-editor-test") { TeamBar.GroupEditorTests.Run(args[1], args[2]); return 0; }
            if (args.Length == 2 && args[0] == "--key-input-test") { InputKeysTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--routing-conflict-test") { RoutingConflictTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--mapping-conflict-ui-test") { RoutingConflictTests.RunUi(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--app-instance-test") { TeamBar.AppInstanceCoordinatorTests.Run(args[1]); return 0; }
            if (args.Length >= 3 && args[0] == "--app-instance-fixture") { TeamBar.AppInstanceCoordinatorTests.RunFixture(args[1], args[2], args[3..]); return 0; }
            if (args.Length == 2 && args[0] == "--app-runtime-test") { AppInputSessionTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--native-host-test") { BackendNativeHostTests.Run(args[1]); return 0; }

            if (args.Length == 2 && args[0] == "--direct-input-test") { DirectInputTests.Run(args[1]); return 0; }

            if (args.Length == 2 && args[0] == "--standalone-test") { StandaloneTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--anchor-test") { AnchorTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--layout-test") { LayoutTests.Run(args[1]); return 0; }
            bool sessionFixture = args.Length == 3 && args[0] == "--session-fixture";
            bool test = args.Length == 2 && args[0] == "--self-test";
            bool fallbackTest = args.Length == 2 && args[0] == "--fallback-test";
            bool integration = fallbackTest || args.Length == 2 && args[0] == "--integration-test";
            string config = Path.Combine(AppContext.BaseDirectory, "buttons.json");
            string? teamPath = null, clientsPath = null, controllerPath = null;
            string logPath = sessionFixture ? args[2] : test || integration ? args[1] : Path.Combine(AppContext.BaseDirectory, $"hotbar-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");
            if (!test && !integration && !sessionFixture)
                for (int i = 0; i < args.Length; i += 2)
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("Expected --config <file> --log <new-file>.");
                    if (args[i] == "--config") config = args[i + 1];
                    else if (args[i] == "--log") logPath = args[i + 1];
                    else if (args[i] == "--team") teamPath = args[i + 1];
                    else if (args[i] == "--clients") clientsPath = args[i + 1];
                    else if (args[i] == "--controller") controllerPath = args[i + 1];
                    else throw new ArgumentException("Unknown option.");
                }
            LiveSetup? live = null;
            if (teamPath is not null || clientsPath is not null || controllerPath is not null)
            {
                if (teamPath is null || clientsPath is null || controllerPath is null) throw new ArgumentException("Live mode requires --team, --clients and --controller.");
                var team = JsonSerializer.Deserialize<TeamDefinition>(File.ReadAllText(teamPath), Json) ?? throw new InvalidDataException("Empty team.");
                var clients = JsonSerializer.Deserialize<ClientFile>(File.ReadAllText(clientsPath), Json) ?? throw new InvalidDataException("Empty client bindings.");
                var identities = ClientSessions.Restore(team, clients);
                live = new(controllerPath, team, clients.Clients, identities);
            }
            if (integration || sessionFixture)
            {
                var fixture = new TeamDefinition(1, [new("test", "Test client")], [new("test-persona", "test", null)],
                    [new("test-button", "Test tap", [new(new(CharacterId: "test"), "5")])]);
                if (sessionFixture) { teamPath = Path.GetFullPath(args[1]); fixture = TeamEditing.Load(teamPath); }
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                live = new(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), fixture,
                    [new("test-persona", Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks)],
                    [new(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Test", 5, fallbackTest ? 0 : 130), fallbackTest)]);
                Routing.Validate(live.Team, live.Clients);
            }
            var profile = live is null ? JsonSerializer.Deserialize<BarProfile>(File.ReadAllText(config), Json)
                ?? throw new InvalidDataException("Empty bar profile.") : Routing.Display(live);
            if (live is null) Validate(profile);
            using var journal = new StreamWriter(new FileStream(Path.GetFullPath(logPath), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            if (live is not null && teamPath is not null && !sessionFixture)
            {
                var standalone = StandaloneButtons.Load(teamPath);
                return FloatingWorkspace.Run(live, standalone, teamPath, journal);
            }
            using var bar = new Hotbar(profile, journal, live, teamPath);
            if (sessionFixture) bar.ReadIdentity = expected => expected;
            if (test) { Routing.SelfTest(); bar.SelfTest(); return 0; }
            if (integration) {
                int decoderCalls = 0;
                bar.ReadIdentity = fallbackTest ? expected => ClientSessions.Verify(expected,
                    _ => new(expected.ProcessId, expected.StartedUtcTicks, expected.Window, expected.Left, "Harness"),
                    _ => { ++decoderCalls; throw new InvalidDataException("Unsupported detector fixture."); }) : expected => expected;
                bar.LiveSelfTest();
                if (fallbackTest) {
                    if (decoderCalls != 0) throw new InvalidOperationException("Manual runtime called the character decoder.");
                    journal.WriteLine(JsonSerializer.Serialize(new { kind = "fallback_test_pass", decoderCalls, gameInput = false }));
                }
                return 0;
            }
            using var session = teamPath is null ? null : new BarSession(teamPath, () => bar.Ready, () =>
            {
                try { if (!bar.IsDisposed) bar.BeginInvoke(() => bar.Close()); } catch (InvalidOperationException) { }
            });
            if (live is null) Application.Run(bar);
            else
            {
                using var context = new ApplicationContext();
                bar.FormClosed += (_, _) => context.ExitThread();
                bar.StartLive(); // Create a hidden HWND; show only after both guards are ready.
                Application.Run(context);
            }
            if (bar.Failed) MessageBox.Show(bar.LastError ?? "The input controller disconnected.", "Team Bar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return bar.Failed ? 1 : 0;
#endif
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (TeamBar.TeamBarApplication.IsLaunch(args) || args.Contains("--team")) MessageBox.Show(error.Message, "Team Bar could not open", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
    }
    internal static void Validate(BarProfile p)
    {
        if (p.Version != 1 || p.Buttons is null || p.Buttons.Length is < 1 or > 6)
            throw new InvalidDataException("Expected version 1 with one to six buttons.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in p.Buttons)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || !ids.Add(b.Id) || b.Label is not { Length: <= 24 }
                || b.Targets is null || b.Targets.Length is < 1 or > 144) throw new InvalidDataException("Invalid button definition.");
            foreach (var t in b.Targets)
            {
                if (string.IsNullOrWhiteSpace(t.Character) || t.Character.Length > 64
                    || t.Key is null)
                    throw new InvalidDataException("Each mapping needs a character and a keyboard key.");
                TeamBar.SpellOverlayKey.Parse(t.Key);
            }
            if (b.Targets.GroupBy(t => t.Character, StringComparer.OrdinalIgnoreCase).Count() > 6
                || b.Targets.GroupBy(t => t.Character, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 24))
                throw new InvalidDataException("Use up to six characters and 24 keypresses per character.");
        }
    }
}

internal sealed class Hotbar : Form
{
    private readonly BarProfile profile;
    private readonly StreamWriter journal;
    private readonly Font titleFont = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font labelFont = new("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font bodyFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Native.WinEventProc focusCallback;
    private nint focusHook;
    private MouseShield? mouseShield;
    private readonly LiveSetup? live;
    private readonly string? profilePath;
    private bool positionShown;
    private Backend? backend;
    private bool busy, connected;
    private Queue<RoutedTap[]> remainingTaps = new();
    private readonly System.Windows.Forms.Timer identityTimer = new() { Interval = 1000 };
    private bool identityBlocked;
    internal Func<DetectedClient, DetectedClient> ReadIdentity = ClientSessions.Verify;
    internal string? LastError;
    internal bool Failed;
    internal bool Ready => Volatile.Read(ref connected) && Volatile.Read(ref positionShown) && !Volatile.Read(ref Failed);
    private int hover = -1, pressed = -1;
    private long clicks;
    private bool dragging, activationFault;
    private bool InputBlocked => identityBlocked || Failed || live is not null && !connected;
    private Point dragOffset;
    private nint downForeground;
    private string status = "Click to preview a mapping. No game keys are sent.";
    private readonly int barWidth, barHeight;
    private static readonly Color Ink = Color.FromArgb(232, 237, 241), Muted = Color.FromArgb(157, 171, 183);
    private static readonly Color Accent = Color.FromArgb(111, 204, 177), Border = Color.FromArgb(58, 70, 81);
    private float DpiScale => DeviceDpi / 96f;
    private RectangleF CloseRect => new(barWidth - 37, 8, 25, 25);
    private RectangleF ButtonRect(int index) => new(12 + index * 222, 48, 212, barHeight - 94);

    public Hotbar(BarProfile p, StreamWriter output, LiveSetup? setup = null, string? path = null)
    {
        profile = p; journal = output; live = setup; profilePath = path;
        identityTimer.Tick += (_, _) => CheckIdentity();
        barWidth = Math.Max(458, 24 + 222 * p.Buttons.Length - 10);
        barHeight = 128 + 21 * p.Buttons.Max(b => b.Targets.Length);
        Text = live is null ? "EverQuest Team Bar — Preview" : "EverQuest Team Bar";
        Icon = TeamBar.AppBranding.ApplicationIcon;
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(barWidth, barHeight);
        BackColor = Color.FromArgb(24, 31, 38);
        DoubleBuffered = true; TopMost = true; TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 100);
        if (profilePath is not null)
            try { if (WindowLayouts.LoadBar(profilePath) is { } saved) Location = WindowLayouts.Clamp(saved, Size, Screen.AllScreens.Select(s => s.WorkingArea).ToArray()); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { Write("position_restore_failed", new { error.Message }); }
        focusCallback = (_, _, window, _, _, _, _) =>
        {
            if (window == Handle) { activationFault = true; status = "Focus warning: the bar became active."; Invalidate(); }
            Write("foreground", new { hwnd = window.ToInt64(), toolbar = window == Handle });
        };
    }
    internal void StartLive()
    {
        if (live is null) throw new InvalidOperationException("No live team configured.");
        backend = new Backend(live, Handle);
        backend.Message += data =>
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(() => HandleBackend(data)); } catch (InvalidOperationException) { }
        };
        Write("connecting", new { clients = live.Clients });
        backend.Start();
    }
    private void HandleBackend(JsonElement data)
    {
        if (IsDisposed || Disposing) return;
        string? kind = data.GetProperty("type").GetString();
        Write("backend", data);
        switch (kind)
        {
            case "ready":
                if (!data.TryGetProperty("alwaysEnabled", out var enabled) || !enabled.GetBoolean()
                    || !data.TryGetProperty("paused", out var oldPause) || oldPause.GetBoolean())
                { LastError = "Restart Team Bar with the updated input controller."; Failed = true; Hide(); Close(); break; }
                connected = true; status = $"Ready · {live!.Clients.Length} clients connected · One tap per click"; Show(); break;
            case "cancelled": remainingTaps.Clear(); pressed = -1; Capture = false; break;
            case "accepted": status = "Sending tap…"; break;
            case "complete":
                if (!busy || data.GetProperty("id").GetInt64() != clicks) break;
                bool ok = data.GetProperty("ok").GetBoolean();
                if (ok && remainingTaps.Count > 0 && CheckIdentity() && !InputBlocked)
                { backend!.Tap(++clicks, remainingTaps.Dequeue()); break; }
                remainingTaps.Clear(); busy = false; status = ok ? "Tap delivered · Foreground kept" : "Tap incomplete · Check the local log"; break;
            case "rejected": remainingTaps.Clear(); busy = false; status = data.GetProperty("reason").GetString() ?? "Tap rejected."; break;
            case "error": remainingTaps.Clear(); LastError = data.GetProperty("message").GetString(); Failed = true; connected = false; Hide(); Close(); break;
        }
        if (identityBlocked) status = "Character changed · Refresh clients in Setup, then Save & apply";
        Invalidate();
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; p.ExStyle &= ~0x40000; return p; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == MouseShield.RoutedMouse)
        {
            int message = (int)m.WParam;
            long packed = m.LParam.ToInt64();
            var screen = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
            var point = PointToClient(screen);
            var args = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
            if (message == 0x201) OnMouseDown(args);
            else if (message == 0x202) OnMouseUp(args);
            else if (message == 0x200) OnMouseMove(args);
            if (message != 0x200) Write("intercepted_mouse", new { message, foreground = Native.GetForegroundWindow().ToInt64() });
            m.Result = 0; return;
        }
        if (m.Msg == 0x21) { m.Result = 3; return; } // MA_NOACTIVATE: keep the mouse message.
        base.WndProc(ref m);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ClientSize = new Size((int)Math.Round(barWidth * DpiScale), (int)Math.Round(barHeight * DpiScale));
        MoveWithoutActivation(WindowLayouts.Clamp(Location, Size, Screen.AllScreens.Select(s => s.WorkingArea).ToArray()));
        focusHook = Native.SetWinEventHook(3, 3, 0, focusCallback, 0, 0, 0);
        if (focusHook == 0) throw new InvalidOperationException("Cannot observe foreground changes.");
        if (!Native.SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x10 | 0x1 | 0x2))
            throw new InvalidOperationException("Cannot position the floating bar.");
        mouseShield = new MouseShield(Handle);
        if (live?.Identities is not null) identityTimer.Start();
        positionShown = true;
        Write("shown", new { mode = live is null ? "preview" : "live", mouseInterception = true, hwnd = Handle.ToInt64(), foreground = Native.GetForegroundWindow().ToInt64(), dpi = DeviceDpi, Left, Top, buttons = profile.Buttons, inputEnabled = !InputBlocked });
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.ScaleTransform(DpiScale, DpiScale);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var border = new Pen(Border);
        g.DrawRectangle(border, 0, 0, barWidth - 1, barHeight - 1);
        Draw(g, "⋮⋮", bodyFont, Muted, new(12, 13));
        Draw(g, "TEAM BAR", titleFont, Ink, new(36, 13));
        Draw(g, live is null ? "PREVIEW" : (InputBlocked ? "CHECK SETUP" : busy ? "SENDING" : "LIVE") + (live.Identities?.Any(c => c.UserAssigned) == true ? " · ASSIGNED" : ""), titleFont, Accent, new(119, 13));
        Draw(g, "×", labelFont, Muted, new(CloseRect.X + 5, 10));
        for (int i = 0; i < profile.Buttons.Length; ++i)
        {
            var rect = ButtonRect(i); var b = profile.Buttons[i];
            using var fill = new SolidBrush(pressed == i ? Color.FromArgb(40, 73, 74) : hover == i ? Color.FromArgb(40, 52, 62) : Color.FromArgb(31, 41, 50));
            g.FillRectangle(fill, rect);
            using var edge = new Pen(hover == i && !InputBlocked && !busy ? Accent : Border);
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
            DrawFit(g, b.Label, labelFont, InputBlocked || busy ? Muted : Ink, new(rect.X + 14, rect.Y + 12, rect.Width - 28, 24));
            for (int j = 0; j < b.Targets.Length; ++j)
            {
                var target = b.Targets[j];
                DrawFit(g, target.Character, bodyFont, Muted, new(rect.X + 14, rect.Y + 40 + j * 21, rect.Width - 55, 21));
                Draw(g, target.Key, titleFont, InputBlocked || busy ? Muted : Accent, new(rect.Right - 30, rect.Y + 40 + j * 21));
            }
        }
        DrawFit(g, status, bodyFont, activationFault ? Color.Salmon : Muted, new(12, barHeight - 29, barWidth - 24, 24));
    }
    private static void Draw(Graphics g, string text, Font font, Color color, PointF point)
    { using var brush = new SolidBrush(color); g.DrawString(text, font, brush, point); }
    private static void DrawFit(Graphics g, string text, Font font, Color color, RectangleF area)
    { using var brush = new SolidBrush(color); using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }; g.DrawString(text, font, brush, area, format); }
    private PointF Logical(Point point) => new(point.X / DpiScale, point.Y / DpiScale);
    private int Hit(PointF p)
    { for (int i = 0; i < profile.Buttons.Length; ++i) if (ButtonRect(i).Contains(p)) return i; return -1; }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var p = Logical(e.Location); downForeground = Native.GetForegroundWindow();
        if (CloseRect.Contains(p)) { pressed = -3; Capture = true; return; }
        if (p.Y < 40) { dragging = true; dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top); Capture = true; return; }
        pressed = InputBlocked || busy ? -1 : Hit(p); if (pressed >= 0) Capture = true; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) MoveWithoutActivation(new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y));
        int next = Hit(Logical(e.Location)); if (next != hover) { hover = next; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int released = pressed; bool wasDrag = dragging; var p = Logical(e.Location);
        dragging = false; pressed = -1; Capture = false;
        if (wasDrag) { SavePosition(); Write("moved", new { Left, Top, foreground = Native.GetForegroundWindow().ToInt64() }); return; }
        if (released == -3 && CloseRect.Contains(p)) { Close(); return; }
        if (!InputBlocked && !busy && released >= 0 && Hit(p) == released) Preview(profile.Buttons[released]);
        Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { base.OnMouseCaptureChanged(e); if (!Capture) { pressed = -1; dragging = false; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    private void Preview(ButtonMapping button)
    {
        nint foreground = Native.GetForegroundWindow();
        bool kept = foreground == downForeground && foreground != Handle;
        if (live is not null)
        {
            if (!CheckIdentity()) return;
            if (!kept || !connected || backend is null) { status = "Tap cancelled: focus or connection changed."; Write("tap_cancelled", new { kept, connected }); return; }
            var targets = Routing.Resolve(live.Team, button.Id, live.Clients);
            if (targets.Length == 0) { status = "No active persona matches this button."; return; }
            ++clicks; busy = true; status = "Sending tap…";
            Write("tap_requested", new { id = clicks, buttonId = button.Id, targets, foreground = foreground.ToInt64() });
            remainingTaps = TapSequence.Create(targets); backend.Tap(clicks, remainingTaps.Dequeue()); return;
        }
        ++clicks; activationFault |= !kept;
        status = $"Preview #{clicks} · {(kept ? "Foreground kept" : "Focus warning")} · No game keys sent";
        Write("preview_action", new { button.Id, gesture = "tap", targets = button.Targets,
            foregroundBefore = downForeground.ToInt64(), foregroundAfter = foreground.ToInt64(), foregroundKept = kept });
    }
    private void MoveWithoutActivation(Point location)
    { if (!Native.SetWindowPos(Handle, 0, location.X, location.Y, 0, 0, 0x10 | 0x1 | 0x4)) throw new InvalidOperationException("Cannot move toolbar."); }
    private void Write(string kind, object data) => journal.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, kind, data }));
    private void SavePosition()
    {
        if (profilePath is null || !positionShown) return;
        try { WindowLayouts.SaveBar(profilePath, Location); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status = "Could not save bar position."; Write("position_save_failed", new { error.Message }); }
    }
    private bool CheckIdentity()
    {
        if (identityBlocked) return false;
        if (live?.Identities is null) return true;
        try
        {
            foreach (var expected in live.Identities)
                if (!Identity.Same(expected, ReadIdentity(expected))) throw new InvalidDataException("The character or active class changed.");
            return true;
        }
        catch (Exception error)
        {
            remainingTaps.Clear(); identityBlocked = true; pressed = -1; Capture = false; identityTimer.Stop();
            status = "Character changed · Refresh clients in Setup, then Save & apply";
            Write("identity_blocked", new { reason = error.Message });
            try { backend?.Send("cancel"); } catch (InvalidOperationException) { connected = false; Hide(); Close(); }
            Invalidate(); return false;
        }
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SavePosition();
        identityTimer.Stop();
        var mouse = mouseShield;
        mouseShield?.Dispose(); mouseShield = null;
        backend?.Dispose(); backend = null;
        if (focusHook != 0) Native.UnhookWinEvent(focusHook);
        Write("closed", new { clicks, activationFault, swallowedMouseEvents = mouse?.Swallowed, mousePostFault = mouse?.PostFault });
        base.OnFormClosed(e);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { identityTimer.Dispose(); backend?.Dispose(); backend = null; mouseShield?.Dispose(); mouseShield = null; titleFont.Dispose(); labelFont.Dispose(); bodyFont.Dispose(); } base.Dispose(disposing); }
    internal void SelfTest()
    {
        static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
        nint before = Native.GetForegroundWindow(); Check(before != 0, "Interactive desktop needed.");
        Show(); Application.DoEvents();
        Check(Native.GetForegroundWindow() == before, "Showing bar changed foreground.");
        Check(Native.SendMessageW(Handle, 0x21, Handle, (nint)(0x2010001)) == 3, "Mouse activation must retain click without activation.");
        void ClickAt(Point point)
        {
            nint packed = (nint)((int)(point.Y * DpiScale) << 16 | (int)(point.X * DpiScale));
            Native.SendMessageW(Handle, 0x201, 1, packed);
            Native.SendMessageW(Handle, 0x202, 0, packed); Application.DoEvents();
        }
        ClickAt(new Point(35, 70)); Check(clicks == 1, "One click must emit one mapped preview.");
        ClickAt(new Point(35, 70)); Check(clicks == 2, "Next deliberate click must remain enabled.");
        MoveWithoutActivation(new Point(Left + 25, Top + 25)); Application.DoEvents();
        Check(Native.GetForegroundWindow() == before && !activationFault, "Interaction or move changed foreground.");
        Write("self_test_pass", new { tests = "nonactivating show, activation response, one click/one mapping, always-enabled clicks, nonactivating move",
            routing = "persona override, character fallback, class fan-out, conflicts, one active persona per character",
            limitation = "Own-window message test; physical mouse activation and EQ mouse-input isolation still need manual validation." });
        Close();
    }
    internal void LiveSelfTest()
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void WaitFor(Func<bool> condition)
        {
            var until = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
            Check(condition(), "Bar/controller integration timed out.");
        }
        void ClickAt(Point point)
        {
            nint packed = (nint)((int)(point.Y * DpiScale) << 16 | (int)(point.X * DpiScale));
            Native.SendMessageW(Handle, 0x201, 1, packed); Native.SendMessageW(Handle, 0x202, 0, packed); Application.DoEvents();
        }
        nint before = Native.GetForegroundWindow();
        Check(!Visible, "Live bar must start hidden."); StartLive();
        WaitFor(() => connected && Visible);
        Check(!InputBlocked && clicks == 0 && Native.GetForegroundWindow() == before, "Connection activated the bar, sent an action, or failed to enable deliberate clicks.");
        ClickAt(new(35, 70)); WaitFor(() => clicks == 1 && !busy);
        Check(status.StartsWith("Tap delivered", StringComparison.Ordinal) && Native.GetForegroundWindow() == before, "Tap acknowledgment/focus check failed.");
        ReadIdentity = expected => expected.UserAssigned
            ? ClientSessions.Verify(expected, _ => new(expected.ProcessId, expected.StartedUtcTicks + 1, expected.Window, expected.Left, "Replaced session"), _ => throw new InvalidOperationException("Manual runtime used decoder."))
            : expected with { Character = new(expected.Character.Name, 2, expected.Character.Level) };
        Check(!CheckIdentity() && InputBlocked && identityBlocked, "Persona change must invalidate the old mapping.");
        ReadIdentity = expected => expected;
        ClickAt(new(35, 70));
        Check(InputBlocked && clicks == 1 && !CheckIdentity(), "Identity change must remain blocked until setup is reviewed.");
        backend!.Send("stop-test"); WaitFor(() => Failed && !Visible);
        Write("integration_test_pass", new { tests = "hidden startup, ready without resume, no startup action, click through controller pipe, acknowledgment, persona-change cancellation, tap blocked after identity change, close on disconnect",
            limitation = "Test-only controller; no game process was opened or input generated." });
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHookData { internal Point Point; internal uint MouseData, Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct Msg { internal nint Hwnd; internal uint Message; internal nuint WParam; internal nint LParam; internal uint Time; internal Point Point; internal uint Private; }
    internal delegate nint MouseProc(int code, nint wp, nint lp);
    internal delegate void WinEventProc(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? module);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int kind, MouseProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wp, nint lp);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint hwnd, uint message, nint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessageW(uint tid, uint message, nint wp, nint lp);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessageW(out Msg message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint SendMessageW(nint hwnd, uint message, nint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
}
