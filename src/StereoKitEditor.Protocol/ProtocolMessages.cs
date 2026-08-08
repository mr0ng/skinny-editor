using System.Text.Json;
using System.Text.Json.Serialization;
using StereoKitEditor.Adapter;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Protocol;

public static class ProtocolVersion
{
    public const int Major = 2;
    public const int Minor = 1;
}

public static class StereoKitCompatibility
{
    public static IReadOnlyList<string> TestedVersions { get; } = ["0.4.0-preview.3557"];

    public static bool IsTested(string runtimeVersion) => TestedVersions.Any(version =>
        string.Equals(runtimeVersion, version, StringComparison.OrdinalIgnoreCase)
        || runtimeVersion.StartsWith(version + " ", StringComparison.OrdinalIgnoreCase));
}

public static class RuntimeCompatibilityPolicy
{
    public static RuntimeCompatibilityResult Evaluate(
        int runtimeProtocolMajor,
        string runtimeAdapterContract,
        string stereoKitVersion,
        bool allowUntestedStereoKit)
    {
        if (runtimeProtocolMajor != ProtocolVersion.Major)
        {
            return new(
                false,
                RuntimeCompatibilityIssue.ProtocolMajor,
                $"Runtime protocol {runtimeProtocolMajor} is incompatible with editor protocol {ProtocolVersion.Major}.");
        }

        if (!string.Equals(runtimeAdapterContract, AdapterContractVersion.Current, StringComparison.Ordinal))
        {
            return new(
                false,
                RuntimeCompatibilityIssue.AdapterContract,
                $"Runtime adapter contract {runtimeAdapterContract} is incompatible with editor contract {AdapterContractVersion.Current}. Rebuild the project against the matching adapter package.");
        }

        if (!allowUntestedStereoKit && !StereoKitCompatibility.IsTested(stereoKitVersion))
        {
            return new(
                false,
                RuntimeCompatibilityIssue.StereoKitVersion,
                $"StereoKit {stereoKitVersion} is untested. Choose the explicit experimental override to run it anyway.");
        }

        return new(true, RuntimeCompatibilityIssue.None, string.Empty);
    }
}

public sealed record RuntimeCompatibilityResult(
    bool IsCompatible,
    RuntimeCompatibilityIssue Issue,
    string Message);

public enum RuntimeCompatibilityIssue
{
    None,
    ProtocolMajor,
    AdapterContract,
    StereoKitVersion,
}

public static class ProtocolCapabilities
{
    public const string ComponentCatalog = "component-catalog";
    public const string FullSceneSnapshots = "full-scene-snapshots";
    public const string SceneChangeSets = "scene-change-sets";
    public const string StructuredDiagnostics = "structured-diagnostics";
    public const string Heartbeat = "heartbeat";
    public const string PlayControl = "play-control";
    public const string SceneCameraTools = "scene-camera-tools";
    public const string AssetCatalog = "asset-catalog";
    public const string RuntimeTelemetry = "runtime-telemetry";
    public const string VisualAssets = "visual-assets";
    public const string SpatialUi = "spatial-ui";

    public static IReadOnlyList<string> EditorDefaults { get; } =
    [
        ComponentCatalog,
        FullSceneSnapshots,
        SceneChangeSets,
        StructuredDiagnostics,
        Heartbeat,
        PlayControl,
        SceneCameraTools,
        AssetCatalog,
        RuntimeTelemetry,
        VisualAssets,
        SpatialUi,
    ];
}

