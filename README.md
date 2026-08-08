# SKinny Editor

**SKinny Editor** is a visual editor for StereoKit projects. This workspace contains the Windows preview, project SDK, automated verification, and product/architecture documentation.

Status: **Windows public preview under active development**.

## Run from source

Supported authoring platform: **Windows**. macOS and Linux editor support are intentionally out of scope for the current roadmap. Requirements are Windows and a .NET 8-or-newer SDK. From the repository root:

```powershell
dotnet restore StereoKitEditor.sln
dotnet build StereoKitEditor.sln
dotnet test StereoKitEditor.sln --no-build
dotnet run --project src/StereoKitEditor.App
```

The first run asks you to trust the sample workspace before MSBuild or project code runs. Review the exact project, working directory, command, and environment-variable names, then choose **Trust and Run** only if you trust this local source.

Open another descriptor from **Open Project** or **Recent**, with `--project <path>`, or by setting `SKINNY_PROJECT`. A portable build with no default project opens the project launcher instead of assuming the source sample is present.

The embedded **Scene** host starts with the editor. Click a primitive—or project component geometry that advertises pick bounds—to select its owning entity. Use `W`, `E`, and `R` for Move, Rotate, and Scale; drag axes, planes, rings, screen/free-rotation handles, enable per-tool snapping, and press `Escape` to cancel a gesture. Multi-object gestures and Ctrl-drag duplication commit as one undo entry. Scene also supports perspective/orthographic projection, an adaptive grid, center/active pivots, a clickable orientation widget, numeric drag feedback, right-mouse fly/look, Alt+left orbit, middle-mouse pan, normalized wheel zoom, `F` to frame selection, and Global/Local Move/Rotate axes. Up/Down move the Scene camera forward/backward, Left/Right move sideways, and holding Shift moves faster. In the sample, the teal Project Marker is a component of `Welcome Cube`, so selecting it highlights that existing Hierarchy entity rather than adding a separate row.

Press `F6` or **Play** to start an isolated **Game** host from a deep-cloned scene snapshot. `F7` pauses/resumes, `F8` steps one paused frame, and `Shift+F6` stops Game without stopping Scene. Per-view status reports the last rendered document revision.

The Windows preview currently provides:

- a dark Avalonia shell with persisted, resizable Hierarchy, Scene, Inspector, Project, and Console panels;
- stable-ID scene/entity/component JSON with unknown-component preservation;
- generic scene-format-2 project components with backed-up format-1 migration;
- cube and sphere creation, selection, enable state, rename, position/rotation/scale editing;
- command-based undo/redo plus atomic save and deterministic reopen;
- a versioned, duplex named-pipe protocol;
- descriptor-driven `dotnet build` and direct launch of an independent StereoKit `.csproj`;
- a reusable `StereoKitEditor.Runtime` package with project-owned initialize/frame/shutdown callbacks;
- a StereoKit-neutral adapter contract, explicit project component catalog, generated Add Component/Inspector UI, and per-instance runtime lifecycle;
- a sample project-owned Marker editable in Scene and isolated Play;
- immutable, content-addressed build generations and visibly stale Play sessions after relevant rebuilds;
- workspace trust, current-user pipes, heartbeat/unresponsive state, structured diagnostics, bounded Scene crash recovery, and Windows Job Object cleanup;
- an always-running, embedded StereoKit Scene host with a persistent fly/orbit/pan camera and frame-selection command;
- distance-scaled axis/planar Move, axis/screen/free Rotate, and axis/uniform Scale tools with multi-selection, center/active pivots, snapping, parent-transform support, Ctrl-drag duplication, numeric feedback, and one undo commit per gesture;
- perspective/orthographic Scene projection, adaptive grid, fixed view presets, and a native clickable orientation widget;
- GLB import with stable sidecar GUIDs, content hashes, bounds/dependency diagnostics, cached thumbnails, and move-safe scene references;
- a built-in Model Renderer with Project-panel creation, fit-to-bounds, model-aware picking, and accurate Frame Selection;
- adapter contract 0.3 typed asset resolution and catalog-change reapplication for project-owned components;
- a separately isolated, embedded Game host with Play, Pause, Step, and Stop;
- deep-cloned Play state that is unaffected by later edit-document changes;
- editor survival and child-process cleanup when either runtime stops or the editor closes.
- child creation, multi-selection, inline rename, duplicate, delete, cycle-safe drag reparenting, and world-transform preservation;
- searchable asset folders, rename/move with stable GUIDs, referenced-delete protection, and recoverable project trash;
- recent-project opening, selectable Scene/Play profiles, persistent watcher/inspection preferences, and unsaved Save/Discard/Cancel protection;
- revisioned change sets, schema-upgrade proposals, compatibility gating, redacted crash bundles, generation retention, and Wait/Restart/Stop hang recovery;
- read-only live runtime inspection with FPS/frame time, memory, object/component counts, and selected-component states;
- reusable scene-subtree templates with fresh IDs on every instantiation;
- an optional Android ADB publish/install/launch deployment provider;
- versioned preview NuGet packages and a verified self-contained `win-x64` ZIP distribution.
- built-in environment and editor-annotation components plus safe declarative Slider/Multiline extension presentations.
- debounced local scene recovery with startup restore/discard choice after an interrupted editing session.

The Windows-only native input regression probe launches the sample Scene briefly, posts one standard wheel notch and one Up-arrow press, checks the reported camera state, and closes the runtime automatically:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-native-scene-input.ps1
```

Pass `-GlbPath <file.glb>` to the same probe to import a real model and verify that the isolated StereoKit runtime loads and draws it before shutting down.

The native embedding bridge is Windows-specific by design. Other desktop platforms are deprioritized until after a successful Windows beta and a later demand review. The adapter, transform tools, GLB pipeline, revisioned synchronization, migrations, richer pickers, and recovery layer are implemented.

## Package the preview

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-windows.ps1
```

This produces a portable self-contained editor, ZIP, SHA-256 checksum, and the four exactly paired project SDK packages under `artifacts/distribution`. See [installation and onboarding](docs/guides/installation-and-onboarding.md).

## Architecture at a glance

SKinny Editor uses a .NET desktop shell with separate StereoKit processes for the live Scene and isolated Play experiences. Projects expose authorable behavior through an explicit component catalog and runtime adapter. See the [architecture overview](docs/architecture/overview.md) for the process model, structured-authoring boundary, asset identity, and trust model.

## Documentation

- [Documentation index](docs/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [Installation and onboarding](docs/guides/installation-and-onboarding.md)
- [Extension authoring](docs/guides/extension-authoring.md)
- [Visual authoring guide](docs/guides/visual-authoring.md)
- [Public roadmap](docs/roadmap/README.md)

## Current implementation frontier

The portable Windows editor foundation and visual-content/spatial-UI implementation are complete. The automated gate covers large scenes and asset libraries, compatibility and migrations, clean SDK consumption, portable packaging, typed visual assets, isolated Scene/Play, and native input. Hardware-dependent DPI, GPU, device-loss, and hands-on viewport acceptance remain. Windows installation and project onboarding are the next major product phase; see the [public roadmap](docs/roadmap/README.md).
