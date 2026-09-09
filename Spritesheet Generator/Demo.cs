using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpriteForge;

public static class Demo
{
    private static string Art(int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return Convert.ToBase64String(stream.ToArray());
    }
    public static Project Create()
    {
        var p = new Project { Name = "Little astronaut", FrameCount = 24, Fps = 12 };
        Brush mint = new SolidColorBrush(Color.FromRgb(95, 219, 195)), dark = new SolidColorBrush(Color.FromRgb(32, 48, 67)), cream = new SolidColorBrush(Color.FromRgb(235, 239, 223)), orange = new SolidColorBrush(Color.FromRgb(255, 185, 95));
        var outline = new Pen(dark, 3);
        Part Bone(string name, Part? parent, double x, double y, double angle, double length)
        {
            var bone = new Part { Name = name, IsBone = true, ParentId = parent?.Id, Rest = new Pose { X = x, Y = y, RotationZ = angle }, Length = length }; p.Parts.Add(bone); return bone;
        }
        Part Sprite(string name, Part bone, int width, int height, double pivotX, Action<DrawingContext> draw, double depth = 10)
        {
            var part = new Part { Name = name, ParentId = bone.Id, Width = width, Height = height, PivotX = pivotX, Thickness = depth, ImageData = Art(width, height, draw) }; p.Parts.Add(part); return part;
        }
        var root = Bone("Root", null, 0, 0, 0, 30);
        var legL = Bone("Leg L", root, -16, -31, -94, 48); var legR = Bone("Leg R", root, 16, -31, -86, 48);
        foreach (var leg in new[] { legL, legR }) Sprite(leg.Name + " sprite", leg, 58, 24, .08, dc => { dc.DrawRoundedRectangle(cream, outline, new Rect(2, 3, 48, 18), 6, 6); dc.DrawRoundedRectangle(dark, null, new Rect(39, 2, 17, 20), 5, 5); });
        var armL = Bone("Arm L", root, -29, 23, -110, 52); var armR = Bone("Arm R", root, 29, 23, -30, 52);
        foreach (var arm in new[] { armL, armR }) Sprite(arm.Name + " sprite", arm, 62, 24, .08, dc => { dc.DrawRoundedRectangle(mint, outline, new Rect(2, 3, 47, 18), 7, 7); dc.DrawEllipse(cream, outline, new Point(51, 12), 9, 9); });
        var body = Sprite("Suit", root, 68, 86, .5, dc => { dc.DrawRoundedRectangle(mint, outline, new Rect(5, 2, 58, 80), 16, 16); dc.DrawRoundedRectangle(cream, null, new Rect(15, 22, 38, 32), 6, 6); dc.DrawRectangle(dark, null, new Rect(22, 29, 24, 9)); dc.DrawEllipse(orange, null, new Point(24, 46), 3, 3); dc.DrawRectangle(dark, null, new Rect(7, 63, 54, 8)); }, 22);
        body.Rest.Z = 5;
        var head = Bone("Head", root, 0, 58, 0, 25);
        var helmet = Sprite("Helmet", head, 78, 68, .5, dc => { dc.DrawRoundedRectangle(cream, outline, new Rect(3, 3, 72, 61), 24, 24); dc.DrawRoundedRectangle(dark, null, new Rect(13, 16, 52, 33), 13, 13); dc.DrawRoundedRectangle(mint, null, new Rect(20, 21, 20, 6), 3, 3); dc.DrawEllipse(orange, null, new Point(55, 33), 4, 4); }, 24); helmet.Rest.Z = 8;
        var star = Sprite("Signal star", root, 22, 22, .5, dc => { var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(new Point(11, 1), true, true); c.PolyLineTo(new[] { new Point(14, 8), new Point(21, 11), new Point(14, 14), new Point(11, 21), new Point(8, 14), new Point(1, 11), new Point(8, 8) }, true, true); } dc.DrawGeometry(orange, null, g); }, 3); star.Rest.X = 63; star.Rest.Y = 75; star.Rest.Z = 10;
        foreach (var part in p.Parts) { part.Keys[0] = part.Rest.Copy(); part.Keys[23] = part.Rest.Copy(); }
        foreach ((int f, double angle) in new[] { (6, 55.0), (12, 25.0), (18, 65.0) }) { var pose = armR.Rest.Copy(); pose.RotationZ = angle; armR.Keys[f] = pose; }
        var bob = root.Rest.Copy(); bob.Y = 5; bob.RotationY = -12; root.Keys[12] = bob;
        var nod = head.Rest.Copy(); nod.RotationZ = -8; head.Keys[12] = nod;
        var hidden = star.Rest.Copy(); hidden.Visible = false; star.Keys[12] = hidden; star.Keys[18] = star.Rest.Copy();
        return p;
    }
}
