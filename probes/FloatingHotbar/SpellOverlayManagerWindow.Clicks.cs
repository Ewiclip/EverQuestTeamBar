using System.Diagnostics;
using System.Windows.Interop;
using W = System.Windows;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private const string RestoredWindowRequired = "Expected one restored EQ window";
    private string? clickSession, lastHostAttempt, ownedHostCharacters, hostConnectionMessage;
    private readonly Dictionary<string, long> clickRegistrations = [];
    private DateTime nextClickCheck;
    private DateTime nextHostRetry = DateTime.MaxValue;
    private int hostRetryFailures;
    private bool connectingClicks, checkingClicks, ownedHostNeedsRestart;
    private AppInputSession? ownedClickHost;
    internal Func<SpellClickRequest, Task<SpellClickReply?>> RequestSpellInput = SpellOverlayInputPipe.Request;
    internal Func<Task<AppInputSession>> StartSpellInputHost = SpellOverlayInputHost.Start;
    internal Func<IDisposable?> AcquireClickTransition = () => SpellOverlayInputHost.TryBeginTransition();
    internal Func<DateTime> ClickClock = () => DateTime.UtcNow;

    private DetectedClient[] ConfiguredInputClients(SpellOverlayDefinition[] configured) => detected
        .Where(c => configured.Any(d => d.Character.Equals(c.Character.Name, StringComparison.OrdinalIgnoreCase)))
        .Concat(SpellDockRules.RecipientsFor(configured, savedDockButtons, detected)).DistinctBy(c => c.ProcessId).ToArray();
    private string ConfiguredCharacters(SpellOverlayDefinition[] configured) => string.Join("|", configured.Select(d => d.Character.ToUpperInvariant())
        .Concat(ConfiguredInputClients(configured).Select(c => c.Character.Name.ToUpperInvariant())).Distinct().Order());
    private string HostAttemptKey(SpellOverlayDefinition[] configured) => ConfiguredCharacters(configured) + ":" + string.Join("|",
        ConfiguredInputClients(configured)
            .OrderBy(c => c.ProcessId).Select(c => $"{c.ProcessId}:{c.StartedUtcTicks}:{c.Character.Class}"));
    private async Task RefreshClickSession()
    {
        var configured = applied.Where(d => d.HasClickMappings).ToArray();
        ClickSessionPanel.Visibility = configured.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        if (review || closed || exiting || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData) || checkingClicks || AppInputSession.IsRecovering
            || AppInputSession.HasPendingReconnect && AppInputSession.Current is null || configured.Length == 0 || ClickClock() < nextClickCheck) return;
        checkingClicks = true; nextClickCheck = ClickClock().AddSeconds(1);
        int epoch = generation;
        try
        {
            if (ownedClickHost is not null && (ownedHostNeedsRestart || ownedHostCharacters != ConfiguredCharacters(configured)))
            {
                using var transition = AcquireClickTransition();
                if (transition is null) return;
                await CloseOwnedClickHost();
            }
            var reply = await RequestSpellInput(new("status"));
            if (closed || exiting || epoch != generation) return;
            if (reply is null) reply = await ConnectSavedClicks(configured, epoch);
            if (closed || exiting || epoch != generation) return;
            if (reply?.RefreshCharactersRequired == true) RefreshChangedCharacters();
            // A successful connection ends the failed-start attempt. In particular,
            // a shared bar can replace our host and later close; permit reconnection.
            if (reply?.Ok == true) { ResetHostRetry(); hostConnectionMessage = reply.Message; }
            if (reply?.Session != clickSession) { clickRegistrations.Clear(); clickSession = reply?.Session; }
            string message = reply?.Message ?? hostConnectionMessage ?? "Waiting for a spell input connection";
            // Inactive persona bars must not consume the native controller's
            // eight simultaneous overlay slots. Release them before registering replacements.
            foreach (var definition in configured.Where(d => !floating[d.Id].IsVisible))
            {
                var window = floating[definition.Id];
                window.SetClickConnection(false, true, "Waiting for this bar's character and class", SendSpellClick);
                if (!clickRegistrations.TryGetValue(definition.Id, out long registeredWindow)) continue;
                var removed = await RequestSpellInput(new("unregister", registeredWindow, definition.Id));
                if (closed || exiting || epoch != generation) return;
                if (removed?.Ok == true) clickRegistrations.Remove(definition.Id);
            }
            foreach (var definition in configured)
            {
                var window = floating[definition.Id];
                if (!window.IsVisible) continue;
                var response = reply;
                long handle = new WindowInteropHelper(window).EnsureHandle().ToInt64();
                if (response?.Ok == true)
                {
                    // A healthy host does not prove this exact window is still registered.
                    // Validate the binding even when the local cache says it is connected.
                    bool known = clickRegistrations.TryGetValue(definition.Id, out long registeredWindow) && registeredWindow == handle;
                    if (known)
                    {
                        response = await RequestSpellInput(new("status", handle, definition.Id));
                        if (closed || exiting || epoch != generation) return;
                        if (response?.Session != clickSession) { InvalidateClickSession(); return; }
                    }
                    if (!known || response?.ReconnectRequired == true)
                    {
                        clickRegistrations.Remove(definition.Id);
                        window.SetClickConnection(false, true, "Connecting spell buttons…", SendSpellClick);
                        response = await RequestSpellInput(new("register", handle, definition.Id));
                    }
                    if (closed || exiting || epoch != generation) return;
                    if (response?.Session != clickSession) { InvalidateClickSession(); return; }
                    if (response?.Ok == true) clickRegistrations[definition.Id] = handle;
                    else clickRegistrations.Remove(definition.Id);
                }
                message = response?.Message ?? message;
                if (response?.RefreshCharactersRequired == true) RefreshChangedCharacters();
                // A stale/incompatible host that still reports Paused fails closed.
                // Current input hosts are ready immediately and require no Resume.
                window.SetClickConnection(response?.Ok == true, response?.Paused ?? true, message, SendSpellClick);
            }
            ClickSessionStatus.Text = message;
        }
        catch (Exception error)
        {
            if (closed || exiting || epoch != generation) return;
            clickSession = null; clickRegistrations.Clear();
            hostConnectionMessage = "Spell buttons unavailable · " + error.Message;
            ClickSessionStatus.Text = hostConnectionMessage;
            foreach (var window in floating.Values) window.SetClickConnection(false, true, hostConnectionMessage, SendSpellClick);
        }
        finally { checkingClicks = false; }
    }
    private void InvalidateClickSession()
    {
        clickSession = null; clickRegistrations.Clear(); nextClickCheck = DateTime.MinValue;
        foreach (var window in floating.Values)
            window.SetClickConnection(false, true, "Reconnecting spell buttons · click again when ready", SendSpellClick);
    }
    private async Task<SpellClickReply?> SendSpellClick(SpellClickRequest request)
    {
        int epoch = generation; string? session = clickSession;
        SpellClickReply? reply;
        try { reply = await RequestSpellInput(request); }
        catch { if (!closed && !exiting && epoch == generation && clickSession == session) InvalidateClickSession(); throw; }
        if (closed || exiting || epoch != generation || clickSession != session) return reply;
        if (reply is null || reply.Session != session) InvalidateClickSession();
        else if (reply.RefreshCharactersRequired)
        {
            RefreshChangedCharacters();
            InvalidateClickSession();
        }
        else if (reply.ReconnectRequired)
        {
            if (request.OverlayId is { } id && clickRegistrations.TryGetValue(id, out long handle) && handle == request.Window)
                clickRegistrations.Remove(id);
            nextClickCheck = DateTime.MinValue;
        }
        // Never retry a tap. The next poll repairs the registration; only a new
        // deliberate gesture can request another keypress.
        return reply;
    }
    private async Task<SpellClickReply?> ConnectSavedClicks(SpellOverlayDefinition[] configured, int epoch)
    {
        if (connectingClicks) return null;
        var waiting = configured.Select(d => d.Character).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => detected.Count(c => c.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) != 1).ToArray();
        if (waiting.Length > 0) return new(false, "Waiting for exactly one loaded " + string.Join(", ", waiting) + " client");
        string attempt = HostAttemptKey(configured);
        if (lastHostAttempt == attempt && ClickClock() < nextHostRetry)
            return new(false, hostConnectionMessage ?? "Spell input could not connect · Show overlays to retry");
        using var transition = AcquireClickTransition();
        if (transition is null) return new(false, "Updating the Team Bar input session…");
        foreach (var process in Process.GetProcessesByName("TeamController"))
            using (process) return new(false, "Waiting for the current Team Bar input session");
        if (lastHostAttempt != attempt) hostRetryFailures = 0;
        connectingClicks = true; lastHostAttempt = attempt; nextHostRetry = DateTime.MaxValue;
        try
        {
            if (ownedClickHost is not null) await CloseOwnedClickHost();
            if (closed || exiting || epoch != generation) return null;
            hostConnectionMessage = ClickSessionStatus.Text = "Connecting spell buttons…";
            var launched = await StartSpellInputHost();
            if (closed || exiting || epoch != generation) { launched.CloseIfOwned(); ResetHostRetry(); return null; }
            ownedClickHost = launched; ownedHostCharacters = ConfiguredCharacters(configured); ownedHostNeedsRestart = false;
            var reply = await RequestSpellInput(new("status"));
            hostConnectionMessage = reply?.Message ?? "Spell input has not connected · Show overlays to retry";
            return reply;
        }
        catch (IOException error) when (TransientInputStartup(error.Message))
        {
            // Reconnect the session, never replay a tap. Loading or minimized
            // clients can become ready without their PID or persona changing.
            hostRetryFailures = Math.Min(hostRetryFailures + 1, 4);
            int delay = Math.Min(30, 5 << (hostRetryFailures - 1));
            nextHostRetry = ClickClock().AddSeconds(delay);
            hostConnectionMessage = error.Message == RestoredWindowRequired
                ? "Waiting for EQ windows · restore each recipient's window; retrying automatically."
                : "Waiting for game input · retrying automatically. " + error.Message;
            return new(false, hostConnectionMessage);
        }
        finally { connectingClicks = false; }
    }
    private void ResetHostRetry()
    { lastHostAttempt = null; hostRetryFailures = 0; nextHostRetry = DateTime.MaxValue; }
    private static bool TransientInputStartup(string message) =>
        message.StartsWith("A client did not begin reading input", StringComparison.Ordinal)
        || message is "The input session has not finished connecting."
            or "A game client exited during connection"
            or RestoredWindowRequired
            or "Input device was replaced; reconnect the controls";
    private Task CloseOwnedClickHost()
    {
        var session = ownedClickHost;
        ownedClickHost = null; ownedHostCharacters = null; ownedHostNeedsRestart = false;
        // A replacement bar may already own input; closing this manager must
        // only stop the spell-only runtime it originally created.
        session?.CloseIfOwned();
        return Task.CompletedTask;
    }
}
