# Installation and onboarding

Status: **current Windows preview workflow; full product onboarding planned in Phase 4**

This document describes what works in the current portable build. The next product phase adds the installer, desktop integration, Project Hub, new-project generation, and non-destructive onboarding of existing StereoKit projects. See [Windows installation, Project Hub, and project onboarding](../roadmap/product-entry-and-onboarding.md).

## Install a packaged build

Run the packaging command from a source checkout:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-windows.ps1
```

The command produces a self-contained `win-x64` folder, a ZIP archive, a SHA-256 checksum, and the matching project SDK packages under `artifacts/distribution`. A packaged editor does not require the .NET desktop runtime, but building StereoKit projects still requires the SDK and workloads used by those projects.

The package is a portable Windows preview, not a signed installer. Extract it to a user-writable directory and run `SKinny.Editor.exe`.

The ZIP includes `examples\HelloEditor\HelloEditor.skproject.json`. It restores from the bundled `sdk` package feed plus NuGet.org, so a new user can open, trust, build, author, and play the example without a source checkout. A .NET SDK is still required to build the example project.

## Open a project

Use **Open Project** or **Recent** in the title bar, or launch directly:

```powershell
SKinny.Editor.exe --project .\Example\Example.skproject.json
```

The editor asks for workspace trust before invoking MSBuild or running project code. Review the project, command, working directory, arguments, and environment-variable names. Trust is keyed by project ID plus canonical descriptor location.

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
