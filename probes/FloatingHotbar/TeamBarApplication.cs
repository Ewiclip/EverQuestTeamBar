using W = System.Windows;
using F = System.Windows.Forms;
namespace TeamBar;

internal sealed partial class TeamBarApplication : IDisposable
{
    internal static TeamBarApplication? Current { get; private set; }
    private readonly W.Application application;
    private readonly Func<string, string, SetupWindow> createSetup;
    private readonly Dictionary<string, SetupWindow> setups = new(StringComparer.OrdinalIgnoreCase);
    private ItemCollectorWindow? collector;
    private readonly F.NotifyIcon tray;
    private readonly F.ContextMenuStrip menu;
    private readonly F.ToolStripItem dataCollectionMenu;
    private readonly IUserSettingsStore preferences;
    private bool exiting, disposed, idleShutdownPending;
    private string? profile;
    private string? controller;

    internal static bool IsLaunch(string[] args) => args.Length == 0
        || args.Length == 1 && args[0] is "--spell-overlays" or "--items" or "--data-collection"
#if !TEAMBAR_PUBLIC_RELEASE
        || args.Length == 3 && args[0] is "--setup" or "--setup-current" or "--setup-edition"
#endif
        ;
    internal static string DefaultProfile()
    {
        if (InstalledAppData.IsInstalledBuild) return InstalledAppData.DefaultProfile();
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string workspaceProfiles = Path.Combine(folder.FullName, "profiles");
            string source = Path.Combine(workspaceProfiles, "team.json");
            if (File.Exists(source) || Directory.Exists(Path.Combine(workspaceProfiles, BuildEdition.Id))
                || File.Exists(Path.Combine(workspaceProfiles, "looping", "team.json"))
                || File.Exists(Path.Combine(workspaceProfiles, "manual", "team.json")))
                return EditionProfiles.Prepare(source);
        }
        return InstalledAppData.DefaultProfile();
    }
    private TeamBarApplication(W.Application app, Func<string, string, SetupWindow>? setupFactory = null, IUserSettingsStore? preferences = null)
    {
        application = app;
        this.preferences = preferences ?? new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath);
        createSetup = setupFactory ?? ((selectedProfile, selectedController) => new(selectedProfile, selectedController));
        menu = new();
        menu.Items.Add("Open Team Bar", null, (_, _) => OpenSafely(ShowDefaultSetup));
        menu.Items.Add(layoutMenu);
        layoutMenu.DropDownItems.Add(layoutSeparator);
        layoutMenu.DropDownItems.Add("Edit layouts…", null, (_, _) => OpenSafely(EditLayouts));
        endGameViewMenu = layoutMenu.DropDownItems.Add("End window layout", null, (_, _) => OpenSafely(EndGameView));
        endGameViewMenu.Visible = false;
        layoutMenu.DropDownOpening += (_, _) => PopulateLayoutMenu();
        menu.Items.Add("Spell overlays", null, (_, _) => OpenSafely(ShowSpells));
        dataCollectionMenu = menu.Items.Add("Data collection", null, (_, _) => OpenSafely(ShowData));
        UpdateDataCollectionMenu();
        menu.Items.Add(new F.ToolStripSeparator());
        menu.Items.Add("Close floating buttons", null, async (_, _) =>
        {
            try { if (AppInputSession.ActiveProfile is { } activeProfile) await AppInputSession.Clear(activeProfile); }
            catch (Exception error) { ShowError(error); }
        });
        menu.Items.Add("Exit spell overlays", null, async (_, _) =>
        {
            try { if (SpellOverlayManagerWindow.CurrentManager is { } manager) await manager.ExitOverlays(); }
            catch (Exception error) { ShowError(error); }
        });
        menu.Items.Add(new F.ToolStripSeparator());
        menu.Items.Add("Exit Team Bar", null, async (_, _) => await Exit());
        tray = new() { Icon = AppBranding.TrayIcon, Text = "Team Bar", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenSafely(ShowDefaultSetup);
    }
    internal static int Run(string[] args)
    {
        // Existing per-user settings already occupy the installed data scope.
        // Import legacy settings only after an explicit user selection in Settings.
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        using var interactionTrace = UiInteractionTrace.TryStart();
        TeamBarApplication? host = null;
        string scope = Path.Combine(Path.GetDirectoryName(LocalUserSettingsStore.DefaultPath)!, "application");
        using var instance = AppInstanceCoordinator.StartOrForward(scope, args,
            request => app.Dispatcher.InvokeAsync(() =>
            {
                if (!IsLaunch(request)) throw new InvalidDataException("Unsupported Team Bar launcher request.");
                host!.Open(request);
            }).Task).GetAwaiter().GetResult();
        if (instance is null) return 0;
        string? layoutRecoveryMessage = null;
        try { GameViewSession.RecoverPendingPlacements(); }
        catch (Exception error) { layoutRecoveryMessage = $"Window layout recovery needs attention. {error.Message}"; }
        using (host = new(app))
        {
            Current = host;
            try
            {
                host.MonitorLaunchSettings();
                host.Open(args);
                if (layoutRecoveryMessage is not null)
                {
                    host.GameViewStatus = layoutRecoveryMessage;
                    host.tray.ShowBalloonTip(5000, "Team Bar window layout", layoutRecoveryMessage, F.ToolTipIcon.Warning);
                }
                app.Dispatcher.BeginInvoke(() => OpenSafely(host.RestoreSpellOverlays));
                app.Run();
            }
            finally { Current = null; }
        }
        return 0;
    }
    private void Open(string[] args)
    {
        if (exiting) throw new InvalidOperationException("Team Bar is closing. Wait for it to finish, then reopen.");
        if (args.Length == 3)
        {
            string file = args[0] == "--setup" ? args[1] : ImportLabLayouts(EditionProfiles.Prepare(args[1]));
            ShowSetup(file, args[2]);
            return;
        }
        profile ??= ImportLabLayouts(DefaultProfile());
        if (args.Length == 1 && args[0] == "--spell-overlays") ShowSpells();
        else if (args.Length == 1 && args[0] is "--items" or "--data-collection") ShowData();
        else ShowDefaultSetup();
    }

    private string ImportLabLayouts(string file)
    {
        try { LayoutLabProfileMigration.ImportForBase(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GameViewStatus = $"Could not import saved Layout Lab layouts. {error.Message}";
            tray.ShowBalloonTip(5000, "Team Bar Screens", GameViewStatus, F.ToolTipIcon.Warning);
        }
        return file;
    }
    private void ShowDefaultSetup() => ShowSetup(profile ?? DefaultProfile(), controller ?? SpellOverlayInputHost.ControllerPath());
    internal SetupWindow? OpenSetupForProfile(string file) => setups.GetValueOrDefault(Path.GetFullPath(file));
    internal void ShowSetup(string selectedProfile, string selectedController)
    {
        profile = Path.GetFullPath(selectedProfile); controller = Path.GetFullPath(selectedController);
        var window = EnsureSetup(profile, controller);
        window.Show(); window.WindowState = W.WindowState.Normal; window.Activate();
        window.ShowHotKeys();
    }
    private SetupWindow EnsureSetup(string file, string controllerPath)
    {
        if (!setups.TryGetValue(file, out var window))
        {
            window = createSetup(file, controllerPath); setups.Add(file, window);
            window.Closed += (_, _) => { setups.Remove(file); CheckIdle(); };
        }
        return window;
    }
    internal void ShowSpells()
    {
        ShowDefaultSetup();
        if (!GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) { setups[profile!].OpenMemorySettings(); return; }
        setups[profile!].ShowSpellSettings(SpellOverlayManagerWindow.EnsureCurrent());
    }
    internal void ShowStandaloneLibrary(string? buttonId = null)
    {
        string selectedProfile = profile ?? DefaultProfile();
        ShowSetup(selectedProfile, controller ?? SpellOverlayInputHost.ControllerPath());
        setups[Path.GetFullPath(selectedProfile)].ShowStandaloneLibrary(buttonId);
    }
    internal void ObservePersonas(DetectedClient[] identities)
    {
        foreach (var setup in setups.Values) setup.ObservePersonas(identities);
    }
    internal void ShowData()
    {
        ShowDefaultSetup();
        if (!DataCollectionAccess.CanCollect) { setups[profile!].OpenMemorySettings(); return; }
        if (collector is null)
        {
            collector = new();
            collector.Closed += (_, _) => { collector = null; CheckIdle(); };
        }
        setups[profile!].ShowDataSettings(collector);
    }
    internal async Task MemoryPreferenceChanged(MemoryFeature feature, bool enabled)
    {
        if (feature == MemoryFeature.ScreenDetection) screenDetectionRevision++;
        UpdateDataCollectionMenu();
        if (!enabled)
        {
            AppInputSession.RevokeGameData(feature);
            if (feature == MemoryFeature.DataCollection) collector?.Close();
            if (feature == MemoryFeature.SpellBarData && SpellOverlayManagerWindow.CurrentManager is { } manager)
                await manager.SuspendMemoryReading();
        }
        foreach (var setup in setups.Values.ToArray())
            try { await setup.RefreshMemoryPreference(feature); }
            catch (Exception error) { setup.InputRecoveryStatus("Could not refresh preferences: " + error.Message); }
        if (enabled && feature == MemoryFeature.SpellBarData) RestoreSpellOverlays();
    }
    private void RestoreSpellOverlays()
    {
        if (GameDataAccess.IsEnabled(MemoryFeature.SpellBarData) && preferences.Load().SpellOverlaysLoaded) SpellOverlayManagerWindow.Open(restore: true);
    }
    private void UpdateDataCollectionMenu()
    {
        dataCollectionMenu.Visible = DataCollectionAccess.Enabled;
        dataCollectionMenu.Enabled = DataCollectionAccess.Enabled;
    }
    internal void CheckIdle()
    {
        if (exiting || disposed || idleShutdownPending || application.Dispatcher.HasShutdownStarted) return;
        idleShutdownPending = true;
        // Defer until the current close/apply operation has finished replacing resources.
        application.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (!IsIdle) return;
                await Backend.WaitForNativeShutdown();
                if (IsIdle) application.Shutdown();
            }
            catch (Exception error) { ShowError(error); }
            finally { idleShutdownPending = false; }
        });
    }
    private bool IsIdle => !exiting && !disposed && setups.Count == 0 && collector is null && activeGameView is null
        && SpellOverlayManagerWindow.CurrentManager is null && AppInputSession.Current is null && !AppInputSession.HasPendingReconnect;
    internal void NotifyInputRecovery(string message, string? profile = null, DetectedClient[]? identities = null)
    {
        tray.ShowBalloonTip(5000, "Team Bar connection", message, F.ToolTipIcon.Info);
        foreach (var setup in setups.Values) setup.InputRecoveryStatus(message, profile, identities);
    }
    internal void NotifyInputFailure(string message)
    {
        tray.ShowBalloonTip(5000, "Team Bar input disconnected", message, F.ToolTipIcon.Warning);
        OpenSafely(ShowDefaultSetup);
    }
    private static void OpenSafely(Action open) { try { open(); } catch (Exception error) { ShowError(error); } }
    private static void ShowError(Exception error) => W.MessageBox.Show(error.Message, "Team Bar", W.MessageBoxButton.OK, W.MessageBoxImage.Information);
    internal async Task Exit()
    {
        if (exiting) return;
        exiting = true;
        try
        {
            foreach (var setup in setups.Values.ToArray()) { setup.CloseForExit(); if (setups.Values.Contains(setup)) return; }
            if (SpellOverlayManagerWindow.CurrentManager is { } manager)
            { await manager.ExitOverlays(preserveLoadedState: true); if (SpellOverlayManagerWindow.CurrentManager is not null) return; }
            EndGameView();
            collector?.Close(); AppInputSession.Stop();
            await ReleaseUpdateCoordinator.Shared.FinishExit(Backend.WaitForNativeShutdown);
            application.Shutdown();
        }
        catch (Exception error) { ShowError(error); }
        finally { exiting = false; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try
        {
            EndGameView();
            // EQ can save its position as the window is restored or closed.
            if (launchSettingsTimer.IsEnabled)
            {
                launchSettingsTimer.Stop();
                CheckLaunchSettings(force: true);
            }
        }
        finally { AppInputSession.Stop(); tray.Dispose(); menu.Dispose(); }
    }
}
