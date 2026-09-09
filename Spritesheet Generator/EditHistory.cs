namespace SpriteForge;

// Snapshots own their mutable data but share immutable image strings. Undoing a
// transform must not duplicate megabytes of embedded image data at every step.
internal sealed class EditorState
{
    public Project Project { get; }
    public string? SelectedId { get; }
    public double Frame { get; }
    public bool Setup { get; }
    private readonly string settings;
    private readonly string?[] images;

    public EditorState(Project project, string? selectedId, double frame, bool setup)
    {
        Project = project.Copy(); SelectedId = selectedId; Frame = frame; Setup = setup;
        var metadata = Project.Copy();
        images = metadata.Parts.SelectMany(p => new[] { p.ImageData, p.BackImageData }).Prepend(metadata.ReferenceImage).ToArray();
        metadata.ReferenceImage = null;
        foreach (var part in metadata.Parts) { part.ImageData = null; part.BackImageData = null; }
        settings = metadata.Serialize();
    }
    public bool SameContent(EditorState other) => settings == other.settings && images.SequenceEqual(other.images);
}

internal sealed class EditHistory
{
    private readonly Stack<EditorState> undo = new();
    private readonly Stack<EditorState> redo = new();
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public bool Commit(EditorState before, EditorState after)
    {
        if (before.SameContent(after)) return false;
        undo.Push(before); redo.Clear(); return true;
    }
    public EditorState? Undo(EditorState current)
    {
        if (undo.Count == 0) return null;
        redo.Push(current); return undo.Pop();
    }
    public EditorState? Redo(EditorState current)
    {
        if (redo.Count == 0) return null;
        undo.Push(current); return redo.Pop();
    }
    public void Clear() { undo.Clear(); redo.Clear(); }
}
