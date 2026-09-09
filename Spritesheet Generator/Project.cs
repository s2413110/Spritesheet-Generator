using System.IO;
using System.Text.Json;
using System.Windows.Media.Media3D;

namespace SpriteForge;

public sealed class Pose
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public Pose Copy() => (Pose)MemberwiseClone();
    public Matrix3D Matrix()
    {
        var m = Matrix3D.Identity;
        m.Scale(new Vector3D(ScaleX, ScaleY, 1));
        m.Rotate(new Quaternion(new Vector3D(1, 0, 0), RotationX));
        m.Rotate(new Quaternion(new Vector3D(0, 1, 0), RotationY));
        m.Rotate(new Quaternion(new Vector3D(0, 0, 1), RotationZ));
        m.Translate(new Vector3D(X, Y, Z));
        return m;
    }
    public static Pose FromMatrix(Matrix3D m, bool visible)
    {
        double sx = new Vector3D(m.M11, m.M12, m.M13).Length;
        double sy = new Vector3D(m.M21, m.M22, m.M23).Length;
        double ry = Math.Asin(Math.Clamp(-m.M13 / sx, -1, 1));
        double rx = Math.Atan2(m.M23 / sy, m.M33);
        double rz = Math.Atan2(m.M12 / sx, m.M11 / sx);
        if (Math.Abs(Math.Cos(ry)) < 0.00001) { rx = 0; rz = Math.Atan2(-m.M21 / sy, m.M22 / sy); }
        return new Pose { X = m.OffsetX, Y = m.OffsetY, Z = m.OffsetZ, ScaleX = sx, ScaleY = sy,
            RotationX = rx * 180 / Math.PI, RotationY = ry * 180 / Math.PI, RotationZ = rz * 180 / Math.PI, Visible = visible };
    }
    public static Pose Interpolate(Pose a, Pose b, double t) => new()
    {
        X = Lerp(a.X, b.X, t), Y = Lerp(a.Y, b.Y, t), Z = Lerp(a.Z, b.Z, t),
        RotationX = Lerp(a.RotationX, b.RotationX, t), RotationY = Lerp(a.RotationY, b.RotationY, t),
        RotationZ = Lerp(a.RotationZ, b.RotationZ, t), ScaleX = Lerp(a.ScaleX, b.ScaleX, t),
        ScaleY = Lerp(a.ScaleY, b.ScaleY, t), Visible = a.Visible
    };
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

public sealed class Part
{
    public Part Copy()
    {
        var copy = (Part)MemberwiseClone();
        copy.Rest = Rest.Copy();
        copy.Keys = new SortedDictionary<int, Pose>(Keys.ToDictionary(k => k.Key, k => k.Value.Copy()));
        return copy;
    }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Bone";
    public bool IsBone { get; set; }
    public string? ParentId { get; set; }
    public Pose Rest { get; set; } = new();
    public SortedDictionary<int, Pose> Keys { get; set; } = new();
    public string? ImageData { get; set; }
    public string? BackImageData { get; set; }
    public double Width { get; set; } = 80;
    public double Height { get; set; } = 80;
    public double PivotX { get; set; } = .5;
    public double PivotY { get; set; } = .5;
    public double Length { get; set; } = 70;
    public double Thickness { get; set; } = 12;
    public bool RoundedDepth { get; set; } = true;
    public Pose At(double frame, bool setup = false)
    {
        if (setup || Keys.Count == 0) return Rest.Copy();
        var previous = Keys.LastOrDefault(k => k.Key <= frame);
        var next = Keys.FirstOrDefault(k => k.Key > frame);
        Pose a = previous.Value ?? Rest;
        double start = previous.Value == null ? 0 : previous.Key;
        if (next.Value == null) return a.Copy();
        return Pose.Interpolate(a, next.Value, Math.Clamp((frame - start) / (next.Key - start), 0, 1));
    }
}

