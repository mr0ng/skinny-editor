using System.IO.Pipes;
using System.Text.Json;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class ProtocolConnectionTests
{
    [Fact]
    public void ReadyMessage_RoundTripsRuntimeModeAndPlayStateAsStrings()
    {
        var message = new ReadyMessage(
            ProtocolVersion.Major,
            ProtocolVersion.Minor,
            "tests",
            "StereoKit tests",
            AdapterContractVersion.Current,
            Guid.Parse("5D996183-A032-465B-AB62-3593154F20D8"),
            "External test project",
            "editor-desktop",
            "build-123",
            "example.adapter",
            AdapterContractVersion.Current,
            "Example.Runtime",
            "nonce-123",
            RuntimeSessionMode.Play,
            RuntimePlayState.Paused,
            ProtocolCapabilities.EditorDefaults);

        var json = JsonSerializer.Serialize(message, SceneSerializer.Options);
        var roundTripped = JsonSerializer.Deserialize<ReadyMessage>(json, SceneSerializer.Options);

        Assert.Contains("\"mode\": \"Play\"", json, StringComparison.Ordinal);
        Assert.Contains("\"playState\": \"Paused\"", json, StringComparison.Ordinal);
        Assert.Contains("\"projectName\": \"External test project\"", json, StringComparison.Ordinal);
        Assert.NotNull(roundTripped);
        Assert.Equal(message with { Capabilities = [] }, roundTripped with { Capabilities = [] });
        Assert.Equal(message.Capabilities, roundTripped.Capabilities);
    }

    [Fact]
    public void SceneViewportMessages_RoundTripCameraAndToolSettings()
    {
        var cameraMessage = new SetSceneCameraMessage(new SceneCameraState(
            new Vector3Value(1.25, -0.5, 3.75),
            4.5,
            127.25,
            -38.5,
            SceneProjection.Orthographic));
        var settingsMessage = new SetSceneToolSettingsMessage(new SceneToolSettings(
            SceneGizmoSpace.Local,
            true,
            0.125,
            SceneTransformTool.Rotate,
            true,
            22.5,
            true,
            0.25,
            false,
            ScenePivotMode.Active));

        var cameraJson = JsonSerializer.Serialize(cameraMessage, SceneSerializer.Options);
        var settingsJson = JsonSerializer.Serialize(settingsMessage, SceneSerializer.Options);
        var cameraRoundTrip = JsonSerializer.Deserialize<SetSceneCameraMessage>(
            cameraJson,
            SceneSerializer.Options);
        var settingsRoundTrip = JsonSerializer.Deserialize<SetSceneToolSettingsMessage>(
            settingsJson,
            SceneSerializer.Options);

        Assert.Contains("\"yawDegrees\": 127.25", cameraJson, StringComparison.Ordinal);
        Assert.Contains("\"gizmoSpace\": \"Local\"", settingsJson, StringComparison.Ordinal);
        Assert.Contains("\"tool\": \"Rotate\"", settingsJson, StringComparison.Ordinal);
        Assert.Contains("\"rotationSnapDegrees\": 22.5", settingsJson, StringComparison.Ordinal);
        Assert.Contains("\"projection\": \"Orthographic\"", cameraJson, StringComparison.Ordinal);
        Assert.Contains("\"pivotMode\": \"Active\"", settingsJson, StringComparison.Ordinal);
        Assert.Equal(cameraMessage, cameraRoundTrip);
        Assert.Equal(settingsMessage, settingsRoundTrip);
        Assert.Contains(ProtocolCapabilities.SceneCameraTools, ProtocolCapabilities.EditorDefaults);
    }

    [Fact]
    public void MultiSelectionAndTransformBatch_RoundTripStableEntityIds()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var selection = new SetSelectionMessage(second, [first, second]);
        var transforms = new TransformsCommittedMessage([
            new EntityTransformValue(first, TransformComponent.Identity with
            {
                Position = new Vector3Value(1, 0, 0),
            }),
            new EntityTransformValue(second, TransformComponent.Identity with
            {
                Position = new Vector3Value(2, 0, 0),
            }),
        ]);

        var selectionJson = JsonSerializer.Serialize(selection, SceneSerializer.Options);
        var transformsJson = JsonSerializer.Serialize(transforms, SceneSerializer.Options);
        var selectionRoundTrip = JsonSerializer.Deserialize<SetSelectionMessage>(selectionJson, SceneSerializer.Options)!;
        var transformsRoundTrip = JsonSerializer.Deserialize<TransformsCommittedMessage>(transformsJson, SceneSerializer.Options)!;

        Assert.Equal(second, selectionRoundTrip.EntityId);
        Assert.Equal([first, second], selectionRoundTrip.EntityIds);
        Assert.Equal([first, second], transformsRoundTrip.Transforms.Select(value => value.EntityId));
    }

    [Fact]
    public void RuntimeTelemetry_RoundTripsLiveInspectionAndPerformanceData()
    {
        var entityId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var message = new RuntimeTelemetryMessage(
            RuntimeSessionMode.Scene,
            RuntimePlayState.Editing,
            42,
            1_024,
            8.25,
            121.21,
            3.5,
            12,
            10,
            24,
            21,
            4_000_000,
            80_000_000,
            new RuntimeInspectedEntityMessage(
                entityId,
                "Selected",
                true,
                [new RuntimeComponentStatusMessage(componentId, "example.marker", "Marker", "Live", true)]));

        var json = JsonSerializer.Serialize(message, SceneSerializer.Options);
        var roundTripped = JsonSerializer.Deserialize<RuntimeTelemetryMessage>(json, SceneSerializer.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(message.Mode, roundTripped.Mode);
        Assert.Equal(message.Revision, roundTripped.Revision);
        Assert.Equal(message.FramesPerSecond, roundTripped.FramesPerSecond);
        Assert.Equal(entityId, roundTripped.InspectedEntity?.EntityId);
        Assert.Contains(ProtocolCapabilities.RuntimeTelemetry, ProtocolCapabilities.EditorDefaults);
        Assert.Equal(componentId, Assert.Single(roundTripped.InspectedEntity!.Components).ComponentId);
    }

    [Fact]
    public async Task DuplexPipe_TransfersTypedEnvelope()
    {
        var pipeName = $"skeditor-test-{Guid.NewGuid():N}";
        await using var serverPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var clientPipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var serverConnected = serverPipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await clientPipe.ConnectAsync(5_000, TestContext.Current.CancellationToken);
        await serverConnected;

        await using var server = new JsonPipeConnection(serverPipe);
        await using var client = new JsonPipeConnection(clientPipe);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<HelloMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var readTask = server.ReadLoopAsync((envelope, _) =>
        {
            if (envelope.Type == MessageTypes.Hello)
            {
                received.TrySetResult(JsonPipeConnection.GetPayload<HelloMessage>(envelope));
            }

            return Task.CompletedTask;
        }, cancellation.Token);

        var sent = new HelloMessage(
            ProtocolVersion.Major,
            ProtocolVersion.Minor,
            "tests",
            "nonce-123",
            Guid.Parse("5D996183-A032-465B-AB62-3593154F20D8"),
            "External test project",
            "editor-desktop",
            "build-123",
            ProtocolCapabilities.EditorDefaults);
        await client.SendAsync(MessageTypes.Hello, sent, cancellation.Token);
        var actual = await received.Task.WaitAsync(cancellation.Token);

        Assert.Equal(sent with { Capabilities = [] }, actual with { Capabilities = [] });
        Assert.Equal(sent.Capabilities, actual.Capabilities);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task DisposeAsync_InterruptsAnInFlightWriteWithoutDeadlocking()
    {
        var stream = new BlockingDuplexStream();
        var connection = new JsonPipeConnection(stream);
        var sendTask = connection.SendAsync(
            MessageTypes.Heartbeat,
            new HeartbeatMessage(99, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await stream.WriteStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await connection.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(() => sendTask);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.SendAsync(
            MessageTypes.Heartbeat,
            new HeartbeatMessage(100, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ComponentCatalog_HashIsStableAcrossRegistrationOrder()
    {
        var first = new EditorComponentDescriptor
        {
            TypeId = "com.example.first",
            SchemaVersion = 1,
            DisplayName = "First",
        };
        var second = new EditorComponentDescriptor
        {
            TypeId = "com.example.second",
            SchemaVersion = 1,
            DisplayName = "Second",
        };

        var forward = EditorComponentCatalog.Create("example", "Example", "0.1", [first, second]);
        var reverse = EditorComponentCatalog.Create("example", "Example", "0.1", [second, first]);

        Assert.Equal(forward.SchemaHash, reverse.SchemaHash);
        Assert.Equal(["com.example.first", "com.example.second"], forward.Components.Select(x => x.TypeId));
    }

    [Theory]
    [InlineData("0.4.0-preview.3557", true)]
    [InlineData("0.4.0-preview.3557 Win32 x64", true)]
    [InlineData("0.4.0-preview.3515 Win32 x64", false)]
    [InlineData("0.5.0", false)]
    public void StereoKitCompatibility_OnlyAcceptsTestedBridgeVersions(string version, bool expected) =>
        Assert.Equal(expected, StereoKitCompatibility.IsTested(version));

    [Theory]
    [InlineData(2, "0.3", "0.4.0-preview.3557", false, true, RuntimeCompatibilityIssue.None)]
    [InlineData(1, "0.3", "0.4.0-preview.3557", false, false, RuntimeCompatibilityIssue.ProtocolMajor)]
    [InlineData(3, "0.3", "0.4.0-preview.3557", false, false, RuntimeCompatibilityIssue.ProtocolMajor)]
    [InlineData(2, "0.1", "0.4.0-preview.3557", false, false, RuntimeCompatibilityIssue.AdapterContract)]
    [InlineData(2, "0.2", "0.4.0-preview.3557", false, false, RuntimeCompatibilityIssue.AdapterContract)]
    [InlineData(2, "0.3", "0.5.0", false, false, RuntimeCompatibilityIssue.StereoKitVersion)]
    [InlineData(2, "0.3", "0.5.0", true, true, RuntimeCompatibilityIssue.None)]
    public void RuntimeCompatibility_MatrixEnforcesMajorContractAndExplicitRuntimeOverride(
        int protocolMajor,
        string adapterContract,
        string stereoKitVersion,
        bool allowUntested,
        bool expectedCompatible,
        RuntimeCompatibilityIssue expectedIssue)
    {
        var result = RuntimeCompatibilityPolicy.Evaluate(
            protocolMajor,
            adapterContract,
            stereoKitVersion,
            allowUntested);

        Assert.Equal(expectedCompatible, result.IsCompatible);
        Assert.Equal(expectedIssue, result.Issue);
        Assert.Equal(expectedCompatible, string.IsNullOrEmpty(result.Message));
    }

    private sealed class BlockingDuplexStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await _disposed.Task.WaitAsync(cancellationToken);
            throw new IOException("The test transport was closed.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.TrySetResult();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
