using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpriteForge;

public sealed class ExportWindow : Window
{
    private readonly Func<Project> projectSource;
    private Project source => projectSource();
    private readonly Action? undo, redo;
    private readonly SceneRenderer renderer;
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 12, 6, 6) };
    private readonly StackPanel fields = new() { Margin = new Thickness(16) };
    private readonly Button refresh = new() { Content = "Update preview" };
    private readonly Button export = new() { Content = "Export PNG…", IsEnabled = false };
    private readonly Button metadata = new() { Content = "Save frame metadata…", IsEnabled = false };
    private readonly List<(TextBox Input, string Name, Action<double> Set, double Min, double Max, bool Integer)> inputs = new();
    private RenderTargetBitmap? rendered;
    private Project? renderedProject;
    private bool busy, stale = true, closed;
    public SheetSettings Settings { get; private set; }
    public event Action? SettingsChanged;

    public ExportWindow(Project project, SceneRenderer renderer, Func<Project>? projectSource = null, Action? undo = null, Action? redo = null)
    {
        this.projectSource = projectSource ?? (() => project); this.renderer = renderer; this.undo = undo; this.redo = redo;
        Settings = JsonSerializer.Deserialize<SheetSettings>(JsonSerializer.Serialize(project.Sheet))!;
        Title = "Spritesheet preview / export"; Width = 1080; Height = 780; MinWidth = 800; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 20, 29)); Foreground = Brushes.WhiteSmoke;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); Content = grid;
        grid.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        fields.Children.Add(new TextBlock { Text = "SHEET LAYOUT", FontSize = 19, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 16) });
        Add("Cell width", Settings.CellWidth, 1, 4096, v => Settings.CellWidth = (int)v);
        Add("Cell height", Settings.CellHeight, 1, 4096, v => Settings.CellHeight = (int)v);
        Add("Columns", Settings.Columns, 1, 600, v => Settings.Columns = (int)v);
        Add("Rows", Settings.Rows, 1, 600, v => Settings.Rows = (int)v);
        Add("Outer margin", Settings.Margin, 0, 1024, v => Settings.Margin = (int)v);
        Add("Cell spacing", Settings.Spacing, 0, 1024, v => Settings.Spacing = (int)v);
        Add("Content offset X", Settings.OffsetX, -8192, 8192, v => Settings.OffsetX = v, false);
        Add("Content offset Y ↓", Settings.OffsetY, -8192, 8192, v => Settings.OffsetY = v, false);
        Add("Export scale", Settings.Scale, .01, 20, v => Settings.Scale = v, false);
        fields.Children.Add(new TextBlock { Text = $"{project.FrameCount} frames at {project.Fps} fps\nLeft to right, then top to bottom.\nUnused cells remain transparent.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 12) });
        refresh.Click += async (_, _) => await Generate(); fields.Children.Add(refresh);
        if (undo != null && redo != null)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var undoButton = new Button { Content = "Undo" }; undoButton.Click += async (_, _) => await RestoreSettings(undo);
            var redoButton = new Button { Content = "Redo" }; redoButton.Click += async (_, _) => await RestoreSettings(redo);
            buttons.Children.Add(undoButton); buttons.Children.Add(redoButton); fields.Children.Add(buttons);
        }
        export.Click += (_, _) => SavePng(); fields.Children.Add(export);
        metadata.Click += (_, _) => SaveMetadata(); fields.Children.Add(metadata);
        fields.Children.Add(message);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open()) { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(44, 51, 63)), null, new Rect(0, 0, 24, 24)); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(53, 61, 74)), null, new Rect(0, 0, 12, 12)); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(53, 61, 74)), null, new Rect(12, 12, 12, 12)); }
        var checker = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 24, 24) };
        var border = new Border { Background = checker, Margin = new Thickness(10), Padding = new Thickness(10), Child = preview }; Grid.SetColumn(border, 1); grid.Children.Add(border);
        Loaded += async (_, _) => await Generate(); Closed += (_, _) => closed = true;
        PreviewKeyDown += async (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.Key is not (Key.Z or Key.Y) || undo == null || redo == null) return;
            e.Handled = true; if (busy) return;
            bool forward = e.Key == Key.Y || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (Keyboard.FocusedElement is TextBox text && (forward ? text.CanRedo : text.CanUndo)) { if (forward) text.Redo(); else text.Undo(); }
            else await RestoreSettings(forward ? redo : undo);
        };
    }
    private void Add(string name, double value, double min, double max, Action<double> set, bool integer = true)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Text = value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) }; Grid.SetColumn(box, 1); row.Children.Add(box); fields.Children.Add(row);
        inputs.Add((box, name, set, min, max, integer)); box.TextChanged += (_, _) => { stale = true; export.IsEnabled = metadata.IsEnabled = false; message.Text = "Settings changed. Update the preview to apply them."; };
    }
    private async Task Generate()
    {
        if (busy) return;
        try
        {
            var parsed = new List<(Action<double> Set, double Value)>();
            foreach (var input in inputs)
            {
                if (!double.TryParse(input.Input.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < input.Min || v > input.Max || input.Integer && v != Math.Truncate(v))
                    throw new InvalidOperationException($"{input.Name}: enter {(input.Integer ? "a whole number" : "a number")} between {input.Min} and {input.Max}.");
                parsed.Add((input.Set, v));
            }
            // Replace the settings instance so the editor's undo snapshot retains the previous settings.
            Settings = JsonSerializer.Deserialize<SheetSettings>(JsonSerializer.Serialize(Settings))!;
            foreach (var item in parsed) item.Set(item.Value);
            var snapshot = source.Copy(); snapshot.Sheet = Settings; SceneRenderer.ValidateExport(snapshot);
            busy = true; fields.IsEnabled = false; export.IsEnabled = metadata.IsEnabled = false;
            var sheet = new WriteableBitmap(Settings.PixelWidth, Settings.PixelHeight, 96, 96, PixelFormats.Pbgra32, null);
            int stride = Settings.CellWidth * 4; byte[] pixels = new byte[stride * Settings.CellHeight];
            for (int f = 0; f < snapshot.FrameCount; f++)
            {
                if (closed) return;
                message.Text = $"Rendering frame {f + 1} / {snapshot.FrameCount}…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (closed) return;
                var bitmap = renderer.Frame(snapshot, f); bitmap.CopyPixels(pixels, stride, 0);
                int x = Settings.Margin + f % Settings.Columns * (Settings.CellWidth + Settings.Spacing), y = Settings.Margin + f / Settings.Columns * (Settings.CellHeight + Settings.Spacing);
                sheet.WritePixels(new Int32Rect(x, y, Settings.CellWidth, Settings.CellHeight), pixels, stride, 0);
            }
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(sheet, new Rect(0, 0, sheet.PixelWidth, sheet.PixelHeight));
            rendered = new RenderTargetBitmap(sheet.PixelWidth, sheet.PixelHeight, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual); rendered.Freeze();
            renderedProject = snapshot; preview.Source = rendered; stale = false; SettingsChanged?.Invoke();
            if (undo != null) foreach (var input in inputs) { input.Input.IsUndoEnabled = false; input.Input.IsUndoEnabled = true; }
            message.Text = $"{rendered.PixelWidth} × {rendered.PixelHeight} px · transparent PNG\nEach cell clips to its bounds. Adjust scale or offsets if the character is cut off.";
        }
        catch (Exception ex) { stale = true; message.Text = ex.Message; }
        finally { busy = false; fields.IsEnabled = true; export.IsEnabled = metadata.IsEnabled = !stale && rendered != null; }
    }
    private void SavePng()
    {
        if (rendered == null || stale) return;
        var dialog = new SaveFileDialog { Filter = "PNG spritesheet|*.png", FileName = source.Name + ".png" }; if (dialog.ShowDialog(this) != true) return;
        try { SceneRenderer.SavePng(rendered, dialog.FileName); message.Text = "Saved: " + dialog.FileName; } catch (Exception ex) { message.Text = ex.Message; }
    }
    private async Task RestoreSettings(Action action)
    {
        if (busy || closed) return;
        action(); var s = source.Sheet;
        double[] values = [s.CellWidth, s.CellHeight, s.Columns, s.Rows, s.Margin, s.Spacing, s.OffsetX, s.OffsetY, s.Scale];
        for (int i = 0; i < inputs.Count; i++) inputs[i].Input.Text = values[i].ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        await Generate();
    }
    private void SaveMetadata()
    {
        if (renderedProject == null || stale) return;
        var dialog = new SaveFileDialog { Filter = "JSON frame metadata|*.json", FileName = source.Name + ".json" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            var p = renderedProject; var s = p.Sheet;
            var frames = Enumerable.Range(0, p.FrameCount).Select(f => new { index = f, x = s.Margin + f % s.Columns * (s.CellWidth + s.Spacing), y = s.Margin + f / s.Columns * (s.CellHeight + s.Spacing), width = s.CellWidth, height = s.CellHeight, durationMs = 1000.0 / p.Fps, originX = s.CellWidth / 2.0 + s.OffsetX, originY = s.CellHeight / 2.0 + s.OffsetY });
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { name = p.Name, fps = p.Fps, width = s.PixelWidth, height = s.PixelHeight, frames }, Project.JsonOptions)); message.Text = "Saved frame metadata.";
        }
        catch (Exception ex) { message.Text = ex.Message; }
    }
    internal async Task<BitmapSource> SmokeSnapshot()
    {
        var originalColumns = source.Sheet.Columns;
        inputs.First(i => i.Name == "Columns").Input.Text = "1";
        inputs.First(i => i.Name == "Rows").Input.Text = "1";
        await Generate();
        if (export.IsEnabled || !stale) throw new Exception("Invalid sheet layout enabled export.");
        inputs.First(i => i.Name == "Columns").Input.Text = "2";
        await Generate();
        if (!export.IsEnabled || rendered == null || rendered.PixelWidth != Settings.PixelWidth || source.Sheet.Columns != originalColumns) throw new Exception("Export preview failed or modified source settings before applying.");
        inputs.First(i => i.Name == "Content offset X").Input.Text = "4";
        if (export.IsEnabled) throw new Exception("Stale preview enabled export.");
        await Generate();
        var content = (FrameworkElement)Content; content.Measure(new Size(1060, 740)); content.Arrange(new Rect(0, 0, 1060, 740)); content.UpdateLayout();
        var screenshot = new RenderTargetBitmap(1060, 740, 96, 96, PixelFormats.Pbgra32); screenshot.Render(content); return screenshot;
    }
    internal async Task VerifyUndoAsync()
    {
        int width = source.Sheet.CellWidth;
        inputs[0].Input.Text = (width + 16).ToString(); await Generate();
        if (source.Sheet.CellWidth != width + 16) throw new Exception("Preview did not apply settings.");
        await RestoreSettings(undo!);
        if (source.Sheet.CellWidth != width || Settings.CellWidth != width || rendered?.PixelWidth != source.Sheet.PixelWidth) throw new Exception("Export dialog undo did not restore and render settings.");
        await RestoreSettings(redo!);
        if (source.Sheet.CellWidth != width + 16 || Settings.CellWidth != width + 16) throw new Exception("Preview refresh erased redo.");
    }
}
