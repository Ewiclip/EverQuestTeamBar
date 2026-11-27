using System.Runtime.InteropServices;
using System.Text.Json;
using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private SpellOverlayInputPipe? spellInputPipe;
    private bool spellMouseSupported;
    private bool pendingSpellTap;
    private long spellRegistrationSequence;
    private readonly Dictionary<long, (int Process, string Overlay)> spellWindows = [];
    private readonly Dictionary<long, TaskCompletionSource<bool>> spellRegistrations = [];
    internal Func<SpellOverlayLibrary> ReadSpellLibrary = () => SpellOverlayStore.Load(SpellOverlayStore.DefaultPath).Library;
    internal Func<int, long?, TargetCapture> ReadSpellTarget = (pid, started) => TargetDecoder.Capture(pid, started, MemoryFeature.SpellBarData);
    internal Func<SavedButton[]> ReadDockButtons = SpellDockRules.LoadButtons;
    internal string SpellPipeName = SpellOverlayInputPipe.Name;
    private void StartSpellInputServer()
    {
        if (spellInputPipe is not null) return;
        string controllerName = Path.GetFileName(live.Controller);
        if (SpellPipeName == SpellOverlayInputPipe.Name
            && !controllerName.Equals("TeamController.exe", StringComparison.OrdinalIgnoreCase)
            && !controllerName.Equals("TeamControllerHost.dll", StringComparison.OrdinalIgnoreCase)) return;
        spellInputPipe = new((request, pid) =>
        {
            var completion = new TaskCompletionSource<SpellClickReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { BeginInvoke(async () =>
            {
                try { completion.TrySetResult(await HandleSpellRequest(request, pid)); }
                catch (Exception error) { completion.TrySetResult(new(false, error.Message)); }
            }); }
            catch (InvalidOperationException) { completion.TrySetResult(new(false, "The floating bar closed.")); }
            return completion.Task;
        }, SpellPipeName, () => { try { BeginInvoke(() => Close()); } catch (InvalidOperationException) { } });
    }
    private async Task<SpellClickReply> HandleSpellRequest(SpellClickRequest request, int requester)
    {
        if (IsDisposed || Disposing || !connected) return new(false, "Connect the floating bar first.");
        if (request.Kind is ("register" or "tap" or "dock-tap") && !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData))
            return new(false, GameDataAccess.DisabledReason(MemoryFeature.SpellBarData));
        if (!spellMouseSupported) return new(false, "Restart the game clients, then apply the updated Team Bar to enable spell buttons.");
        if (request.Kind == "status")
        {
            if ((request.Window != 0 || request.OverlayId is not null) && !HasSpellRegistration(request, requester))
                return MissingSpellRegistration;
            return SpellStatus();
        }
        if (request.Kind == "close-spell-host")
            return surfaces.Count == 0 && request.ProcessId == Environment.ProcessId
                ? new(true, "Spell input host closing", false)
                : new(false, "This input host is shared or belongs to another session.");
        if (request.Kind is "register" or "unregister")
        {
            GetWindowThreadProcessId((nint)request.Window, out uint owner);
            if (owner != requester) return new(false, "The spell window changed.");
            foreach (long stale in spellWindows.Where(p => !IsWindow((nint)p.Key)).Select(p => p.Key).ToArray()) spellWindows.Remove(stale);
            if (request.Kind == "register")
            {
                var definition = ReadSpellLibrary().Overlays.SingleOrDefault(d => d.Id == request.OverlayId);
                if (definition is null || !definition.Enabled || !definition.HasClickMappings) return new(false, "Save a spell key or docked button before connecting.");
                if (!live.Identities!.Any(c => c.Character.Name.Equals(definition.Character, StringComparison.OrdinalIgnoreCase)))
                    return new(false, $"Include {definition.Character} when applying your floating bar.");
                if (spellWindows.Count >= 8 && !spellWindows.ContainsKey(request.Window)) return new(false, "Use up to eight clickable overlays.");
                spellWindows[request.Window] = (requester, definition.Id);
            }
            else if (spellWindows.TryGetValue(request.Window, out var prior)
                && prior.Process == requester && prior.Overlay == request.OverlayId) spellWindows.Remove(request.Window);
            long id = ++spellRegistrationSequence;
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); spellRegistrations.Add(id, ack);
            try
            {
                backend!.Send("overlays " + id + " " + string.Join(" ", spellWindows.Select(p => $"{p.Value.Process} {p.Key}")));
                await ack.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Write("spell_overlay_registration", new { request.Kind, request.Window, request.OverlayId, requester, count = spellWindows.Count });
                return SpellStatus();
            }
            finally { spellRegistrations.Remove(id); }
        }
        if (request.Kind is not ("tap" or "dock-tap")) return new(false, "Unknown spell button action.");
        if (!HasSpellRegistration(request, requester))
        {
            Write("spell_overlay_registration_missing", new { request.Window, request.OverlayId, requester });
            return MissingSpellRegistration;
        }
        GetWindowThreadProcessId((nint)request.Window, out uint windowOwner);
        if (windowOwner != requester || !IsWindowVisible((nint)request.Window)) return new(false, "The spell overlay is hidden or changed.");
        if (cancelInFlight || busy) return new(false, "Previous request still finishing · click again");
        if (!CheckIdentities()) return SpellStatus();
        if (request.Foreground == 0 || Native.GetForegroundWindow().ToInt64() != request.Foreground || request.Foreground == request.Window)
            return new(false, "Click cancelled · focus changed.");
        var library = ReadSpellLibrary();
        RoutedTap[] taps;
        try
        {
            taps = request.Kind == "dock-tap" ? SpellDockRules.Resolve(library, ReadDockButtons(), request, live.Identities!, ReadSpellTarget)
                : [SpellOverlayClickRules.Resolve(library, request, live.Identities!, ReadSpellTarget)];
        }
        catch (InputRosterChangedException error)
        {
            // This gesture is rejected before creating a sequence. Only a new
            // click after discovery and reconnection may send input.
            Write("spell_input_roster_rejected", new { request.OverlayId, request.GroupId, request.ButtonId, error.Message,
                inputSent = false, inputReplayed = false });
            AppInputSession.Current?.RequestRosterRefresh();
            return new(false, error.Message, RefreshCharactersRequired: true);
        }
        if (Native.GetForegroundWindow().ToInt64() != request.Foreground) return new(false, "Click cancelled · focus changed.");
        busy = true; pendingSpellTap = true; pendingSince = TapClock();
        try { BeginTapSequence(taps, spellChannel: request.Kind != "dock-tap"); }
        catch { busy = pendingSpellTap = false; FailSession("Spell click delivery failed · reconnect input controls"); throw; }
        Write("spell_click_requested", new { id = sequence, request.OverlayId, request.SpellId, request.GroupId, request.ButtonId, targets = taps.Select(t => new { t.Label, t.Key }) });
        return new(true, "Requested " + string.Join(" · ", taps.Select(t => t.Key + " on " + t.Label)), false);
    }
    private static SpellClickReply MissingSpellRegistration => new(false, "Reconnecting spell buttons · click again when ready", ReconnectRequired: true);
    private bool HasSpellRegistration(SpellClickRequest request, int requester)
    {
        if (!spellWindows.TryGetValue(request.Window, out var registered)
            || registered.Process != requester || registered.Overlay != request.OverlayId || !IsWindow((nint)request.Window)) return false;
        GetWindowThreadProcessId((nint)request.Window, out uint owner);
        return owner == requester;
    }
    private SpellClickReply SpellStatus() => identityBlocked
        ? new(false, identityMismatchWaiting ? "Character identity changed · refreshing characters; click again when ready" : identityProblem,
            RefreshCharactersRequired: identityMismatchWaiting)
        : identityUnavailable ? new(false, IdentityWaitingMessage)
        : cancelInFlight ? new(false, "Input updating · wait for a new click")
        : new(true, "Spell input ready · one keypress per click", false);
    private void ReceiveSpellRegistration(JsonElement data)
    {
        if (data.TryGetProperty("id", out var id) && spellRegistrations.TryGetValue(id.GetInt64(), out var ack))
        {
            if (data.TryGetProperty("ok", out var ok) && !ok.GetBoolean()) ack.TrySetException(new IOException("Overlay registration changed · reconnect spell buttons"));
            else ack.TrySetResult(true);
        }
    }
    private void StopSpellInputServer()
    {
        spellInputPipe?.Dispose(); spellInputPipe = null;
        foreach (var ack in spellRegistrations.Values) ack.TrySetException(new IOException("Floating controls closed."));
        spellWindows.Clear();
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
