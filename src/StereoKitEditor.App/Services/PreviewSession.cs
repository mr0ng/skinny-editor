using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.App.Services;

public sealed class RuntimeSession(RuntimeSessionMode mode) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _scenePushGate = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private NamedPipeServerStream? _pipe;
    private JsonPipeConnection? _connection;
    private Process? _process;
    private OwnedProcessJob? _processJob;
    private Task? _readTask;
    private Task? _heartbeatTask;
    private bool _intentionalStop;
    private bool _wasEverReady;
    private bool _unresponsiveReported;
    private int _fatalFailureReported;
    private long _lastHeartbeatAckUtcTicks;
    private long _heartbeatSequence;
    private RuntimeLaunchIdentity? _expectedIdentity;
    private TaskCompletionSource<ReadyMessage>? _readySignal;
    private TaskCompletionSource<EditorComponentCatalog>? _catalogSignal;
    private SceneDocument? _lastSentScene;
    private long _lastSentRevision = -1;
    private SceneDocument? _lastDesiredScene;
    private long _lastDesiredRevision = -1;
    private bool _allowUntestedStereoKit;

    public RuntimeSessionMode Mode { get; } = mode;
    public bool IsRunning => _process is { HasExited: false };
    public bool IsReady { get; private set; }
    public long AppliedRevision { get; private set; } = -1;
    public RuntimePlayState PlayState { get; private set; } = mode == RuntimeSessionMode.Scene
        ? RuntimePlayState.Editing
        : RuntimePlayState.Paused;
    public nint NativeWindowHandle { get; private set; }
    public EditorComponentCatalog? ComponentCatalog { get; private set; }
    public IReadOnlyList<string> NegotiatedCapabilities { get; private set; } = [];
    public string? BuildId => _expectedIdentity?.BuildId;

    public event EventHandler<RuntimeEventArgs>? EventReceived;

    public async Task StartAsync(
        string runtimeAssemblyPath,
        string workingDirectory,
        SceneDocument scene,
        long revision,
        Guid? selectedEntityId,
        RuntimeLaunchIdentity identity,
        IReadOnlyList<string>? launchArguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        SceneCameraState? sceneCamera = null,
        SceneToolSettings? sceneToolSettings = null,
        IReadOnlyList<RuntimeAssetDescriptor>? runtimeAssets = null,
        CancellationToken cancellationToken = default,
        bool allowUntestedStereoKit = false,
        IReadOnlyList<Guid>? selectedEntityIds = null)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync($"Restarting {Mode.ToString().ToLowerInvariant()} host");

            runtimeAssemblyPath = Path.GetFullPath(runtimeAssemblyPath);
            workingDirectory = Path.GetFullPath(workingDirectory);
            if (!File.Exists(runtimeAssemblyPath))
            {
                throw new FileNotFoundException("The configured runtime assembly does not exist.", runtimeAssemblyPath);
            }

            var pipeName = $"skeditor-{Mode.ToString().ToLowerInvariant()}-{Environment.ProcessId}-{Guid.NewGuid():N}";
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            };
            startInfo.ArgumentList.Add(runtimeAssemblyPath);
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add(Mode.ToString().ToLowerInvariant());
            foreach (var argument in launchArguments ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var pair in environment ?? new Dictionary<string, string>())
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            _intentionalStop = false;
            _wasEverReady = false;
            _unresponsiveReported = false;
            Interlocked.Exchange(ref _fatalFailureReported, 0);
            _lastHeartbeatAckUtcTicks = DateTime.UtcNow.Ticks;
            _heartbeatSequence = 0;
            IsReady = false;
            AppliedRevision = -1;
            NativeWindowHandle = 0;
            ComponentCatalog = null;
            NegotiatedCapabilities = [];
            _lastSentScene = null;
            _lastSentRevision = -1;
            _lastDesiredScene = null;
            _lastDesiredRevision = -1;
            _allowUntestedStereoKit = allowUntestedStereoKit;
            PlayState = Mode == RuntimeSessionMode.Scene ? RuntimePlayState.Editing : RuntimePlayState.Paused;
            _expectedIdentity = identity;
            _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _catalogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => EmitOutput("Info", args.Data);
            process.ErrorDataReceived += (_, args) => EmitOutput("Error", args.Data);
            process.Exited += (_, _) =>
            {
                IsReady = false;
                NativeWindowHandle = 0;
                var exitCode = TryGetExitCode(process);
                var unexpected = !_intentionalStop && _wasEverReady;
                var exitException = new EndOfStreamException(
                    $"{Mode} host exited before startup completed (code {exitCode}).");
                _readySignal?.TrySetException(exitException);
                _catalogSignal?.TrySetException(exitException);
                Emit(new(
                    RuntimeEventKind.Stopped,
                    _intentionalStop
                        ? $"{Mode} host stopped."
                        : $"{Mode} host exited unexpectedly (code {exitCode}).",
                    Unexpected: unexpected,
                    BuildId: identity.BuildId,
                    ExitCode: int.TryParse(exitCode, out var parsedExitCode) ? parsedExitCode : null));
            };
            _process = process;

            if (!process.Start())
            {
                throw new InvalidOperationException($"The {Mode} host process could not be started.");
            }

            _processJob = OwnedProcessJob.TryCreateAndAssign(process, out var jobError);
            if (jobError is not null)
            {
                Emit(new(
                    RuntimeEventKind.Log,
                    $"Windows Job Object process-tree containment was unavailable: {jobError}",
                    Level: "Warning"));
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Emit(new(
                RuntimeEventKind.Status,
                $"Connecting to {Mode.ToString().ToLowerInvariant()} host from {Path.GetFileName(runtimeAssemblyPath)}…"));

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _pipe.WaitForConnectionAsync(connectTimeout.Token);

            _connection = new JsonPipeConnection(_pipe);
            _readTask = _connection.ReadLoopAsync(HandleMessageAsync, _sessionCancellation.Token);

            var sessionNonce = Guid.NewGuid().ToString("N");
            identity = identity with { SessionNonce = sessionNonce };
            _expectedIdentity = identity;
            await _connection.SendAsync(
                MessageTypes.Hello,
                new HelloMessage(
                    ProtocolVersion.Major,
                    ProtocolVersion.Minor,
                    typeof(RuntimeSession).Assembly.GetName().Version?.ToString() ?? "prototype",
                    sessionNonce,
                    identity.ProjectId,
                    identity.ProjectName,
                    identity.ProfileId,
                    identity.BuildId,
                    ProtocolCapabilities.EditorDefaults),
                _sessionCancellation.Token);

            await Task.WhenAll(
                _readySignal.Task.WaitAsync(connectTimeout.Token),
                _catalogSignal.Task.WaitAsync(connectTimeout.Token));
            _heartbeatTask = HeartbeatLoopAsync(_sessionCancellation.Token);
            await PushAssetCatalogAsync(runtimeAssets ?? [], _sessionCancellation.Token);
            await PushSceneAsync(scene, revision, _sessionCancellation.Token);
            await SetSelectionsAsync(selectedEntityId, selectedEntityIds, _sessionCancellation.Token);
            if (Mode == RuntimeSessionMode.Scene)
            {
                await SetSceneCameraAsync(sceneCamera ?? SceneCameraState.Default, _sessionCancellation.Token);
                await SetSceneToolSettingsAsync(
                    sceneToolSettings ?? SceneToolSettings.Default,
                    _sessionCancellation.Token);
            }
        }
        catch
        {
            await StopCoreAsync($"{Mode} host failed to start");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task PushSceneAsync(
        SceneDocument scene,
        long revision,
        CancellationToken cancellationToken = default)
    {
        if (_connection is null)
        {
            return;
        }

        var desired = SceneSerializer.Clone(scene);
        await _scenePushGate.WaitAsync(cancellationToken);
        try
        {
            _lastDesiredScene = desired;
            _lastDesiredRevision = revision;
            if (_lastSentScene is not null
                && revision == _lastSentRevision)
            {
                return;
            }

            if (_lastSentScene is not null
                && NegotiatedCapabilities.Contains(ProtocolCapabilities.SceneChangeSets, StringComparer.Ordinal)
                && SceneChangeSetBuilder.TryCreate(
                    _lastSentScene,
                    _lastSentRevision,
                    desired,
                    revision,
                    out var changeSet))
            {
                await _connection.SendAsync(
                    MessageTypes.LoadSceneChangeSet,
                    changeSet!,
                    cancellationToken);
            }
            else
            {
                await SendSceneSnapshotAsync(desired, revision, cancellationToken);
            }

            _lastSentScene = desired;
            _lastSentRevision = revision;
        }
        finally
        {
            _scenePushGate.Release();
        }
    }

    public Task PushAssetCatalogAsync(
        IReadOnlyList<RuntimeAssetDescriptor> assets,
        CancellationToken cancellationToken = default) =>
        _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(
                MessageTypes.LoadAssetCatalog,
                new LoadAssetCatalogMessage(assets),
                cancellationToken);

    public Task SetSelectionAsync(Guid? entityId, CancellationToken cancellationToken = default) =>
        SetSelectionsAsync(entityId, entityId is { } id ? [id] : [], cancellationToken);

    public Task SetSelectionsAsync(
        Guid? entityId,
        IReadOnlyList<Guid>? entityIds,
        CancellationToken cancellationToken = default) =>
        _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(
                MessageTypes.SetSelection,
                new SetSelectionMessage(entityId, entityIds),
                cancellationToken);

    public Task SetSceneCameraAsync(
        SceneCameraState camera,
        CancellationToken cancellationToken = default)
    {
        if (Mode != RuntimeSessionMode.Scene)
        {
            throw new InvalidOperationException("Only a Scene session has an editor camera.");
        }

        return _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(
                MessageTypes.SetSceneCamera,
                new SetSceneCameraMessage(camera),
                cancellationToken);
    }

    public Task FrameSelectionAsync(Guid? entityId, CancellationToken cancellationToken = default)
    {
        if (Mode != RuntimeSessionMode.Scene)
        {
            throw new InvalidOperationException("Only a Scene session can frame an editor selection.");
        }

        return _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(
                MessageTypes.FrameSelection,
                new FrameSelectionMessage(entityId),
                cancellationToken);
    }

    public Task SetSceneToolSettingsAsync(
        SceneToolSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (Mode != RuntimeSessionMode.Scene)
        {
            throw new InvalidOperationException("Only a Scene session has editor tool settings.");
        }

        return _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(
                MessageTypes.SetSceneToolSettings,
                new SetSceneToolSettingsMessage(settings),
                cancellationToken);
    }

    public Task SetPlayStateAsync(RuntimePlayState state, CancellationToken cancellationToken = default)
    {
        if (Mode != RuntimeSessionMode.Play)
        {
            throw new InvalidOperationException("Only a Play session has a play state.");
        }

        return _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(MessageTypes.SetPlayState, new SetPlayStateMessage(state), cancellationToken);
    }

    public Task StepPlayAsync(CancellationToken cancellationToken = default)
    {
        if (Mode != RuntimeSessionMode.Play)
        {
            throw new InvalidOperationException("Only a Play session can step.");
        }

        return _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(MessageTypes.StepPlay, new StepPlayMessage(), cancellationToken);
    }

    public async Task StopAsync(string reason = "Stopped by user")
    {
        try
        {
            _sessionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another lifecycle operation completed teardown while Stop was requested.
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync(reason);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task HandleMessageAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MessageTypes.Ready:
                {
                    var ready = JsonPipeConnection.GetPayload<ReadyMessage>(envelope);
                    if (ready.Mode != Mode)
                    {
                        throw new InvalidDataException($"Expected a {Mode} host but connected to {ready.Mode}.");
                    }

                    var compatibility = RuntimeCompatibilityPolicy.Evaluate(
                        ready.ProtocolMajor,
                        ready.AdapterContractVersion,
                        ready.StereoKitVersion,
                        _allowUntestedStereoKit);

                    try
                    {
                        ValidateIdentity(ready);
                        if (!compatibility.IsCompatible)
                        {
                            if (compatibility.Issue == RuntimeCompatibilityIssue.StereoKitVersion)
                            {
                                Emit(new(
                                    RuntimeEventKind.CompatibilityBlocked,
                                    $"StereoKit {ready.StereoKitVersion} has not been verified with this SKinny Editor runtime bridge.",
                                    StereoKitVersion: ready.StereoKitVersion));
                            }

                            throw new InvalidDataException(compatibility.Message);
                        }
                    }
                    catch (Exception exception)
                    {
                        _readySignal?.TrySetException(exception);
                        _catalogSignal?.TrySetException(exception);
                        throw;
                    }

                    IsReady = true;
                    _wasEverReady = true;
                    PlayState = ready.PlayState;
                    NegotiatedCapabilities = ready.Capabilities;
                    var projectName = string.IsNullOrWhiteSpace(ready.ProjectName)
                        ? "Unnamed project"
                        : ready.ProjectName;
                    Emit(new(
                        RuntimeEventKind.Ready,
                        $"Connected · {projectName} · {ready.AdapterId} {ready.AdapterVersion} · contract {ready.AdapterContractVersion} · {Mode} · StereoKit {ready.StereoKitVersion}"));
                    _readySignal?.TrySetResult(ready);
                    _ = ResolveNativeWindowSafelyAsync(cancellationToken);
                    break;
                }
            case MessageTypes.ComponentCatalog:
                {
                    var catalog = JsonPipeConnection.GetPayload<ComponentCatalogMessage>(envelope).Catalog;
                    ComponentCatalog = catalog;
                    _catalogSignal?.TrySetResult(catalog);
                    var shortHash = catalog.SchemaHash[..Math.Min(8, catalog.SchemaHash.Length)];
                    Emit(new(
                        RuntimeEventKind.CatalogReady,
                        $"{Mode} catalog: {catalog.Components.Count} project component(s), schema {shortHash}.",
                        Catalog: catalog));
                    break;
                }
            case MessageTypes.AppliedRevision:
                {
                    var applied = JsonPipeConnection.GetPayload<AppliedRevisionMessage>(envelope);
                    AppliedRevision = applied.Revision;
                    Emit(new(
                        RuntimeEventKind.RevisionApplied,
                        $"{Mode} host rendered revision {applied.Revision}.",
                        Revision: applied.Revision));
                    break;
                }
            case MessageTypes.EntityPicked:
                {
                    var picked = JsonPipeConnection.GetPayload<EntityPickedMessage>(envelope);
                    Emit(new(RuntimeEventKind.EntityPicked, "Scene entity picked.", EntityId: picked.EntityId));
                    break;
                }
            case MessageTypes.TransformCommitted:
                {
                    var committed = JsonPipeConnection.GetPayload<TransformCommittedMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.TransformCommitted,
                        "Scene transform committed.",
                        EntityId: committed.EntityId,
                        Transform: committed.Transform));
                    break;
                }
            case MessageTypes.PlayStateChanged:
                {
                    var state = JsonPipeConnection.GetPayload<PlayStateChangedMessage>(envelope);
                    PlayState = state.State;
                    Emit(new(
                        RuntimeEventKind.PlayStateChanged,
                        $"Play state: {state.State}",
                        PlayState: state.State));
                    break;
                }
            case MessageTypes.RuntimeLog:
                {
                    var log = JsonPipeConnection.GetPayload<RuntimeLogMessage>(envelope);
                    Emit(new(RuntimeEventKind.Log, log.Text, Level: log.Level));
                    if (RuntimeFailureClassifier.TryClassifyFatalLog(log.Level, log.Text, out var failure))
                    {
                        ReportFatalFailure(failure, log.Text);
                    }

                    break;
                }
            case MessageTypes.TransformsCommitted:
                {
                    var committed = JsonPipeConnection.GetPayload<TransformsCommittedMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.TransformsCommitted,
                        $"{committed.Transforms.Count} Scene transforms committed.",
                        Transforms: committed.Transforms));
                    break;
                }
            case MessageTypes.ComponentDataCommitted:
                {
                    var committed = JsonPipeConnection.GetPayload<ComponentDataCommittedMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.ComponentDataCommitted,
                        committed.Description,
                        EntityId: committed.EntityId,
                        ComponentId: committed.ComponentId,
                        ComponentData: committed.Data));
                    break;
                }
            case MessageTypes.DuplicateSelectionRequested:
                {
                    var request = JsonPipeConnection.GetPayload<DuplicateSelectionRequestedMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.DuplicateSelectionRequested,
                        "Duplicate-drag requested.",
                        EntityIds: request.EntityIds));
                    break;
                }
            case MessageTypes.RuntimeTelemetry:
                {
                    var telemetry = JsonPipeConnection.GetPayload<RuntimeTelemetryMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.Telemetry,
                        $"{telemetry.FramesPerSecond:0} FPS",
                        Revision: telemetry.Revision,
                        Telemetry: telemetry));
                    break;
                }
            case MessageTypes.SceneResyncRequired:
                {
                    var resync = JsonPipeConnection.GetPayload<SceneResyncRequiredMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.Log,
                        $"{Mode} requested a full scene resynchronization: {resync.Reason}",
                        Level: "Warning"));
                    await ResynchronizeSceneAsync(cancellationToken);
                    break;
                }
            case MessageTypes.ComponentMigrationProposal:
                {
                    var proposal = JsonPipeConnection.GetPayload<ComponentMigrationProposalMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.ComponentMigrationProposed,
                        $"{proposal.Upgrades.Count} component schema upgrade{(proposal.Upgrades.Count == 1 ? string.Empty : "s")} available.",
                        Revision: proposal.DocumentRevision,
                        MigrationProposal: proposal));
                    break;
                }
            case MessageTypes.SceneCameraChanged:
                {
                    var camera = JsonPipeConnection.GetPayload<SceneCameraChangedMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.SceneCameraChanged,
                        "Scene camera changed.",
                        Camera: camera.Camera));
                    break;
                }
            case MessageTypes.SceneToolSettingsChanged:
                {
                    var settings = JsonPipeConnection.GetPayload<SceneToolSettingsChangedMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.SceneToolSettingsChanged,
                        $"Scene tool changed to {settings.Settings.Tool}.",
                        ToolSettings: settings.Settings));
                    break;
                }
            case MessageTypes.Diagnostic:
                {
                    var diagnostic = JsonPipeConnection.GetPayload<StructuredDiagnosticMessage>(envelope);
                    Emit(new(
                        RuntimeEventKind.Diagnostic,
                        diagnostic.Message,
                        Level: diagnostic.Severity.ToString(),
                        EntityId: diagnostic.EntityId,
                        Diagnostic: diagnostic));
                    break;
                }
            case MessageTypes.HeartbeatAck:
                {
                    var heartbeat = JsonPipeConnection.GetPayload<HeartbeatAckMessage>(envelope);
                    if (heartbeat.Sequence <= Volatile.Read(ref _heartbeatSequence))
                    {
                        Interlocked.Exchange(ref _lastHeartbeatAckUtcTicks, DateTime.UtcNow.Ticks);
                        if (_unresponsiveReported)
                        {
                            _unresponsiveReported = false;
                            Emit(new(RuntimeEventKind.Responsive, $"{Mode} host is responding again."));
                        }
                    }

                    break;
                }
            case MessageTypes.FatalError:
                {
                    var error = JsonPipeConnection.GetPayload<FatalErrorMessage>(envelope);
                    var failure = new InvalidOperationException(error.Message);
                    _readySignal?.TrySetException(failure);
                    _catalogSignal?.TrySetException(failure);
                    var message = error.Detail is null ? error.Message : $"{error.Message}\n{error.Detail}";
                    if (_wasEverReady)
                    {
                        ReportFatalFailure(error.Message, error.Detail);
                    }
                    else
                    {
                        Emit(new(RuntimeEventKind.Error, message));
                    }

                    break;
                }
        }

    }

    private async Task ResolveNativeWindowAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || _process is null)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!_process.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            _process.Refresh();
            if (_process.MainWindowHandle != 0)
            {
                NativeWindowHandle = _process.MainWindowHandle;
                Emit(new(
                    RuntimeEventKind.WindowReady,
                    $"{Mode} native window is ready.",
                    WindowHandle: NativeWindowHandle));
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Emit(new(RuntimeEventKind.Error, $"{Mode} host did not expose a native window."));
    }

    private async Task ResolveNativeWindowSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ResolveNativeWindowAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // The process can stop while its native window is still being discovered.
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var connection = _connection;
                if (connection is null || !IsReady)
                {
                    continue;
                }

                var sequence = Interlocked.Increment(ref _heartbeatSequence);
                await connection.SendAsync(
                    MessageTypes.Heartbeat,
                    new HeartbeatMessage(sequence, DateTimeOffset.UtcNow),
                    cancellationToken);

                if (!_unresponsiveReported
                    && DateTime.UtcNow - new DateTime(
                        Interlocked.Read(ref _lastHeartbeatAckUtcTicks),
                        DateTimeKind.Utc) > TimeSpan.FromSeconds(5))
                {
                    _unresponsiveReported = true;
                    Emit(new(
                        RuntimeEventKind.Unresponsive,
                        $"{Mode} host has missed five seconds of heartbeats."));
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Session shutdown closes the timer and pipe.
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        _intentionalStop = true;
        if (!process.HasExited && _connection is not null)
        {
            try
            {
                await _connection.SendAsync(MessageTypes.Stop, new StopMessage(reason));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The child already disconnected.
            }
        }

        if (!process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        _sessionCancellation?.Cancel();
        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping.
            }
        }

        if (_readTask is not null)
        {
            try
            {
                await _readTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Expected when closing the pipe.
            }
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        else if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
        }

        process.Dispose();
        _processJob?.Dispose();
        _processJob = null;
        _sessionCancellation?.Dispose();
        _connection = null;
        _pipe = null;
        _process = null;
        _readTask = null;
        _heartbeatTask = null;
        _sessionCancellation = null;
        IsReady = false;
        AppliedRevision = -1;
        NativeWindowHandle = 0;
        ComponentCatalog = null;
        NegotiatedCapabilities = [];
        _lastSentScene = null;
        _lastSentRevision = -1;
        _lastDesiredScene = null;
        _lastDesiredRevision = -1;
        _allowUntestedStereoKit = false;
        _expectedIdentity = null;
        _readySignal = null;
        _catalogSignal = null;
    }

    private void ValidateIdentity(ReadyMessage ready)
    {
        var expected = _expectedIdentity
            ?? throw new InvalidOperationException("Runtime identity was not initialized.");
        if (ready.ProjectId != expected.ProjectId
            || !string.Equals(ready.ProfileId, expected.ProfileId, StringComparison.Ordinal)
            || !string.Equals(ready.BuildId, expected.BuildId, StringComparison.Ordinal)
            || !string.Equals(ready.SessionNonce, expected.SessionNonce, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The connected runtime did not match the requested project, profile, build, and session identity.");
        }
    }

    private Task SendSceneSnapshotAsync(
        SceneDocument scene,
        long revision,
        CancellationToken cancellationToken) =>
        _connection is null
            ? Task.CompletedTask
            : _connection.SendAsync(
                MessageTypes.LoadSceneSnapshot,
                new LoadSceneSnapshotMessage(revision, scene),
                cancellationToken);

    private async Task ResynchronizeSceneAsync(CancellationToken cancellationToken)
    {
        await _scenePushGate.WaitAsync(cancellationToken);
        try
        {
            if (_lastDesiredScene is null)
            {
                return;
            }

            await SendSceneSnapshotAsync(_lastDesiredScene, _lastDesiredRevision, cancellationToken);
            _lastSentScene = _lastDesiredScene;
            _lastSentRevision = _lastDesiredRevision;
        }
        finally
        {
            _scenePushGate.Release();
        }
    }

    private void EmitOutput(string level, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Emit(new(RuntimeEventKind.Log, text, Level: level));
        }
    }

    private void ReportFatalFailure(string message, string? detail)
    {
        if (Interlocked.CompareExchange(ref _fatalFailureReported, 1, 0) != 0)
        {
            return;
        }

        IsReady = false;
        Emit(new(
            RuntimeEventKind.FatalFailure,
            string.IsNullOrWhiteSpace(detail) ? message : $"{message}\n{detail}",
            BuildId: BuildId));
    }

    private void Emit(RuntimeEventArgs args) => EventReceived?.Invoke(this, args);

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync("Editor closing");
        _lifecycleGate.Dispose();
    }
}

