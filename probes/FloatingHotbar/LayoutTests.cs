using System.Diagnostics;
using System.Text.Json;

internal static class LayoutTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static void Run(string log)
    {
        if (File.Exists(log)) throw new IOException("Test output must be new.");
        string profile = Path.GetFullPath(log) + ".profile.json";
        if (File.Exists(profile)) throw new IOException("Test profile must be new.");
        Rectangle[] areas = [new(-1920, 0, 1920, 1040), new(0, 40, 1920, 1040)];
        Check(WindowLayouts.Clamp(new(-1500, 100), new(600, 180), areas) == new Point(-1500, 100), "Negative monitor coordinates were lost.");
        Check(WindowLayouts.Clamp(new(1880, 1050), new(600, 180), areas) == new Point(1320, 900), "Partially offscreen bar was not clamped.");
        Check(WindowLayouts.Clamp(new(-1500, 100), new(600, 180), [areas[1]]) == new Point(0, 100), "Removed monitor was not handled.");
        Check(WindowLayouts.Clamp(new(100, 100), new(5000, 2000), [areas[1]]) == new Point(0, 40), "Oversized bar header must stay reachable.");
        bool rejected = false;
        try { WindowLayouts.Validate(new(1, [new("Runa", false, 0, 0, 800, 600), new("runa", false, 0, 0, 800, 600)])); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Duplicate layout character accepted.");
        using (var window = new Form { Text = "Team Bar layout test — no game input", StartPosition = FormStartPosition.Manual, Bounds = new(180, 180, 800, 450) })
        {
            window.Show(); Application.DoEvents();
            var original = WindowLayouts.Capture("Fixture", window.Handle);
            window.Bounds = new(260, 240, 660, 390); Application.DoEvents();
            WindowLayouts.Restore(window.Handle, original); Application.DoEvents();
            Check(WindowLayouts.Capture("Fixture", window.Handle) == original, "Normal window size/position did not restore.");
            window.WindowState = FormWindowState.Maximized; Application.DoEvents();
            var maximized = WindowLayouts.Capture("Fixture", window.Handle);
            window.WindowState = FormWindowState.Normal; Application.DoEvents();
            WindowLayouts.Restore(window.Handle, maximized); Application.DoEvents();
            Check(WindowLayouts.Capture("Fixture", window.Handle) == maximized, "Maximized window placement did not restore.");
            window.Close();
        }
        var team = new TeamDefinition(1, [new("test", "Test client")], [new("test-persona", "test", null)],
            [new("test-button", "Before apply", [new(new(CharacterId: "test"), "5")])]);
        WindowLayouts.SaveJson(profile, team);
        var area = Screen.PrimaryScreen!.WorkingArea; var position = new Point(area.Left + 120, area.Top + 160);
        WindowLayouts.SaveBar(profile, position); Check(WindowLayouts.LoadBar(profile) == position, "Bar position round trip failed.");
        Check(BarSession.Name(profile) == BarSession.Name(profile.ToUpperInvariant()), "Profile endpoint must ignore path case.");
        Task.Run(async () =>
        {
            Check(await BarSession.Request(profile + ".unrelated", "status") is null, "Unexpected endpoint collision.");
            for (int i = 0; i < 2; ++i)
            {
                string sessionLog = log + $".session-{i}.jsonl";
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in new[] { "--session-fixture", profile, sessionLog }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Fixture launch failed.");
                try
                {
                    await BarSession.WaitReady(profile, process);
                    var state = await BarSession.Request(profile, "status");
                    Check(state is { Ready: true } && state.ProcessId == process.Id, "Wrong bar answered status.");
                    Check(await BarSession.CloseExisting(profile), "Apply did not find its existing bar.");
                    Check(process.HasExited && process.ExitCode == 0, "The old bar did not exit cleanly before reopening.");
                }
                finally { if (!process.HasExited) { await BarSession.CloseExisting(profile); if (!process.HasExited) process.Kill(); } }
                var records = File.ReadLines(sessionLog).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                var shown = records.Single(r => r.GetProperty("kind").GetString() == "shown").GetProperty("data");
                Check(shown.GetProperty("inputEnabled").GetBoolean(), "Reopened bar must be ready for deliberate clicks without Resume.");
                Check(shown.GetProperty("Left").GetInt32() == position.X && shown.GetProperty("Top").GetInt32() == position.Y, "Reopened bar lost its saved position.");
                Check(shown.GetProperty("buttons")[0].GetProperty("Label").GetString() == (i == 0 ? "Before apply" : "After apply"), "Updated button did not reach the new bar.");
                Check(!records.Any(r => r.GetProperty("kind").GetString() == "tap_requested"), "Apply generated unexpected input.");
                Check(records.Any(r => r.GetProperty("kind").GetString() == "closed"), "Old bar skipped cleanup.");
                Check(WindowLayouts.LoadBar(profile) == position, "Closing bar did not persist position.");
                team = team with { Buttons = [team.Buttons[0] with { Label = "After apply" }] }; WindowLayouts.SaveJson(profile, team);
            }
            Check(await BarSession.Request(profile, "status") is null, "Closed session left an active endpoint.");
        }).GetAwaiter().GetResult();
        using var output = new FileStream(log, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new { passed = true, tests = "negative monitor coordinates, removed monitor, partially offscreen and oversized bar, duplicate layout rejection, real test-window normal/maximized restore, saved bar position, isolated profile endpoint, two complete child-process/controller lifetimes, automatic close/wait/reopen, edited label reloaded, ready without resume, no tap on apply", limitation = "Own windows and protocol fixture only; no game process opened or game input generated." });
    }
}
