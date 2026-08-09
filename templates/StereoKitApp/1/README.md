# __PROJECT_NAME__

This is a normal StereoKit application with an explicit SKinny Editor adapter and an initial authored scene.

## Run the application

```powershell
dotnet run --project __PROJECT_NAME__.csproj
```

## Open it in SKinny Editor

Open `__PROJECT_NAME__.skproject.json` from SKinny Editor. The first open asks you to trust the project before restoring, building, or running its Scene process. Press `F6` after the Scene viewport is ready to start an isolated Game session.

The pinned SKinny Editor __SKINNY_SDK_VERSION__ packages used by this project are in `.skinny/sdk`. NuGet restores third-party dependencies from NuGet.org into `.skinny/packages`.

Add project-owned editor components, bindings, and actions in `EditorAdapter.cs`. Authored content lives in `Scenes`, while imported content belongs in `Assets`.
