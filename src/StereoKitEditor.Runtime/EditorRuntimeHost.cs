using System.IO.Pipes;
using System.Diagnostics;
using System.Text.Json;
using StereoKit;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

public static partial class EditorRuntimeHost
{
    private static readonly object StateGate = new();
    private static SceneDocument _scene = new();
    private static Guid? _selectedEntityId;
    private static IReadOnlyList<Guid> _selectedEntityIds = [];
    private static long _pendingRevision;
    private static long _appliedRevision = -1;
    private static int _stopRequested;
    private static int _stepRequested;
    private static RuntimeSessionMode _mode = RuntimeSessionMode.Scene;
    private static RuntimePlayState _playState = RuntimePlayState.Editing;
    private static float _simulationTime;
    private static SceneViewportController? _sceneViewport;
    private static IEditorProjectAdapter? _adapter;
    private static IEditorRuntimeExtension? _legacyExtension;
    private static EditorAdapterBuilder? _adapterBuilder;
    private static EditorComponentCatalog? _componentCatalog;
    private static RuntimeComponentManager? _componentManager;
    private static RuntimeInteractionResolver? _interactionResolver;
    private static readonly TaskCompletionSource<HelloMessage> HelloReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static EditorRuntimeContext? _runtimeContext;
    private static JsonPipeConnection? _connection;
    private static CancellationToken _sessionCancellation;
    private static Material? _entityMaterial;
    private static Material? _floorMaterial;
    private static VisualResourceCache? _visualResources;
    private static SpatialUiRenderer? _spatialUi;
    private static Dictionary<Guid, RuntimeAssetDescriptor> _runtimeAssets = [];
    private static long _assetCatalogVersion;
    private static long _lastMigrationProposalRevision = -1;
    private static readonly IEditorAssetResolver AssetResolver = new HostAssetResolver();
    private static readonly Dictionary<Guid, CachedModel> Models = [];
    private static readonly Dictionary<string, Model> ModelVariants = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReportedAssetErrors = new(StringComparer.Ordinal);
    private static IReadOnlySet<string> _negotiatedCapabilities = new HashSet<string>(StringComparer.Ordinal);
    private static long _lastTelemetryTimestamp;
    private static double _smoothedFrameTimeMilliseconds;
    private static bool _initialPlayCameraApplied;

    public static bool IsEditorLaunch(string[] args) =>
        !string.IsNullOrWhiteSpace(GetArgument(args, "--pipe"));

    public static int Run(string[] args, IEditorProjectAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        _legacyExtension = null;
        return RunCore(args);
    }

    public static int Run(string[] args, IEditorRuntimeExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        _adapter = new EmptyEditorProjectAdapter(extension.DisplayName);
        _legacyExtension = extension;
        return RunCore(args);
    }

