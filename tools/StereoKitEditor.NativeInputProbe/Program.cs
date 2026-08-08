using System.Runtime.InteropServices;
using System.Threading.Channels;
using StereoKitEditor.Assets;
using StereoKitEditor.App.Services;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The native scene input probe currently requires Windows.");
    return 2;
}

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var runtimeAssembly = Path.Combine(
    repositoryRoot,
    "samples",
    "HelloStereoKitProject",
    "bin",
    "Debug",
    "net8.0",
    "HelloStereoKitProject.dll");
if (!File.Exists(runtimeAssembly))
{
    Console.Error.WriteLine($"Build the Debug solution before running this probe. Missing: {runtimeAssembly}");
    return 3;
}

var cameraEvents = Channel.CreateUnbounded<SceneCameraState>();
var runtimeEvents = Channel.CreateUnbounded<RuntimeEventArgs>();
string? assetProbeRoot = null;
var migrationHost = new SceneEntity { Name = "Migration Probe" };
migrationHost.Components.Add(SceneComponentRecord.Create(
    "com.example.marker",
    new
    {
        color = new[] { 0.1, 0.72, 0.66, 1.0 },
        size = 0.12,
        verticalOffset = 0.18,
        visible = true,
        shape = "Cube",
    }));
var probeScene = new SceneDocument { Roots = [migrationHost] };
await using var session = new RuntimeSession(RuntimeSessionMode.Scene);
session.EventReceived += (_, args) =>
{
    runtimeEvents.Writer.TryWrite(args);
    if (args.Kind == RuntimeEventKind.SceneCameraChanged && args.Camera is not null)
    {
        cameraEvents.Writer.TryWrite(args.Camera);
    }
};

