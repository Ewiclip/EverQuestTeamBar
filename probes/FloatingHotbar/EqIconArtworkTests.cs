using System.Buffers.Binary;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using D = System.Drawing;

namespace TeamBar;

internal static class EqIconArtworkTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.GetFullPath(output) + ".atlas-" + Guid.NewGuid().ToString("N");
        string ui = Path.Combine(root, "uifiles", "default");
        Directory.CreateDirectory(ui);
        try
        {
            void Manifest(string file, string animation, bool vertical, params string[] frames)
            {
                var xml = new XElement("XML", new XElement("Ui2DAnimation", new XAttribute("item", animation),
                    new XElement("Grid", "true"), new XElement("Vertical", vertical), new XElement("CellWidth", 40),
                    new XElement("CellHeight", 40), frames.Select(f => new XElement("Frames", new XElement("Texture", f),
                        new XElement("Location", new XElement("X", 0), new XElement("Y", 0)),
                        new XElement("Size", new XElement("CX", 256), new XElement("CY", 256))))));
                xml.Save(Path.Combine(ui, file));
            }
            Manifest("EQUI_DragItems.xml", "A_DragItem", true, "dragitem1.dds", "dragitem2.dds", "dragitem3.dds");
            Manifest("EQUI_SpellIcons.xml", "A_SpellIcons", false, "Spells01.tga", "Spells02.tga");
            var raw = new byte[128 + 256 * 256 * 4];
            "DDS "u8.CopyTo(raw);
            void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
            Write(4, 124); Write(8, 0x100f); Write(12, 256); Write(16, 256); Write(20, 1024); Write(76, 32);
            Write(80, 0x41); Write(88, 32); Write(92, 0xff0000); Write(96, 0xff00); Write(100, 0xff); Write(104, 0xff000000);
            var tga = new byte[18 + 256 * 256 * 4];
            tga[2] = 2; tga[13] = 1; tga[15] = 1; tga[16] = 32; tga[17] = 0x28;
            for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int offset = (y * 256 + x) * 4;
                var pixel = raw.AsSpan(128 + offset, 4);
                pixel[0] = (byte)x; pixel[1] = (byte)y; pixel[2] = 211; pixel[3] = (byte)(x >= 200 && y >= 200 ? 0 : 180);
                pixel.CopyTo(tga.AsSpan(18 + offset, 4));
            }
            File.WriteAllBytes(Path.Combine(ui, "dragitem1.dds"), raw);
            File.WriteAllBytes(Path.Combine(ui, "dragitem3.dds"), raw);
            File.WriteAllBytes(Path.Combine(ui, "Spells01.tga"), tga);
            var atlas = new EqIconArtwork(root);
            static bool At(BitmapSource? bitmap, byte blue, byte green)
            {
                if (bitmap is not { IsFrozen: true, PixelWidth: 40, PixelHeight: 40 }) return false;
                var pixel = new byte[4];
                bitmap.CopyPixels(new(0, 0, 1, 1), pixel, 4, 0);
                return pixel[0] == blue && pixel[1] == green && pixel[2] == 211 && pixel[3] == 180;
            }
            check(At(atlas.Get("eq:item:501"), 0, 40) && At(atlas.Get("eq:spell:1"), 40, 0),
                "The item offset and both XML grid directions preserve RGBA and select the correct cell.");
            check(atlas.Keys().Count == 108 && !atlas.Keys().Contains("eq:item:536") && At(atlas.Get("eq:item:572"), 0, 0),
                "A missing item sheet keeps later IDs in place and is excluded from the picker.");
            check(atlas.Get("eq:item:499") is null && atlas.Get("eq:spell:36") is null && atlas.Get("eq:item:535") is null
                && atlas.Get("swords") is null && atlas.Get("eq:item:../../../x") is null,
                "Empty cells, missing images and unrelated or malformed keys safely fall back.");
            using var bitmap = new D.Bitmap(40, 40);
            using var graphics = D.Graphics.FromImage(bitmap);
            check(atlas.Draw(graphics, "eq:item:501", new D.RectangleF(0, 0, 40, 40)) && bitmap.GetPixel(10, 10).R >= 208
                && !atlas.Draw(graphics, "legacy-icon", new D.RectangleF(0, 0, 40, 40)),
                "Floating controls draw the same item tile with its alpha channel and allow glyph fallback.");
            File.WriteAllBytes(Path.Combine(ui, "dragitem1.dds"), [0, 0, 0]);
            var afterPatch = new EqIconArtwork(root);
            check(afterPatch.Get("eq:item:500") is null && At(afterPatch.Get("eq:spell:1"), 40, 0),
                "An unreadable item sheet leaves the other artwork available.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
