using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

internal sealed class RuntimeComponentManager(
    EditorAdapterBuilder builder,
    RuntimeSessionMode mode,
    IEditorAssetResolver assetResolver,
    IEditorInteractionResolver interactionResolver,
    Action<StructuredDiagnosticMessage> reportDiagnostic)
{
    private readonly Dictionary<Guid, LiveComponent> _live = [];
    private readonly Dictionary<Guid, long> _failedCreationRevisions = [];
    private readonly Dictionary<Guid, (long Revision, long AssetCatalogVersion)> _failedSteps = [];
    private readonly HashSet<(Guid ComponentId, int StoredVersion, long Revision)> _reportedSchemaIssues = [];

    public int LiveComponentCount => _live.Count;

    public RuntimeComponentManager(
        EditorAdapterBuilder builder,
        RuntimeSessionMode mode,
        IEditorAssetResolver assetResolver,
        Action<StructuredDiagnosticMessage> reportDiagnostic)
        : this(
            builder,
            mode,
            assetResolver,
            new RuntimeInteractionResolver(builder, mode, () => SceneUiInteractionMode.Edit),
            reportDiagnostic)
    {
    }

    public void SynchronizeAndStep(
        SceneDocument scene,
        long revision,
        RuntimePlayState playState,
        float deltaTime,
        float simulationTime,
        CancellationToken cancellationToken)
    {
        var desired = new HashSet<Guid>();
        foreach (var entity in scene.Traverse())
        {
            if (!entity.Enabled)
            {
                continue;
            }

            foreach (var component in OrderComponents(entity.Components.Records))
            {
                if (!component.Enabled
                    || !builder.TryGetRegistration(component.TypeId, out var registration)
                    || !SupportsMode(registration.Descriptor.Modes))
                {
                    continue;
                }

                var runtimeComponent = component;
                if (component.SchemaVersion != registration.Descriptor.SchemaVersion)
                {
                    if (component.SchemaVersion < registration.Descriptor.SchemaVersion
                        && builder.TryMigrate(
                            component.TypeId,
                            component.SchemaVersion,
                            component.Data,
                            out var migratedVersion,
                            out var migratedData,
                            out _))
                    {
                        runtimeComponent = new SceneComponentRecord
                        {
                            Id = component.Id,
                            TypeId = component.TypeId,
                            SchemaVersion = migratedVersion,
                            Enabled = component.Enabled,
                            Data = migratedData,
                            ExtensionData = component.ExtensionData,
                        };
                    }
                    else
                    {
                        var issue = (component.Id, component.SchemaVersion, revision);
                        if (_reportedSchemaIssues.Add(issue))
                        {
                            reportDiagnostic(new(
                                DiagnosticSeverity.Warning,
                                DiagnosticOrigin.Component,
                                "SKED-COMPONENT-SCHEMA",
                                $"'{registration.Descriptor.DisplayName}' schema {component.SchemaVersion} cannot run against schema {registration.Descriptor.SchemaVersion}.",
                                component.SchemaVersion > registration.Descriptor.SchemaVersion
                                    ? "Open the project with a newer adapter or preserve this component without editing it."
                                    : "Register a complete deterministic migration chain or remove the component.",
                                EntityId: entity.Id,
                                ComponentId: component.Id,
                                ComponentTypeId: component.TypeId,
                                DocumentRevision: revision));
                        }

                        continue;
                    }
                }

                desired.Add(component.Id);
                var context = CreateContext(
                    entity,
                    runtimeComponent,
                    playState,
                    deltaTime,
                    simulationTime,
                    cancellationToken);
                if (!_live.TryGetValue(component.Id, out var live))
                {
                    if (_failedCreationRevisions.TryGetValue(component.Id, out var failedRevision)
                        && failedRevision == revision)
                    {
                        continue;
                    }

                    live = CreateLiveComponent(entity, runtimeComponent, registration, context, revision);
                    if (live is null)
                    {
                        _failedCreationRevisions[component.Id] = revision;
                        continue;
                    }

                    _failedCreationRevisions.Remove(component.Id);
                    _live.Add(component.Id, live);
                }
                else if (!string.Equals(live.TypeId, runtimeComponent.TypeId, StringComparison.Ordinal))
                {
                    Destroy(live);
                    _live.Remove(component.Id);
                    _failedCreationRevisions.Remove(component.Id);
                    live = CreateLiveComponent(entity, runtimeComponent, registration, context, revision);
                    if (live is null)
                    {
                        _failedCreationRevisions[component.Id] = revision;
                        continue;
                    }

                    _live.Add(component.Id, live);
                }
                else if (live.LastRevision != revision
                    || live.LastAssetCatalogVersion != assetResolver.CatalogVersion)
                {
                    try
                    {
                        live.Runtime.Apply(context, runtimeComponent.Data);
                    }
                    catch (Exception exception)
                    {
                        reportDiagnostic(new(
                            DiagnosticSeverity.Error,
                            DiagnosticOrigin.Component,
                            "SKED-COMPONENT-APPLY",
                            $"Could not apply '{registration.Descriptor.DisplayName}' values on '{entity.Name}'. The previous valid values remain active.",
                            "Correct the highlighted component values or remove the component.",
                            EntityId: entity.Id,
                            ComponentId: component.Id,
                            ComponentTypeId: component.TypeId,
                            DocumentRevision: revision,
                            ExceptionDetail: exception.ToString()));
                    }

                    live.LastRevision = revision;
                    live.LastAssetCatalogVersion = assetResolver.CatalogVersion;
                }

                live.LastContext = context;
                var stepSignature = (revision, assetResolver.CatalogVersion);
                if (_failedSteps.TryGetValue(component.Id, out var failedStep)
                    && failedStep == stepSignature)
                {
                    continue;
                }

                try
                {
                    live.Runtime.Step(context);
                    _failedSteps.Remove(component.Id);
                }
                catch (Exception exception)
                {
                    _failedSteps[component.Id] = stepSignature;
                    reportDiagnostic(new(
                        DiagnosticSeverity.Error,
                        DiagnosticOrigin.Component,
                        "SKED-COMPONENT-STEP",
                        $"'{registration.Descriptor.DisplayName}' failed while updating '{entity.Name}' and has been paused for this revision.",
                        "Correct the component or project code; editing and other components will continue.",
                        EntityId: entity.Id,
                        ComponentId: component.Id,
                        ComponentTypeId: component.TypeId,
                        DocumentRevision: revision,
                        ExceptionDetail: exception.ToString()));
                }
            }
        }

        foreach (var removed in _live.Values.Where(component => !desired.Contains(component.ComponentId)).ToArray())
        {
            Destroy(removed);
            _live.Remove(removed.ComponentId);
            _failedSteps.Remove(removed.ComponentId);
        }

        foreach (var removed in _failedCreationRevisions.Keys.Where(id => !desired.Contains(id)).ToArray())
        {
            _failedCreationRevisions.Remove(removed);
        }
    }

    public void DestroyAll()
    {
        foreach (var component in _live.Values.Reverse().ToArray())
        {
            Destroy(component);
        }

        _live.Clear();
        _failedCreationRevisions.Clear();
        _failedSteps.Clear();
        _reportedSchemaIssues.Clear();
    }

    public IReadOnlyList<EditorPickBounds> GetLocalPickBounds(Guid entityId)
    {
        var result = new List<EditorPickBounds>();
        foreach (var live in _live.Values)
        {
            if (live.EntityId != entityId
                || live.Runtime is not IEditorComponentPickBoundsProvider provider)
            {
                continue;
            }

            try
            {
                var bounds = provider.GetLocalPickBounds(live.LastContext);
                if (bounds is { } value
                    && value.SizeX > 0
                    && value.SizeY > 0
                    && value.SizeZ > 0)
                {
                    result.Add(value);
                }
            }
            catch (Exception exception)
            {
                reportDiagnostic(new(
                    DiagnosticSeverity.Warning,
                    DiagnosticOrigin.Component,
                    "SKED-COMPONENT-PICK-BOUNDS",
                    $"Component '{live.TypeId}' could not provide Scene pick bounds.",
                    "Fix the component's optional pick-bounds provider; rendering will continue.",
                    EntityId: live.EntityId,
                    ComponentId: live.ComponentId,
                    ComponentTypeId: live.TypeId,
                    ExceptionDetail: exception.ToString()));
            }
        }

        return result;
    }

    public IReadOnlyList<RuntimeComponentStatusMessage> GetComponentStatuses(SceneEntity entity)
    {
        var result = new List<RuntimeComponentStatusMessage>(entity.Components.Records.Count);
        foreach (var component in entity.Components.Records)
        {
            var registered = builder.TryGetRegistration(component.TypeId, out var registration);
            var displayName = registered
                ? registration!.Descriptor.DisplayName
                : component.TypeId;
            var isLive = _live.ContainsKey(component.Id);
            var state = !entity.Enabled
                ? "Entity disabled"
                : !component.Enabled
                    ? "Disabled"
                    : _failedCreationRevisions.ContainsKey(component.Id)
                        ? "Create failed"
                        : _failedSteps.ContainsKey(component.Id)
                            ? "Step paused after error"
                            : isLive
                                ? "Live"
                                : !registered
                                    ? "Adapter unavailable"
                                    : !SupportsMode(registration!.Descriptor.Modes)
                                        ? $"Not active in {mode}"
                                        : "Waiting or schema-incompatible";
            result.Add(new(
                component.Id,
                component.TypeId,
                displayName,
                state,
                isLive));
        }

        return result;
    }

    private LiveComponent? CreateLiveComponent(
        SceneEntity entity,
        SceneComponentRecord component,
        EditorComponentRegistration registration,
        EditorComponentContext context,
        long revision)
    {
        try
        {
            var runtime = registration.Factory();
            runtime.Create(context, component.Data);
            return new(
                entity.Id,
                component.Id,
                component.TypeId,
                runtime,
                context,
                revision,
                assetResolver.CatalogVersion);
        }
        catch (Exception exception)
        {
            reportDiagnostic(new(
                DiagnosticSeverity.Error,
                DiagnosticOrigin.Component,
                "SKED-COMPONENT-CREATE",
                $"Could not create '{registration.Descriptor.DisplayName}' on '{entity.Name}'.",
                "Inspect the component values and project runtime log.",
                EntityId: entity.Id,
                ComponentId: component.Id,
                ComponentTypeId: component.TypeId,
                DocumentRevision: revision,
                ExceptionDetail: exception.ToString()));
            return null;
        }
    }

    private void Destroy(LiveComponent live)
    {
        try
        {
            live.Runtime.Destroy(live.LastContext);
        }
        catch (Exception exception)
        {
            reportDiagnostic(new(
                DiagnosticSeverity.Warning,
                DiagnosticOrigin.Component,
                "SKED-COMPONENT-DESTROY",
                $"Component '{live.TypeId}' failed during cleanup.",
                ComponentId: live.ComponentId,
                ComponentTypeId: live.TypeId,
                ExceptionDetail: exception.ToString()));
        }
    }

    private EditorComponentContext CreateContext(
        SceneEntity entity,
        SceneComponentRecord component,
        RuntimePlayState playState,
        float deltaTime,
        float simulationTime,
        CancellationToken cancellationToken)
    {
        var transform = entity.Components.Transform;
        return new(
            mode == RuntimeSessionMode.Scene ? EditorRuntimeMode.Scene : EditorRuntimeMode.Play,
            playState switch
            {
                RuntimePlayState.Playing => EditorRuntimePlayState.Playing,
                RuntimePlayState.Paused => EditorRuntimePlayState.Paused,
                _ => EditorRuntimePlayState.Editing,
            },
            entity.Id,
            component.Id,
            new EditorTransformState(
                transform.Position.X,
                transform.Position.Y,
                transform.Position.Z,
                transform.Rotation.X,
                transform.Rotation.Y,
                transform.Rotation.Z,
                transform.Rotation.W,
                transform.Scale.X,
                transform.Scale.Y,
                transform.Scale.Z),
            deltaTime,
            simulationTime,
            cancellationToken,
            assetResolver,
            interactionResolver);
    }

    private bool SupportsMode(EditorComponentModes modes) => mode == RuntimeSessionMode.Scene
        ? modes.HasFlag(EditorComponentModes.Scene)
        : modes.HasFlag(EditorComponentModes.Play);

    private IReadOnlyList<SceneComponentRecord> OrderComponents(
        IReadOnlyList<SceneComponentRecord> components)
    {
        var result = new List<SceneComponentRecord>(components.Count);
        var visited = new HashSet<Guid>();
        foreach (var component in components)
        {
            Visit(component);
        }

        return result;

        void Visit(SceneComponentRecord component)
        {
            if (!visited.Add(component.Id))
            {
                return;
            }

            if (builder.TryGetRegistration(component.TypeId, out var registration))
            {
                foreach (var requiredTypeId in registration.Descriptor.RequiredComponentTypeIds)
                {
                    foreach (var dependency in components.Where(candidate =>
                                 string.Equals(candidate.TypeId, requiredTypeId, StringComparison.Ordinal)))
                    {
                        Visit(dependency);
                    }
                }
            }

            result.Add(component);
        }
    }

    private sealed class LiveComponent(
        Guid entityId,
        Guid componentId,
        string typeId,
        IEditorComponentRuntime runtime,
        EditorComponentContext lastContext,
        long lastRevision,
        long lastAssetCatalogVersion)
    {
        public Guid EntityId { get; } = entityId;
        public Guid ComponentId { get; } = componentId;
        public string TypeId { get; } = typeId;
        public IEditorComponentRuntime Runtime { get; } = runtime;
        public EditorComponentContext LastContext { get; set; } = lastContext;
        public long LastRevision { get; set; } = lastRevision;
        public long LastAssetCatalogVersion { get; set; } = lastAssetCatalogVersion;
    }
}
