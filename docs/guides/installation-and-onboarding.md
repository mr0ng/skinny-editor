# Installation and onboarding

Status: **current Windows preview workflow; starter generation and transactional existing-project import available**

This document describes the supported portable build. The launcher can generate a project from the bundled starter template, and existing StereoKit projects can be inspected and scaffolded without running MSBuild or project code. The remaining product phase completes the Project Hub, broadens template/version selection, and adds deeper trust-gated onboarding validation. Installer, signing, and automatic-update work is deferred while the portable ZIP remains sufficient. See [Windows project entry and onboarding](../roadmap/product-entry-and-onboarding.md).

## Install a packaged build

Download `SKinny-Editor-0.3.0-preview.1-win-x64.zip` and its adjacent checksum
from [GitHub Releases](https://github.com/mr0ng/skinny-editor/releases). Extract
the ZIP to a user-writable directory and run `SKinny.Editor.exe`.

The executable is not code-signed yet, so Windows may display a trust or
SmartScreen warning. Verify that the archive came from the repository's
release page and compare its SHA-256 hash when desired.

To create the same package from a source checkout, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-windows.ps1
```

The command produces a versioned, self-contained `win-x64` folder, ZIP
archive, SHA-256 checksum, license notices, and matching project SDK packages
under `artifacts/distribution`. A packaged editor does not require the .NET
desktop runtime, but building StereoKit projects still requires the SDK and
workloads used by those projects.

The package is a portable Windows preview. Extract it to a user-writable directory and run `SKinny.Editor.exe`.

The ZIP includes `examples\HelloEditor\HelloEditor.skproject.json`. It restores from the bundled `sdk` package feed plus NuGet.org, so a new user can open, trust, build, author, and play the example without a source checkout. A .NET SDK is still required to build the example project.

## Open a project

Use **Open Project > Open SKinny Editor Project…** in the title bar, choose
**Recent Projects…** from the same menu, or launch directly:

```powershell
SKinny.Editor.exe --project .\Example\Example.skproject.json
```

The editor asks for workspace trust before invoking MSBuild or running project code. Review the project, command, working directory, arguments, and environment-variable names. Trust is keyed by project ID plus canonical descriptor location.

## Create a starter project

The packaged editor's no-project launcher includes **New Project**. Choose a C#-safe project name and an existing parent location. SKinny Editor creates a new folder containing a solution, normal StereoKit application project, standalone entry point, editor adapter, descriptor, initial scene, assets folder, and project README.

Creation is staged and never overwrites an existing destination. Every generated project and scene receives fresh IDs. The matching preview SDK packages are copied from the packaged editor into the project's `.skinny/sdk` feed so the generated project remains buildable if its folder is moved.

After creation, SKinny Editor opens the descriptor and uses the normal trust boundary. Trusting the project starts the existing restore/build and Scene launch workflow; press `F6` to verify the isolated Game session. Template/version selection, dedicated creation progress, and automatic Scene/Play probing remain planned follow-ups.

The New Project action requires a packaged editor that contains the bundled `sdk` feed. A source build without packaged SDK files reports how to create a packaged build instead of generating a project that cannot restore.

## Import an existing StereoKit project

Choose **Open Project > Import Existing StereoKit Project…** from either the
no-project launcher or an open editor window, then select a `.sln` or `.csproj`.
The first pass is safe inspection only: it reads solution, project, package,
target-framework, source-shape, and existing descriptor metadata without
evaluating MSBuild targets, restoring packages, loading an assembly, or running
the application.

The compatibility report separates content that can become authorable from
procedural draw calls, dynamic objects, services, and UI that remain opaque.
For supported projects, choose and review one of two integration shapes:

- **Main-project opt-in** previews a pinned runtime package reference, an
  isolated adapter helper, descriptor, scene, and authoring roots. Connecting
  the generated startup helper remains an explicit source review step.
- **Dedicated editor head** creates a separate editor-only project that
  references the selected production project without changing its composition
  root or normal launch path.

The preview lists every create/modify action and its text diff. Applying uses a
logged transaction under `.skinny/onboarding/<transaction-id>` with original
and output hashes, backups for modified files, safe descriptor validation, and
a persistent `report.json`. The dialog can roll the transaction back. Rollback
removes only generated files that still match their reviewed output and restores
modified files only if they have not changed since onboarding; later edits are
preserved and reported as conflicts.

Restore, build, adapter handshake, Scene, and Play still happen only after the
generated descriptor is opened and workspace trust is granted. The persistent
report calls out that boundary and any remaining manual adapter work.

## Add the runtime SDK to a StereoKit project

For a local package feed, add the generated folder as a NuGet source and reference the runtime package:

```xml
<ItemGroup>
  <PackageReference Include="SKinny.Editor.Runtime" Version="0.3.0-preview.1" />
</ItemGroup>
```

The runtime package brings the matching adapter, protocol, and scene packages as dependencies. Preview packages are exactly versioned while the public adapter remains pre-1.0.

At application startup, route editor launches into the isolated runtime head and keep the normal application path unchanged:

```csharp
if (EditorRuntimeHost.IsEditorLaunch(args))
{
    return EditorRuntimeHost.Run(args, new ExampleEditorAdapter());
}

return RunNormalApplication(args);
```

Register component schemas explicitly in the adapter. The desktop editor receives only the resulting data catalog; it never loads the project assembly:

```csharp
public sealed class ExampleEditorAdapter : IEditorProjectAdapter
{
    public string Id => "com.example.editor";
    public string DisplayName => "Example";
    public string Version => "0.1.0";

    public void Configure(EditorAdapterBuilder builder)
    {
        builder.RegisterComponent(
            ExampleComponents.MarkerDescriptor,
            () => new MarkerRuntime());
    }

    public void Initialize(EditorProjectRuntimeContext context) { }
    public void Step(EditorProjectRuntimeContext context) { }
    public void Shutdown(EditorProjectRuntimeContext context) { }
}
```

The complete sample is in `samples/HelloStereoKitProject`.

See [extension authoring](extension-authoring.md) for property kinds, safe declarative presentations, migrations, runtime lifecycle, pick bounds, and a complete verification checklist.

## Project descriptor

Format 2 keeps normal `.csproj` files and describes the authoring entry points:

```json
{
  "formatVersion": 2,
  "projectId": "11111111-2222-3333-4444-555555555555",
  "name": "Example",
  "solution": "Example.sln",
  "assetsRoot": "Assets",
  "scenesRoot": "Scenes",
  "startupScene": "Scenes/Main.skscene.json",
  "defaultSceneProfile": "editor-desktop",
  "defaultPlayProfile": "editor-desktop",
  "runtimeProfiles": [
    {
      "id": "editor-desktop",
      "displayName": "Editor Desktop",
      "project": "src/Example/Example.csproj",
      "configuration": "Debug",
      "targetFramework": "net8.0",
      "workingDirectory": "src/Example",
      "arguments": [],
      "environment": {},
      "modes": ["Scene", "Play"]
    }
  ]
}
```

Multiple Scene and Play profiles may point at different dedicated heads or configurations. The toolbar selectors choose which profile builds next.

## Optional Android ADB deployment

One explicit device provider is included for team validation. Add a profile when the project has a working Android target:

```json
"deploymentProfiles": [
  {
    "id": "test-headset",
    "displayName": "Test Headset",
    "provider": "android-adb",
    "project": "src/Example.Android/Example.Android.csproj",
    "configuration": "Release",
    "targetFramework": "net8.0-android",
    "apkPath": "src/Example.Android/bin/Release/net8.0-android/publish/com.example.apk",
    "packageName": "com.example.app",
    "mainActivity": ".MainActivity",
    "deviceSerial": "OPTIONAL-ADB-SERIAL"
  }
]
```

The visible **Deploy** action runs `dotnet publish`, verifies the configured APK, executes `adb install -r`, and launches the package. It never elevates. Android SDK tooling, the .NET Android workload, an authorized device, and `adb` on `PATH` are prerequisites.

## First authoring session

1. Open and trust the project.
2. Wait for the Scene profile to build and the embedded Scene viewport to report ready.
3. Import a GLB, then double-click or drag it into Scene.
4. Create children, rename with `F2`, and drag objects in Hierarchy to reparent them.
5. Use `W`, `E`, and `R` for Move, Rotate, and Scale. `F` frames the selection.
6. Add project components from Inspector and edit their generated fields.
7. Save a reusable subtree with **Save as Template…** and instantiate it from Project.
8. Press `F6` for isolated Play; use `F7`, `F8`, and `Shift+F6` for pause, step, and stop.
9. Use the read-only Live section in Inspector for performance and component state.
10. Save with `Ctrl+S`. Closing with changes offers Save, Discard, or Cancel.

## Recovery locations

- Editor preferences, recent projects, build cache, thumbnails, and diagnostics: `%LOCALAPPDATA%\SKinnyEditor`
- Interrupted-session scene recovery: `%LOCALAPPDATA%\SKinnyEditor\Recovery` (local-only, offered before project code runs, removed after Save or Discard)
- Recoverable asset deletes: `<project>\.skinny\Trash\Assets`
- Reusable scene templates: `<project>\Templates`
- Source-controlled asset identity: `<asset>.skmeta`