public static class MessageTypes
{
    public const string Hello = "hello";
    public const string Ready = "ready";
    public const string ComponentCatalog = "componentCatalog";
    public const string LoadSceneSnapshot = "loadSceneSnapshot";
    public const string LoadSceneChangeSet = "loadSceneChangeSet";
    public const string SceneResyncRequired = "sceneResyncRequired";
    public const string ComponentMigrationProposal = "componentMigrationProposal";
    public const string LoadAssetCatalog = "loadAssetCatalog";
    public const string AppliedRevision = "appliedRevision";
    public const string SetSelection = "setSelection";
    public const string EntityPicked = "entityPicked";
    public const string TransformCommitted = "transformCommitted";
    public const string TransformsCommitted = "transformsCommitted";
    public const string ComponentDataCommitted = "componentDataCommitted";
    public const string DuplicateSelectionRequested = "duplicateSelectionRequested";
    public const string SetSceneCamera = "setSceneCamera";
    public const string SceneCameraChanged = "sceneCameraChanged";
    public const string FrameSelection = "frameSelection";
    public const string SetSceneToolSettings = "setSceneToolSettings";
    public const string SceneToolSettingsChanged = "sceneToolSettingsChanged";
    public const string SetPlayState = "setPlayState";
    public const string StepPlay = "stepPlay";
    public const string PlayStateChanged = "playStateChanged";
    public const string RuntimeLog = "runtimeLog";
    public const string Diagnostic = "diagnostic";
    public const string RuntimeTelemetry = "runtimeTelemetry";
    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeatAck";
    public const string Stop = "stop";
    public const string FatalError = "fatalError";
}

public sealed record ProtocolEnvelope(string Type, JsonElement Payload);

public sealed record HelloMessage(
    int ProtocolMajor,
    int ProtocolMinor,
    string EditorVersion,
    string SessionNonce,
    Guid ProjectId,
    string ProjectName,
    string ProfileId,
    string BuildId,
    IReadOnlyList<string> Capabilities);

public sealed record ReadyMessage(
    int ProtocolMajor,
    int ProtocolMinor,
    string RuntimeVersion,
    string StereoKitVersion,
    string AdapterContractVersion,
    Guid ProjectId,
    string ProjectName,
    string ProfileId,
    string BuildId,
    string AdapterId,
    string AdapterVersion,
    string AssemblyName,
    string SessionNonce,
    RuntimeSessionMode Mode,
    RuntimePlayState PlayState,
    IReadOnlyList<string> Capabilities);

public sealed record ComponentCatalogMessage(EditorComponentCatalog Catalog);
public sealed record LoadAssetCatalogMessage(IReadOnlyList<RuntimeAssetDescriptor> Assets);
public sealed record LoadSceneSnapshotMessage(long Revision, SceneDocument Scene);
public sealed record AppliedRevisionMessage(long Revision);
public sealed record ComponentMigrationProposalMessage(
    long DocumentRevision,
    IReadOnlyList<ComponentMigrationPatch> Upgrades);
public sealed record ComponentMigrationPatch(
    Guid EntityId,
    Guid ComponentId,
    string ComponentTypeId,
    string DisplayName,
    int FromSchemaVersion,
    int ToSchemaVersion,
    JsonElement MigratedData);
public sealed record SetSelectionMessage(Guid? EntityId, IReadOnlyList<Guid>? EntityIds = null);
public sealed record EntityPickedMessage(Guid? EntityId);
public sealed record TransformCommittedMessage(Guid EntityId, TransformComponent Transform);
public sealed record EntityTransformValue(Guid EntityId, TransformComponent Transform);
public sealed record TransformsCommittedMessage(IReadOnlyList<EntityTransformValue> Transforms);
public sealed record ComponentDataCommittedMessage(
    Guid EntityId,
    Guid ComponentId,
    JsonElement Data,
    string Description);
public sealed record DuplicateSelectionRequestedMessage(IReadOnlyList<Guid> EntityIds);
public sealed record SetSceneCameraMessage(SceneCameraState Camera);
public sealed record SceneCameraChangedMessage(SceneCameraState Camera);
public sealed record FrameSelectionMessage(Guid? EntityId);
public sealed record SetSceneToolSettingsMessage(SceneToolSettings Settings);
public sealed record SceneToolSettingsChangedMessage(SceneToolSettings Settings);
public sealed record SetPlayStateMessage(RuntimePlayState State);
public sealed record StepPlayMessage;
public sealed record PlayStateChangedMessage(RuntimePlayState State, ulong Frame);
public sealed record RuntimeLogMessage(string Level, string Text);
public sealed record RuntimeTelemetryMessage(
    RuntimeSessionMode Mode,
    RuntimePlayState PlayState,
    long Revision,
    ulong Frame,
    double FrameTimeMilliseconds,
    double FramesPerSecond,
    double SimulationTimeSeconds,
    int EntityCount,
    int EnabledEntityCount,
    int ComponentCount,
    int LiveComponentCount,
    long ManagedMemoryBytes,
    long WorkingSetBytes,
    RuntimeInspectedEntityMessage? InspectedEntity);
