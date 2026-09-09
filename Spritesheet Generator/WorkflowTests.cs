using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpriteForge;

internal static class WorkflowTests
{
    internal static void Run(Action<string, Action> test, string output)
    {
        test("Transparent overlapping sprites render correctly regardless of insertion order", () =>
        {
            var p = new Project { FrameCount = 1, Sheet = new SheetSettings { CellWidth = 64, CellHeight = 64, Columns = 1, Rows = 1 } };
            p.Parts.Add(new Part { ImageData = TestImage(true), Width = 32, Height = 32, Thickness = 0, Rest = new Pose { Z = 10 } });
            p.Parts.Add(new Part { ImageData = TestImage(false), Width = 32, Height = 32, Thickness = 0 });
            var image = new SceneRenderer().Frame(p, 0); var pixels = Pixels(image);
            var hole = Pixel(pixels, 64, 32, 32); var solid = Pixel(pixels, 64, 40, 26); var blend = Pixel(pixels, 64, 22, 26);
            SceneRenderer.SavePng(image, Path.Combine(output, "overlap-audit.png"));
            Assert(hole.A == 255 && hole.B > 200, $"Rear sprite is missing through the front sprite's transparent hole: {hole}; solid: {solid}; blend: {blend}.");
            Assert(solid.R > 180 && solid.B < 80, "Near sprite is incorrectly behind the rear sprite.");
            Assert(blend.A == 255 && blend.R > 70 && blend.B > 90, "Semitransparent foreground failed to blend with rear sprite.");
        });
        test("Equal-depth layers respect the visible layer order", () =>
        {
            var p = new Project { FrameCount = 1, Sheet = new SheetSettings { CellWidth = 64, CellHeight = 64, Columns = 1, Rows = 1 } };
            p.Parts.Add(new Part { ImageData = TestImage(false), Width = 32, Height = 32, Thickness = 0 });
            p.Parts.Add(new Part { ImageData = TestImage(true), Width = 32, Height = 32, Thickness = 0 });
            var renderer = new SceneRenderer(); var first = Pixel(Pixels(renderer.Frame(p, 0)), 64, 40, 26);
            p.Parts.Reverse(); var second = Pixel(Pixels(renderer.Frame(p, 0)), 64, 40, 26);
            Assert(first.R > 180 && second.B > 200, "Reordering equal-depth sprite layers does not reorder rendered output.");
        });
        test("Inspecting precise numeric fields does not round or modify the project", () =>
        {
            var editor = new EditorWindow(); editor.VerifyPrecisionAndLayerOrder(); editor.Close();
        });
    }
    internal static string TestImage(bool cutout)
    {
        byte[] data = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int i = (y * 32 + x) * 4;
            data[i] = cutout ? (byte)30 : (byte)230; data[i + 1] = 60; data[i + 2] = cutout ? (byte)220 : (byte)20;
            data[i + 3] = !cutout ? (byte)255 : x < 4 || x >= 28 || y < 4 || y >= 28 || x >= 12 && x < 20 && y >= 12 && y < 20 ? (byte)0 : x < 8 ? (byte)128 : (byte)255;
        }
        var source = BitmapSource.Create(32, 32, 192, 192, PixelFormats.Bgra32, null, data, 128);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var stream = new MemoryStream(); encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
    internal static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var data = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(data, image.PixelWidth * 4, 0); return data;
    }
    internal static (byte B, byte G, byte R, byte A) Pixel(byte[] data, int width, int x, int y) { int i = (y * width + x) * 4; return (data[i], data[i + 1], data[i + 2], data[i + 3]); }
    internal static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
