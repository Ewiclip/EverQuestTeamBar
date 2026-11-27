using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using D = System.Drawing;
using DI = System.Drawing.Imaging;

namespace TeamBar;

// EQ's A_DragItem and A_SpellIcons grids each use 36 40px cells in a 256px sheet.
// The XML frame order determines IDs, including when a sheet is missing on disk.
internal sealed class EqIconArtwork
{
    private const int Tile = 40, TilesPerRow = 6, TilesPerSheet = 36, FirstItem = 500;
    private readonly object gate = new();
    private sealed record Grid(bool Vertical, string?[] Frames);
    private readonly Lazy<Grid> itemFrames, spellFrames;
    private readonly Lazy<IReadOnlyList<string>> keys;
    private readonly OrderedDictionary<string, BitmapSource?> sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly OrderedDictionary<string, BitmapSource?> tiles = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, D.Bitmap> drawingTiles = new(StringComparer.Ordinal);
    private static EqIconArtwork? shared;
    internal static EqIconArtwork Shared => LazyInitializer.EnsureInitialized(ref shared, () => new(EqIconInstallation.Directory));
    internal string GameDirectory { get; }

    internal static void UseDirectory(string gameDirectory)
    {
        var old = Interlocked.Exchange(ref shared, new EqIconArtwork(gameDirectory));
        if (old is null) return;
        lock (old.gate)
        {
            foreach (var bitmap in old.drawingTiles.Values) bitmap.Dispose();
            old.drawingTiles.Clear();
        }
    }

    internal EqIconArtwork(string? gameDirectory)
    {
        GameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? "" : Path.GetFullPath(gameDirectory);
        itemFrames = new(() => ReadManifest("EQUI_DragItems.xml", "A_DragItem", @"\Adragitem[0-9]{1,4}\.(dds|tga)\z"));
        spellFrames = new(() => ReadManifest("EQUI_SpellIcons.xml", "A_SpellIcons", @"\Aspells[0-9]{1,4}\.(dds|tga)\z"));
        keys = new(() => FrameKeys(itemFrames.Value.Frames, "item", FirstItem).Concat(FrameKeys(spellFrames.Value.Frames, "spell", 0)).ToArray());
    }

    internal IReadOnlyList<string> Keys() => keys.Value;
    private static IEnumerable<string> FrameKeys(string?[] frames, string kind, int start)
    {
        for (int sheet = 0; sheet < frames.Length; sheet++)
            if (frames[sheet] is not null)
                for (int cell = 0; cell < TilesPerSheet; cell++)
                    yield return Key(kind, start + sheet * TilesPerSheet + cell);
    }

    private Grid ReadManifest(string filename, string animationName, string filenamePattern)
    {
        if (GameDirectory.Length == 0) return new(false, []);
        string directory = Path.Combine(GameDirectory, "uifiles", "default");
        string manifest = Path.Combine(directory, filename);
        if (!File.Exists(manifest)) manifest = Path.Combine(directory, "EQUI_Animations.xml");
        try
        {
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            var animation = XDocument.Load(reader).Root?.Elements("Ui2DAnimation").FirstOrDefault(a => (string?)a.Attribute("item") == animationName);
            if ((string?)animation?.Element("Grid") != "true" || !bool.TryParse((string?)animation.Element("Vertical"), out bool vertical)
                || (string?)animation.Element("CellWidth") != "40" || (string?)animation.Element("CellHeight") != "40") return new(false, []);
            var frames = animation.Elements("Frames").Take(1025).ToArray();
            if (frames.Length > 1024) return new(false, []);
            var names = new Regex(filenamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return new(vertical, frames.Select(frame =>
            {
                string name = (string?)frame.Element("Texture") ?? "";
                if (!names.IsMatch(name) || (string?)frame.Element("Location")?.Element("X") != "0" || (string?)frame.Element("Location")?.Element("Y") != "0"
                    || (string?)frame.Element("Size")?.Element("CX") != "256" || (string?)frame.Element("Size")?.Element("CY") != "256") return null;
                string path = Path.Combine(directory, name);
                return File.Exists(path) ? path : null;
            }).ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException) { return new(false, []); }
    }

    private static string Key(string kind, int id) => "eq:" + kind + ":" + id.ToString(CultureInfo.InvariantCulture);
    internal BitmapSource? Get(string kind, int id) => Get(Key(kind, id));
    internal BitmapSource? Get(string? key)
    {
        if (key is null) return null;
        bool item = key.StartsWith("eq:item:", StringComparison.Ordinal);
        if (!item && !key.StartsWith("eq:spell:", StringComparison.Ordinal)) return null;
        if (!int.TryParse(key.AsSpan(item ? 8 : 9), NumberStyles.None, CultureInfo.InvariantCulture, out int id)) return null;
        int cell = item ? id - FirstItem : id;
        var grid = item ? itemFrames.Value : spellFrames.Value;
        if (cell < 0 || cell / TilesPerSheet >= grid.Frames.Length || grid.Frames[cell / TilesPerSheet] is not { } file) return null;
        lock (gate)
        {
            if (tiles.TryGetValue(key, out var result)) return result;
            if (!sheets.TryGetValue(file, out var sheet))
            {
                sheet = ReadSheet(file);
                if (sheets.Count >= 12) sheets.RemoveAt(0);
                sheets[file] = sheet;
            }
            if (sheet is not null)
            {
                int fastAxis = cell % TilesPerRow * Tile, slowAxis = cell % TilesPerSheet / TilesPerRow * Tile;
                var rect = grid.Vertical ? new Int32Rect(slowAxis, fastAxis, Tile, Tile) : new Int32Rect(fastAxis, slowAxis, Tile, Tile);
                var pixels = new byte[Tile * Tile * 4];
                sheet.CopyPixels(rect, pixels, Tile * 4, 0);
                // Unused atlas slots are occasionally fully transparent.
                for (int alpha = 3; alpha < pixels.Length; alpha += 4)
                {
                    if (pixels[alpha] == 0) continue;
                    result = BitmapSource.Create(Tile, Tile, 96, 96, PixelFormats.Bgra32, null, pixels, Tile * 4);
                    result.Freeze();
                    break;
                }
            }
            // A copied cell can outlive its sheet without retaining the full atlas.
            if (tiles.Count >= 512) tiles.RemoveAt(0);
            tiles[key] = result;
            return result;
        }
    }

    private static BitmapSource? ReadSheet(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length is < 18 or > 2097152) return null;
            byte[] bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            var sheet = path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                ? SpellIcons.DecodeTga(bytes) : DecodeDds(bytes);
            return sheet.PixelWidth == 256 && sheet.PixelHeight == 256 ? sheet : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException
            or NotSupportedException or ArgumentException or InvalidOperationException or COMException) { return null; }
    }

