using System.Text.Json;

namespace TeamBar;

internal static class FloatingThemeTests
{
    internal static void Run(string path)
    {
        static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        var store = new LocalUserSettingsStore(path); var first = store.Load();
        Check(first.Floating.ThemeId == "light", "New floating controls must use Light by default.");
        var custom = FloatingThemes.Presets[1] with { Id = "custom-test", Name = "Ocean", Text = "#E8F9FF", BarBorder = "#5599AA" };
        var next = first.Floating with { CustomThemes = [custom], ThemeId = custom.Id, Opacity = .6, Scale = 1.45, Locked = true, HideHeaders = true };
        store.SaveFloating(next, first.Floating); store.Save(first with { Theme = AppearanceTheme.Light });
        Check(LocalUserSettingsStore.SameFloating(store.Load().Floating, next), "Changing the editor theme discarded floating preferences.");
        Check(store.Load().Floating.Selected == custom, "Named custom palette did not round-trip.");
        byte[] saved = File.ReadAllBytes(path); bool rejected = false;
        try { store.SaveFloating(first.Floating, first.Floating); } catch (IOException) { rejected = true; }
        Check(rejected && saved.SequenceEqual(File.ReadAllBytes(path)), "Stale custom themes overwrote another window's edits.");
        foreach (var invalid in new[] { next with { Scale = double.NaN }, next with { Opacity = 0 }, next with { ThemeId = "missing" }, next with { CustomThemes = [custom with { ButtonBorder = "transparent" }] }, next with { CustomThemes = [custom with { Name = "Dark" }] } })
        {
            rejected = false; try { store.SaveFloating(invalid, next); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && saved.SequenceEqual(File.ReadAllBytes(path)), "Invalid theme replaced saved settings.");
        }
        using var doc = JsonDocument.Parse("{\"future\":true}");
        var extra = next with { Extra = new() { ["future"] = doc.RootElement.Clone() } };
        store.SaveFloating(extra, next); store.Save(new() { Theme = AppearanceTheme.Dark });
        Check(store.Load().Floating.Extra!.ContainsKey("future"), "Unknown floating settings were lost.");
        foreach (var mode in Enum.GetValues<AppearanceTheme>())
        {
            var effective = FloatingThemes.ForApplication(store.Load() with { Theme = mode });
            Check(effective.Selected == FloatingThemes.ForAppTheme(mode), "Floating rendering must follow app appearance.");
            Check(effective.ThemeId == custom.Id && effective.CustomThemes.Single() == custom, "Applying app appearance discarded saved palettes.");
            Check(!JsonSerializer.Serialize(effective).Contains("ApplicationTheme"), "Transient appearance must not change the settings schema.");
        }
        foreach (bool hide in new[] { false, true })
        {
            var look = new FloatingAppearance { HideHeaders = hide };
            bool header = FloatingPainter.ShowsHeader("Named bar", look);
            var size = FloatingPainter.LogicalSize(2, header);
            var firstAction = FloatingPainter.ActionBounds(0, header);
            var action = FloatingPainter.ActionBounds(1, header);
            Check(!FloatingPainter.ShowsHeader("", look) && firstAction.Left == 3 && firstAction.Top == (header ? 24 : 3) &&
                size.Width - action.Right == 3 && size.Height - action.Bottom == 3 && action.Width == 206 && action.Height == 42 &&
                action.Left - firstAction.Right == 18, "Floating buttons retain their size and gutter with three-pixel outer edges and room for the optional title.");
            Check(FloatingPainter.ActionAt(2, new(2, 30), header) == -1 && FloatingPainter.ActionAt(2, new(220, 30), header) == -1 &&
                FloatingPainter.ActionAt(2, new(size.Width - 2, 30), header) == -1, "Bar padding sends a tap.");
            Check(FloatingPainter.ActionAt(2, new(250, 30), header) == 1 && FloatingPainter.ActionAt(2, new(40, firstAction.Top + 1), header) == 0,
                "Painted button and hit region disagree.");
            Check(FloatingPainter.ActionAt(1, new(40, 12), header) == (hide ? 0 : -1), "Hidden header leaves a stale hit region.");
        }
        foreach (var preset in FloatingThemes.Presets)
        {
            using var image = FloatingPainter.Preview(new() { ThemeId = preset.Id });
            Check(image.Width == 1440, "Preset failed to render.");
            var look = new FloatingAppearance { ThemeId = preset.Id };
            var size = FloatingPainter.LogicalSize(1, FloatingPainter.ShowsHeader("BUTTON", look));
            using var surface = new System.Drawing.Bitmap(size.Width, size.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(surface))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                FloatingPainter.Draw(graphics, "BUTTON", [FloatingPainter.Samples[0]], look);
            }
            Check(surface.GetPixel(0, 0).A == 0 && surface.GetPixel(size.Width / 2, 2).A > 0,
                "Every palette must render the rounded shell without a square corner.");
            using var outline = FloatingPainter.SurfaceOutline(new(0, 0, size.Width, size.Height));
            Check(!outline.IsVisible(0, 0) && outline.IsVisible(40, 12) && outline.IsVisible(40, 45),
                "Rounded shell must retain header dragging and action hit areas.");
        }
    }
    internal static void Export(string directory)
    {
        ExportControls(directory, AppearanceTheme.Light, "23-floating-controls-light.png");
        ExportControls(directory, AppearanceTheme.Dark, "24-floating-controls-dark.png");
    }
    private static void ExportControls(string directory, AppearanceTheme theme, string filename)
    {
        var look = FloatingThemes.ForApplication(new() { Theme = theme });
        using var image = new System.Drawing.Bitmap(1440, 610);
        using var g = System.Drawing.Graphics.FromImage(image);
        g.Clear(FloatingThemes.Color(ThemePalette.Color("AppBackground", theme))); g.ScaleTransform(2, 2);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var title = new System.Drawing.Font("Segoe UI Semibold", 18, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var caption = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var text = new System.Drawing.SolidBrush(FloatingThemes.Color(look.Selected.Text));
        using var muted = new System.Drawing.SolidBrush(FloatingThemes.Color(look.Selected.DetailText!));
        g.DrawString(theme + " · Buttons and groups", title, text, 14, 13);
        g.DrawString("Actual renderer · sample mappings · no game input", caption, muted, 14, 40);
        g.DrawString("Hotbar", caption, muted, 14, 68); g.DrawString("Single button", caption, muted, 480, 68);
        ButtonMapping[] single = [new("target", "Cycle targets", [new("Fellwort", "NumpadDivide")])];
        var state = g.Save(); g.TranslateTransform(14, 87); FloatingPainter.Draw(g, "TEAM BAR", FloatingPainter.Samples, look); g.Restore(state);
        state = g.Save(); g.TranslateTransform(480, 87); FloatingPainter.Draw(g, "STANDALONE", single, look with { Locked = true }); g.Restore(state);
        g.DrawString("Headers hidden · hover and press feedback", caption, muted, 14, 192);
        var compact = look with { HideHeaders = true, Locked = true };
        state = g.Save(); g.TranslateTransform(14, 215); FloatingPainter.Draw(g, "TEAM BAR", FloatingPainter.Samples, compact, hovered: 1); g.Restore(state);
        state = g.Save(); g.TranslateTransform(480, 215); FloatingPainter.Draw(g, "STANDALONE", single, compact, pressed: 0); g.Restore(state);
        image.Save(Path.Combine(directory, filename), System.Drawing.Imaging.ImageFormat.Png);
    }
}