try
{
    await session.StartAsync(
        runtimeAssembly,
        Path.GetDirectoryName(runtimeAssembly)!,
        probeScene,
        revision: 1,
        selectedEntityId: null,
        new RuntimeLaunchIdentity(
            Guid.NewGuid(),
            "Native Input Probe",
            "default",
            "debug"),
        sceneCamera: SceneCameraState.Default);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (session.NativeWindowHandle == 0)
    {
        await Task.Delay(50, timeout.Token);
    }

    ComponentMigrationProposalMessage? migrationProposal = null;
    while (migrationProposal is null)
    {
        var runtimeEvent = await runtimeEvents.Reader.ReadAsync(timeout.Token);
        if (runtimeEvent.Kind == RuntimeEventKind.ComponentMigrationProposed)
        {
            migrationProposal = runtimeEvent.MigrationProposal;
        }
        else if (runtimeEvent.Kind is RuntimeEventKind.Error or RuntimeEventKind.Stopped)
        {
            throw new InvalidOperationException(
                $"The runtime stopped before proposing the sample migration: {runtimeEvent.Message}");
        }
    }

    var migration = AssertSingle(migrationProposal.Upgrades, "sample migration");
    if (migration.FromSchemaVersion != 1
        || migration.ToSchemaVersion != 2
        || migration.MigratedData.GetProperty("label").GetString() != "Marker")
    {
        throw new InvalidOperationException("The sample component migration proposal was incomplete.");
    }

    const double baselineDistance = 0.75;
    const int wheelDelta = 120;
    var wheelWordParameter = (nint)((long)wheelDelta << 16);
    if (!PostMessage(session.NativeWindowHandle, 0x020A, wheelWordParameter, 0))
    {
        throw new InvalidOperationException("Posting WM_MOUSEWHEEL failed.");
    }

    var wheelCamera = await cameraEvents.Reader.ReadAsync(timeout.Token);
    var expectedWheelDistance = baselineDistance * Math.Exp(-0.12);
    if (Math.Abs(wheelCamera.Distance - expectedWheelDistance) > 0.0001)
    {
        throw new InvalidOperationException(
            $"Wheel distance was {wheelCamera.Distance}; expected {expectedWheelDistance}.");
    }

    const int upArrow = 0x26;
    if (!PostMessage(session.NativeWindowHandle, 0x0100, upArrow, 0))
    {
        throw new InvalidOperationException("Posting Up-arrow key down failed.");
    }
    await Task.Delay(300, timeout.Token);
    if (!PostMessage(session.NativeWindowHandle, 0x0101, upArrow, 0))
    {
        throw new InvalidOperationException("Posting Up-arrow key up failed.");
    }

    var arrowCamera = await cameraEvents.Reader.ReadAsync(timeout.Token);
    if (Math.Abs(arrowCamera.Pivot.Z - SceneCameraState.Default.Pivot.Z) < 0.0001)
    {
        throw new InvalidOperationException("The Up-arrow did not move the scene camera pivot.");
    }

    probeScene = SceneSerializer.Clone(probeScene);
    probeScene.Roots[0].Name = "Migration Probe Updated";
    await session.PushSceneAsync(probeScene, revision: 2, timeout.Token);
    await WaitForRevisionAsync(runtimeEvents, 2, timeout.Token);

    if (args.Length > 0)
    {
        var glbPath = Path.GetFullPath(args[0]);
        if (!File.Exists(glbPath))
        {
            throw new FileNotFoundException("The optional GLB probe file does not exist.", glbPath);
        }

        assetProbeRoot = Path.Combine(Path.GetTempPath(), $"skinny-runtime-asset-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(assetProbeRoot);
        var assetDatabase = new AssetDatabase(
            Guid.NewGuid(),
            assetProbeRoot,
            "Assets",
            Path.Combine(assetProbeRoot, "Cache"));
        var asset = await assetDatabase.ImportAsync(glbPath, timeout.Token);
        if (asset.HasErrors)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                asset.Metadata.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        var bounds = asset.Metadata.Bounds is { } measuredBounds
            ? new RuntimeAssetBounds(
                measuredBounds.CenterX,
                measuredBounds.CenterY,
                measuredBounds.CenterZ,
                measuredBounds.SizeX,
                measuredBounds.SizeY,
                measuredBounds.SizeZ)
            : null;
        await session.PushAssetCatalogAsync(
        [
            new RuntimeAssetDescriptor(
                asset.Metadata.AssetId,
                asset.Metadata.Kind.ToString(),
                asset.SourceFullPath,
                asset.Metadata.ContentHash,
                bounds,
                []),
        ], timeout.Token);
        var assetScene = SceneSerializer.Clone(probeScene);
        assetScene.Roots.Add(new SceneEntity
        {
            Name = "Runtime GLB Probe",
            Components =
            {
                Transform = new(
                    new Vector3Value(0, 0, -0.65),
                    QuaternionValue.Identity,
                    Vector3Value.One),
                ModelRenderer = new()
                {
                    AssetId = asset.Metadata.AssetId,
                    FitToBounds = true,
                    MaximumSize = 0.5,
                },
            },
        });
        await session.PushSceneAsync(
            assetScene,
            revision: 3,
            timeout.Token);
        await WaitForRevisionAsync(runtimeEvents, 3, timeout.Token);

        Console.WriteLine($"  GLB runtime draw: {Path.GetFileName(glbPath)} · {asset.Metadata.ContentHash[..8]}");
    }

    Console.WriteLine("Native scene input probe passed.");
    Console.WriteLine("  Scene change set: revision 1 -> 2");
    Console.WriteLine("  Component migration: schema 1 -> 2 proposed");
    Console.WriteLine($"  Wheel: {baselineDistance:F6} -> {wheelCamera.Distance:F6} (expected {expectedWheelDistance:F6})");
    Console.WriteLine(
        $"  Up arrow pivot: ({arrowCamera.Pivot.X:F6}, {arrowCamera.Pivot.Y:F6}, {arrowCamera.Pivot.Z:F6})");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Native scene input probe failed: {exception.Message}");
    return 1;
}
finally
{
    await session.StopAsync("Native input probe complete");
    if (assetProbeRoot is not null && Directory.Exists(assetProbeRoot))
    {
        Directory.Delete(assetProbeRoot, recursive: true);
    }
}

[DllImport("user32.dll", SetLastError = true)]
static extern bool PostMessage(nint window, uint message, nint wordParameter, nint longParameter);

static T AssertSingle<T>(IReadOnlyList<T> items, string label) => items.Count == 1
    ? items[0]
    : throw new InvalidOperationException($"Expected one {label}, received {items.Count}.");

static async Task WaitForRevisionAsync(
    Channel<RuntimeEventArgs> events,
    long revision,
    CancellationToken cancellationToken)
{
    while (true)
    {
        var runtimeEvent = await events.Reader.ReadAsync(cancellationToken);
        if (runtimeEvent.Kind == RuntimeEventKind.RevisionApplied && runtimeEvent.Revision == revision)
        {
            return;
        }

        if (runtimeEvent.Kind is RuntimeEventKind.Error or RuntimeEventKind.Stopped)
        {
            throw new InvalidOperationException(
                $"The runtime stopped before applying revision {revision}: {runtimeEvent.Message}");
        }
    }
}