    private static int RunCore(string[] args)
    {
        var pipeName = GetArgument(args, "--pipe");
        var modeName = GetArgument(args, "--mode");
        if (string.IsNullOrWhiteSpace(pipeName)
            || !Enum.TryParse(modeName, ignoreCase: true, out _mode))
        {
            Console.Error.WriteLine("Usage: SKinny Preview Host --pipe <name> --mode <scene|play>");
            return 2;
        }

        _playState = _mode == RuntimeSessionMode.Scene
            ? RuntimePlayState.Editing
            : RuntimePlayState.Playing;
        return RunAsync(pipeName).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string pipeName)
    {
        using var shutdown = new CancellationTokenSource();
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(10_000, shutdown.Token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not connect to editor pipe: {exception.Message}");
            return 3;
        }

        await using var connection = new JsonPipeConnection(pipe);
        _connection = connection;
        _sessionCancellation = shutdown.Token;
        var readTask = connection.ReadLoopAsync(HandleMessageAsync, shutdown.Token);

        Log.Subscribe((level, text) =>
        {
            Console.WriteLine($"[StereoKit {level}] {text.TrimEnd()}");
            _ = TrySendAsync(MessageTypes.RuntimeLog, new RuntimeLogMessage(level.ToString(), text.TrimEnd()));
        });

        HelloMessage hello;
        try
        {
            hello = await HelloReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), shutdown.Token);
        }
        catch (TimeoutException)
        {
            await connection.SendAsync(
                MessageTypes.FatalError,
                new FatalErrorMessage("Editor handshake timed out.", "No protocol Hello message arrived."),
                shutdown.Token);
            shutdown.Cancel();
            return 4;
        }

        if (hello.ProtocolMajor != ProtocolVersion.Major)
        {
            await connection.SendAsync(
                MessageTypes.FatalError,
                new FatalErrorMessage(
                    $"Protocol {hello.ProtocolMajor}.{hello.ProtocolMinor} is incompatible with runtime protocol {ProtocolVersion.Major}.{ProtocolVersion.Minor}.",
                    "Update the editor or the project's StereoKit editor runtime package."),
                shutdown.Token);
            shutdown.Cancel();
            return 5;
        }

        _adapterBuilder = new EditorAdapterBuilder();
        try
        {
            RegisterBuiltInComponents(_adapterBuilder);
            _adapter!.Configure(_adapterBuilder);
            _adapterBuilder.ValidateRegistrations();
            _componentCatalog = EditorComponentCatalog.Create(
                _adapter.Id,
                _adapter.DisplayName,
                _adapter.Version,
                _adapterBuilder.Descriptors,
                _adapterBuilder.BindingDescriptors,
                _adapterBuilder.ActionDescriptors);
        }
        catch (Exception exception)
        {
            await connection.SendAsync(
                MessageTypes.FatalError,
                new FatalErrorMessage("The project adapter catalog is invalid.", exception.ToString()),
                shutdown.Token);
            shutdown.Cancel();
            return 6;
        }

        var title = _mode == RuntimeSessionMode.Scene
            ? $"{hello.ProjectName} — Scene"
            : $"{hello.ProjectName} — Game";
        var settings = new SKSettings
        {
            appName = title,
            assetsFolder = AppContext.BaseDirectory,
            mode = _mode == RuntimeSessionMode.Scene ? AppMode.Window : AppMode.Simulator,
            flatscreenPosX = -32_000,
            flatscreenPosY = -32_000,
            flatscreenWidth = 1100,
            flatscreenHeight = 720,
            logFilter = LogLevel.Info,
            standbyMode = StandbyMode.None,
        };

        if (!SK.Initialize(settings))
        {
            await connection.SendAsync(
                MessageTypes.FatalError,
                new FatalErrorMessage("StereoKit initialization failed.", "No simulator window was created."),
                shutdown.Token);
            shutdown.Cancel();
            return 7;
        }

        // Authorable scenes own their background through Environment Settings.
        // StereoKit otherwise draws its bright default skybox over ClearColor.
        Renderer.EnableSky = false;

        _entityMaterial = Material.Default.Copy();
        _floorMaterial = Material.Default.Copy();
        _floorMaterial[MatParamName.ColorTint] = new Color(0.15f, 0.17f, 0.20f, 1);
        _visualResources = new VisualResourceCache(ResolveRuntimeAsset);
        _sceneViewport = _mode == RuntimeSessionMode.Scene
            ? new SceneViewportController(
                camera => _ = TrySendAsync(
                    MessageTypes.SceneCameraChanged,
                    new SceneCameraChangedMessage(camera)),
                settings => _ = TrySendAsync(
                    MessageTypes.SceneToolSettingsChanged,
                    new SceneToolSettingsChangedMessage(settings)),
                (entityId, transform) => _ = TrySendAsync(
                    MessageTypes.TransformCommitted,
                    new TransformCommittedMessage(entityId, transform)),
                transforms => _ = TrySendAsync(
                    MessageTypes.TransformsCommitted,
                    new TransformsCommittedMessage(transforms)),
                entityIds => _ = TrySendAsync(
                    MessageTypes.DuplicateSelectionRequested,
                    new DuplicateSelectionRequestedMessage(entityIds)),
                GetEntityLocalBounds)
            : null;
        _runtimeContext = new EditorRuntimeContext(_mode, _playState, _scene);
        _legacyExtension?.Initialize(_runtimeContext);
        _adapter!.Initialize(CreateProjectContext(0));
        _interactionResolver = new RuntimeInteractionResolver(
            _adapterBuilder!,
            _mode,
            () => _sceneViewport?.ToolSettings.UiInteractionMode ?? SceneUiInteractionMode.Edit);
        _spatialUi = new SpatialUiRenderer(
            _visualResources,
            _interactionResolver,
            _mode,
            ReportVisualAssetErrorOnce,
            (entityId, componentId, data, description) => _ = TrySendAsync(
                MessageTypes.ComponentDataCommitted,
                new ComponentDataCommittedMessage(entityId, componentId, data, description)));
        _componentManager = new RuntimeComponentManager(
            _adapterBuilder!,
            _mode,
            AssetResolver,
            _interactionResolver,
            diagnostic => _ = TrySendAsync(MessageTypes.Diagnostic, diagnostic));
        Console.WriteLine($"Editor adapter project initialized: {_adapter.DisplayName}");

        // StereoKit requires initialization, pre-run GPU asset creation, and SK.Run
        // to remain on the same OS thread. Do not await between Initialize and Run.
        var negotiatedCapabilities = NegotiateCapabilities(hello.Capabilities);
        _negotiatedCapabilities = negotiatedCapabilities.ToHashSet(StringComparer.Ordinal);
        connection.SendAsync(
                MessageTypes.Ready,
                new ReadyMessage(
                    ProtocolVersion.Major,
                    ProtocolVersion.Minor,
                    typeof(EditorRuntimeHost).Assembly.GetName().Version?.ToString() ?? "prototype",
                    SK.VersionName,
                    AdapterContractVersion.Current,
                    hello.ProjectId,
                    hello.ProjectName,
                    hello.ProfileId,
                    hello.BuildId,
                    _adapter.Id,
                    _adapter.Version,
                    typeof(EditorRuntimeHost).Assembly.GetName().Name ?? "StereoKitEditor.Runtime",
                    hello.SessionNonce,
                    _mode,
                    _playState,
                    negotiatedCapabilities),
                shutdown.Token)
            .GetAwaiter()
            .GetResult();
        connection.SendAsync(
                MessageTypes.ComponentCatalog,
                new ComponentCatalogMessage(_componentCatalog!),
                shutdown.Token)
            .GetAwaiter()
            .GetResult();

        try
        {
            SK.Run(Step, Shutdown);
        }
        catch (Exception exception)
        {
            await TrySendAsync(
                MessageTypes.FatalError,
                new FatalErrorMessage($"The StereoKit {_mode} host crashed.", exception.ToString()));
            return 8;
        }
        finally
        {
            shutdown.Cancel();
            try
            {
                await readTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during normal shutdown.
            }
            catch (IOException)
            {
                // The editor may close the pipe as part of process teardown.
            }
        }

        return 0;
    }

    private static void Step()
    {
        if (Interlocked.CompareExchange(ref _stopRequested, 0, 0) != 0)
        {
            SK.Quit();
            return;
        }

        ApplyInitialPlayCamera();
        AdvancePlayClock();

        SceneDocument scene;
        Guid? selection;
        IReadOnlyList<Guid> selections;
        long revision;
        lock (StateGate)
        {
            scene = _scene;
            selection = _selectedEntityId;
            selections = _selectedEntityIds;
            revision = _pendingRevision;
        }

        var environment = ResolveEnvironment(scene);
        Renderer.ClearColor = ToColor(environment.ClearColor);
        if (environment.FloorVisible)
        {
            _floorMaterial![MatParamName.ColorTint] = ToColor(environment.FloorColor);
            Mesh.Cube.Draw(
                _floorMaterial,
                Matrix.TRS(new Vec3(0, -0.13f, -0.65f), Quat.Identity, new Vec3(2.2f, 0.02f, 2.2f)));
        }

        _runtimeContext!.Mode = _mode;
        _runtimeContext.PlayState = _playState;
        _runtimeContext.Scene = scene;
        _runtimeContext.SimulationTime = _simulationTime;
        _legacyExtension?.Step(_runtimeContext);
        _adapter!.Step(CreateProjectContext(Time.Stepf));
        _componentManager!.SynchronizeAndStep(
            scene,
            revision,
            _playState,
            Time.Stepf,
            _simulationTime,
            _sessionCancellation);

        var pointerConsumed = _sceneViewport?.Step(scene, selection, selections, revision) ?? false;
        var wantsPick = _mode == RuntimeSessionMode.Scene
            && !pointerConsumed
            && Input.Key(Key.MouseLeft).IsJustActive();
        var pickRay = wantsPick ? Input.Mouse.Ray : default;
        Guid? pickedEntityId = null;
        var nearestHitDistance = float.MaxValue;

        foreach (var root in scene.Roots)
        {
            DrawEntity(root, selection, selections, wantsPick, pickRay, ref pickedEntityId, ref nearestHitDistance);
        }

        if (wantsPick)
        {
            lock (StateGate)
            {
                _selectedEntityId = pickedEntityId;
                _selectedEntityIds = pickedEntityId is { } id ? [id] : [];
            }

            selection = pickedEntityId;
            _ = TrySendAsync(MessageTypes.EntityPicked, new EntityPickedMessage(pickedEntityId));
            _ = TrySendAsync(
                MessageTypes.RuntimeLog,
                new RuntimeLogMessage(
                    "Diagnostic",
                    $"Pick at {Input.Mouse.pos.x:0},{Input.Mouse.pos.y:0}: {pickedEntityId?.ToString() ?? "none"}"));
        }

        if (revision != _appliedRevision)
        {
            _appliedRevision = revision;
            _ = TrySendAsync(MessageTypes.AppliedRevision, new AppliedRevisionMessage(revision));
        }

        PublishTelemetry(scene, selection, revision);
    }

    private static void ApplyInitialPlayCamera()
    {
        if (_mode != RuntimeSessionMode.Play || _initialPlayCameraApplied)
        {
            return;
        }

        // The simulator remembers its last virtual head pose between processes.
        // Play sessions should still open on an authored scene predictably, so
        // map that pose to a stable, forward-facing editor starting point once.
        // CameraRoot remains free after this frame for normal simulator movement.
        var targetHead = Matrix.TR(new Vec3(0, 0.10f, -0.10f), Quat.Identity);
        Renderer.CameraRoot = targetHead * Input.Head.ToMatrix().Inverse;
        _initialPlayCameraApplied = true;
    }

    private static void PublishTelemetry(SceneDocument scene, Guid? selectedEntityId, long revision)
    {
        if (!_negotiatedCapabilities.Contains(ProtocolCapabilities.RuntimeTelemetry))
        {
            return;
        }

        var currentFrameTime = Math.Max(0, Time.Stepf * 1_000.0);
        _smoothedFrameTimeMilliseconds = _smoothedFrameTimeMilliseconds <= 0
            ? currentFrameTime
            : (_smoothedFrameTimeMilliseconds * 0.9) + (currentFrameTime * 0.1);
        var now = Stopwatch.GetTimestamp();
        if (now - _lastTelemetryTimestamp < Stopwatch.Frequency / 2)
        {
            return;
        }

        _lastTelemetryTimestamp = now;
        var entities = scene.Traverse().ToArray();
        var inspected = selectedEntityId is { } id ? scene.FindEntity(id) : null;
        using var process = Process.GetCurrentProcess();
        _ = TrySendAsync(
            MessageTypes.RuntimeTelemetry,
            new RuntimeTelemetryMessage(
                _mode,
                _playState,
                revision,
                Time.Frame,
                _smoothedFrameTimeMilliseconds,
                _smoothedFrameTimeMilliseconds > 0 ? 1_000.0 / _smoothedFrameTimeMilliseconds : 0,
                _simulationTime,
                entities.Length,
                entities.Count(entity => entity.Enabled),
                entities.Sum(entity => entity.Components.Records.Count),
                _componentManager?.LiveComponentCount ?? 0,
                GC.GetTotalMemory(forceFullCollection: false),
                process.WorkingSet64,
                inspected is null
                    ? null
                    : new RuntimeInspectedEntityMessage(
                        inspected.Id,
                        inspected.Name,
                        inspected.Enabled,
                        _componentManager?.GetComponentStatuses(inspected) ?? [])));
    }

    private static void AdvancePlayClock()
    {
        if (_mode != RuntimeSessionMode.Play)
        {
            return;
        }

        if (_playState == RuntimePlayState.Playing)
        {
            _simulationTime += Time.Stepf;
        }
        else if (Interlocked.Exchange(ref _stepRequested, 0) != 0)
        {
            _simulationTime += 1.0f / 60.0f;
            _ = TrySendAsync(
                MessageTypes.PlayStateChanged,
                new PlayStateChangedMessage(_playState, Time.Frame));
        }
    }

    private static void DrawEntity(
        SceneEntity entity,
        Guid? selection,
        IReadOnlyList<Guid> selections,
        bool wantsPick,
        Ray pickRay,
        ref Guid? pickedEntityId,
        ref float nearestHitDistance)
    {
        if (!entity.Enabled)
        {
            return;
        }

        var transform = entity.Components.Transform;
        var position = ToVec3(transform.Position);
        if (_mode == RuntimeSessionMode.Play)
        {
            var phase = Math.Abs(entity.Id.GetHashCode() % 1000) / 1000.0f * MathF.PI * 2;
            position.y += MathF.Sin((_simulationTime * 2.0f) + phase) * 0.015f;
        }

        Hierarchy.Push(Matrix.TRS(position, ToQuat(transform.Rotation), ToVec3(transform.Scale)));

        var renderer = entity.Components.PrimitiveMeshRenderer;
        if (renderer is { Visible: true })
        {
            var color = selections.Contains(entity.Id)
                ? new Color(1.0f, 0.64f, 0.12f, 1)
                : new Color(
                    (float)renderer.Color.R,
                    (float)renderer.Color.G,
                    (float)renderer.Color.B,
                    (float)renderer.Color.A);

            var mesh = renderer.Primitive switch
            {
                PrimitiveKind.Sphere => Mesh.Sphere,
                PrimitiveKind.Quad => Mesh.Quad,
                _ => Mesh.Cube,
            };
            var material = _visualResources!.GetMaterial(
                renderer.MaterialAssetId,
                renderer.BaseColorTextureOverrideId,
                renderer.UvScale.X,
                renderer.UvScale.Y,
                renderer.UvOffset.X,
                renderer.UvOffset.Y,
                imageDefaults: false,
                doubleSided: renderer.Primitive == PrimitiveKind.Quad,
                RenderSurfacePreset.WorldOpaque,
                out var materialError);
            if (materialError is not null)
            {
                ReportVisualAssetErrorOnce(entity, materialError);
            }

            var meshTransform = renderer.Primitive == PrimitiveKind.Quad
                ? Matrix.TRS(Vec3.Zero, Quat.FromAngles(0, 180, 0), Vec3.One * 0.20f)
                : Matrix.S(0.20f);
            mesh.Draw(material, meshTransform, color, RenderLayer.Layer0);

            if (wantsPick)
            {
                var worldBounds = renderer.Primitive == PrimitiveKind.Quad
                    ? CurrentWorldBounds(Vec3.Zero, new Vec3(0.20f, 0.20f, 0.006f))
                    : CurrentWorldBounds(0.20f);
                if (worldBounds.Intersect(pickRay, out var worldHit))
                {
                    var distance = Vec3.DistanceSq(pickRay.position, worldHit);
                    if (distance < nearestHitDistance)
                    {
                        nearestHitDistance = distance;
                        pickedEntityId = entity.Id;
                    }
                }
            }
        }

        DrawModelRenderer(entity, selections.Contains(entity.Id), wantsPick, pickRay, ref pickedEntityId, ref nearestHitDistance);
        DrawImageRenderer(entity, selections.Contains(entity.Id), wantsPick, pickRay, ref pickedEntityId, ref nearestHitDistance);
        DrawTextRenderer(entity, selections.Contains(entity.Id), wantsPick, pickRay, ref pickedEntityId, ref nearestHitDistance);
        var isSpatialUiPanel = entity.Components.UiPanel is { Visible: true };
        if (isSpatialUiPanel)
        {
            _spatialUi?.Draw(
                entity,
                selections,
                _sceneViewport?.ToolSettings.UiInteractionMode ?? SceneUiInteractionMode.Edit,
                wantsPick,
                pickRay,
                ref pickedEntityId,
                ref nearestHitDistance);
        }
        DrawEditorAnnotation(entity);

        if (wantsPick)
        {
            foreach (var componentBounds in _componentManager!.GetLocalPickBounds(entity.Id))
            {
                var worldBounds = CurrentWorldBounds(
                    new Vec3(
                        (float)componentBounds.CenterX,
                        (float)componentBounds.CenterY,
                        (float)componentBounds.CenterZ),
                    new Vec3(
                        (float)componentBounds.SizeX,
                        (float)componentBounds.SizeY,
                        (float)componentBounds.SizeZ));
                if (worldBounds.Intersect(pickRay, out var worldHit))
                {
                    var distance = Vec3.DistanceSq(pickRay.position, worldHit);
                    if (distance < nearestHitDistance)
                    {
                        nearestHitDistance = distance;
                        pickedEntityId = entity.Id;
                    }
                }
            }
        }

        if (!isSpatialUiPanel)
        {
            foreach (var child in entity.Children)
            {
                DrawEntity(child, selection, selections, wantsPick, pickRay, ref pickedEntityId, ref nearestHitDistance);
            }
        }

        Hierarchy.Pop();
    }

    private static Bounds CurrentWorldBounds(float size) =>
        CurrentWorldBounds(Vec3.Zero, new Vec3(size, size, size));

    private static Bounds CurrentWorldBounds(Vec3 center, Vec3 size)
    {
        var half = size * 0.5f;
        var minimum = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var maximum = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var x in new[] { center.x - half.x, center.x + half.x })
        {
            foreach (var y in new[] { center.y - half.y, center.y + half.y })
            {
                foreach (var z in new[] { center.z - half.z, center.z + half.z })
                {
                    var point = Hierarchy.ToWorld(new Vec3(x, y, z));
                    minimum = new Vec3(
                        MathF.Min(minimum.x, point.x),
                        MathF.Min(minimum.y, point.y),
                        MathF.Min(minimum.z, point.z));
                    maximum = new Vec3(
                        MathF.Max(maximum.x, point.x),
                        MathF.Max(maximum.y, point.y),
                        MathF.Max(maximum.z, point.z));
                }
            }
        }

        return Bounds.FromCorners(minimum, maximum);
    }

    private static Bounds CurrentWorldBounds(Matrix localTransform, Vec3 size)
    {
        var half = size * 0.5f;
        var minimum = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var maximum = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var x in new[] { -half.x, half.x })
        {
            foreach (var y in new[] { -half.y, half.y })
            {
                foreach (var z in new[] { -half.z, half.z })
                {
                    var point = Hierarchy.ToWorld(localTransform.Transform(new Vec3(x, y, z)));
                    minimum = new Vec3(
                        MathF.Min(minimum.x, point.x),
                        MathF.Min(minimum.y, point.y),
                        MathF.Min(minimum.z, point.z));
                    maximum = new Vec3(
                        MathF.Max(maximum.x, point.x),
                        MathF.Max(maximum.y, point.y),
                        MathF.Max(maximum.z, point.z));
                }
            }
        }

        return Bounds.FromCorners(minimum, maximum);
    }

    private static Task HandleMessageAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MessageTypes.Hello:
                {
                    var hello = JsonPipeConnection.GetPayload<HelloMessage>(envelope);
                    HelloReceived.TrySetResult(hello);
                    break;
                }
            case MessageTypes.Heartbeat:
                {
                    var heartbeat = JsonPipeConnection.GetPayload<HeartbeatMessage>(envelope);
                    _ = TrySendAsync(
                        MessageTypes.HeartbeatAck,
                        new HeartbeatAckMessage(heartbeat.Sequence, DateTimeOffset.UtcNow));
                    break;
                }
            case MessageTypes.LoadSceneSnapshot:
                {
                    var snapshot = JsonPipeConnection.GetPayload<LoadSceneSnapshotMessage>(envelope);
                    lock (StateGate)
                    {
                        if (snapshot.Revision >= _pendingRevision)
                        {
                            _scene = snapshot.Scene;
                            _pendingRevision = snapshot.Revision;
                            SendMigrationProposalLocked();
                        }
                    }

                    break;
                }
            case MessageTypes.SetSelection:
                {
                    if (_mode == RuntimeSessionMode.Scene)
                    {
                        var selection = JsonPipeConnection.GetPayload<SetSelectionMessage>(envelope);
                        lock (StateGate)
                        {
                            _selectedEntityId = selection.EntityId;
                            _selectedEntityIds = (selection.EntityIds ?? (selection.EntityId is { } id ? [id] : []))
                                .Where(id => id != Guid.Empty)
                                .Distinct()
                                .ToArray();
                        }
                    }

                    break;
                }
            case MessageTypes.LoadSceneChangeSet:
                {
                    var changeSet = JsonPipeConnection.GetPayload<LoadSceneChangeSetMessage>(envelope);
                    lock (StateGate)
                    {
                        if (SceneChangeSetApplier.TryApply(
                                _scene,
                                _pendingRevision,
                                changeSet,
                                out var updated,
                                out var error))
                        {
                            _scene = updated!;
                            _pendingRevision = changeSet.Revision;
                            SendMigrationProposalLocked();
                        }
                        else
                        {
                            _ = TrySendAsync(
                                MessageTypes.SceneResyncRequired,
                                new SceneResyncRequiredMessage(
                                    _pendingRevision,
                                    changeSet.BaseRevision,
                                    error ?? "The scene change set could not be applied."));
                        }
                    }

                    break;
                }
            case MessageTypes.LoadAssetCatalog:
                {
                    var catalog = JsonPipeConnection.GetPayload<LoadAssetCatalogMessage>(envelope);
                    Dictionary<Guid, RuntimeAssetDescriptor> previous;
                    Dictionary<Guid, RuntimeAssetDescriptor> updated;
                    lock (StateGate)
                    {
                        previous = _runtimeAssets;
                        updated = catalog.Assets
                            .Where(asset => asset.AssetId != Guid.Empty)
                            .GroupBy(asset => asset.AssetId)
                            .ToDictionary(group => group.Key, group => group.Last());
                        _runtimeAssets = updated;
                        _assetCatalogVersion++;
                    }

                    var changed = FindChangedAssets(previous, updated);
                    var invalidationCatalog = previous.Values.Concat(updated.Values)
                        .GroupBy(asset => asset.AssetId)
                        .ToDictionary(group => group.Key, group => group.Last());
                    _visualResources?.Invalidate(changed, invalidationCatalog);
                    if (changed.Any(id => (invalidationCatalog.TryGetValue(id, out var asset) ? asset.Kind : string.Empty)
                            is "Model" or "Material"))
                    {
                        ModelVariants.Clear();
                    }

                    break;
                }
            case MessageTypes.SetSceneCamera:
                {
                    if (_mode == RuntimeSessionMode.Scene)
                    {
                        var message = JsonPipeConnection.GetPayload<SetSceneCameraMessage>(envelope);
                        _sceneViewport?.SetCamera(message.Camera);
                    }

                    break;
                }
            case MessageTypes.FrameSelection:
                {
                    if (_mode == RuntimeSessionMode.Scene)
                    {
                        var message = JsonPipeConnection.GetPayload<FrameSelectionMessage>(envelope);
                        _sceneViewport?.Frame(message.EntityId);
                    }

                    break;
                }
            case MessageTypes.SetSceneToolSettings:
                {
                    if (_mode == RuntimeSessionMode.Scene)
                    {
                        var message = JsonPipeConnection.GetPayload<SetSceneToolSettingsMessage>(envelope);
                        _sceneViewport?.SetToolSettings(message.Settings);
                    }

                    break;
                }
            case MessageTypes.SetPlayState:
                {
                    if (_mode == RuntimeSessionMode.Play)
                    {
                        var state = JsonPipeConnection.GetPayload<SetPlayStateMessage>(envelope);
                        _playState = state.State == RuntimePlayState.Editing
                            ? RuntimePlayState.Paused
                            : state.State;
                        _ = TrySendAsync(
                            MessageTypes.PlayStateChanged,
                            new PlayStateChangedMessage(_playState, Time.Frame));
                    }

                    break;
                }
            case MessageTypes.StepPlay:
                if (_mode == RuntimeSessionMode.Play)
                {
                    _playState = RuntimePlayState.Paused;
                    Interlocked.Exchange(ref _stepRequested, 1);
                }

                break;
            case MessageTypes.Stop:
                Interlocked.Exchange(ref _stopRequested, 1);
                break;
        }

        return Task.CompletedTask;
    }

    private static async Task TrySendAsync<T>(string type, T payload)
    {
        try
        {
            if (_connection is not null)
            {
                await _connection.SendAsync(type, payload);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"Editor connection closed: {exception.Message}");
        }
    }

    private static void Shutdown()
    {
        _sceneViewport?.ResetPointerState();
        _sceneViewport = null;
        if (_runtimeContext is not null)
        {
            _componentManager?.DestroyAll();
            _legacyExtension?.Shutdown(_runtimeContext);
            _adapter?.Shutdown(CreateProjectContext(0));
        }

        Models.Clear();
        ModelVariants.Clear();
        _spatialUi = null;
        _visualResources?.InvalidateAll();
        _visualResources = null;
        ReportedAssetErrors.Clear();
        lock (StateGate)
        {
            _runtimeAssets = [];
        }

        Console.WriteLine($"StereoKit {_mode} host shut down cleanly.");
    }

    private static Vec3 ToVec3(Vector3Value value) => new((float)value.X, (float)value.Y, (float)value.Z);
    private static Quat ToQuat(QuaternionValue value) => new((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);

    private static EditorProjectRuntimeContext CreateProjectContext(float deltaTime) => new(
        _mode == RuntimeSessionMode.Scene ? EditorRuntimeMode.Scene : EditorRuntimeMode.Play,
        _playState switch
        {
            RuntimePlayState.Playing => EditorRuntimePlayState.Playing,
            RuntimePlayState.Paused => EditorRuntimePlayState.Paused,
            _ => EditorRuntimePlayState.Editing,
        },
        deltaTime,
        _simulationTime,
        _sessionCancellation);

    private static IReadOnlyList<string> NegotiateCapabilities(IReadOnlyList<string> editorCapabilities) =>
        ProtocolCapabilities.EditorDefaults
            .Intersect(editorCapabilities, StringComparer.Ordinal)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();

    private static void SendMigrationProposalLocked()
    {
        if (_adapterBuilder is null || _lastMigrationProposalRevision == _pendingRevision)
        {
            return;
        }

        _lastMigrationProposalRevision = _pendingRevision;
        var upgrades = new List<ComponentMigrationPatch>();
        foreach (var entity in _scene.Traverse())
        {
            foreach (var component in entity.Components.Records)
            {
                if (!_adapterBuilder.TryGetRegistration(component.TypeId, out var registration)
                    || component.SchemaVersion >= registration.Descriptor.SchemaVersion
                    || !_adapterBuilder.TryMigrate(
                        component.TypeId,
                        component.SchemaVersion,
                        component.Data,
                        out var targetVersion,
                        out var migratedData,
                        out _))
                {
                    continue;
                }

                upgrades.Add(new ComponentMigrationPatch(
                    entity.Id,
                    component.Id,
                    component.TypeId,
                    registration.Descriptor.DisplayName,
                    component.SchemaVersion,
                    targetVersion,
                    migratedData));
            }
        }

        if (upgrades.Count > 0)
        {
            _ = TrySendAsync(
                MessageTypes.ComponentMigrationProposal,
                new ComponentMigrationProposalMessage(_pendingRevision, upgrades));
        }
    }

    private static void RegisterBuiltInComponents(EditorAdapterBuilder builder)
    {
        RegisterPhase5BuiltInComponents(builder);

        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = BuiltInComponentTypes.ModelRenderer,
                SchemaVersion = 1,
                DisplayName = "Model Renderer",
                Category = "Rendering",
                Description = "Renders a GLB model referenced by its stable project asset ID.",
                ConflictingComponentTypeIds =
                [
                    BuiltInComponentTypes.PrimitiveMeshRenderer,
                    BuiltInComponentTypes.ImageRenderer,
                    BuiltInComponentTypes.TextRenderer,
                    BuiltInComponentTypes.UiPanel,
                ],
                DefaultData = JsonSerializer.SerializeToElement(new
                {
                    assetId = string.Empty,
                    materialAssetId = (string?)null,
                    visible = true,
                    fitToBounds = true,
                    maximumSize = 0.5,
                }),
                Properties =
                [
                    new()
                    {
                        Name = "assetId",
                        DisplayName = "Model Asset",
                        Kind = EditorPropertyKind.AssetReference,
                        AcceptedAssetKinds = ["Model"],
                        IsRequired = true,
                        Description = "Stable GUID of a GLB in the Project panel.",
                    },
                    new()
                    {
                        Name = "materialAssetId",
                        DisplayName = "Material Override",
                        Kind = EditorPropertyKind.AssetReference,
                        AcceptedAssetKinds = ["Material"],
                        Description = "Optional material applied to all model visual slots.",
                    },
                    new()
                    {
                        Name = "visible",
                        DisplayName = "Visible",
                        Kind = EditorPropertyKind.Boolean,
                    },
                    new()
                    {
                        Name = "fitToBounds",
                        DisplayName = "Fit and Center",
                        Kind = EditorPropertyKind.Boolean,
                    },
                    new()
                    {
                        Name = "maximumSize",
                        DisplayName = "Maximum Size",
                        Kind = EditorPropertyKind.Number,
                        Minimum = 0.01,
                        Maximum = 100,
                        Increment = 0.01,
                        Units = "m",
                    },
                ],
            },
            static () => new NoOpBuiltInRuntime());

        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = BuiltInComponentTypes.EnvironmentSettings,
                SchemaVersion = 1,
                DisplayName = "Environment Settings",
                Category = "Rendering",
                Description = "Controls the preview background and floor. The first enabled instance in hierarchy order is used.",
                DefaultData = JsonSerializer.SerializeToElement(new EnvironmentSettingsComponent()),
                Properties =
                [
                    new()
                    {
                        Name = "clearColor",
                        DisplayName = "Background",
                        Kind = EditorPropertyKind.Color,
                    },
                    new()
                    {
                        Name = "floorVisible",
                        DisplayName = "Show Floor",
                        Kind = EditorPropertyKind.Boolean,
                    },
                    new()
                    {
                        Name = "floorColor",
                        DisplayName = "Floor Color",
                        Kind = EditorPropertyKind.Color,
                    },
                ],
            },
            static () => new NoOpBuiltInRuntime());

        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = BuiltInComponentTypes.EditorAnnotation,
                SchemaVersion = 1,
                DisplayName = "Editor Annotation",
                Category = "Editor",
                Description = "Adds a visible authoring marker and a source-controlled note to an object.",
                Modes = EditorComponentModes.Scene,
                DefaultData = JsonSerializer.SerializeToElement(new EditorAnnotationComponent()),
                Properties =
                [
                    new()
                    {
                        Name = "label",
                        DisplayName = "Note",
                        Kind = EditorPropertyKind.String,
                        Presentation = EditorPropertyPresentation.MultilineText,
                    },
                    new()
                    {
                        Name = "color",
                        DisplayName = "Marker Color",
                        Kind = EditorPropertyKind.Color,
                    },
                    new()
                    {
                        Name = "visible",
                        DisplayName = "Visible",
                        Kind = EditorPropertyKind.Boolean,
                    },
                ],
            },
            static () => new NoOpBuiltInRuntime());
    }

    private static EnvironmentSettingsComponent ResolveEnvironment(SceneDocument scene)
    {
        foreach (var entity in scene.Traverse().Where(entity => entity.Enabled))
        {
            var component = entity.Components.FindByType(BuiltInComponentTypes.EnvironmentSettings);
            if (component is not { Enabled: true })
            {
                continue;
            }

            try
            {
                return component.Data.Deserialize<EnvironmentSettingsComponent>(SceneSerializer.Options)
                    ?? new EnvironmentSettingsComponent();
            }
            catch (JsonException)
            {
                return new EnvironmentSettingsComponent();
            }
        }

        return new EnvironmentSettingsComponent();
    }

    private static void DrawEditorAnnotation(SceneEntity entity)
    {
        if (_mode != RuntimeSessionMode.Scene)
        {
            return;
        }

        var component = entity.Components.FindByType(BuiltInComponentTypes.EditorAnnotation);
        if (component is not { Enabled: true })
        {
            return;
        }

        EditorAnnotationComponent? annotation;
        try
        {
            annotation = component.Data.Deserialize<EditorAnnotationComponent>(SceneSerializer.Options);
        }
        catch (JsonException)
        {
            return;
        }

        if (annotation is not { Visible: true })
        {
            return;
        }

        var color = ToColor32(annotation.Color);
        const float size = 0.055f;
        const float height = 0.16f;
        Lines.Add(new Vec3(0, height - size, 0), new Vec3(0, height + size, 0), color, 0.008f);
        Lines.Add(new Vec3(-size, height, 0), new Vec3(size, height, 0), color, 0.008f);
        Lines.Add(new Vec3(0, height, -size), new Vec3(0, height, size), color, 0.008f);
    }

    private static Color ToColor(ColorValue color) => new(
        (float)Math.Clamp(color.R, 0, 1),
        (float)Math.Clamp(color.G, 0, 1),
        (float)Math.Clamp(color.B, 0, 1),
        (float)Math.Clamp(color.A, 0, 1));

    private static Color32 ToColor32(ColorValue color) => new(
        (byte)Math.Round(Math.Clamp(color.R, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.G, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.B, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.A, 0, 1) * 255));

    private static void DrawImageRenderer(
        SceneEntity entity,
        bool selected,
        bool wantsPick,
        Ray pickRay,
        ref Guid? pickedEntityId,
        ref float nearestHitDistance)
    {
        var renderer = entity.Components.ImageRenderer;
        if (renderer is not { Visible: true } || renderer.TextureAssetId == Guid.Empty)
        {
            return;
        }

        if (!_visualResources!.TryGetTexture(renderer.TextureAssetId, out var texture, out var textureError))
        {
            ReportVisualAssetErrorOnce(entity, textureError ?? "The image texture could not be loaded.");
            return;
        }

        var aspect = texture.Height > 0 ? texture.Width / (double)texture.Height : 1;
        var size = ResolveImageSize(renderer, texture.Width, texture.Height, aspect);
        var uvScale = Vector2Value.One;
        var uvOffset = Vector2Value.Zero;
        if (renderer.SizingMode == ImageSizingMode.Fill && size.Y > 0)
        {
            var containerAspect = size.X / size.Y;
            if (containerAspect > aspect)
            {
                uvScale = new(1, Math.Clamp(aspect / containerAspect, 0.0001, 1));
                uvOffset = new(0, (1 - uvScale.Y) * 0.5);
            }
            else if (containerAspect < aspect)
            {
                uvScale = new(Math.Clamp(containerAspect / aspect, 0.0001, 1), 1);
                uvOffset = new((1 - uvScale.X) * 0.5, 0);
            }
        }

        var material = _visualResources.GetMaterial(
            null,
            renderer.TextureAssetId,
            uvScale.X,
            uvScale.Y,
            uvOffset.X,
            uvOffset.Y,
            imageDefaults: true,
            renderer.DoubleSided,
            renderer.SurfacePreset,
            out var materialError);
        if (materialError is not null)
        {
            ReportVisualAssetErrorOnce(entity, materialError);
        }

        var localMatrix = ImageLocalMatrix(renderer, size);
        var color = selected ? new Color(1, 0.68f, 0.22f, 1) : ToColor(renderer.Tint);
        Mesh.Quad.Draw(material, localMatrix, color, RenderLayer.Layer0);
        if (wantsPick)
        {
            TryPickLocalBox(
                entity.Id,
                localMatrix,
                new Vec3((float)size.X, (float)size.Y, 0.006f),
                pickRay,
                ref pickedEntityId,
                ref nearestHitDistance);
        }
    }

    private static void DrawTextRenderer(
        SceneEntity entity,
        bool selected,
        bool wantsPick,
        Ray pickRay,
        ref Guid? pickedEntityId,
        ref float nearestHitDistance)
    {
        var renderer = entity.Components.TextRenderer;
        if (renderer is not { Visible: true })
        {
            return;
        }

        var style = _visualResources!.GetTextStyle(
            renderer.TextStyleAssetId,
            renderer.FontAssetId,
            renderer.CharacterHeight,
            renderer.Color,
            out var styleError);
        if (styleError is not null)
        {
            ReportVisualAssetErrorOnce(entity, styleError);
        }

        if (renderer.SurfacePreset == RenderSurfacePreset.Overlay)
        {
            style.Material.DepthTest = DepthTest.Always;
            style.Material.DepthWrite = false;
            style.Material.QueueOffset = Math.Max(style.Material.QueueOffset, 50);
        }

        var resolvedBounds = ResolveTextBounds(renderer, style);
        var bounds = new Vec2((float)resolvedBounds.X, (float)resolvedBounds.Y);
        var localMatrix = Matrix.TR(Vec3.Zero, BillboardLocalRotation(renderer.Billboard));
        var tint = selected ? new Color(1, 0.68f, 0.22f, 1) : Color.White;
        Text.Add(
            renderer.Text ?? string.Empty,
            localMatrix,
            bounds,
            ToTextFit(renderer.Fit),
            style,
            tint,
            ToPivot(renderer.Pivot),
            ToAlign(renderer.HorizontalAlignment, renderer.VerticalAlignment),
            0,
            0,
            0);

        if (wantsPick)
        {
            var center = new Vec3(
                (float)((0.5 - renderer.Pivot.X) * resolvedBounds.X),
                (float)((renderer.Pivot.Y - 0.5) * resolvedBounds.Y),
                0);
            var boundsMatrix = Matrix.TRS(center, BillboardLocalRotation(renderer.Billboard), Vec3.One);
            TryPickLocalBox(
                entity.Id,
                boundsMatrix,
                new Vec3(bounds.x, bounds.y, 0.006f),
                pickRay,
                ref pickedEntityId,
                ref nearestHitDistance);
        }
    }

    private static Vector2Value ResolveTextBounds(TextRendererComponent renderer, TextStyle style)
    {
        var authored = new Vector2Value(
            Math.Max(0.001, renderer.Bounds.X),
            Math.Max(0.001, renderer.Bounds.Y));
        if (renderer.Fit != TextFitMode.Overflow || string.IsNullOrEmpty(renderer.Text))
        {
            return authored;
        }

        var measured = Text.SizeLayout(renderer.Text, style);
        return new(
            Math.Max(0.001, measured.x),
            Math.Max(0.001, measured.y));
    }

    private static Vector2Value ResolveImageSize(
        ImageRendererComponent renderer,
        int textureWidth,
        int textureHeight,
        double aspect)
    {
        var requested = new Vector2Value(
            Math.Max(0.001, renderer.Size.X),
            Math.Max(0.001, renderer.Size.Y));
        return renderer.SizingMode switch
        {
            ImageSizingMode.NativePixels => new(
                Math.Max(0.001, textureWidth / Math.Max(1, renderer.PixelsPerMeter)),
                Math.Max(0.001, textureHeight / Math.Max(1, renderer.PixelsPerMeter))),
            ImageSizingMode.PreserveAspect => new(requested.X, Math.Max(0.001, requested.X / Math.Max(0.0001, aspect))),
            ImageSizingMode.Fit => FitAspect(requested, aspect),
            _ => requested,
        };
    }

    private static Vector2Value FitAspect(Vector2Value bounds, double aspect)
    {
        var containerAspect = bounds.X / bounds.Y;
        return containerAspect > aspect
            ? new(bounds.Y * aspect, bounds.Y)
            : new(bounds.X, bounds.X / Math.Max(0.0001, aspect));
    }

    private static Matrix ImageLocalMatrix(ImageRendererComponent renderer, Vector2Value size)
    {
        var center = new Vec3(
            (float)((0.5 - renderer.Pivot.X) * size.X),
            (float)((renderer.Pivot.Y - 0.5) * size.Y),
            0);
        return Matrix.TRS(
            center,
            BillboardLocalRotation(renderer.Billboard),
            new Vec3((float)size.X, (float)size.Y, 1));
    }

    private static Quat BillboardLocalRotation(BillboardMode mode)
    {
        if (mode == BillboardMode.None)
        {
            // StereoKit's planar drawing APIs face local -Z. Editor-authored
            // planar content should face the default +Z viewing direction.
            return Quat.FromAngles(0, 180, 0);
        }

        var worldPosition = Hierarchy.ToWorld(Vec3.Zero);
        var direction = Renderer.CameraRoot.Translation - worldPosition;
        if (mode == BillboardMode.YAxisOnly)
        {
            direction.y = 0;
        }

        return direction.MagnitudeSq < 0.000001f
            ? Quat.Identity
            : Hierarchy.ToLocal(Quat.LookDir(direction.Normalized));
    }

    private static void TryPickLocalBox(
        Guid entityId,
        Matrix localTransform,
        Vec3 size,
        Ray pickRay,
        ref Guid? pickedEntityId,
        ref float nearestHitDistance)
    {
        var worldBounds = CurrentWorldBounds(localTransform, size);
        if (!worldBounds.Intersect(pickRay, out var worldHit))
        {
            return;
        }

        var distance = Vec3.DistanceSq(pickRay.position, worldHit);
        if (distance < nearestHitDistance)
        {
            nearestHitDistance = distance;
            pickedEntityId = entityId;
        }
    }

    private static TextFit ToTextFit(TextFitMode fit) => fit switch
    {
        TextFitMode.Wrap => TextFit.Wrap,
        TextFitMode.Clip => TextFit.Clip,
        TextFitMode.Squeeze => TextFit.Squeeze,
        TextFitMode.Exact => TextFit.Exact,
        TextFitMode.Overflow => TextFit.Overflow,
        _ => TextFit.None,
    };

    private static Pivot ToPivot(Vector2Value pivot) => (pivot.X, pivot.Y) switch
    {
        (<= 0.25, <= 0.25) => Pivot.TopLeft,
        (>= 0.75, <= 0.25) => Pivot.TopRight,
        (<= 0.25, >= 0.75) => Pivot.BottomLeft,
        (>= 0.75, >= 0.75) => Pivot.BottomRight,
        (_, <= 0.25) => Pivot.TopCenter,
        (_, >= 0.75) => Pivot.BottomCenter,
        (<= 0.25, _) => Pivot.CenterLeft,
        (>= 0.75, _) => Pivot.CenterRight,
        _ => Pivot.Center,
    };

    private static Align ToAlign(TextHorizontalAlignment horizontal, TextVerticalAlignment vertical) =>
        (horizontal, vertical) switch
        {
            (TextHorizontalAlignment.Left, TextVerticalAlignment.Top) => Align.TopLeft,
            (TextHorizontalAlignment.Center, TextVerticalAlignment.Top) => Align.TopCenter,
            (TextHorizontalAlignment.Right, TextVerticalAlignment.Top) => Align.TopRight,
            (TextHorizontalAlignment.Left, TextVerticalAlignment.Center) => Align.CenterLeft,
            (TextHorizontalAlignment.Center, TextVerticalAlignment.Center) => Align.Center,
            (TextHorizontalAlignment.Right, TextVerticalAlignment.Center) => Align.CenterRight,
            (TextHorizontalAlignment.Left, TextVerticalAlignment.Bottom) => Align.BottomLeft,
            (TextHorizontalAlignment.Center, TextVerticalAlignment.Bottom) => Align.BottomCenter,
            _ => Align.BottomRight,
        };

    private static void DrawModelRenderer(
        SceneEntity entity,
        bool selected,
        bool wantsPick,
        Ray pickRay,
        ref Guid? pickedEntityId,
        ref float nearestHitDistance)
    {
        var renderer = entity.Components.ModelRenderer;
        if (renderer is not { Visible: true } || renderer.AssetId == Guid.Empty)
        {
            return;
        }

        RuntimeAssetDescriptor? asset;
        lock (StateGate)
        {
            _runtimeAssets.TryGetValue(renderer.AssetId, out asset);
        }

        if (asset is null)
        {
            ReportAssetErrorOnce(
                $"missing:{renderer.AssetId}",
                "SKINNY-ASSET-REFERENCE-MISSING",
                $"Entity '{entity.Name}' references missing asset '{renderer.AssetId}'.",
                entity.Id,
                renderer.AssetId);
            return;
        }

        if (!File.Exists(asset.SourcePath))
        {
            ReportAssetErrorOnce(
                $"file:{asset.AssetId}:{asset.ContentHash}",
                "SKINNY-ASSET-SOURCE-MISSING",
                $"Model source was not found: {asset.SourcePath}",
                entity.Id,
                asset.AssetId);
            return;
        }

        if (!Models.TryGetValue(asset.AssetId, out var cached)
            || !string.Equals(cached.ContentHash, asset.ContentHash, StringComparison.Ordinal))
        {
            cached = new(asset.ContentHash, Model.FromFile(asset.SourcePath));
            Models[asset.AssetId] = cached;
        }

        var localTransform = ModelLocalTransform(renderer, asset.Bounds);
        var color = selected
            ? new Color(1.0f, 0.72f, 0.24f, 1)
            : Color.White;
        DrawModelWithMaterials(cached.Model, renderer, asset, localTransform, color, entity);

        if (wantsPick && TryGetModelLocalBounds(renderer, asset.Bounds, out var bounds))
        {
            var worldBounds = CurrentWorldBounds(
                new Vec3((float)bounds.CenterX, (float)bounds.CenterY, (float)bounds.CenterZ),
                new Vec3((float)bounds.SizeX, (float)bounds.SizeY, (float)bounds.SizeZ));
            if (worldBounds.Intersect(pickRay, out var worldHit))
            {
                var distance = Vec3.DistanceSq(pickRay.position, worldHit);
                if (distance < nearestHitDistance)
                {
                    nearestHitDistance = distance;
                    pickedEntityId = entity.Id;
                }
            }
        }
    }

    private static void DrawModelWithMaterials(
        Model source,
        ModelRendererComponent renderer,
        RuntimeAssetDescriptor asset,
        Matrix transform,
        Color color,
        SceneEntity entity)
    {
        if (renderer.MaterialAssetId is null && renderer.MaterialOverrides.Count == 0)
        {
            source.Draw(transform, color, RenderLayer.Layer0);
            return;
        }

        if (renderer.MaterialOverrides.Count == 0 && renderer.MaterialAssetId is { } globalMaterialId)
        {
            var material = _visualResources!.GetMaterial(
                globalMaterialId,
                null,
                1, 1, 0, 0,
                imageDefaults: false,
                doubleSided: false,
                RenderSurfacePreset.WorldOpaque,
                out var error);
            if (error is not null)
            {
                ReportVisualAssetErrorOnce(entity, error);
            }

            source.Draw(material, transform, color, RenderLayer.Layer0);
            return;
        }

        var variantKey = $"{asset.AssetId:N}:{asset.ContentHash}:{renderer.MaterialAssetId:N}:"
            + string.Join(';', renderer.MaterialOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value:N}"));
        if (!ModelVariants.TryGetValue(variantKey, out var variant))
        {
            variant = source.Copy();
            if (renderer.MaterialAssetId is { } defaultMaterialId)
            {
                var defaultMaterial = _visualResources!.GetMaterial(
                    defaultMaterialId, null, 1, 1, 0, 0, false, false,
                    RenderSurfacePreset.WorldOpaque, out var defaultError);
                if (defaultError is not null)
                {
                    ReportVisualAssetErrorOnce(entity, defaultError);
                }

                for (var index = 0; index < variant.Visuals.Count; index++)
                {
                    variant.Visuals[index].Material = defaultMaterial;
                }
            }

            foreach (var (slotKey, materialId) in renderer.MaterialOverrides)
            {
                var slotIndex = ResolveModelSlotIndex(asset, slotKey);
                if (slotIndex < 0 || slotIndex >= variant.Visuals.Count)
                {
                    ReportVisualAssetErrorOnce(entity, $"Model material slot '{slotKey}' was not found.");
                    continue;
                }

                var slotMaterial = _visualResources!.GetMaterial(
                    materialId, null, 1, 1, 0, 0, false, false,
                    RenderSurfacePreset.WorldOpaque, out var slotError);
                if (slotError is not null)
                {
                    ReportVisualAssetErrorOnce(entity, slotError);
                }

                variant.Visuals[slotIndex].Material = slotMaterial;
            }

            ModelVariants[variantKey] = variant;
        }

        variant.Draw(transform, color, RenderLayer.Layer0);
    }

    private static int ResolveModelSlotIndex(RuntimeAssetDescriptor asset, string slotKey)
    {
        if (int.TryParse(slotKey, out var numeric))
        {
            return numeric;
        }

        if (asset.Metadata is not { } metadata
            || !metadata.TryGetProperty("model", out var model)
            || !model.TryGetProperty("materialSlots", out var slots)
            || slots.ValueKind != JsonValueKind.Array)
        {
            return -1;
        }

        foreach (var slot in slots.EnumerateArray())
        {
            if (slot.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), slotKey, StringComparison.OrdinalIgnoreCase)
                && slot.TryGetProperty("index", out var index)
                && index.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return -1;
    }

    private static Matrix ModelLocalTransform(ModelRendererComponent renderer, RuntimeAssetBounds? bounds)
    {
        if (!renderer.FitToBounds || bounds is null)
        {
            return Matrix.Identity;
        }

        var largest = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        var fit = largest > 0 && double.IsFinite(largest)
            ? Math.Clamp(renderer.MaximumSize, 0.01, 100) / largest
            : 1;
        var scale = (float)fit;
        return Matrix.TRS(
            new Vec3(
                (float)(-bounds.CenterX * fit),
                (float)(-bounds.CenterY * fit),
                (float)(-bounds.CenterZ * fit)),
            Quat.Identity,
            new Vec3(scale, scale, scale));
    }

    private static bool TryGetModelLocalBounds(
        ModelRendererComponent renderer,
        RuntimeAssetBounds? bounds,
        out EditorPickBounds result)
    {
        if (bounds is null || bounds.SizeX <= 0 || bounds.SizeY <= 0 || bounds.SizeZ <= 0)
        {
            result = default;
            return false;
        }

        if (!renderer.FitToBounds)
        {
            result = new(
                bounds.CenterX,
                bounds.CenterY,
                bounds.CenterZ,
                bounds.SizeX,
                bounds.SizeY,
                bounds.SizeZ);
            return true;
        }

        var largest = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        var fit = largest > 0 && double.IsFinite(largest)
            ? Math.Clamp(renderer.MaximumSize, 0.01, 100) / largest
            : 1;
        result = new(0, 0, 0, bounds.SizeX * fit, bounds.SizeY * fit, bounds.SizeZ * fit);
        return true;
    }

    private static EditorPickBounds? GetEntityLocalBounds(SceneEntity entity)
    {
        var bounds = new List<EditorPickBounds>();
        if (entity.Components.PrimitiveMeshRenderer is { Visible: true } primitiveRenderer)
        {
            bounds.Add(primitiveRenderer.Primitive == PrimitiveKind.Quad
                ? new(0, 0, 0, 0.2, 0.2, 0.006)
                : new(0, 0, 0, 0.2, 0.2, 0.2));
        }

        if (entity.Components.ImageRenderer is { Visible: true } imageRenderer)
        {
            var size = imageRenderer.Size;
            if (_visualResources is not null
                && _visualResources.TryGetTexture(imageRenderer.TextureAssetId, out var texture, out _))
            {
                var aspect = texture.Height > 0 ? texture.Width / (double)texture.Height : 1;
                size = ResolveImageSize(imageRenderer, texture.Width, texture.Height, aspect);
            }

            bounds.Add(new(
                (0.5 - imageRenderer.Pivot.X) * size.X,
                (imageRenderer.Pivot.Y - 0.5) * size.Y,
                0,
                Math.Max(0.001, size.X),
                Math.Max(0.001, size.Y),
                0.006));
        }

        if (entity.Components.TextRenderer is { Visible: true } textRenderer)
        {
            var textBounds = textRenderer.Bounds;
            if (_visualResources is not null)
            {
                var style = _visualResources.GetTextStyle(
                    textRenderer.TextStyleAssetId,
                    textRenderer.FontAssetId,
                    textRenderer.CharacterHeight,
                    textRenderer.Color,
                    out _);
                textBounds = ResolveTextBounds(textRenderer, style);
            }

            bounds.Add(new(
                (0.5 - textRenderer.Pivot.X) * textBounds.X,
                (textRenderer.Pivot.Y - 0.5) * textBounds.Y,
                0,
                Math.Max(0.001, textBounds.X),
                Math.Max(0.001, textBounds.Y),
                0.006));
        }

        if (entity.Components.UiPanel is { Visible: true } uiPanel)
        {
            bounds.Add(new(
                0,
                0,
                0,
                Math.Max(0.08, uiPanel.Size.X),
                Math.Max(0.06, uiPanel.Size.Y),
                0.012));
        }

        var modelRenderer = entity.Components.ModelRenderer;
        if (modelRenderer is { Visible: true })
        {
            RuntimeAssetDescriptor? asset;
            lock (StateGate)
            {
                _runtimeAssets.TryGetValue(modelRenderer.AssetId, out asset);
            }

            if (asset is not null && TryGetModelLocalBounds(modelRenderer, asset.Bounds, out var modelBounds))
            {
                bounds.Add(modelBounds);
            }
        }

        if (_componentManager is not null)
        {
            bounds.AddRange(_componentManager.GetLocalPickBounds(entity.Id));
        }

        if (bounds.Count == 0)
        {
            return null;
        }

        var minimumX = bounds.Min(value => value.CenterX - (value.SizeX * 0.5));
        var minimumY = bounds.Min(value => value.CenterY - (value.SizeY * 0.5));
        var minimumZ = bounds.Min(value => value.CenterZ - (value.SizeZ * 0.5));
        var maximumX = bounds.Max(value => value.CenterX + (value.SizeX * 0.5));
        var maximumY = bounds.Max(value => value.CenterY + (value.SizeY * 0.5));
        var maximumZ = bounds.Max(value => value.CenterZ + (value.SizeZ * 0.5));
        return new(
            (minimumX + maximumX) * 0.5,
            (minimumY + maximumY) * 0.5,
            (minimumZ + maximumZ) * 0.5,
            maximumX - minimumX,
            maximumY - minimumY,
            maximumZ - minimumZ);
    }

    private static void ReportAssetErrorOnce(
        string key,
        string code,
        string message,
        Guid entityId,
        Guid assetId)
    {
        if (!ReportedAssetErrors.Add(key))
        {
            return;
        }

        _ = TrySendAsync(
            MessageTypes.Diagnostic,
            new StructuredDiagnosticMessage(
                DiagnosticSeverity.Error,
                DiagnosticOrigin.Asset,
                code,
                message,
                "Refresh or repair the model asset in the Project panel.",
                EntityId: entityId,
                ExceptionDetail: $"Asset ID: {assetId}"));
    }

    private static RuntimeAssetDescriptor? ResolveRuntimeAsset(Guid assetId)
    {
        lock (StateGate)
        {
            return _runtimeAssets.TryGetValue(assetId, out var asset) ? asset : null;
        }
    }

    private static IReadOnlySet<Guid> FindChangedAssets(
        IReadOnlyDictionary<Guid, RuntimeAssetDescriptor> previous,
        IReadOnlyDictionary<Guid, RuntimeAssetDescriptor> updated)
    {
        var changed = previous.Keys.Concat(updated.Keys)
            .Where(id => !previous.TryGetValue(id, out var oldAsset)
                || !updated.TryGetValue(id, out var newAsset)
                || !string.Equals(oldAsset.ContentHash, newAsset.ContentHash, StringComparison.Ordinal)
                || !string.Equals(oldAsset.Kind, newAsset.Kind, StringComparison.OrdinalIgnoreCase)
                || oldAsset.Metadata?.GetRawText() != newAsset.Metadata?.GetRawText())
            .ToHashSet();
        var all = previous.Values.Concat(updated.Values).GroupBy(asset => asset.AssetId).Select(group => group.Last()).ToArray();
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var dependent in all.Where(asset => asset.EffectiveDependencies.Any(changed.Contains)))
            {
                expanded |= changed.Add(dependent.AssetId);
            }
        }

        return changed;
    }

    private static void ReportVisualAssetErrorOnce(SceneEntity entity, string message)
    {
        var key = $"visual:{entity.Id:N}:{message}";
        if (!ReportedAssetErrors.Add(key))
        {
            return;
        }

        _ = TrySendAsync(
            MessageTypes.Diagnostic,
            new StructuredDiagnosticMessage(
                DiagnosticSeverity.Error,
                DiagnosticOrigin.Asset,
                "SKINNY-VISUAL-ASSET",
                $"'{entity.Name}' could not render one or more visual assets: {message}",
                "Repair or replace the referenced Texture, Material, Font, or Text Style in the Project panel.",
                EntityId: entity.Id));
    }

    private sealed record CachedModel(string ContentHash, Model Model);

    private sealed class HostAssetResolver : IEditorAssetResolver
    {
        public long CatalogVersion
        {
            get
            {
                lock (StateGate)
                {
                    return _assetCatalogVersion;
                }
            }
        }

        public bool TryResolve(Guid assetId, out EditorRuntimeAsset asset)
        {
            RuntimeAssetDescriptor? descriptor;
            lock (StateGate)
            {
                _runtimeAssets.TryGetValue(assetId, out descriptor);
            }

            if (descriptor is null)
            {
                asset = null!;
                return false;
            }

            asset = new(
                descriptor.AssetId,
                descriptor.Kind,
                descriptor.SourcePath,
                descriptor.ContentHash,
                descriptor.Bounds is { } bounds
                    ? new EditorRuntimeAssetBounds(
                        bounds.CenterX,
                        bounds.CenterY,
                        bounds.CenterZ,
                        bounds.SizeX,
                        bounds.SizeY,
                        bounds.SizeZ)
                    : null,
                descriptor.Diagnostics,
                descriptor.Metadata,
                descriptor.EffectiveDependencies);
            return true;
        }
    }

    private sealed class NoOpBuiltInRuntime : IEditorComponentRuntime
    {
        public void Create(EditorComponentContext context, JsonElement data) { }
        public void Apply(EditorComponentContext context, JsonElement data) { }
        public void Step(EditorComponentContext context) { }
        public void Destroy(EditorComponentContext context) { }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
