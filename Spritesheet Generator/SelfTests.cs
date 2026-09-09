using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace SpriteForge;

internal static class SelfTests
{
    public static int Run()
    {
        System.Diagnostics.PresentationTraceSources.Refresh();
        using var bindingMessages = new StringWriter();
        using var bindingListener = new BindingTraceListener(bindingMessages);
        var bindingTrace = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        var previousBindingLevel = bindingTrace.Switch.Level;
        bindingTrace.Listeners.Add(bindingListener);
        bindingTrace.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var log = new List<string>(); var output = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        void Test(string name, Action test) { try { test(); log.Add("PASS " + name); } catch (Exception ex) { log.Add("FAIL " + name + ": " + ex); } }
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        bool Near(double a, double b) => Math.Abs(a - b) < .001;
        Test("Bone hierarchy inherits rotation and translation", () =>
        {
            var p = new Project(); var root = new Part { IsBone = true, Rest = new Pose { X = 10, Y = 20, RotationZ = 90 } }; var child = new Part { IsBone = true, ParentId = root.Id, Rest = new Pose { X = 30 } }; p.Parts.AddRange([root, child]); var point = p.World(child, 0).Transform(new Point3D()); Assert(Near(point.X, 10) && Near(point.Y, 50), "Child did not follow rotated parent.");
        });
        Test("Linear transform interpolation and stepped visibility", () =>
        {
            var p = new Part(); p.Keys[0] = new Pose(); p.Keys[10] = new Pose { X = 100, RotationY = 360, Visible = false }; p.Keys[15] = new Pose { Visible = true };
            Assert(Near(p.At(5).X, 50) && Near(p.At(5).RotationY, 180), "Incorrect interpolation."); Assert(p.At(9.99).Visible && !p.At(10).Visible && !p.At(14.9).Visible && p.At(15).Visible, "Visibility did not hold until next key."); Assert(Near(p.At(10, true).X, 0), "Setup should ignore keys.");
        });
        Test("Binding preserves a 3D rest pose and rejects cycles", () =>
        {
            var p = new Project(); var bone = new Part { IsBone = true, Rest = new Pose { X = 22, Y = -10, RotationX = 15, RotationY = 35, RotationZ = 40 } }; var sprite = new Part { Rest = new Pose { X = 17, Y = 55, RotationX = 5, RotationY = 13, RotationZ = 100, ScaleX = 1.3, ScaleY = .8 } }; p.Parts.AddRange([bone, sprite]); var before = p.World(sprite, 0, true); p.Reparent(sprite, bone.Id); var after = p.World(sprite, 0, true);
            foreach (var point in new[] { new Point3D(), new Point3D(20, 4, 3), new Point3D(-12, 25, -6) }) Assert((before.Transform(point) - after.Transform(point)).Length < .001, "Binding moved sprite.");
            var child = new Part { IsBone = true, ParentId = bone.Id }; p.Parts.Add(child); bool rejected = false; try { p.Reparent(bone, child.Id); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, "Cycle accepted.");
        });
        Test("Embedded project round trip and invalid hierarchy validation", () =>
        {
            var p = Demo.Create(); var loaded = Project.Deserialize(p.Serialize()); Assert(loaded.Parts.Count == p.Parts.Count && loaded.Parts.Last().ImageData == p.Parts.Last().ImageData && !loaded.Parts.Last().At(12).Visible, "Round trip lost content.");
            loaded.Parts[0].ParentId = "missing"; bool rejected = false; try { loaded.Validate(); } catch (InvalidDataException) { rejected = true; } Assert(rejected, "Invalid parent accepted.");
            File.WriteAllText(System.IO.Path.Combine(output, "Astronaut.spriteforge"), p.Serialize());
        });
        Test("Actual rendered PNG, margins, cells, visibility, and reference exclusion", () =>
        {
            var p = new Project { FrameCount = 2, Sheet = new SheetSettings { CellWidth = 48, CellHeight = 48, Columns = 2, Rows = 1, Margin = 3, Spacing = 5 } };
            var sprite = new Part { Width = 20, Height = 20, Thickness = 0, ImageData = SolidImage() }; sprite.Keys[0] = sprite.Rest.Copy(); sprite.Keys[1] = new Pose { Visible = false }; p.Parts.Add(sprite); p.ReferenceImage = sprite.ImageData; p.ReferenceScale = 5;
            var renderer = new SceneRenderer(); var sheet = renderer.Sheet(p); Assert(sheet.PixelWidth == 107 && sheet.PixelHeight == 54, "Wrong sheet dimensions.");
            byte[] pixels = Pixels(sheet); int Alpha(int x, int y) => pixels[(y * sheet.PixelWidth + x) * 4 + 3];
            Assert(Alpha(27, 27) > 200, "Visible sprite is missing."); Assert(Alpha(80, 27) == 0, "Hidden sprite or reference leaked into export."); Assert(Alpha(0, 0) == 0 && Alpha(53, 27) == 0, "Margin or spacing is not transparent.");
            var path = System.IO.Path.Combine(output, "visibility-test.png"); SceneRenderer.SavePng(sheet, path); using var stream = File.OpenRead(path); var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); Assert(decoded.Frames[0].PixelWidth == 107, "PNG round trip failed.");
            p.Sheet.OffsetX = 8; p.Sheet.OffsetY = 7; var shifted = renderer.Frame(p, 0); var shiftedPixels = Pixels(shifted); Assert(shiftedPixels[(31 * 48 + 32) * 4 + 3] > 200 && shiftedPixels[(15 * 48 + 15) * 4 + 3] == 0, "Content offset is wrong.");
        });
        Test("Generated depth fills edge-on rotation", () =>
        {
            var p = new Project { FrameCount = 1, Sheet = new SheetSettings { CellWidth = 64, CellHeight = 64, Columns = 1, Rows = 1 } }; var part = new Part { ImageData = SolidImage(), Width = 30, Height = 30, Thickness = 16, RoundedDepth = false, Rest = new Pose { RotationY = 90 } }; p.Parts.Add(part);
            var renderer = new SceneRenderer(); var frame = renderer.Frame(p, 0); var pixels = Pixels(frame); int opaque = Enumerable.Range(0, 64 * 64).Count(i => pixels[i * 4 + 3] > 200); Assert(opaque > 300, $"No filled side surface: {opaque} opaque pixels."); SceneRenderer.SavePng(frame, System.IO.Path.Combine(output, "depth-test.png"));
        });
        Test("Export rejects insufficient cells and excessive output", () =>
        {
            var p = new Project { Sheet = new SheetSettings { Columns = 1, Rows = 1 } }; bool rejected = false; try { SceneRenderer.ValidateExport(p); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, "Insufficient cells accepted."); p.FrameCount = 1; p.Sheet.CellWidth = 16384; p.Sheet.CellHeight = 16384; rejected = false; try { SceneRenderer.ValidateExport(p); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, "Excessive allocation accepted.");
        });
        Test("Demo animation renders a complete sheet", () =>
        {
            var p = Demo.Create(); var renderer = new SceneRenderer(); var sheet = renderer.Sheet(p); SceneRenderer.SavePng(sheet, System.IO.Path.Combine(output, "Astronaut.png")); Assert(Pixels(sheet).Where((_, i) => i % 4 == 3).Count(a => a > 0) > 10000, "Demo rendered empty.");
        });
        Test("Editor layout, automatic key editing, undo and redo", () =>
        {
            var editor = new EditorWindow(); var screenshot = editor.SmokeSnapshot(); SceneRenderer.SavePng(screenshot, System.IO.Path.Combine(output, "editor-preview.png")); editor.Close();
        });
        Test("Export dialog preview, invalid layout, and stale-preview protection", () =>
        {
            var p = new Project { FrameCount = 2, Sheet = new SheetSettings { CellWidth = 96, CellHeight = 96, Columns = 3, Rows = 1 } };
            p.Parts.Add(new Part { ImageData = SolidImage(), Width = 40, Height = 40, Thickness = 10 });
            var window = new ExportWindow(p, new SceneRenderer()); var task = window.SmokeSnapshot();
            var pump = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => pump.Continue = false)));
            System.Windows.Threading.Dispatcher.PushFrame(pump);
            SceneRenderer.SavePng(task.GetAwaiter().GetResult(), System.IO.Path.Combine(output, "export-preview.png")); window.Close();
        });
        Test("Undo coverage, drag cancellation, keyboard routing, and saved-state tracking", () =>
        {
            var editor = new EditorWindow();
            editor.VerifyUndoSafety(System.IO.Path.Combine(output, "visibility-test.png")); editor.Close();
        });
        Test("Undo and redo while the export dialog remains open", () =>
        {
            var p = new Project { FrameCount = 1, Sheet = new SheetSettings { CellWidth = 64, CellHeight = 64, Rows = 1, Columns = 1 } };
            p.Parts.Add(new Part { ImageData = SolidImage(), Width = 30, Height = 30 });
            var history = new EditHistory(); EditorState State() => new(p, null, 0, true);
            var window = new ExportWindow(p, new SceneRenderer(), () => p,
                () => { if (history.Undo(State()) is { } state) p = state.Project.Copy(); },
                () => { if (history.Redo(State()) is { } state) p = state.Project.Copy(); });
            window.SettingsChanged += () => { var before = State(); p.Sheet = window.Settings.Copy(); history.Commit(before, State()); };
            Pump(window.VerifyUndoAsync()); window.Close();
        });
        Test("One-minute timer, recovery snapshots, failed writes, and project switching", () =>
        {
            var store = new AutosaveStore(System.IO.Path.Combine(output, "autosave-tests", Guid.NewGuid().ToString("N")));
            var editor = new EditorWindow(store); Pump(editor.VerifyAutosaveSafetyAsync()); editor.Close();
        });
        Test("Atomic project backup and recovery from a corrupt newest copy", () =>
        {
            var directory = System.IO.Path.Combine(output, "storage-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "test.spriteforge"); var p = Demo.Create();
            ProjectStorage.WriteAtomic(path, p.Serialize()); p.ReferenceX = 20; ProjectStorage.WriteAtomic(path, p.Serialize());
            Assert(Project.Deserialize(File.ReadAllText(path)).ReferenceX == 20 && Project.Deserialize(File.ReadAllText(path + ".bak")).ReferenceX == 0, "Manual save backup is incorrect.");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool failed = false; try { ProjectStorage.WriteAtomic(path, "replacement"); } catch (IOException) { failed = true; }
                Assert(failed, "Locked destination was unexpectedly replaced.");
            }
            Assert(Project.Deserialize(File.ReadAllText(path)).ReferenceX == 20, "Failed write corrupted project.");
            Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed write left temporary files.");
            var store = new AutosaveStore(directory); var session = Guid.NewGuid(); var snapshot = new EditorState(p, p.Parts[0].Id, 7, false);
            Pump(store.WriteAsync(session, snapshot, path)); p.ReferenceX = 30; Pump(store.WriteAsync(session, new EditorState(p, null, 0, true), path));
            File.WriteAllText(store.PathFor(session), "{ interrupted/corrupt data");
            var entries = store.List(out int bad);
            Assert(bad == 1 && entries.Count == 1 && entries[0].IsBackup, "Corrupt newest copy prevented previous-copy recovery.");
            var recovered = new AutosaveStore(directory).Read(entries[0].Path);
            Assert(recovered.Project.ReferenceX == 20 && recovered.Frame == 7 && !recovered.Setup && recovered.SelectedId == p.Parts[0].Id && recovered.Project.Parts[0].Keys.Count > 0, "Recovery lost project or editing context across store instances.");
        });
        WorkflowTests.Run(Test, output);
        Test("WPF controls produce no binding warnings or errors", () =>
        {
            var editor = new EditorWindow(); Pump(editor.VerifyContainerLifecycleAsync()); editor.Close();
            Pump(System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task);
            bindingListener.Flush();
            File.WriteAllText(System.IO.Path.Combine(output, "binding-diagnostics.txt"), bindingMessages.ToString());
            File.WriteAllText(System.IO.Path.Combine(output, "binding-first-stack.txt"), bindingListener.FirstErrorStack ?? "");
            Assert(bindingMessages.GetStringBuilder().Length == 0, "WPF reported binding diagnostics. See artifacts/binding-diagnostics.txt.");
        });
        bindingTrace.Listeners.Remove(bindingListener); bindingTrace.Switch.Level = previousBindingLevel;
        File.WriteAllLines(System.IO.Path.Combine(output, "self-test-results.txt"), log); foreach (string line in log) Console.WriteLine(line);
        return log.Any(s => s.StartsWith("FAIL")) ? 1 : 0;
    }
    private static byte[] Pixels(BitmapSource bitmap) { var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels; }
    private sealed class BindingTraceListener(TextWriter writer) : System.Diagnostics.TextWriterTraceListener(writer)
    {
        public string? FirstErrorStack { get; private set; }
        public override void Write(string? message)
        {
            if (FirstErrorStack == null && message?.Contains("Cannot find source") == true) FirstErrorStack = Environment.StackTrace;
            base.Write(message);
        }
        public override void WriteLine(string? message) { Write(message); base.WriteLine(""); }
    }
    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static string SolidImage()
    {
        byte[] pixels = Enumerable.Range(0, 16 * 16).SelectMany(_ => new byte[] { 90, 200, 80, 255 }).ToArray(); var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return Convert.ToBase64String(stream.ToArray());
    }
}
