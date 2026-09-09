using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace SpriteForge;

public sealed class SceneRenderer
{
    private readonly Dictionary<string, BitmapSource> images = new();
    private readonly Dictionary<string, (string Signature, string? Front, string? Back, Model3DGroup Model)> models = new();
    public void Clear() { images.Clear(); models.Clear(); }
    public BitmapSource Image(string data)
    {
        if (images.TryGetValue(data, out var cached)) return cached;
        using var stream = new MemoryStream(Convert.FromBase64String(data));
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        if (image.PixelWidth > 8192 || image.PixelHeight > 8192) throw new InvalidDataException("Images must be at most 8192 pixels on each side.");
        images[data] = image;
        return image;
    }
    public static string Import(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Choose an image smaller than 32 MB.");
        return Convert.ToBase64String(File.ReadAllBytes(path));
    }
    private static IEnumerable<(Part Part, Matrix3D World)> SpriteTransforms(Project project, double frame, bool setup) =>
        project.Parts.Where(p => !p.IsBone && p.ImageData != null && project.Visible(p, frame, setup))
            .Select(p => (Part: p, World: project.World(p, frame, setup)))
            // WPF writes depth even for transparent texels. Draw far to near so
            // alpha holes and translucent edges reveal the already-rendered rear.
            .OrderBy(p => p.World.OffsetZ);