    internal static BitmapSource DecodeDds(byte[] bytes)
    {
        if (bytes.Length < 128 || !bytes.AsSpan(0, 4).SequenceEqual("DDS "u8) || BitConverter.ToUInt32(bytes, 4) != 124
            || BitConverter.ToUInt32(bytes, 12) != 256 || BitConverter.ToUInt32(bytes, 16) != 256 || BitConverter.ToUInt32(bytes, 76) != 32)
            throw new InvalidDataException("Invalid EQ item icon texture.");
        BitmapSource bitmap;
        if (BitConverter.ToUInt32(bytes, 84) == 0)
        {
            // WIC supports the BC-compressed DDS sheets. EQ also includes a raw BGRA sheet.
            const int stride = 256 * 4;
            if (BitConverter.ToUInt32(bytes, 80) != 0x41 || BitConverter.ToUInt32(bytes, 88) != 32 || BitConverter.ToUInt32(bytes, 92) != 0xff0000
                || BitConverter.ToUInt32(bytes, 96) != 0xff00 || BitConverter.ToUInt32(bytes, 100) != 0xff || BitConverter.ToUInt32(bytes, 104) != 0xff000000
                || (BitConverter.ToUInt32(bytes, 8) & 8) != 0 && BitConverter.ToUInt32(bytes, 20) != stride
                || bytes.Length < 128 + 256 * stride) throw new InvalidDataException("Unsupported raw DDS layout.");
            bitmap = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, bytes.AsSpan(128, 256 * stride).ToArray(), stride);
        }
        else
        {
            var format = bytes.AsSpan(84, 4);
            if (!(format.SequenceEqual("DXT1"u8) || format.SequenceEqual("DXT3"u8) || format.SequenceEqual("DXT5"u8)))
                throw new InvalidDataException("Unsupported DDS compression.");
            using var input = new MemoryStream(bytes, writable: false);
            bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        }
        bitmap.Freeze();
        return bitmap;
    }

    internal bool Draw(D.Graphics graphics, string? key, D.RectangleF bounds)
    {
        if (key is null) return false;
        lock (gate)
        {
            if (!drawingTiles.TryGetValue(key, out var bitmap))
            {
                if (Get(key) is not { } source) return false;
                bitmap = new D.Bitmap(Tile, Tile, DI.PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(new D.Rectangle(0, 0, Tile, Tile), DI.ImageLockMode.WriteOnly, DI.PixelFormat.Format32bppArgb);
                try { source.CopyPixels(Int32Rect.Empty, data.Scan0, Tile * data.Stride, data.Stride); }
                finally { bitmap.UnlockBits(data); }
                if (drawingTiles.Count >= 128)
                {
                    drawingTiles.GetAt(0).Value.Dispose();
                    drawingTiles.RemoveAt(0);
                }
                drawingTiles.Add(key, bitmap);
            }
            graphics.DrawImage(bitmap, bounds);
            return true;
        }
    }
}
