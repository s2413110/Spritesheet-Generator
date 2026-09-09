using System.IO;
using System.Text;
using System.Text.Json;

namespace SpriteForge;

internal static class ProjectStorage
{
    // A flushed sibling temporary file is atomically swapped into place. A failed
    // write cannot truncate the last successful project or its previous version.
    public static void WriteAtomic(string path, string content)
    {
        path = Path.GetFullPath(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
                writer.Write(content); writer.Flush(); stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class RecoveryDocument
{
    public int Version { get; set; } = 1;
    public DateTimeOffset SavedAtUtc { get; set; }
    public string? OriginalPath { get; set; }
    public Project Project { get; set; } = new();
    public string? SelectedId { get; set; }
    public double Frame { get; set; }
    public bool Setup { get; set; }
}

internal sealed record RecoveryEntry(string Path, string Name, DateTimeOffset SavedAtUtc, string? OriginalPath, bool IsBackup)
{
    public override string ToString() => $"{SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}   {Name}{(IsBackup ? " (previous copy)" : "")}\n{OriginalPath ?? "Never saved to a project file"}";
}

internal sealed class AutosaveStore
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public string DirectoryPath { get; }
    public AutosaveStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpriteForge", "Autosaves");
    public string PathFor(Guid session) => Path.Combine(DirectoryPath, session.ToString("N") + ".recovery");
    public Task WriteAsync(Guid session, EditorState state, string? originalPath)
    {
        // Capture everything before dispatching; a different project may be open
        // by the time disk I/O finishes. This copy cannot follow later UI edits.
        var document = new RecoveryDocument { Project = state.Project, OriginalPath = originalPath, SavedAtUtc = DateTimeOffset.UtcNow, SelectedId = state.SelectedId, Frame = state.Frame, Setup = state.Setup };
        return Task.Run(() =>
        {
            document.Project.Validate();
            Directory.CreateDirectory(DirectoryPath);
            ProjectStorage.WriteAtomic(PathFor(session), JsonSerializer.Serialize(document, Project.JsonOptions));
        });
    }
    public RecoveryDocument Read(string path)
    {
        var document = JsonSerializer.Deserialize<RecoveryDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty recovery copy.");
        if (document.Version != 1 || document.Project == null || !double.IsFinite(document.Frame)) throw new InvalidDataException("Invalid recovery copy.");
        document.Project.Validate(); return document;
    }
    public IReadOnlyList<RecoveryEntry> List(out int unreadable)
    {
        unreadable = 0; var entries = new List<RecoveryEntry>();
        if (!Directory.Exists(DirectoryPath)) return entries;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.recovery*").Where(p => p.EndsWith(".recovery", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".recovery.bak", StringComparison.OrdinalIgnoreCase)))
        {
            try { var copy = Read(path); entries.Add(new RecoveryEntry(path, copy.Project.Name, copy.SavedAtUtc, copy.OriginalPath, path.EndsWith(".bak"))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException) { unreadable++; }
        }
        return entries.OrderByDescending(e => e.SavedAtUtc).ToArray();
    }
}
