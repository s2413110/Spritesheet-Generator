using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpriteForge;

public sealed class EditorWindow : Window
{
    private Project project = new();
    private readonly SceneRenderer renderer = new();
    private readonly ListBox hierarchy = new();
    private readonly StackPanel inspector = new();
    private readonly Grid stage = new() { ClipToBounds = true, Background = Brush("#191E29") };
    private readonly Canvas backdrop = new() { IsHitTestVisible = false };
    private readonly Viewport3D viewport = new() { IsHitTestVisible = false };
    private readonly Canvas overlay = new() { Background = Brushes.Transparent };
    private readonly Canvas timeline = new() { Background = Brush("#161B25"), Height = 150 };
    private readonly TextBlock status = new();
    private readonly TextBlock frameLabel = new();
    private readonly TextBlock modeLabel = new();
    private readonly Button playButton = new() { Content = "▶ Play" };
    private readonly ComboBox mode = new() { Width = 136, ItemsSource = new[] { "Rig setup", "Animate" }, SelectedIndex = 0 };
    private readonly Slider scrub = new() { Minimum = 0, Maximum = 23, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch playback = new();
    private readonly EditHistory history = new();
    private readonly AutosaveStore autosaves;
    private readonly DispatcherTimer autosaveTimer = new(DispatcherPriority.Background) { Interval = AutosaveStore.Interval };
    private readonly TextBlock autosaveStatus = new() { Text = "Autosave every minute", Margin = new Thickness(12, 8, 4, 0), Foreground = Brush("#65DDC2") };
    private EditorState? savedState, lastAutosavedState, dragBefore;
    private Guid sessionId = Guid.NewGuid();
    private bool autosaveBusy, autosaveDeferred, closed, suppressInputCommit;
    private int inspectorVersion;
    private string? selectedId, filePath;
    private bool rebuilding, dirty, drawingBone, dragging, rotateDrag, showBones = true;
    private double frame, zoom = 1.5, playStart;
    private Point dragStart;
    private Pose? dragPose;
    private Part? draftBone;
    private bool Setup => mode.SelectedIndex == 0;
    private Part? Selected => project.Parts.FirstOrDefault(p => p.Id == selectedId);
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    public EditorWindow() : this(new AutosaveStore()) { }
    internal EditorWindow(AutosaveStore autosaves)
    {
        this.autosaves = autosaves;
        Title = "Mandriel's Workshop"; Width = 1480; Height = 940; MinWidth = 1100; MinHeight = 730;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#10141D"); Foreground = Brush("#DCE3F0"); FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        SetStyles();
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(12) }; Content = root;
        var heading = new DockPanel { Margin = new Thickness(4, 0, 4, 12) };
        heading.Children.Add(new TextBlock { Text = "Mandriel's Workshop", FontSize = 23, FontWeight = FontWeights.Bold, Foreground = Brush("#65DDC2") });
        heading.Children.Add(new TextBlock { Text = "   /   rig · animate · export", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#8794AD") });
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AddButton(toolbar, "New", NewProject); AddButton(toolbar, "Open…", OpenProject); AddButton(toolbar, "Save", () => SaveProject(false)); AddButton(toolbar, "Save as…", () => SaveProject(true));
        AddButton(toolbar, "Undo", Undo); AddButton(toolbar, "Redo", Redo);
        AddButton(toolbar, "Recover autosave…", RecoverAutosave);
        AddButton(toolbar, "Import sprites…", ImportSprites); AddButton(toolbar, "Reference…", ImportReference);
        AddButton(toolbar, "Demo character", LoadDemo); AddButton(toolbar, "Preview / Export…", Export); AddButton(toolbar, "Help", Help);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        status.Margin = new Thickness(8, 8, 4, 0); status.Foreground = Brush("#9BAAC2");
        var statusBar = new DockPanel(); DockPanel.SetDock(autosaveStatus, Dock.Right); statusBar.Children.Add(autosaveStatus); statusBar.Children.Add(status);
        DockPanel.SetDock(statusBar, Dock.Bottom); root.Children.Add(statusBar);
        var timelinePanel = new DockPanel { Height = 214, Margin = new Thickness(0, 10, 0, 0) };
        var transport = new WrapPanel();
        playButton.Click += (_, _) => TogglePlay(); transport.Children.Add(playButton);
        AddButton(transport, "|◀", () => SetFrame(0)); AddButton(transport, "◀", () => SetFrame(frame - 1)); AddButton(transport, "▶", () => SetFrame(frame + 1));
        frameLabel.Width = 114; frameLabel.VerticalAlignment = VerticalAlignment.Center; transport.Children.Add(frameLabel);
        scrub.ValueChanged += (_, _) => { if (!rebuilding) SetFrame(scrub.Value); }; transport.Children.Add(scrub);
        AddButton(transport, "◆ Key selected", KeySelected); AddButton(transport, "◆ Key all", KeyAll); AddButton(transport, "Delete key", DeleteKey);
        AddButton(transport, "Animation settings…", AnimationSettings);
        DockPanel.SetDock(transport, Dock.Top); timelinePanel.Children.Add(transport);
        var timelineScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = timeline };
        timelinePanel.Children.Add(timelineScroll); DockPanel.SetDock(timelinePanel, Dock.Bottom); root.Children.Add(timelinePanel);
        var main = new Grid(); main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) }); main.ColumnDefinitions.Add(new ColumnDefinition()); main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(286) }); root.Children.Add(main);
        var left = new DockPanel { Background = Brush("#1B2230"), Margin = new Thickness(0, 0, 10, 0) };
        var leftTop = new StackPanel { Margin = new Thickness(10) }; leftTop.Children.Add(Label("CHARACTER / LAYERS"));
        leftTop.Children.Add(mode); mode.SelectionChanged += (_, _) => { if (rebuilding) return; Stop(); drawingBone = false; Refresh(); };
        var boneButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddButton(boneButtons, "+ Bone", BeginBone); AddButton(boneButtons, "Remove", RemovePart); leftTop.Children.Add(boneButtons);
        var order = new WrapPanel(); AddButton(order, "Layer ↑", () => MoveLayer(1)); AddButton(order, "Layer ↓", () => MoveLayer(-1)); leftTop.Children.Add(order);
        leftTop.Children.Add(new TextBlock { Text = "Select a bone, then + Bone to draw a child. Bind imported sprites in the inspector.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#98A6BD"), Margin = new Thickness(0, 10, 0, 0) });
        DockPanel.SetDock(leftTop, Dock.Top); left.Children.Add(leftTop);
        hierarchy.SelectionChanged += (_, _) => { if (rebuilding) return; selectedId = (hierarchy.SelectedItem as ListBoxItem)?.Tag as string; RefreshInspector(); Render(); DrawTimeline(); };
        hierarchy.Background = Brushes.Transparent; hierarchy.BorderThickness = new Thickness(0); left.Children.Add(hierarchy); main.Children.Add(left);
        var center = new DockPanel(); Grid.SetColumn(center, 1); main.Children.Add(center);
        var stageToolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) }; stageToolbar.Children.Add(modeLabel);
        var bonesCheck = new CheckBox { Content = "Show bones", IsChecked = true, Margin = new Thickness(12, 5, 8, 5) };
        bonesCheck.Click += (_, _) => { showBones = bonesCheck.IsChecked == true; Render(); }; stageToolbar.Children.Add(bonesCheck);
        AddButton(stageToolbar, "−", () => { zoom = Math.Max(.1, zoom / 1.25); Render(); }); AddButton(stageToolbar, "+", () => { zoom = Math.Min(8, zoom * 1.25); Render(); });
        AddButton(stageToolbar, "Fit", Fit); DockPanel.SetDock(stageToolbar, Dock.Top); center.Children.Add(stageToolbar);
        stage.Children.Add(backdrop); stage.Children.Add(viewport); stage.Children.Add(overlay); center.Children.Add(stage);
        stage.SizeChanged += (_, _) => Render(); overlay.MouseLeftButtonDown += PointerDown; overlay.MouseMove += PointerMove; overlay.MouseLeftButtonUp += PointerUp;
        overlay.LostMouseCapture += (_, _) => FinishDrag();
        overlay.MouseWheel += (_, e) => { zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), .1, 8); Render(); };
        stage.AllowDrop = true; stage.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files); };
        var right = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("#1B2230"), Margin = new Thickness(10, 0, 0, 0) };
        inspector.Margin = new Thickness(12); Grid.SetColumn(right, 2); main.Children.Add(right);
        timeline.SizeChanged += (_, _) => DrawTimeline(); timeline.MouseLeftButtonDown += TimelineClick;
        timer.Tick += (_, _) => { frame = (playStart + playback.Elapsed.TotalSeconds * project.Fps) % project.FrameCount; UpdateFrameLabel(); Render(); DrawTimeline(); };
        PreviewKeyDown += Shortcut;
        Closing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; else { closed = true; timer.Stop(); autosaveTimer.Stop(); } };
        autosaveTimer.Tick += async (_, _) => await AutosaveNowAsync();
        Loaded += (_, _) =>
        {
            Refresh(); autosaveTimer.Start(); autosaveStatus.ToolTip = autosaves.DirectoryPath;
            try { if (Directory.Exists(autosaves.DirectoryPath) && Directory.EnumerateFiles(autosaves.DirectoryPath, "*.recovery*").Any()) Say("Recovery copies are available. Choose Recover autosave to restore work from an earlier session."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say("Could not check recovery copies: " + ex.Message); }
        };
        savedState = Capture();
        Say("Start with Demo character, or import sprite images and draw your rig. Images can also be dropped onto the canvas.");
    }

    private void SetStyles()
    {
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2B374B"))); button.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#ECF3FF")));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#42516A"))); button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6))); button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
        Resources.Add(typeof(Button), button);
        var text = new Style(typeof(TextBox)); text.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#111823"))); text.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#E4ECF8"))); text.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#42516A"))); text.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5))); Resources.Add(typeof(TextBox), text);
        var list = new Style(typeof(ListBoxItem)); list.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#DCE3F0"))); list.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 7, 4, 7))); Resources.Add(typeof(ListBoxItem), list);
        var check = new Style(typeof(CheckBox)); check.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#DCE3F0"))); Resources.Add(typeof(CheckBox), check);
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Brush("#91A2BE"), Margin = new Thickness(0, 8, 0, 10) };
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text }; button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void Say(string message) => status.Text = message;
    private EditorState Capture() => new(project, selectedId, frame, Setup);
    private void UpdateDirty(EditorState? state = null)
    {
        dirty = savedState == null || !savedState.SameContent(state ?? Capture()); UpdateTitle();
    }
    private void Restore(EditorState state)
    {
        suppressInputCommit = true;
        try { Keyboard.ClearFocus(); project = state.Project.Copy(); selectedId = state.SelectedId; frame = state.Frame; rebuilding = true; mode.SelectedIndex = state.Setup ? 0 : 1; }
        finally { rebuilding = false; suppressInputCommit = false; }
        renderer.Clear(); UpdateDirty(); Refresh();
    }
    private void Change(Action action, bool full = false)
    {
        Stop(); var before = Capture();
        try { action(); project.Validate(); history.Commit(before, Capture()); UpdateDirty(); }
        catch { Restore(before); throw; }
        if (full) Refresh(); else { Render(); DrawTimeline(); }
    }
    private void UpdateTitle() => Title = $"{(dirty ? "* " : "")}{project.Name} — Mandriel's Workshop";
    private void Undo()
    {
        Stop(); if (dragging) { CancelDrag(); return; }
        if (history.Undo(Capture()) is { } state) Restore(state);
    }
    private void Redo()
    {
        Stop(); if (dragging) { CancelDrag(); return; }
        if (history.Redo(Capture()) is { } state) Restore(state);
    }
    private void Refresh()
    {
        frame = Math.Clamp(frame, 0, project.FrameCount - 1); rebuilding = true;
        hierarchy.Items.Clear();
        foreach (var part in project.Parts.AsEnumerable().Reverse())
        {
            int depth = 0; var parent = part.ParentId;
            while (parent != null) { depth++; parent = project.Parts.FirstOrDefault(p => p.Id == parent)?.ParentId; }
            var item = new ListBoxItem { Content = $"{new string(' ', depth * 2)}{(part.IsBone ? "◇" : "▧")}  {part.Name}", Tag = part.Id, ToolTip = part.IsBone ? "Bone" : "Sprite layer" };
            hierarchy.Items.Add(item); if (part.Id == selectedId) hierarchy.SelectedItem = item;
        }
        scrub.Maximum = project.FrameCount - 1; rebuilding = false; UpdateTitle(); RefreshInspector(); UpdateFrameLabel(); Render(); DrawTimeline();
    }
    private void EditPose(Action<Pose> edit)
    {
        if (Selected is not { } part) return;
        Change(() => { var pose = part.At((int)frame, Setup); edit(pose); if (Setup) part.Rest = pose; else part.Keys[(int)frame] = pose; });
    }
    private void Number(Panel host, string label, double value, double min, double max, Action<double> setter, bool integer = false)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(102) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var input = new TextBox { Text = value.ToString("0.###", CultureInfo.InvariantCulture), ToolTip = $"{min} … {max}" }; Grid.SetColumn(input, 1); row.Children.Add(input); host.Children.Add(row);
        double current = value;
        int version = inspectorVersion;
        void Commit(bool silent = false)
        {
            if (suppressInputCommit || version != inspectorVersion) return;
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double next) || !double.IsFinite(next) || next < min || next > max || (integer && next != Math.Truncate(next)))
            { if (!silent) { input.Text = current.ToString("0.###", CultureInfo.InvariantCulture); Say($"{label}: enter {(integer ? "a whole number" : "a number")} from {min} to {max}."); } return; }
            if (Math.Abs(next - current) < .000001) return; setter(next); current = next; ResetTextUndo(input);
        }
        input.Tag = (Action<bool>)Commit;
        input.LostKeyboardFocus += (_, _) => Commit(); input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; } };
    }
    private void Check(Panel host, string label, bool value, Action<bool> setter)
    {
        var input = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 6) }; input.Click += (_, _) => setter(input.IsChecked == true); host.Children.Add(input);
    }
    private void RefreshInspector()
    {
        inspectorVersion++;
        inspector.Children.Clear(); modeLabel.Text = Setup ? "RIG SETUP" : "ANIMATE • AUTO KEY"; modeLabel.VerticalAlignment = VerticalAlignment.Center; modeLabel.Foreground = Brush("#65DDC2");
        if (Selected is { } p)
        {
            inspector.Children.Add(Label(p.IsBone ? "BONE INSPECTOR" : "SPRITE INSPECTOR"));
            var name = new TextBox { Text = p.Name, Margin = new Thickness(0, 0, 0, 8) };
            int version = inspectorVersion;
            void CommitName(bool silent = false)
            {
                if (suppressInputCommit || version != inspectorVersion || string.IsNullOrWhiteSpace(name.Text) || name.Text == p.Name) return;
                Change(() => p.Name = name.Text); ResetTextUndo(name);
                if (!silent) Refresh();
            }
            name.Tag = (Action<bool>)CommitName;
            name.LostKeyboardFocus += (_, _) => CommitName();
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); Keyboard.ClearFocus(); e.Handled = true; } }; inspector.Children.Add(name);
            inspector.Children.Add(new TextBlock { Text = Setup ? "Rest pose · animation keys override it" : $"Frame {(int)frame + 1} · edits create a key", Foreground = Brush("#65DDC2"), Margin = new Thickness(0, 0, 0, 8) });
            var pose = p.At(frame, Setup);
            Number(inspector, "Position X", pose.X, -10000, 10000, v => EditPose(a => a.X = v));
            Number(inspector, "Position Y (up)", pose.Y, -10000, 10000, v => EditPose(a => a.Y = v));
            Number(inspector, "Depth Z", pose.Z, -5000, 5000, v => EditPose(a => a.Z = v));
            Number(inspector, "Rotate X / tilt", pose.RotationX, -3600, 3600, v => EditPose(a => a.RotationX = v));
            Number(inspector, "Rotate Y / turn", pose.RotationY, -3600, 3600, v => EditPose(a => a.RotationY = v));
            Number(inspector, "Rotate Z / roll", pose.RotationZ, -3600, 3600, v => EditPose(a => a.RotationZ = v));
            if (!p.IsBone)
            {
                Number(inspector, "Scale X", pose.ScaleX, .01, 100, v => EditPose(a => a.ScaleX = v));
                Number(inspector, "Scale Y", pose.ScaleY, .01, 100, v => EditPose(a => a.ScaleY = v));
            }
            Check(inspector, p.IsBone ? "Visible (includes children)" : "Visible in this pose", pose.Visible, v => EditPose(a => a.Visible = v));
            inspector.Children.Add(Label("RIG CONFIGURATION"));
            var config = new StackPanel { IsEnabled = Setup }; inspector.Children.Add(config);
            config.Children.Add(new TextBlock { Text = p.IsBone ? "Parent bone" : "Bind to bone", Margin = new Thickness(0, 0, 0, 5) });
            var parents = new ComboBox { Margin = new Thickness(0, 0, 0, 8) }; parents.Items.Add(new ComboBoxItem { Content = "None (world)", Tag = null });
            foreach (var bone in project.Parts.Where(b => b.IsBone && b.Id != p.Id && !project.IsDescendant(b, p.Id))) parents.Items.Add(new ComboBoxItem { Content = bone.Name, Tag = bone.Id });
            parents.SelectedItem = parents.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == p.ParentId);
            parents.SelectionChanged += (_, _) => { var id = (parents.SelectedItem as ComboBoxItem)?.Tag as string; if (id != p.ParentId) Change(() => project.Reparent(p, id), true); }; config.Children.Add(parents);
            if (p.IsBone) Number(config, "Bone length", p.Length, 1, 2000, v => Change(() => p.Length = v));
            else
            {
                Number(config, "Width", p.Width, 1, 8192, v => Change(() => p.Width = v)); Number(config, "Height", p.Height, 1, 8192, v => Change(() => p.Height = v));
                Number(config, "Pivot X (0–1)", p.PivotX, 0, 1, v => Change(() => p.PivotX = v)); Number(config, "Pivot Y (0–1)", p.PivotY, 0, 1, v => Change(() => p.PivotY = v));
                Number(config, "3D thickness", p.Thickness, 0, 200, v => Change(() => p.Thickness = v)); Check(config, "Rounded depth profile", p.RoundedDepth, v => Change(() => p.RoundedDepth = v));
                var buttons = new WrapPanel(); AddButton(buttons, "Back image…", ImportBack); AddButton(buttons, "Clear back", () => Change(() => p.BackImageData = null)); config.Children.Add(buttons);
                config.Children.Add(new TextBlock { Text = "3D sides are generated from edge colors. A back image improves rear views. Thickness 0 gives a flat sprite.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#98A6BD"), Margin = new Thickness(0, 8, 0, 0) });
            }
            if (!Setup) inspector.Children.Add(new TextBlock { Text = "Switch to Rig setup to edit binding, dimensions, or thickness.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#98A6BD") });
        }
        else
        {
            inspector.Children.Add(Label("BUILD YOUR CHARACTER"));
            inspector.Children.Add(new TextBlock { Text = "1. Import sprite parts and a reference.\n\n2. Draw bones and choose a parent for each joint.\n\n3. Select a sprite and bind it to a bone.\n\n4. Switch to Animate, select a frame, and pose the rig.\n\n5. Preview and export your sheet.", TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        }
        inspector.Children.Add(Label("REFERENCE / BACKGROUND"));
        if (project.ReferenceImage != null)
        {
            Check(inspector, "Show reference", project.ReferenceVisible, v => Change(() => project.ReferenceVisible = v));
            Number(inspector, "Opacity", project.ReferenceOpacity, 0, 1, v => Change(() => project.ReferenceOpacity = v));
            Number(inspector, "Scale", project.ReferenceScale, .01, 20, v => Change(() => project.ReferenceScale = v));
            Number(inspector, "Position X", project.ReferenceX, -10000, 10000, v => Change(() => project.ReferenceX = v));
            Number(inspector, "Position Y (up)", project.ReferenceY, -10000, 10000, v => Change(() => project.ReferenceY = v));
            AddButton(inspector, "Remove reference", () => Change(() => project.ReferenceImage = null, true));
        }
        else AddButton(inspector, "Import reference…", ImportReference);
        inspector.Children.Add(new TextBlock { Text = "The reference stays behind the character and is excluded from exports.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#98A6BD"), Margin = new Thickness(0, 8, 0, 0) });
    }

    private Point Screen(Point3D p) => new(stage.ActualWidth / 2 + p.X * zoom, stage.ActualHeight / 2 - p.Y * zoom);
    private Point3D World(Point p) => new((p.X - stage.ActualWidth / 2) / zoom, (stage.ActualHeight / 2 - p.Y) / zoom, 0);
    private static void Line(Canvas canvas, Point a, Point b, Brush brush, double thickness = 1) => canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });
    private static void Text(Canvas canvas, string text, double x, double y, Brush brush, double size = 11)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = size, IsHitTestVisible = false }; Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
    }
    private static void Dot(Canvas canvas, Point at, double radius, Brush fill)
    {
        var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = fill, Stroke = Brush("#111823"), StrokeThickness = 2, IsHitTestVisible = false }; Canvas.SetLeft(dot, at.X - radius); Canvas.SetTop(dot, at.Y - radius); canvas.Children.Add(dot);
    }
    private void Render()
    {
        double w = stage.ActualWidth, h = stage.ActualHeight; if (w <= 0 || h <= 0) return;
        backdrop.Children.Clear(); overlay.Children.Clear();
        double grid = 32 * zoom; while (grid < 16) grid *= 2;
        for (double x = w / 2 % grid; x < w; x += grid) Line(backdrop, new Point(x, 0), new Point(x, h), Brush("#252E3D"));
        for (double y = h / 2 % grid; y < h; y += grid) Line(backdrop, new Point(0, y), new Point(w, y), Brush("#252E3D"));
        Line(backdrop, new Point(w / 2, 0), new Point(w / 2, h), Brush("#3B495E")); Line(backdrop, new Point(0, h / 2), new Point(w, h / 2), Brush("#3B495E"));
        if (project.ReferenceVisible && project.ReferenceImage != null)
        {
            var source = renderer.Image(project.ReferenceImage); var reference = new Image { Source = source, Width = source.PixelWidth * project.ReferenceScale * zoom, Height = source.PixelHeight * project.ReferenceScale * zoom, Opacity = project.ReferenceOpacity };
            Canvas.SetLeft(reference, w / 2 + project.ReferenceX * zoom - reference.Width / 2); Canvas.SetTop(reference, h / 2 - project.ReferenceY * zoom - reference.Height / 2); backdrop.Children.Add(reference);
        }
        var s = project.Sheet;
        var rect = new Rectangle { Width = s.CellWidth / s.Scale * zoom, Height = s.CellHeight / s.Scale * zoom, Stroke = Brush("#4B657C"), StrokeDashArray = new DoubleCollection { 5, 4 }, IsHitTestVisible = false };
        Canvas.SetLeft(rect, w / 2 - (s.CellWidth / 2.0 + s.OffsetX) / s.Scale * zoom); Canvas.SetTop(rect, h / 2 - (s.CellHeight / 2.0 + s.OffsetY) / s.Scale * zoom); overlay.Children.Add(rect);
        renderer.Populate(viewport, project, frame, Setup, w, h, zoom);
        foreach (var p in project.Parts)
        {
            var matrix = project.World(p, frame, Setup); var origin = Screen(matrix.Transform(new Point3D())); var color = p.Id == selectedId ? Brush("#FFE295") : Brush("#65DDC2");
            if (p.IsBone && showBones)
            {
                var tip = Screen(matrix.Transform(new Point3D(p.Length, 0, 0))); var delta = tip - origin; var normal = new Vector(-delta.Y, delta.X); if (normal.Length > 0) normal.Normalize();
                var polygon = new Polygon { Points = new PointCollection { origin, origin + delta * .22 + normal * 6, tip, origin + delta * .22 - normal * 6 }, Stroke = color, Fill = new SolidColorBrush(Color.FromArgb(60, 101, 221, 194)), StrokeThickness = 1.5, IsHitTestVisible = false }; overlay.Children.Add(polygon);
                Dot(overlay, origin, 5, color); Dot(overlay, tip, 4, color); if (p.Id == selectedId) Text(overlay, p.Name, tip.X + 8, tip.Y - 10, color);
            }
            else if (!p.IsBone && p.Id == selectedId)
            {
                var corners = new[] { new Point3D(-p.PivotX * p.Width, p.PivotY * p.Height, 0), new Point3D((1 - p.PivotX) * p.Width, p.PivotY * p.Height, 0), new Point3D((1 - p.PivotX) * p.Width, (p.PivotY - 1) * p.Height, 0), new Point3D(-p.PivotX * p.Width, (p.PivotY - 1) * p.Height, 0) }.Select(v => Screen(matrix.Transform(v))).ToArray();
                for (int i = 0; i < 4; i++) Line(overlay, corners[i], corners[(i + 1) % 4], color); Dot(overlay, origin, 5, color);
            }
        }
        Text(overlay, drawingBone ? "DRAW BONE • drag from joint to tip · Esc to cancel" : $"{zoom * 100:0}%  ·  {(Setup ? "Drag parts to place · drag bone tips to rotate" : "Pose edits create keys automatically")}", 12, 12, Brush("#B6C3D7"));
        Text(overlay, "Dashed box = export cell", 12, h - 25, Brush("#879BB5"));
    }
    private void BeginBone()
    {
        if (project.Parts.Count >= 300) { Say("A project supports up to 300 bones and sprites."); return; }
        Stop(); mode.SelectedIndex = 0; drawingBone = true; Render(); Say("Drag on the canvas from the new bone’s joint to its tip. The selected bone becomes its parent.");
    }
    private Point3D LocalPointer(Point screen, Part part, bool rest)
    {
        var world = World(screen);
        if (project.Parts.FirstOrDefault(p => p.Id == part.ParentId) is not { } parent) return world;
        var m = project.World(parent, frame, rest);
        // Intersect the viewing ray with the parent's local XY plane, including tilted parents.
        m.Invert(); var a = m.Transform(new Point3D(world.X, world.Y, 10000)); var b = m.Transform(new Point3D(world.X, world.Y, -10000));
        double dz = b.Z - a.Z;
        if (Math.Abs(dz) < .000001) return m.Transform(world);
        return a + (b - a) * ((part.At(frame, rest).Z - a.Z) / dz);
    }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        Stop(); var point = e.GetPosition(stage); dragStart = point;
        if (drawingBone)
        {
            dragBefore = Capture(); draftBone = new Part { Name = $"Bone {project.Parts.Count(p => p.IsBone) + 1}", IsBone = true, ParentId = Selected is { IsBone: true } parent ? parent.Id : null };
            var local = LocalPointer(point, draftBone, true); draftBone.Rest.X = local.X; draftBone.Rest.Y = local.Y; draftBone.Length = 1; project.Parts.Add(draftBone); selectedId = draftBone.Id; dragging = true; overlay.CaptureMouse(); Refresh(); return;
        }
        Part? hit = null; rotateDrag = false;
        if (showBones)
        {
            foreach (var p in project.Parts.Where(p => p.IsBone).OrderBy(p => p.Id == selectedId ? 0 : 1))
            {
                var m = project.World(p, frame, Setup); var origin = Screen(m.Transform(new Point3D())); var tip = Screen(m.Transform(new Point3D(p.Length, 0, 0)));
                if ((point - tip).Length < 11) { hit = p; rotateDrag = true; break; }
                if ((point - origin).Length < 11 || DistanceToSegment(point, origin, tip) < 6) { hit = p; break; }
            }
        }
        if (hit == null)
            foreach (var p in project.Parts.AsEnumerable().Reverse().Where(p => !p.IsBone && project.Visible(p, frame, Setup)))
            {
                var m = project.World(p, frame, Setup); m.Invert(); var world = World(point);
                var a = m.Transform(new Point3D(world.X, world.Y, 10000)); var b = m.Transform(new Point3D(world.X, world.Y, -10000));
                if (Math.Abs(b.Z - a.Z) < .00001) continue;
                var local = a + (b - a) * (-a.Z / (b.Z - a.Z));
                if (local.X >= -p.PivotX * p.Width && local.X <= (1 - p.PivotX) * p.Width && local.Y <= p.PivotY * p.Height && local.Y >= (p.PivotY - 1) * p.Height) { hit = p; break; }
            }
        selectedId = hit?.Id; Refresh();
        if (hit != null) { dragBefore = Capture(); dragPose = hit.At(frame, Setup); dragging = true; overlay.CaptureMouse(); }
    }
    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var d = end - start; if (d.LengthSquared == 0) return (point - start).Length;
        return (point - (start + d * Math.Clamp(Vector.Multiply(point - start, d) / d.LengthSquared, 0, 1))).Length;
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (!dragging || Selected is not { } p) return;
        var point = e.GetPosition(stage); var local = LocalPointer(point, p, Setup);
        if (draftBone != null)
        {
            var delta = new Vector(local.X - p.Rest.X, local.Y - p.Rest.Y); p.Length = Math.Clamp(delta.Length, 1, 2000); p.Rest.RotationZ = Math.Atan2(delta.Y, delta.X) * 180 / Math.PI;
        }
        else if (dragPose != null)
        {
            var pose = dragPose.Copy(); var start = LocalPointer(dragStart, p, Setup);
            if (rotateDrag)
            {
                double a = Math.Atan2(start.Y - pose.Y, start.X - pose.X), b = Math.Atan2(local.Y - pose.Y, local.X - pose.X);
                double delta = (b - a) * 180 / Math.PI; if (delta > 180) delta -= 360; if (delta < -180) delta += 360; pose.RotationZ += delta;
            }
            else { pose.X += local.X - start.X; pose.Y += local.Y - start.Y; }
            if (Setup) p.Rest = pose; else p.Keys[(int)frame] = pose;
        }
        Render(); DrawTimeline();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        FinishDrag();
    }
    private void FinishDrag()
    {
        if (!dragging) return;
        dragging = false; drawingBone = false; draftBone = null; dragPose = null; overlay.ReleaseMouseCapture();
        var before = dragBefore; dragBefore = null;
        if (before != null)
        {
            try { project.Validate(); history.Commit(before, Capture()); UpdateDirty(); }
            catch { Restore(before); throw; }
        }
        Refresh();
        ResumeDeferredAutosave();
    }
    private void CancelDrag()
    {
        if (!dragging) return;
        dragging = false; drawingBone = false; draftBone = null; dragPose = null; overlay.ReleaseMouseCapture();
        var before = dragBefore; dragBefore = null; if (before != null) Restore(before);
        ResumeDeferredAutosave();
    }
    private void ResumeDeferredAutosave()
    {
        if (!autosaveDeferred) return; autosaveDeferred = false;
        Dispatcher.BeginInvoke(new Action(async () => await AutosaveNowAsync()), DispatcherPriority.Background);
    }
    private void Fit()
    {
        zoom = Math.Clamp(Math.Min(stage.ActualWidth / (project.Sheet.CellWidth / project.Sheet.Scale + 80), stage.ActualHeight / (project.Sheet.CellHeight / project.Sheet.Scale + 80)), .1, 8); Render();
    }

    private void UpdateFrameLabel()
    {
        frameLabel.Text = $"  {(int)frame + 1:000} / {project.FrameCount:000}  · {project.Fps} fps"; rebuilding = true; scrub.Value = (int)frame; rebuilding = false;
    }
    private void SetFrame(double value)
    {
        Stop(); frame = Math.Clamp(Math.Round(value), 0, project.FrameCount - 1); UpdateFrameLabel(); RefreshInspector(); Render(); DrawTimeline();
    }
    private void Stop()
    {
        timer.Stop(); playback.Stop(); playButton.Content = "▶ Play"; frame = Math.Floor(frame);
    }
    private void TogglePlay()
    {
        if (timer.IsEnabled) { Stop(); SetFrame(frame); return; }
        mode.SelectedIndex = 1; playStart = frame; playback.Restart(); timer.Start(); playButton.Content = "❚❚ Pause";
    }
    private void KeySelected()
    {
        if (Selected is not { } p) { Say("Select a bone or sprite to add a keyframe."); return; }
        mode.SelectedIndex = 1; Change(() => p.Keys[(int)frame] = p.At(frame), true);
    }
    private void KeyAll()
    {
        mode.SelectedIndex = 1; Change(() => { foreach (var p in project.Parts) p.Keys[(int)frame] = p.At(frame); }, true);
    }
    private void DeleteKey()
    {
        if (Selected is { } p && p.Keys.ContainsKey((int)frame)) Change(() => p.Keys.Remove((int)frame), true);
    }
    private double TimelineStep => Math.Max(1, (timeline.ActualWidth - 155) / project.FrameCount);
    private void DrawTimeline()
    {
        if (timeline.ActualWidth <= 0) return; timeline.Children.Clear(); timeline.Height = Math.Max(150, 30 + project.Parts.Count * 25); double step = TimelineStep;
        int interval = Math.Max(1, (int)Math.Ceiling(28 / step));
        for (int f = 0; f < project.FrameCount; f += interval) { double x = 150 + (f + .5) * step; Text(timeline, (f + 1).ToString(), x - 4, 3, Brush("#8393AC")); Line(timeline, new Point(x, 22), new Point(x, timeline.Height), Brush("#242F40")); }
        Text(timeline, "KEYFRAME TRACKS", 10, 4, Brush("#93A4BD"));
        for (int row = 0; row < project.Parts.Count; row++)
        {
            var p = project.Parts[row]; double y = 30 + row * 25;
            if (p.Id == selectedId) { var bg = new Rectangle { Width = timeline.ActualWidth, Height = 25, Fill = Brush("#26364A") }; Canvas.SetTop(bg, y - 5); timeline.Children.Add(bg); }
            Text(timeline, (p.IsBone ? "◇ " : "▧ ") + (p.Name.Length > 17 ? p.Name[..17] + "…" : p.Name), 10, y, Brush("#C8D4E5"));
            foreach (var key in p.Keys)
            {
                double x = 150 + (key.Key + .5) * step;
                var diamond = new Polygon { Points = new PointCollection { new Point(x, y + 1), new Point(x + 5, y + 7), new Point(x, y + 13), new Point(x - 5, y + 7) }, Fill = key.Value.Visible ? Brush("#65DDC2") : Brush("#F28D99"), ToolTip = $"Frame {key.Key + 1} · {(key.Value.Visible ? "visible" : "hidden")}" }; timeline.Children.Add(diamond);
            }
        }
        double cursor = 150 + ((int)frame + .5) * step; Line(timeline, new Point(cursor, 20), new Point(cursor, timeline.Height), Brush("#FFE295"), 2);
    }
    private void TimelineClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(timeline); int row = (int)Math.Floor((point.Y - 25) / 25);
        if (row >= 0 && row < project.Parts.Count) selectedId = project.Parts[row].Id;
        mode.SelectedIndex = 1;
        if (point.X >= 150) SetFrame(Math.Floor((point.X - 150) / TimelineStep)); Refresh();
    }
    private void AnimationSettings()
    {
        Stop(); var content = new StackPanel { Margin = new Thickness(20) }; var dialog = Dialog("Animation settings", content, 390, 260);
        int count = project.FrameCount, fps = project.Fps;
        Number(content, "Frame count", count, 1, 600, v => count = (int)v, true); Number(content, "Frames per second", fps, 1, 120, v => fps = (int)v, true);
        content.Children.Add(new TextBlock { Text = "Shortening an animation removes keys beyond its last frame. Undo restores them.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        AddButton(content, "Apply", () => { ApplyAnimationSettings(count, fps); dialog.Close(); }); dialog.ShowDialog();
    }
    private void ApplyAnimationSettings(int count, int fps) => Change(() =>
    {
        project.FrameCount = count; project.Fps = fps;
        foreach (var p in project.Parts) foreach (int key in p.Keys.Keys.Where(k => k >= count).ToArray()) p.Keys.Remove(key);
    }, true);
    private Window Dialog(string title, object content, double width, double height) => new() { Title = title, Owner = this, Content = content, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Resources = Resources };

    private void ImportSprites()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Multiselect = true }; if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames);
    }
    private void ImportFiles(string[] files)
    {
        Stop(); var parts = new List<Part>();
        try
        {
            foreach (string path in files)
            {
                var data = SceneRenderer.Import(path); var image = renderer.Image(data);
                parts.Add(new Part { Name = System.IO.Path.GetFileNameWithoutExtension(path), ImageData = data, Width = image.PixelWidth, Height = image.PixelHeight, Thickness = 0 });
            }
            if (parts.Count + project.Parts.Count > 300) throw new InvalidOperationException("A project supports up to 300 bones and sprites.");
            Change(() => { project.Parts.AddRange(parts); selectedId = parts.LastOrDefault()?.Id; }, true); mode.SelectedIndex = 0; Say($"Imported {parts.Count} sprite(s). Resize them in the inspector, then choose a bone to bind each part.");
        }
        catch (Exception ex) { Say("Import failed: " + ex.Message); }
    }
    private void ImportReference()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif" }; if (dialog.ShowDialog(this) != true) return;
        try { var data = SceneRenderer.Import(dialog.FileName); renderer.Image(data); Change(() => { project.ReferenceImage = data; project.ReferenceVisible = true; }, true); } catch (Exception ex) { Say(ex.Message); }
    }
    private void ImportBack()
    {
        if (Selected is not { IsBone: false } p) return;
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" }; if (dialog.ShowDialog(this) != true) return;
        try { var data = SceneRenderer.Import(dialog.FileName); renderer.Image(data); Change(() => p.BackImageData = data); } catch (Exception ex) { Say(ex.Message); }
    }
    private void RemovePart()
    {
        if (Selected is not { } part) return;
        if (!Setup) { Say("Switch to Rig setup to remove a part. Use visibility to hide it in an animation."); return; }
        Change(() => { foreach (var child in project.Parts.Where(p => p.ParentId == part.Id).ToArray()) project.Reparent(child, part.ParentId); project.Parts.Remove(part); selectedId = null; }, true);
    }
    private void MoveLayer(int direction)
    {
        if (Selected is not { IsBone: false } p) return;
        Change(() => { int index = project.Parts.IndexOf(p); int target = Math.Clamp(index + direction, 0, project.Parts.Count - 1); project.Parts.RemoveAt(index); project.Parts.Insert(target, p); }, true);
        Say("Layer order controls equal-depth parts. Depth Z determines overlap when parts rotate in 3D.");
    }
    private bool ConfirmDiscard()
    {
        FinishDrag(); CommitFocusedInput();
        if (!dirty) return true;
        var result = MessageBox.Show(this, "Save changes to this character?", "Unsaved project", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveProject(false);
    }
    private void NewProject()
    {
        if (!ConfirmDiscard()) return; Stop(); project = new(); ResetProject();
    }
    private void ResetProject()
    {
        selectedId = null; filePath = null; frame = 0; dirty = false; history.Clear(); renderer.Clear(); mode.SelectedIndex = 0;
        sessionId = Guid.NewGuid(); lastAutosavedState = null; savedState = Capture();
        autosaveStatus.Text = "Autosave every minute"; Refresh();
    }
    private void OpenProject()
    {
        if (!ConfirmDiscard()) return; var dialog = new OpenFileDialog { Filter = "Mandriel's Workshop project or backup|*.spriteforge;*.spriteforge.bak" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 256 * 1024 * 1024) throw new InvalidDataException("Project exceeds 256 MB.");
            var loaded = Project.Deserialize(File.ReadAllText(dialog.FileName));
            var validator = new SceneRenderer(); foreach (var part in loaded.Parts) { if (part.ImageData != null) validator.Image(part.ImageData); if (part.BackImageData != null) validator.Image(part.BackImageData); } if (loaded.ReferenceImage != null) validator.Image(loaded.ReferenceImage);
            Stop(); project = loaded; ResetProject();
            if (dialog.FileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) { savedState = null; UpdateDirty(); }
            else filePath = dialog.FileName;
            Say("Project opened. Imported images are embedded in the project.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open project"); }
    }
    private bool SaveProject(bool saveAs)
    {
        FinishDrag(); CommitFocusedInput(); Stop(); string? path = filePath;
        if (saveAs || path == null)
        {
            var dialog = new SaveFileDialog { Filter = "Mandriel's Workshop project|*.spriteforge", FileName = project.Name + ".spriteforge" }; if (dialog.ShowDialog(this) != true) return false; path = dialog.FileName;
        }
        try
        {
            var toSave = project.Copy(); toSave.Name = System.IO.Path.GetFileNameWithoutExtension(path); toSave.Validate();
            ProjectStorage.WriteAtomic(path, toSave.Serialize());
            project.Name = toSave.Name; filePath = path; savedState = Capture(); UpdateDirty(); Say("Project saved. The previous saved version is kept in a .bak file."); return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save project"); return false; }
    }
    private void LoadDemo()
    {
        if (!ConfirmDiscard()) return; Stop(); project = Demo.Create(); ResetProject(); savedState = null; UpdateDirty(); selectedId = project.Parts.First(p => p.Name == "Arm R").Id; mode.SelectedIndex = 1; Refresh(); Fit(); Say("Demo loaded: scrub or play the wave. The star disappears at frame 13 and returns at frame 19. Select an arm bone to pose it.");
    }
    private void Export()
    {
        Stop(); var dialog = new ExportWindow(project, renderer, () => project, Undo, Redo) { Owner = this, Resources = Resources };
        dialog.SettingsChanged += () => Change(() => project.Sheet = dialog.Settings.Copy()); dialog.ShowDialog();
    }
    private void CommitFocusedInput()
    {
        if (Keyboard.FocusedElement is TextBox { Tag: Action<bool> commit }) commit(true);
    }
    private static void ResetTextUndo(TextBox input) { input.IsUndoEnabled = false; input.IsUndoEnabled = true; }
    private async Task AutosaveNowAsync()
    {
        if (closed || autosaveBusy) return;
        if (dragging) { autosaveDeferred = true; return; }
        var session = sessionId;
        try
        {
            CommitFocusedInput();
            if (!dirty && lastAutosavedState == null) return;
            var state = Capture();
            if (lastAutosavedState?.SameContent(state) == true) return;
            autosaveBusy = true; autosaveStatus.Text = "Autosaving…";
            await autosaves.WriteAsync(session, state, filePath);
            if (closed || session != sessionId) return;
            lastAutosavedState = state;
            autosaveStatus.Text = $"Recovery autosaved {DateTime.Now:HH:mm:ss}";
            autosaveStatus.ToolTip = autosaves.PathFor(session);
        }
        catch (Exception ex)
        {
            if (!closed && session == sessionId)
            {
                autosaveStatus.Text = "Autosave failed — use Ctrl+S";
                autosaveStatus.ToolTip = ex.Message;
                Say("Autosave could not write a recovery copy. Your edits are still open: " + ex.Message);
            }
        }
        finally { autosaveBusy = false; }
    }
    private async void RecoverAutosave()
    {
        try
        {
            var result = await Task.Run(() => { var entries = autosaves.List(out int unreadable); return (entries, unreadable); });
            if (closed) return;
            var panel = new DockPanel { Margin = new Thickness(18) };
            var note = new TextBlock { Text = "Choose a recovery copy. It opens as an unsaved project so you can inspect it before saving.\n" + autosaves.DirectoryPath + (result.unreadable > 0 ? $"\n{result.unreadable} unreadable copy/copies skipped; previous copies are listed when available." : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
            var list = new ListBox { ItemsSource = result.entries, SelectedIndex = result.entries.Count > 0 ? 0 : -1, Background = Brush("#1B2230") };
            var buttons = new WrapPanel(); var dialog = Dialog("Recover autosaved project", panel, 740, 460);
            AddButton(buttons, "Restore selected copy", () =>
            {
                if (list.SelectedItem is not RecoveryEntry entry) return;
                try
                {
                    var copy = autosaves.Read(entry.Path);
                    var validator = new SceneRenderer();
                    foreach (var p in copy.Project.Parts) { if (p.ImageData != null) validator.Image(p.ImageData); if (p.BackImageData != null) validator.Image(p.BackImageData); }
                    if (copy.Project.ReferenceImage != null) validator.Image(copy.Project.ReferenceImage);
                    if (!ConfirmDiscard()) return;
                    Stop(); project = copy.Project; ResetProject(); savedState = null;
                    selectedId = copy.SelectedId; frame = Math.Clamp(copy.Frame, 0, project.FrameCount - 1); mode.SelectedIndex = copy.Setup ? 0 : 1; UpdateDirty(); Refresh(); dialog.Close();
                    Say("Recovered autosave. Use Save as to keep the restored project; the original file has not been changed.");
                }
                catch (Exception ex) { note.Text = "Could not restore this copy: " + ex.Message; }
            });
            AddButton(buttons, "Cancel", () => dialog.Close()); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
            if (result.entries.Count == 0) note.Text += "\nNo recovery copies are available yet. Changed projects are autosaved every minute.";
            dialog.ShowDialog();
        }
        catch (Exception ex) { Say("Could not list recovery copies: " + ex.Message); }
    }
    private void Help() => MessageBox.Show(this,
        "UNDO AND AUTOSAVE\nCtrl+Z undoes project edits; Ctrl+Y or Ctrl+Shift+Z redoes them. A drag is one undo step. Esc cancels it. Undo covers images, bones, bindings, transforms, keys, visibility, reference settings, animation settings, and applied export settings. Uncommitted text has its own undo until committed. History remains through saves and lasts until you switch projects.\n\nChanged projects are autosaved every minute to a separate recovery copy, including unnamed work. Use Recover autosave to restore it after a restart. The status bar shows success or failure. Ctrl+S saves the main project file. Previous manual saves and recovery copies are kept as .bak files.\n\n" +
        "RIG SETUP\nImport separate PNG body parts. A reference image stays behind your character. Select a bone and click + Bone to draw a child; click empty space first to draw a root. Bones point along local +X. Bind sprites using the inspector. Move the sprite relative to its bone and set its pivot.\n\nANIMATE\nSwitch to Animate. Click a timeline frame and drag a bone tip to rotate it; drag its joint to move it. Inspector edits automatically create a key on that part. Key all captures a full pose. Transforms interpolate linearly; visibility holds until the next key. Put a visible key after a hidden key to make the sprite return. Rotation values support multiple turns.\n\n3D ROTATION\nUse X/Y rotation and nonzero thickness to give sprites volume. Rounded depth creates a curved profile; side colors come from the sprite. Optional back artwork replaces the rear texture. This is an approximation, not reconstruction of unseen details.\n\nEXPORT\nThe dashed box is the export cell. Preview / Export sets dimensions, rows, columns, spacing, margin, scale, and content offset. Exports contain only sprites, with transparency, in row-major order.\n\nSHORTCUTS\nCtrl+S save · Ctrl+O open · Ctrl+Z undo · Ctrl+Y redo\nSpace play/pause · Left/Right frame · K key selected · Delete remove part/key\nMouse wheel zoom · Esc cancel bone drawing\n\nRig changes affect the whole animation. Binding keeps the rest placement; existing keys stay local to the new parent, so build the rig before animating.", "Mandriel's Workshop guide");
    private void Shortcut(object sender, KeyEventArgs e)
    {
        if (HandleHistoryShortcut(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement)) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.S) SaveProject(false); else if (e.Key == Key.O) OpenProject(); else return;
        }
        else if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        else switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SetFrame(frame - 1); break;
            case Key.Right: SetFrame(frame + 1); break;
            case Key.K: KeySelected(); break;
            case Key.Delete: if (Setup) RemovePart(); else DeleteKey(); break;
            case Key.Escape:
                CancelDrag(); drawingBone = false; Render(); break;
            default: return;
        }
        e.Handled = true;
    }
    private bool HandleHistoryShortcut(Key key, ModifierKeys modifiers, IInputElement? focus)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) || key is not (Key.Z or Key.Y)) return false;
        bool redo = key == Key.Y || modifiers.HasFlag(ModifierKeys.Shift);
        if (focus is TextBox text && (redo ? text.CanRedo : text.CanUndo))
        {
            if (redo) text.Redo(); else text.Undo();
        }
        else if (redo) Redo(); else Undo();
        return true;
    }
    internal BitmapSource SmokeSnapshot()
    {
        project = Demo.Create(); selectedId = project.Parts.First(p => p.Name == "Arm R").Id; mode.SelectedIndex = 1; Refresh();
        var content = (FrameworkElement)Content;
        content.Measure(new Size(1456, 892)); content.Arrange(new Rect(0, 0, 1456, 892)); content.UpdateLayout(); Fit();
        SetFrame(5); var before = Selected!.At(5).RotationZ;
        EditPose(p => p.RotationZ = before + 12);
        if (!Selected!.Keys.ContainsKey(5) || Math.Abs(Selected.At(5).RotationZ - before - 12) > .001) throw new Exception("Editor auto key failed.");
        Undo(); if (Selected!.Keys.ContainsKey(5)) throw new Exception("Undo failed to restore key track.");
        Redo(); if (!Selected!.Keys.ContainsKey(5)) throw new Exception("Redo failed to restore key track.");
        Undo(); SetFrame(0); Refresh(); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1456, 892, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); dirty = false; return bitmap;
    }
    internal void VerifyUndoSafety(string imagePath)
    {
        project = Demo.Create(); ResetProject();
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void RoundTrip(string label, Action action)
        {
            var before = Capture(); action(); var after = Capture();
            Assert(!before.SameContent(after), label + " did not change the project.");
            Undo(); Assert(before.SameContent(Capture()), label + " could not be undone.");
            Redo(); Assert(after.SameContent(Capture()), label + " could not be redone.");
        }
        RoundTrip("Image import", () => ImportFiles([imagePath]));
        RoundTrip("Draw bone", () =>
        {
            dragBefore = Capture(); dragging = true;
            draftBone = new Part { Name = "Test bone", IsBone = true }; project.Parts.Add(draftBone); selectedId = draftBone.Id; FinishDrag();
        });
        selectedId = project.Parts.First(p => !p.IsBone).Id;
        RoundTrip("Sprite resize", () => Change(() => { Selected!.Width += 5; Selected.Height += 3; Selected.PivotX = .25; }));
        RoundTrip("Rename", () => Change(() => Selected!.Name = "Renamed sprite"));
        RoundTrip("Bind sprite", () => Change(() => project.Reparent(Selected!, project.Parts.Last(p => p.IsBone).Id)));
        RoundTrip("Layer order", () => MoveLayer(1));
        RoundTrip("Back texture and depth", () => Change(() => { Selected!.BackImageData = project.Parts.First(p => p.ImageData != null).ImageData; Selected.Thickness = 33; Selected.RoundedDepth = false; }));
        RoundTrip("Reference import and settings", () => Change(() => { project.ReferenceImage = project.Parts.First(p => p.ImageData != null).ImageData; project.ReferenceScale = 1.7; project.ReferenceX = 19; project.ReferenceY = -5; project.ReferenceOpacity = .6; project.ReferenceVisible = false; }));
        RoundTrip("Reference removal", () => Change(() => project.ReferenceImage = null));
        mode.SelectedIndex = 1; SetFrame(5);
        RoundTrip("Keyed scale, position, rotation, visibility", () => EditPose(p => { p.X += 7; p.ScaleX = 2; p.RotationY = 75; p.Visible = false; }));
        RoundTrip("Delete key", DeleteKey);
        RoundTrip("Key selected", KeySelected);
        RoundTrip("Key all", KeyAll);
        RoundTrip("Animation trimming", () => ApplyAnimationSettings(12, 20));
        Undo(); // Restore later keys for the deletion test.
        mode.SelectedIndex = 0; selectedId = project.Parts.First(p => p.Name == "Root").Id;
        RoundTrip("Delete parent bone and rebind children", RemovePart);
        Assert(Selected == null, "Deleted part remained selected."); Undo(); Assert(Selected?.Name == "Root", "Undo did not restore deleted bone selection.");
        selectedId = project.Parts.First(p => !p.IsBone).Id;
        RoundTrip("Export settings", () => Change(() => project.Sheet = new SheetSettings { CellWidth = 160, CellHeight = 200, Columns = 8, Rows = 3, Spacing = 2, Margin = 3, OffsetX = 12, OffsetY = -5, Scale = .8 }));
        RoundTrip("Whole drag", () => { dragBefore = Capture(); dragging = true; for (int i = 0; i < 20; i++) Selected!.Rest.X += 1; FinishDrag(); });
        Undo(); int undoCount = history.UndoCount, redoCount = history.RedoCount; var beforeCancel = Capture();
        dragBefore = Capture(); dragging = true; Selected!.Rest.X += 50; CancelDrag();
        Assert(beforeCancel.SameContent(Capture()) && history.UndoCount == undoCount && history.RedoCount == redoCount, "Cancel destroyed history.");
        dragBefore = Capture(); dragging = true; FinishDrag();
        Change(() => { }); Assert(history.UndoCount == undoCount && history.RedoCount == redoCount, "No-op or selection destroyed redo.");
        var beforeFailure = Capture();
        try { Change(() => { project.Fps = 0; project.Parts.Clear(); }); throw new Exception("Invalid action was accepted."); } catch (InvalidDataException) { }
        Assert(beforeFailure.SameContent(Capture()) && history.RedoCount == redoCount, "Failed action did not roll back atomically.");
        var beforeMany = Capture();
        for (int i = 0; i < 45; i++) Change(() => project.ReferenceX += 1);
        for (int i = 0; i < 45; i++) Undo();
        Assert(beforeMany.SameContent(Capture()), "History still truncates after 30 edits.");
        Change(() => project.ReferenceX += 1); var beforeDropdownUndo = project.ReferenceX;
        Assert(HandleHistoryShortcut(Key.Z, ModifierKeys.Control, new ComboBox()) && project.ReferenceX == beforeDropdownUndo - 1, "Ctrl+Z failed with dropdown focus.");
        HandleHistoryShortcut(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, new ComboBox());
        Assert(project.ReferenceX == beforeDropdownUndo, "Ctrl+Shift+Z failed.");
        // Exercise the exact numeric commit callback used by keyboard focus and autosave.
        var host = new StackPanel(); Number(host, "Reference X", project.ReferenceX, -10000, 10000, v => Change(() => project.ReferenceX = v));
        var field = (TextBox)((Grid)host.Children[0]).Children[1]; field.Text = "123"; ((Action<bool>)field.Tag)(true);
        Assert(project.ReferenceX == 123 && !field.CanUndo, "Committed numeric edits still have an independent text undo stack.");
        HandleHistoryShortcut(Key.Z, ModifierKeys.Control, field); Assert(project.ReferenceX == beforeDropdownUndo, "Ctrl+Z in committed field did not undo project change.");
        field.Text = "456"; ((Action<bool>)field.Tag)(true); Assert(project.ReferenceX == beforeDropdownUndo, "Detached inspector committed into restored project.");
        savedState = Capture(); UpdateDirty(); Change(() => project.ReferenceY += 1); Undo(); Assert(!dirty, "Undo to saved content is still marked dirty.");
        dirty = false;
    }
    internal async Task VerifyAutosaveSafetyAsync()
    {
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        Assert(autosaveTimer.Interval == TimeSpan.FromMinutes(1), "Autosave interval is not one minute.");
        project = Demo.Create(); ResetProject(); Change(() => project.ReferenceX = 17);
        int undoCount = history.UndoCount;
        // Accelerate only the test clock; this exercises the real timer event.
        autosaveTimer.Interval = TimeSpan.FromMilliseconds(30); autosaveTimer.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (lastAutosavedState == null && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert(lastAutosavedState != null, "Timer never autosaved the unnamed project.");
        }
        finally { autosaveTimer.Stop(); autosaveTimer.Interval = AutosaveStore.Interval; }
        string path = autosaves.PathFor(sessionId);
        Assert(autosaves.Read(path).Project.ReferenceX == 17 && filePath == null && dirty, "Autosave did not preserve unnamed work or incorrectly marked it manually saved.");
        Assert(history.UndoCount == undoCount, "Autosave changed undo history.");
        DateTime timestamp = File.GetLastWriteTimeUtc(path); await AutosaveNowAsync();
        Assert(File.GetLastWriteTimeUtc(path) == timestamp, "Unchanged project was rewritten.");
        Undo(); await AutosaveNowAsync(); Assert(autosaves.Read(path).Project.ReferenceX == 0, "Autosave did not capture undo back to saved state.");
        Redo(); Assert(project.ReferenceX == 17, "Autosave cleared redo.");
        filePath = System.IO.Path.Combine(autosaves.DirectoryPath, "manual.spriteforge"); Assert(SaveProject(false), "Manual save failed.");
        var manual = File.ReadAllText(filePath); Change(() => project.ReferenceX = 25); await AutosaveNowAsync();
        Assert(File.ReadAllText(filePath) == manual && autosaves.Read(path).OriginalPath == filePath, "Autosave overwrote manual file or lost its source path.");
        Assert(autosaves.Read(path + ".bak").Project.ReferenceX == 0, "Previous recovery copy was not retained.");
        var captured = Capture(); Change(() => project.ReferenceX = 31); await AutosaveNowAsync();
        Assert(captured.Project.ReferenceX == 25, "History snapshot shares mutable model data.");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Change(() => project.ReferenceX = 39); await AutosaveNowAsync();
            Assert(autosaveStatus.Text.StartsWith("Autosave failed"), "Autosave failure was hidden.");
        }
        Assert(autosaves.Read(path).Project.ReferenceX == 31 && dirty, "Failed autosave destroyed the previous copy or dirty state.");
        await AutosaveNowAsync(); Assert(autosaves.Read(path).Project.ReferenceX == 39, "Autosave did not recover after a disk failure.");
        dragBefore = Capture(); dragging = true; project.ReferenceX = 40; await AutosaveNowAsync();
        Assert(autosaves.Read(path).Project.ReferenceX == 39, "Autosave captured a half-finished drag."); FinishDrag();
        var deferredDeadline = DateTime.UtcNow.AddSeconds(5);
        while (lastAutosavedState?.Project.ReferenceX != 40 && DateTime.UtcNow < deferredDeadline) await Task.Delay(20);
        Assert(lastAutosavedState?.Project.ReferenceX == 40, "Deferred autosave was not resumed after the drag.");
        Change(() => project.ReferenceX = 41); var saving = AutosaveNowAsync();
        var oldSession = sessionId; project = new Project { Name = "Different project" }; ResetProject(); await saving;
        Assert(lastAutosavedState == null && autosaves.Read(autosaves.PathFor(oldSession)).Project.ReferenceX == 41, "In-flight autosave followed a project switch.");
        Assert(!File.Exists(autosaves.PathFor(sessionId)), "Old snapshot was written under new project identity.");
        dirty = false;
    }
}