public sealed record RuntimeInspectedEntityMessage(
    Guid EntityId,
    string Name,
    bool Enabled,
    IReadOnlyList<RuntimeComponentStatusMessage> Components);
public sealed record RuntimeComponentStatusMessage(
    Guid ComponentId,
    string TypeId,
    string DisplayName,
    string State,
    bool IsLive);
public sealed record HeartbeatMessage(long Sequence, DateTimeOffset SentAt);
public sealed record HeartbeatAckMessage(long Sequence, DateTimeOffset ReceivedAt);
public sealed record StopMessage(string Reason);
public sealed record FatalErrorMessage(string Message, string? Detail);

public sealed record RuntimeAssetDescriptor(
    Guid AssetId,
    string Kind,
    string SourcePath,
    string ContentHash,
    RuntimeAssetBounds? Bounds,
    IReadOnlyList<string> Diagnostics,
    JsonElement? Metadata = null,
    IReadOnlyList<Guid>? Dependencies = null)
{
    public IReadOnlyList<Guid> EffectiveDependencies => Dependencies ?? [];
}

public sealed record RuntimeAssetBounds(
    double CenterX,
    double CenterY,
    double CenterZ,
    double SizeX,
    double SizeY,
    double SizeZ);

public sealed record SceneCameraState(
    Vector3Value Pivot,
    double Distance,
    double YawDegrees,
    double PitchDegrees,
    SceneProjection Projection = SceneProjection.Perspective)
{
    public static SceneCameraState Default { get; } = new(
        new Vector3Value(0, 0.03, -0.65),
        0.75,
        0,
        0);
}

public sealed record SceneToolSettings(
    SceneGizmoSpace GizmoSpace,
    bool TranslationSnapEnabled,
    double TranslationSnap,
    SceneTransformTool Tool = SceneTransformTool.Move,
    bool RotationSnapEnabled = false,
    double RotationSnapDegrees = 15,
    bool ScaleSnapEnabled = false,
    double ScaleSnap = 0.1,
    bool ShowGrid = true,
    ScenePivotMode PivotMode = ScenePivotMode.Center,
    SceneUiInteractionMode UiInteractionMode = SceneUiInteractionMode.Edit)
{
    public static SceneToolSettings Default { get; } = new(
        SceneGizmoSpace.Global,
        false,
        0.05);
}

[JsonConverter(typeof(JsonStringEnumConverter<SceneTransformTool>))]
public enum SceneTransformTool
{
    Move,
    Rotate,
    Scale,
}

[JsonConverter(typeof(JsonStringEnumConverter<SceneGizmoSpace>))]
public enum SceneGizmoSpace
{
    Global,
    Local,
}

[JsonConverter(typeof(JsonStringEnumConverter<SceneProjection>))]
public enum SceneProjection
{
    Perspective,
    Orthographic,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScenePivotMode>))]
public enum ScenePivotMode
{
    Center,
    Active,
}

[JsonConverter(typeof(JsonStringEnumConverter<SceneUiInteractionMode>))]
public enum SceneUiInteractionMode
{
    Edit,
    Preview,
}

public sealed record StructuredDiagnosticMessage(
    DiagnosticSeverity Severity,
    DiagnosticOrigin Origin,
    string Code,
    string Message,
    string? SuggestedAction = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    Guid? EntityId = null,
    Guid? ComponentId = null,
    string? ComponentTypeId = null,
    long? DocumentRevision = null,
    string? ExceptionDetail = null);

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Fatal,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticOrigin>))]
public enum DiagnosticOrigin
{
    Build,
    Adapter,
    Component,
    Runtime,
    Protocol,
    Asset,
    Editor,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeSessionMode>))]
public enum RuntimeSessionMode
{
    Scene,
    Play,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimePlayState>))]
public enum RuntimePlayState
{
    Editing,
    Playing,
    Paused,
}
