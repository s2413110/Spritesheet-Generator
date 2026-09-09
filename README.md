# Mandriel's Workshop

A Windows desktop editor for building characters from images, rigging them with bones, and exporting keyframed animations as transparent PNG spritesheets. Built with .NET 10 and WPF, with no third-party packages or online services required.

The application executable is `Mandriel's Workshop.exe`. Existing `.spriteforge` projects and the recovery folder remain compatible with earlier versions.

## Run

Open `Spritesheet Generator.slnx` in Visual Studio and run the project, or:

```powershell
dotnet run --project "Spritesheet Generator"
```

Choose **Demo character** to try an animated astronaut. Press **Play** or scrub the timeline. Its signal star is hidden at frame 13 and visible again at frame 19.

## Workflow

1. In **Rig setup**, import separate body-part images. Drag them onto the canvas or use **Import sprites**. Set width/height and scale in the inspector. Transparent PNGs work best.
2. Import an optional reference. Its position, scale, opacity, and visibility are independently adjustable. It always remains behind the character and never appears in exports.
3. Click **+ Bone** and drag from joint to tip. Select an existing bone first to create a child; click empty canvas first to create a root. Bones point along their local positive X axis.
4. Select a sprite and use **Bind to bone**. Its rest placement is preserved. Adjust its local position and pivot relative to the bone. Draw additional bones and change parent bindings in the inspector.
5. Switch to **Animate**. Select a timeline frame. Drag bone tips to rotate, joints to move, or edit numeric transforms. Each edit automatically creates a key on the selected part. **Key all** captures the entire pose; **Key selected** captures one track.
6. Uncheck **Visible in this pose** to hide a sprite at that keyframe. Add a visible key later to restore it. Visibility holds until the next key; position, scale, and rotation interpolate linearly. Hiding a bone hides its children. Rotation values can exceed 360° for multiple turns.
7. Use **Animation settings** for frame count and FPS. **Preview / Export** sets cell size, rows/columns, outer margin, spacing, content offsets, and scale. Click **Update preview** after changes, then **Export PNG**. Optional JSON metadata records frame rectangles, durations, and origins.
8. Save `.spriteforge` projects to preserve the rig, keys, sheet settings, and embedded images. Undo/redo covers editing operations.

The canvas origin is centered; positive Y is up. Export content offset Y is down, following image coordinates. Frames fill the sheet left to right, then top to bottom. The dashed canvas rectangle shows the export crop. Layer arrows change drawing order at equal depth; Z determines spatial overlap.

## Undo and recovery

**Ctrl+Z** undoes project edits; **Ctrl+Y** or **Ctrl+Shift+Z** redoes them. This covers sprite imports/removal, bone creation/removal and binding, transforms and resizing, names, layer order, reference images and their settings, back textures and depth settings, visibility and other keys, animation length/FPS, and applied sheet settings. The export dialog also supports undo/redo while open.

A drag is one history step. Clicking/selecting without changing anything does not consume a step or clear redo. Esc cancels a drag without disturbing earlier history. Undo restores the affected selection, animation frame, and editing mode. While typing into a field, Ctrl+Z first undoes uncommitted text; once the field is committed it uses the project history. Saving and autosaving keep history intact. There is no fixed 30-step cutoff; history lasts for the current open project session and resets when creating, opening, or recovering a project. Navigation, zoom, playback, and writing exported files are not project edits and are not undone.

**Changed projects autosave every 60 seconds**, including projects that have never been named or manually saved. Valid pending inspector input is committed before autosaving. If the interval falls during a drag, autosave runs when the drag finishes. Disk writes run in the background, and the status bar shows the last successful autosave or a visible failure message. Unchanged content is not rewritten.

Autosaves are separate recovery copies in `%LOCALAPPDATA%\SpriteForge\Autosaves`. They contain the full project with embedded images, keys, and settings, plus the selected part and frame. **Ctrl+S still saves the main `.spriteforge` file**; autosave does not overwrite it. Use **Recover autosave…** to select a timestamped copy after a crash or restart. Recovery opens as an unsaved project, allowing inspection before choosing where to save. Startup indicates when copies are available. Recovery copies remain available across sessions, including clean closes.

Both manual saves and recovery writes use a flushed temporary file followed by an atomic replacement, keeping the previous successful version as `.bak`. The recovery picker also lists previous copies if the newest one is unreadable. A manual `character.spriteforge.bak` can be opened using **Open** and saved to a new project file. A failed write leaves the last successful copy intact and autosave retries at its next interval.

## 3D sprite rotation

X/Y/Z rotation works on bones and sprites. Set a sprite's **3D thickness** above zero to create volume. A rounded profile curves the front and back surfaces; disabling it produces a constant-depth extrusion. Side surfaces sample colors from opaque edges. A separate back image can replace the rear texture, mapped to the same local coordinates as the front; preview a 180° turn to check its orientation.

This is an initial approximation inspired by [Smack Studio's published technique](https://smackstudio.com/here-is-our-technique-for-animating-3d-without-using-3d-models/), which uses generated and editable height maps. This application uses a procedural depth profile and a mesh of up to 96 × 96 cells per sprite. Full-resolution textures are retained; side silhouettes are sampled at mesh resolution. It does not infer detailed unseen artwork, offer painted height maps, or reproduce Smack Studio's renderer. Complex cutouts, overlapping translucent parts, and strong rotations can expose approximation artifacts.

Sprites are rigidly bound to one bone each. Hierarchical forward kinematics is supported; weighted mesh deformation and inverse kinematics are not implemented. Build the rig before animating: reparenting preserves the rest pose, while existing animation keys stay local to the new parent. Reference and geometry settings apply to the whole project; transform and visibility keys animate per part. A project currently contains one animation clip.

## Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+S / Ctrl+O | Save / open |
| Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z | Undo / redo |
| Space | Play / pause |
| Left / Right | Previous / next frame |
| K | Key selected part |
| Delete | Remove part in setup; remove selected key in animation |
| Mouse wheel | Zoom |
| Esc | Cancel drawing/dragging |

## Verification

```powershell
dotnet build "Spritesheet Generator.slnx"
dotnet run --project "Spritesheet Generator" -- --self-test
```

The self-test runner checks hierarchical transforms, 3D rest-pose preservation on binding, interpolation and visibility boundaries, project round trips, actual PNG rendering and transparency, reference exclusion, export layout/offsets, edge-on depth rendering, and output limits. It writes its report and sample PNG/project artifacts to `artifacts/` in the working directory.

Safety tests additionally cover undo across edit categories, canceled/no-op drags, keyboard routing, saved-state tracking, more than 30 history steps, undo inside export, the autosave timer (with an accelerated test clock), recovery of unnamed projects, unchanged-content skipping, deferred saves after drags, locked-file failures and retries, project switching during a write, atomic backups, and recovery when the newest copy is corrupt. Test recovery files stay under `artifacts/`; tests do not write to your normal recovery folder.

Limits: 600 frames, 300 total bones/sprites, imported images up to 8192 px per side and 32 MB per file, and exported sheets up to 32 million pixels (16384 px per side maximum). Preview rendering can take time for large rigs or sheets.
