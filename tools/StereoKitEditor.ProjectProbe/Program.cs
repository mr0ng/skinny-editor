using StereoKitEditor.App.Services;
using StereoKitEditor.Assets;
using System.Text.Json;
using StereoKitEditor.ProjectSystem;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: StereoKitEditor.ProjectProbe <project.skproject.json> " +
        "[expected-component-type-id] [expected-log-fragment]");
    return 2;
}

var descriptor = EditorProjectDefinition.Load(Path.GetFullPath(args[0]));
var expectedComponent = args.Length > 1 ? args[1] : null;
var expectedLogFragment = args.Length > 2 ? args[2] : null;
var scene = SceneSerializer.Deserialize(await File.ReadAllTextAsync(descriptor.ResolveStartupScenePath()));
var assetDatabase = new AssetDatabase(
    descriptor.ProjectId,
    descriptor.ProjectDirectory,
    descriptor.AssetsRoot);
var assets = await assetDatabase.RefreshAsync();
var runtimeAssets = assets.Select(asset => new RuntimeAssetDescriptor(
    asset.Metadata.AssetId,
    asset.Metadata.Kind.ToString(),
    asset.SourceFullPath,
    asset.Metadata.ContentHash,
    asset.Metadata.Bounds is { } bounds
        ? new RuntimeAssetBounds(
            bounds.CenterX,
            bounds.CenterY,
            bounds.CenterZ,
            bounds.SizeX,
            bounds.SizeY,
            bounds.SizeZ)
        : null,
    asset.Metadata.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray(),
    JsonSerializer.SerializeToElement(new
    {
        importerSettings = asset.Metadata.ImporterSettings,
        model = asset.Metadata.Model,
        texture = asset.Metadata.Texture,
        font = asset.Metadata.Font,
        material = asset.Metadata.Material,
        textStyle = asset.Metadata.TextStyle,
    }, SceneSerializer.Options),
    asset.Metadata.AssetDependencies)).ToArray();

if (assets.Any(asset => asset.HasErrors))
{
    foreach (var asset in assets.Where(asset => asset.HasErrors))
    foreach (var diagnostic in asset.Metadata.Diagnostics)
    {
        Console.Error.WriteLine($"{asset.Metadata.SourcePath}: {diagnostic.Code}: {diagnostic.Message}");
    }

    return 3;
}

try
{
    foreach (var mode in new[] { RuntimeSessionMode.Scene, RuntimeSessionMode.Play })
    {
        var profileMode = mode == RuntimeSessionMode.Scene
            ? RuntimeProfileMode.Scene
            : RuntimeProfileMode.Play;
        var profile = descriptor.CreateRuntimeProjectSpec(profileMode);
        var build = await new DotnetProjectBuilder().BuildAsync(
            profile,
            output => Console.WriteLine($"[{mode} build] {output.Text}"));
        var revisionApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedLogObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new RuntimeSession(mode);
        session.EventReceived += (_, runtimeEvent) =>
        {
            if (runtimeEvent.Kind != RuntimeEventKind.Log
                || runtimeEvent.Message.StartsWith("[ModelViewer]", StringComparison.Ordinal)
                || string.Equals(runtimeEvent.Level, "Error", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[{mode} {runtimeEvent.Kind}] {runtimeEvent.Message}");
            }
            if (runtimeEvent.Kind == RuntimeEventKind.RevisionApplied && runtimeEvent.Revision == 1)
            {
                revisionApplied.TrySetResult();
            }

            if (expectedLogFragment is not null
                && runtimeEvent.Kind == RuntimeEventKind.Log
                && runtimeEvent.Message.Contains(expectedLogFragment, StringComparison.Ordinal))
            {
                expectedLogObserved.TrySetResult();
            }

            if (runtimeEvent.Kind == RuntimeEventKind.Error
                || (runtimeEvent.Kind == RuntimeEventKind.Stopped && runtimeEvent.Unexpected))
            {
                failed.TrySetResult(new InvalidOperationException(runtimeEvent.Message));
            }
        };

        await session.StartAsync(
            build.TargetPath,
            profile.WorkingDirectory,
            mode == RuntimeSessionMode.Scene ? scene : SceneSerializer.Clone(scene),
            revision: 1,
            selectedEntityId: scene.Roots.FirstOrDefault()?.Id,
            new RuntimeLaunchIdentity(
                descriptor.ProjectId,
                descriptor.Name,
                profile.ProfileId,
                build.BuildId),
            profile.Arguments,
            profile.Environment,
            runtimeAssets: runtimeAssets);

        if (expectedComponent is not null
            && session.ComponentCatalog?.Components.Any(component =>
                string.Equals(component.TypeId, expectedComponent, StringComparison.Ordinal)) != true)
        {
            throw new InvalidOperationException(
                $"Runtime catalog did not advertise expected component '{expectedComponent}'.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var success = expectedLogFragment is null
            ? revisionApplied.Task
            : Task.WhenAll(revisionApplied.Task, expectedLogObserved.Task);
        var completed = await Task.WhenAny(success, failed.Task).WaitAsync(timeout.Token);
        if (completed == failed.Task)
        {
            throw await failed.Task;
        }

        await success.WaitAsync(timeout.Token);
        Console.WriteLine(
            $"{mode} verified · adapter {session.ComponentCatalog?.AdapterId} · " +
            $"component {expectedComponent ?? "(not required)"} · " +
            $"log {expectedLogFragment ?? "(not required)"} · build {build.BuildId[..8]}");
    }

    Console.WriteLine($"Project adapter probe passed for {descriptor.Name} in Scene and Play.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Project adapter probe failed: {exception}");
    return 1;
}