public sealed class SheetSettings
{
    public SheetSettings Copy() => (SheetSettings)MemberwiseClone();
    public int CellWidth { get; set; } = 256;
    public int CellHeight { get; set; } = 256;
    public int Columns { get; set; } = 6;
    public int Rows { get; set; } = 4;
    public int Margin { get; set; }
    public int Spacing { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Scale { get; set; } = 1;
    public int PixelWidth => checked(2 * Margin + Columns * CellWidth + (Columns - 1) * Spacing);
    public int PixelHeight => checked(2 * Margin + Rows * CellHeight + (Rows - 1) * Spacing);
}

public sealed class Project
{
    public Project Copy()
    {
        var copy = (Project)MemberwiseClone();
        copy.Parts = Parts.Select(p => p.Copy()).ToList();
        copy.Sheet = Sheet.Copy();
        return copy;
    }
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled character";
    public int FrameCount { get; set; } = 24;
    public int Fps { get; set; } = 12;
    public List<Part> Parts { get; set; } = new();
    public string? ReferenceImage { get; set; }
    public double ReferenceX { get; set; }
    public double ReferenceY { get; set; }
    public double ReferenceScale { get; set; } = 1;
    public double ReferenceOpacity { get; set; } = .3;
    public bool ReferenceVisible { get; set; } = true;
    public SheetSettings Sheet { get; set; } = new();
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
    public static Project Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize<Project>(json) ?? throw new InvalidDataException("Empty project.");
        p.Validate();
        return p;
    }
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("This project version is not supported.");
        if (FrameCount < 1 || FrameCount > 600 || Fps < 1 || Fps > 120 || Parts == null || Sheet == null)
            throw new InvalidDataException("Invalid project settings.");
        if (Parts.Count > 300 || Parts.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id)) || Parts.Select(p => p.Id).Distinct().Count() != Parts.Count)
            throw new InvalidDataException("Invalid or duplicate parts.");
        foreach (var part in Parts)
        {
            var visited = new HashSet<string> { part.Id };
            for (var parent = part.ParentId; parent != null;)
            {
                var node = Parts.FirstOrDefault(p => p.Id == parent);
                if (node == null || !node.IsBone || !visited.Add(parent)) throw new InvalidDataException("Invalid bone hierarchy.");
                parent = node.ParentId;
            }
            if (part.Rest == null || part.Keys == null || part.Keys.Any(k => k.Key < 0 || k.Key >= FrameCount || k.Value == null))
                throw new InvalidDataException("Invalid keyframes.");
            foreach (var pose in part.Keys.Values.Prepend(part.Rest))
                if (new[] { pose.X, pose.Y, pose.Z, pose.RotationX, pose.RotationY, pose.RotationZ, pose.ScaleX, pose.ScaleY }.Any(v => !double.IsFinite(v)) || pose.ScaleX <= 0 || pose.ScaleY <= 0)
                    throw new InvalidDataException("Invalid transform.");
            if (new[] { part.Width, part.Height, part.Length, part.Thickness, part.PivotX, part.PivotY }.Any(v => !double.IsFinite(v)) || part.Width <= 0 || part.Width > 8192 || part.Height <= 0 || part.Height > 8192 || part.Length < 1 || part.Length > 2000 || part.PivotX < 0 || part.PivotX > 1 || part.PivotY < 0 || part.PivotY > 1 || part.Thickness < 0 || part.Thickness > 200)
                throw new InvalidDataException("Invalid sprite dimensions.");
        }
        if (new[] { ReferenceX, ReferenceY, ReferenceScale, ReferenceOpacity, Sheet.OffsetX, Sheet.OffsetY, Sheet.Scale }.Any(v => !double.IsFinite(v)) || ReferenceScale <= 0 || ReferenceOpacity < 0 || ReferenceOpacity > 1 || Sheet.Scale <= 0 || Sheet.CellWidth < 1 || Sheet.CellHeight < 1 || Sheet.Columns < 1 || Sheet.Rows < 1 || Sheet.Margin < 0 || Sheet.Spacing < 0)
            throw new InvalidDataException("Invalid canvas settings.");
    }
    public Matrix3D World(Part part, double frame, bool setup = false)
    {
        var m = part.At(frame, setup).Matrix();
        if (Parts.FirstOrDefault(p => p.Id == part.ParentId) is { } parent) m.Append(World(parent, frame, setup));
        return m;
    }
    public bool Visible(Part part, double frame, bool setup = false) => part.At(frame, setup).Visible &&
        (Parts.FirstOrDefault(p => p.Id == part.ParentId) is not { } parent || Visible(parent, frame, setup));
    public bool IsDescendant(Part part, string ancestor) => part.ParentId != null &&
        (part.ParentId == ancestor || (Parts.FirstOrDefault(p => p.Id == part.ParentId) is { } parent && IsDescendant(parent, ancestor)));
    public void Reparent(Part part, string? parentId)
    {
        var parent = Parts.FirstOrDefault(p => p.Id == parentId);
        if (parentId != null && (parent == null || !parent.IsBone || parent.Id == part.Id || IsDescendant(parent, part.Id)))
            throw new InvalidOperationException("A bone cannot be parented to itself or its descendants.");
        // Hierarchy changes belong to setup. Preserve the rest placement; animation remains local to the new parent.
        var world = World(part, 0, true);
        if (parent != null)
        {
            var inverse = World(parent, 0, true);
            inverse.Invert();
            world.Append(inverse);
        }
        part.ParentId = parentId;
        part.Rest = Pose.FromMatrix(world, part.Rest.Visible);
    }
}
