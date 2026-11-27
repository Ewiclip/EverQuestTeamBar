using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace TeamBar;

// Watched spell icons follow the Live game that supplied those spell IDs.
internal sealed class SpellIcons
{
    private static readonly string LiveDirectory = Path.GetDirectoryName(MemoryReader.LiveExecutablePath)!;
    private static readonly Lazy<EqIconArtwork> LiveAtlas = new(() => new(LiveDirectory));
    internal BitmapSource? Get(int? id)
    {
        if (id is null or < 0) return null;
        var selected = EqIconArtwork.Shared;
        var atlas = string.Equals(selected.GameDirectory, LiveDirectory, StringComparison.OrdinalIgnoreCase) ? selected : LiveAtlas.Value;
        return atlas.Get("spell", id.Value);
    }
    internal static BitmapSource DecodeTga(byte[] bytes)
    {
        if (bytes.Length < 18 || bytes[1] != 0 || bytes[2] != 2 || bytes[16] is not (24 or 32) || (bytes[17] & 0xc0) != 0)
            throw new InvalidDataException("Unsupported TGA image.");
        int width = BitConverter.ToUInt16(bytes, 12), height = BitConverter.ToUInt16(bytes, 14), bpp = bytes[16] / 8, start = 18 + bytes[0];
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || (long)start + (long)width * height * bpp > bytes.Length)
            throw new InvalidDataException("Truncated or oversized TGA image.");
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int srcY = (bytes[17] & 0x20) != 0 ? y : height - 1 - y, srcX = (bytes[17] & 0x10) != 0 ? width - 1 - x : x;
            int src = start + (srcY * width + srcX) * bpp, dst = (y * width + x) * 4;
            pixels[dst] = bytes[src]; pixels[dst + 1] = bytes[src + 1]; pixels[dst + 2] = bytes[src + 2];
            pixels[dst + 3] = bpp == 4 && (bytes[17] & 15) != 0 ? bytes[src + 3] : (byte)255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }
}