    public Part? HitSprite(Project project, double frame, bool setup, Point point)
    {
        foreach (var entry in SpriteTransforms(project, frame, setup).Reverse())
        {
            var p = entry.Part; var inverse = entry.World;
            bool back = entry.World.Transform(new Vector3D(0, 0, 1)).Z < 0;
            inverse.Invert();
            var a = inverse.Transform(new Point3D(point.X, point.Y, 10000));
            var b = inverse.Transform(new Point3D(point.X, point.Y, -10000));
            if (Math.Abs(b.Z - a.Z) < .00001) continue;
            var local = a + (b - a) * (-a.Z / (b.Z - a.Z));
            double u = local.X / p.Width + p.PivotX, v = p.PivotY - local.Y / p.Height;
            if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
            var image = new FormatConvertedBitmap(Image(back && p.BackImageData != null ? p.BackImageData : p.ImageData!), PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            image.CopyPixels(new Int32Rect((int)(u * image.PixelWidth), (int)(v * image.PixelHeight), 1, 1), pixel, 4, 0);
            if (pixel[3] > 16) return p;
        }
        return null;
    }
    private Model3DGroup Geometry(Part p)
    {
        var signature = $"{p.Width}/{p.Height}/{p.PivotX}/{p.PivotY}/{p.Thickness}/{p.RoundedDepth}";
        if (models.TryGetValue(p.Id, out var cached) && cached.Signature == signature && cached.Front == p.ImageData && cached.Back == p.BackImageData) return cached.Model;
        var image = Image(p.ImageData!);
        var front = new MeshGeometry3D(); var back = new MeshGeometry3D(); var sides = new MeshGeometry3D();
        var material = new DiffuseMaterial(new ImageBrush(image) { ViewportUnits = BrushMappingMode.RelativeToBoundingBox });
        var backMaterial = p.BackImageData == null ? material : new DiffuseMaterial(new ImageBrush(Image(p.BackImageData)));
        int nx = Math.Min(image.PixelWidth, 96), ny = Math.Min(image.PixelHeight, 96);
        if (p.Thickness == 0) nx = ny = 1;
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; source.CopyPixels(pixels, image.PixelWidth * 4, 0);
        bool[,] solid = new bool[nx, ny];
        for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
        {
            int px = Math.Min(image.PixelWidth - 1, (int)((x + .5) * image.PixelWidth / nx));
            int py = Math.Min(image.PixelHeight - 1, (int)((y + .5) * image.PixelHeight / ny));
            solid[x, y] = pixels[(py * image.PixelWidth + px) * 4 + 3] > 16;
        }
        double Depth(double u, double v) => p.Thickness * .5 * (p.RoundedDepth ? .25 + .75 * Math.Sqrt(Math.Max(0, 1 - Math.Pow(2 * u - 1, 2))) * Math.Sqrt(Math.Max(0, 1 - Math.Pow(2 * v - 1, 2))) : 1);
        Point3D Vertex(double u, double v, double sign) => new((u - p.PivotX) * p.Width, (p.PivotY - v) * p.Height, sign * Depth(u, v));
        for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
        {
            double u = (double)x / nx, v = (double)y / ny, u1 = (double)(x + 1) / nx, v1 = (double)(y + 1) / ny;
            var uv = new[] { new Point(u, v), new Point(u, v1), new Point(u1, v1), new Point(u1, v) };
            var f = new[] { Vertex(u, v, 1), Vertex(u, v1, 1), Vertex(u1, v1, 1), Vertex(u1, v, 1) };
            var b = new[] { Vertex(u, v, -1), Vertex(u, v1, -1), Vertex(u1, v1, -1), Vertex(u1, v, -1) };
            // The full textured surface retains fine alpha details even below the depth mesh resolution.
            Quad(front, f, uv); Quad(back, b, uv);
            if (p.Thickness <= 0 || !solid[x, y]) continue;
            var colorUv = Enumerable.Repeat(new Point((u + u1) / 2, (v + v1) / 2), 4).ToArray();
            if (x == 0 || !solid[x - 1, y]) Quad(sides, new[] { f[0], b[0], b[1], f[1] }, colorUv);
            if (x == nx - 1 || !solid[x + 1, y]) Quad(sides, new[] { f[2], b[2], b[3], f[3] }, colorUv);
            if (y == 0 || !solid[x, y - 1]) Quad(sides, new[] { f[3], b[3], b[0], f[0] }, colorUv);
            if (y == ny - 1 || !solid[x, y + 1]) Quad(sides, new[] { f[1], b[1], b[2], f[2] }, colorUv);
        }
        var result = new Model3DGroup();
        // Front faces point forward, backs are reversed so a custom back image is only seen from behind.
        for (int i = 0; i < back.TriangleIndices.Count; i += 3)
            (back.TriangleIndices[i + 1], back.TriangleIndices[i + 2]) = (back.TriangleIndices[i + 2], back.TriangleIndices[i + 1]);
        result.Children.Add(new GeometryModel3D(front, material));
        result.Children.Add(new GeometryModel3D(back, backMaterial));
        result.Children.Add(new GeometryModel3D(sides, material) { BackMaterial = material });
        result.Freeze(); models[p.Id] = (signature, p.ImageData, p.BackImageData, result); return result;
    }
    private static void Quad(MeshGeometry3D mesh, Point3D[] vertices, Point[] uv)
    {
        int start = mesh.Positions.Count;
        for (int i = 0; i < 4; i++) { mesh.Positions.Add(vertices[i]); mesh.TextureCoordinates.Add(uv[i]); }
        foreach (int i in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(start + i);
    }
    public void Populate(Viewport3D viewport, Project project, double frame, bool setup, double width, double height, double zoom, double offsetX = 0, double offsetY = 0)
    {
        viewport.Children.Clear();
        viewport.Camera = new OrthographicCamera(new Point3D(-offsetX / zoom, offsetY / zoom, 10000), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), width / zoom) { NearPlaneDistance = .1, FarPlaneDistance = 20000 };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White));
        foreach (var entry in SpriteTransforms(project, frame, setup))
        {
            var group = new Model3DGroup { Transform = new MatrixTransform3D(entry.World) };
            group.Children.Add(Geometry(entry.Part)); scene.Children.Add(group);
        }
        viewport.Children.Add(new ModelVisual3D { Content = scene });
    }
    public RenderTargetBitmap Frame(Project project, double frame)
    {
        var s = project.Sheet;
        var viewport = new Viewport3D { Width = s.CellWidth, Height = s.CellHeight, ClipToBounds = true };
        Populate(viewport, project, frame, false, s.CellWidth, s.CellHeight, s.Scale, s.OffsetX, s.OffsetY);
        viewport.Measure(new Size(s.CellWidth, s.CellHeight)); viewport.Arrange(new Rect(0, 0, s.CellWidth, s.CellHeight)); viewport.UpdateLayout();
        var bitmap = new RenderTargetBitmap(s.CellWidth, s.CellHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport); bitmap.Freeze(); return bitmap;
    }
    public static void ValidateExport(Project project)
    {
        project.Validate(); var s = project.Sheet;
        if ((long)s.Columns * s.Rows < project.FrameCount) throw new InvalidOperationException($"The sheet needs room for {project.FrameCount} frames. Increase rows or columns.");
        if (s.PixelWidth > 16384 || s.PixelHeight > 16384 || (long)s.PixelWidth * s.PixelHeight > 32_000_000)
            throw new InvalidOperationException("The sheet is too large. Keep each side below 16384 pixels and the total below 32 million pixels.");
    }
    public RenderTargetBitmap Sheet(Project project, Action<int>? progress = null)
    {
        ValidateExport(project); var s = project.Sheet;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            for (int i = 0; i < project.FrameCount; i++)
            {
                dc.DrawImage(Frame(project, i), new Rect(s.Margin + i % s.Columns * (s.CellWidth + s.Spacing), s.Margin + i / s.Columns * (s.CellHeight + s.Spacing), s.CellWidth, s.CellHeight));
                progress?.Invoke(i + 1);
            }
        var result = new RenderTargetBitmap(s.PixelWidth, s.PixelHeight, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
