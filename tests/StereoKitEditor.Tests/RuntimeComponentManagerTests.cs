using System.Text.Json;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Runtime;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class RuntimeComponentManagerTests
{
    [Fact]
    public void AssetCatalogChange_ReappliesLiveComponentWithoutDocumentRevisionChange()
    {
        var runtime = new RecordingRuntime();
        var builder = CreateBuilder(() => runtime);
        var assets = new MutableAssetResolver();
        var manager = new RuntimeComponentManager(builder, RuntimeSessionMode.Scene, assets, _ => { });
        var scene = CreateScene();

        Step(manager, scene, revision: 1);
        Step(manager, scene, revision: 1);
        assets.CatalogVersion++;
        Step(manager, scene, revision: 1);

        Assert.Equal(1, runtime.CreateCalls);
        Assert.Equal(1, runtime.ApplyCalls);
        Assert.Equal(3, runtime.StepCalls);
        Assert.Same(assets, runtime.LastContext?.Assets);
    }

    [Fact]
    public void StepFailure_IsReportedOnceAndDoesNotEscapeOrRetryUntilStateChanges()
    {
        var runtime = new RecordingRuntime { ThrowDuringStep = true };
        var builder = CreateBuilder(() => runtime);
        var assets = new MutableAssetResolver();
        var diagnostics = new List<StructuredDiagnosticMessage>();
        var manager = new RuntimeComponentManager(
            builder,
            RuntimeSessionMode.Scene,
            assets,
            diagnostics.Add);
        var scene = CreateScene();

        Step(manager, scene, revision: 1);
        Step(manager, scene, revision: 1);
        Step(manager, scene, revision: 2);

        Assert.Equal(2, runtime.StepCalls);
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Equal("SKED-COMPONENT-STEP", diagnostic.Code));
    }

    [Fact]
    public void OlderSchema_UsesMigratedDataForRuntimeWithoutMutatingDocument()
    {
        var runtime = new RecordingRuntime();
        var builder = new EditorAdapterBuilder();
        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "com.example.asset-aware",
                SchemaVersion = 2,
                DisplayName = "Asset Aware",
                DefaultData = JsonSerializer.SerializeToElement(new { label = "new" }),
            },
            () => runtime,
            [
                new EditorComponentMigration
                {
                    FromVersion = 1,
                    ToVersion = 2,
                    Upgrade = _ => JsonSerializer.SerializeToElement(new { label = "migrated" }),
                },
            ]);
        var manager = new RuntimeComponentManager(
            builder,
            RuntimeSessionMode.Scene,
            new MutableAssetResolver(),
            _ => { });
        var scene = CreateScene();
        var stored = scene.Traverse().SelectMany(entity => entity.Components.Records)
            .Single(component => component.TypeId == "com.example.asset-aware");

        Step(manager, scene, revision: 1);

        Assert.Equal("migrated", runtime.LastData?.GetProperty("label").GetString());
        Assert.Equal(1, stored.SchemaVersion);
        Assert.False(stored.Data.TryGetProperty("label", out _));
    }

    [Fact]
    public void Dependencies_CreateBeforeConsumersRegardlessOfSceneOrder()
    {
        var order = new List<string>();
        var builder = new EditorAdapterBuilder();
        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "com.example.service",
                SchemaVersion = 1,
                DisplayName = "Service",
            },
            () => new OrderedRuntime("service", order));
        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "com.example.consumer",
                SchemaVersion = 1,
                DisplayName = "Consumer",
                RequiredComponentTypeIds = ["com.example.service"],
            },
            () => new OrderedRuntime("consumer", order));
        builder.ValidateRegistrations();
        var entity = new SceneEntity { Name = "Host" };
        entity.Components.Add(SceneComponentRecord.Create("com.example.consumer", new { }));
        entity.Components.Add(SceneComponentRecord.Create("com.example.service", new { }));
        var manager = new RuntimeComponentManager(
            builder,
            RuntimeSessionMode.Scene,
            new MutableAssetResolver(),
            _ => { });

        Step(manager, new SceneDocument { Roots = [entity] }, revision: 1);

        Assert.Equal(["service", "consumer"], order);
    }

    private static EditorAdapterBuilder CreateBuilder(Func<IEditorComponentRuntime> factory)
    {
        var builder = new EditorAdapterBuilder();
        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "com.example.asset-aware",
                SchemaVersion = 1,
                DisplayName = "Asset Aware",
                DefaultData = JsonSerializer.SerializeToElement(new { }),
            },
            factory);
        return builder;
    }

    private static SceneDocument CreateScene()
    {
        var entity = new SceneEntity { Name = "Host" };
        entity.Components.Add(SceneComponentRecord.Create("com.example.asset-aware", new { }));
        return new SceneDocument { Roots = [entity] };
    }

    private static void Step(RuntimeComponentManager manager, SceneDocument scene, long revision) =>
        manager.SynchronizeAndStep(
            scene,
            revision,
            RuntimePlayState.Editing,
            1f / 60f,
            0,
            CancellationToken.None);

    private sealed class RecordingRuntime : IEditorComponentRuntime
    {
        public int CreateCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int StepCalls { get; private set; }
        public bool ThrowDuringStep { get; init; }
        public EditorComponentContext? LastContext { get; private set; }
        public JsonElement? LastData { get; private set; }

        public void Create(EditorComponentContext context, JsonElement data)
        {
            CreateCalls++;
            LastContext = context;
            LastData = data.Clone();
        }

        public void Apply(EditorComponentContext context, JsonElement data)
        {
            ApplyCalls++;
            LastContext = context;
            LastData = data.Clone();
        }

        public void Step(EditorComponentContext context)
        {
            StepCalls++;
            LastContext = context;
            if (ThrowDuringStep)
            {
                throw new InvalidOperationException("Fixture step failure");
            }
        }

        public void Destroy(EditorComponentContext context)
        {
        }
    }

    private sealed class MutableAssetResolver : IEditorAssetResolver
    {
        public long CatalogVersion { get; set; }

        public bool TryResolve(Guid assetId, out EditorRuntimeAsset asset)
        {
            asset = null!;
            return false;
        }
    }

    private sealed class OrderedRuntime(string name, ICollection<string> order) : IEditorComponentRuntime
    {
        public void Create(EditorComponentContext context, JsonElement data) => order.Add(name);
        public void Apply(EditorComponentContext context, JsonElement data) { }
        public void Step(EditorComponentContext context) { }
        public void Destroy(EditorComponentContext context) { }
    }
}