public sealed record RuntimeEventArgs(
    RuntimeEventKind Kind,
    string Message,
    long? Revision = null,
    string? Level = null,
    Guid? EntityId = null,
    TransformComponent? Transform = null,
    RuntimePlayState? PlayState = null,
    nint WindowHandle = 0,
    EditorComponentCatalog? Catalog = null,
    StructuredDiagnosticMessage? Diagnostic = null,
    bool Unexpected = false,
    string? BuildId = null,
    int? ExitCode = null,
    SceneCameraState? Camera = null,
    SceneToolSettings? ToolSettings = null,
    ComponentMigrationProposalMessage? MigrationProposal = null,
    string? StereoKitVersion = null,
    RuntimeTelemetryMessage? Telemetry = null,
    IReadOnlyList<EntityTransformValue>? Transforms = null,
    IReadOnlyList<Guid>? EntityIds = null,
    Guid? ComponentId = null,
    JsonElement? ComponentData = null);

public enum RuntimeEventKind
{
    Status,
    Ready,
    CatalogReady,
    WindowReady,
    RevisionApplied,
    EntityPicked,
    TransformCommitted,
    TransformsCommitted,
    ComponentDataCommitted,
    DuplicateSelectionRequested,
    SceneCameraChanged,
    SceneToolSettingsChanged,
    ComponentMigrationProposed,
    PlayStateChanged,
    Log,
    Diagnostic,
    Telemetry,
    Unresponsive,
    Responsive,
    CompatibilityBlocked,
    FatalFailure,
    Error,
    Stopped,
}

public sealed record RuntimeLaunchIdentity(
    Guid ProjectId,
    string ProjectName,
    string ProfileId,
    string BuildId,
    string SessionNonce = "");
