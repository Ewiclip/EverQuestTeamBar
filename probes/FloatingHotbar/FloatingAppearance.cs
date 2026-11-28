using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record FloatingTheme(string Id, string Name, string Text, string Button, string ButtonBorder,
    string Bar, string BarBorder, string Accent, bool Beveled = false, bool SerifHeadings = false, string? DetailText = null)
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}

internal sealed record FloatingAppearance
{
    public int Version { get; init; } = 1;
    public string ThemeId { get; init; } = "light";
    public FloatingTheme[] CustomThemes { get; init; } = [];
    public double Scale { get; init; } = 1;
    public double Opacity { get; init; } = 1;
    public bool Locked { get; init; }
    public bool HideHeaders { get; init; }
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
    [JsonIgnore] public AppearanceTheme? ApplicationTheme { get; init; }
    internal FloatingTheme Selected => ApplicationTheme is { } theme ? FloatingThemes.ForAppTheme(theme) : FloatingThemes.All(this).Single(t => t.Id == ThemeId);
}

internal static class FloatingThemes
{
    internal static FloatingAppearance ForApplication(UserSettings settings) => settings.Floating with { ApplicationTheme = settings.Theme };
    internal static FloatingTheme ForAppTheme(AppearanceTheme theme)
    {
        string Color(string key) => ThemePalette.Color(key, theme);
        return new("app-" + theme, theme.ToString(), Color("Text"), Color("Field"), Color("Line"),
            Color("Surface"), Color("PanelLine"), Color("Accent"), DetailText: Color("Muted"));
    }
    internal static readonly FloatingTheme[] Presets =
    [
        new("light", "Light", "#182B3A", "#FFFFFF", "#BBCBD5", "#E9EFF3", "#95AAB9", "#146B59"),
        new("dark", "Dark", "#EFF5F8", "#202D38", "#4A6070", "#121C25", "#5C7485", "#80DFC8"),
        new("everquest", "EverQuest", "#F1E9CE", "#222626", "#898264", "#14181C", "#AEA477", "#E0CB88", true),
        // Homepage card, heading, body and rim colors from https://eqtradeskills.com/.
        new("eqtradeskills", "EQTradeskills.com", "#DEAE35", "#15181E", "#634B2C", "#0E1016", "#6D4A22", "#F3CC49", true, true, "#DED5C4")
    ];
    internal static IEnumerable<FloatingTheme> All(FloatingAppearance value) => Presets.Concat(value.CustomThemes);
    internal static bool IsColor(string value) => value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
    internal static Color Color(string value) => ColorTranslator.FromHtml(value);
    internal static void Validate(FloatingAppearance value)
    {
        if (value is null || value.Version != 1 || value.CustomThemes is null || value.CustomThemes.Length > 40 ||
            !double.IsFinite(value.Scale) || value.Scale < .65 || value.Scale > 1.75 ||
            !double.IsFinite(value.Opacity) || value.Opacity < .35 || value.Opacity > 1)
            throw new InvalidDataException("Floating preferences are invalid or need a newer version of Team Bar.");
        foreach (var theme in value.CustomThemes)
        {
            if (theme is null || string.IsNullOrWhiteSpace(theme.Id) || theme.Id.Length > 80 || string.IsNullOrWhiteSpace(theme.Name) || theme.Name.Length > 40 ||
                new[] { theme.Text, theme.Button, theme.ButtonBorder, theme.Bar, theme.BarBorder, theme.Accent }.Any(c => !IsColor(c)) ||
                theme.DetailText is not null && !IsColor(theme.DetailText))
                throw new InvalidDataException("A custom theme needs a name and valid colors in #RRGGBB format.");
        }
        var themes = All(value).ToArray();
        if (themes.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != themes.Length ||
            themes.Select(t => t.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != themes.Length || !themes.Any(t => t.Id == value.ThemeId))
            throw new InvalidDataException("Choose an existing theme. Each custom theme needs a unique name and identifier.");
    }
}

// The runtime and Settings preview share geometry and paint code. No theme uses game textures.
internal static class FloatingPainter
{
    internal const int OuterPadding = 3;
    private const int HeaderHeight = 18;
    private const int ActionWidth = 206, ActionHeight = 42;
    private const int ColumnGap = 18, RowGap = 9;
    internal static bool ShowsHeader(string heading, FloatingAppearance appearance) => !appearance.HideHeaders && !string.IsNullOrWhiteSpace(heading);
    internal static Size LogicalSize(int columns, bool showHeader)
    {
        var last = ActionBounds(Math.Max(0, columns - 1), showHeader);
        return new(columns == 0 ? 0 : (int)last.Right + OuterPadding, (int)last.Bottom + OuterPadding);
    }
    internal static RectangleF ActionBounds(int column, bool showHeader, int row = 0) =>
        new(OuterPadding + column * (ActionWidth + ColumnGap),
            OuterPadding + (showHeader ? HeaderHeight + OuterPadding : 0) + row * (ActionHeight + RowGap), ActionWidth, ActionHeight);
    internal static string ActionHint(ButtonMapping action)
    {
        string detail = action.DisabledReason ?? string.Join("\n", action.Targets.Select(t => t.Character + " · " + t.Key));
        return string.IsNullOrWhiteSpace(action.Label) ? detail : action.Label + "\n" + detail;
    }
    internal static Color ConflictColor(FloatingTheme theme) => FloatingThemes.Color(theme.Button).GetBrightness() > .5f
        ? System.Drawing.Color.FromArgb(170, 37, 48) : System.Drawing.Color.FromArgb(242, 122, 132);
    internal static int ActionAt(int count, PointF point, bool showHeader)
    {
        for (int i = 0; i < count; i++) if (ActionBounds(i, showHeader).Contains(point)) return i;
        return -1;
    }
    internal static Color Mix(Color a, Color b, float amount) => System.Drawing.Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
    private static GraphicsPath Shape(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius == 0) { path.AddRectangle(r); return path; }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    internal static GraphicsPath SurfaceOutline(RectangleF bounds, float scale = 1) => Shape(bounds, 9 * scale);
    private static void DrawGrip(Graphics g, Color color, bool locked)
    {
        using var pen = new Pen(color, 1.1f);
        float x = OuterPadding + 3, y = OuterPadding + 3;
        if (locked)
        {
            g.DrawArc(pen, x + 1.5f, y, 6, 7, 180, 180);
            using var body = Shape(new(x, y + 5, 9, 7), 1.5f); g.DrawPath(pen, body);
        }
        else
        {
            using var dots = new SolidBrush(color);
            for (int col = 0; col < 2; col++)
                for (int row = 0; row < 3; row++) g.FillEllipse(dots, x + col * 5, y + 1 + row * 4, 1.8f, 1.8f);
        }
    }
    internal static void Draw(Graphics g, string heading, ButtonMapping[] actions, FloatingAppearance appearance,
        bool busy = false, int pressed = -1, int hovered = -1, RectangleF[]? cells = null, Size? canvas = null)
    {
        if (actions.Length == 0 && canvas is null) return;
        bool showHeader = ShowsHeader(heading, appearance);
        var theme = appearance.Selected; var size = canvas ?? LogicalSize(actions.Length, showHeader);
        var bg = FloatingThemes.Color(theme.Bar); var fg = FloatingThemes.Color(theme.Text); var accent = FloatingThemes.Color(theme.Accent);
        var conflictColor = ConflictColor(theme);
        var mutedColor = theme.DetailText is null ? Mix(fg, bg, .24f) : FloatingThemes.Color(theme.DetailText);
        using var text = new SolidBrush(fg); using var muted = new SolidBrush(mutedColor); using var accentBrush = new SolidBrush(accent);
        using var border = new Pen(FloatingThemes.Color(theme.BarBorder)); using var fill = new SolidBrush(bg);
        using var headingFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var labelFormat = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var shell = SurfaceOutline(new(.5f, .5f, size.Width - 1, size.Height - 1));
        g.FillPath(fill, shell); g.DrawPath(border, shell);
        if (showHeader)
        {
            DrawGrip(g, mutedColor, appearance.Locked);
            const int headingLeft = OuterPadding + HeaderHeight;
            g.DrawString(heading, headingFont, accentBrush, new RectangleF(headingLeft, OuterPadding, size.Width - headingLeft - OuterPadding, HeaderHeight), format);
        }
        for (int i = 0; i < actions.Length; i++)
        {
            var r = cells?[i] ?? ActionBounds(i, showHeader); var action = actions[i];
            bool disabled = action.DisabledReason is not null;
            var color = FloatingThemes.Color(theme.Button);
            if (disabled) color = Mix(color, conflictColor, .16f);
            else if (i == pressed) color = Mix(color, accent, .24f); else if (i == hovered) color = Mix(color, accent, .1f);
            using var buttonFill = new SolidBrush(color);
            using var buttonBorder = new Pen(disabled ? conflictColor : i == pressed || i == hovered ? accent : FloatingThemes.Color(theme.ButtonBorder), disabled || i == pressed ? 2 : 1);
            using var labelBrush = new SolidBrush(disabled ? conflictColor : fg);
            using var shape = Shape(r, 5);
            g.FillPath(buttonFill, shape); g.DrawPath(buttonBorder, shape);
            bool hasLabel = !string.IsNullOrWhiteSpace(action.Label);
            float inset = 9;
            float centerX = r.X + r.Width / 2;
            string glyph = OverlayButtonIcons.Glyph(action.Icon);
            if (action.Icon?.StartsWith("eq:", StringComparison.Ordinal) == true && EqIconArtwork.Shared.Draw(g, action.Icon,
                new RectangleF(hasLabel ? r.X + inset : centerX - 12, r.Y + (r.Height - 24) / 2, 24, 24)))
                inset += 29;
            else if (glyph.Length > 0)
            {
                using var iconFont = new Font("Segoe UI Symbol", 18, FontStyle.Regular, GraphicsUnit.Pixel);
                using var glyphFormat = new StringFormat(labelFormat) { Alignment = hasLabel ? StringAlignment.Near : StringAlignment.Center };
                g.DrawString(glyph, iconFont, labelBrush, new RectangleF(hasLabel ? r.X + inset : centerX - 13, r.Y, 26, r.Height), glyphFormat);
                inset += 26;
            }
            if (hasLabel) g.DrawString(action.Label, labelFont, labelBrush, new RectangleF(r.X + inset, r.Y, r.Width - inset - 9, r.Height), labelFormat);
            if (busy) g.FillEllipse(muted, r.Right - 8, r.Y + 5, 3, 3);
        }
    }
    internal static readonly ButtonMapping[] Samples = [new("invis", "Invisibility", [new("Runa", "5"), new("Fellwort", "5")]), new("assist", "Assist", [new("Runa", "1"), new("Fellwort", "2")])];
    internal static Bitmap Preview(FloatingAppearance appearance)
    {
        var bitmap = new Bitmap(1440, 250); using var g = Graphics.FromImage(bitmap);
        g.Clear(appearance.ApplicationTheme == AppearanceTheme.Light ? System.Drawing.Color.FromArgb(243, 246, 248) : System.Drawing.Color.FromArgb(16, 23, 30)); g.ScaleTransform(2, 2);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel); using var brush = new SolidBrush(FloatingThemes.Color(appearance.Selected.DetailText ?? appearance.Selected.Text));
        g.DrawString("TEAM BAR", font, brush, 14, 12); g.DrawString("STANDALONE BUTTON", font, brush, 480, 12);
        // Layer composition previews opacity without making the settings window transparent.
        using var controls = new Bitmap(1440, 280); using (var cg = Graphics.FromImage(controls))
        {
            cg.ScaleTransform(2, 2);
            cg.TranslateTransform(14, 7); Draw(cg, "TEAM BAR", Samples, appearance);
            cg.ResetTransform(); cg.ScaleTransform(2, 2); cg.TranslateTransform(480, 7); Draw(cg, "STANDALONE", [Samples[0]], appearance);
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)appearance.Opacity });
        g.DrawImage(controls, new Rectangle(0, 31, 720, 140), 0, 0, 1440, 280, GraphicsUnit.Pixel, attrs);
        return bitmap;
    }
}
