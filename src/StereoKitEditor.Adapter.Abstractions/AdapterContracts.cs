using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StereoKitEditor.Adapter;

public static class AdapterContractVersion
{
    public const int Major = 0;
    public const int Minor = 3;
    public const string Current = "0.3";
}

[JsonConverter(typeof(JsonStringEnumConverter<EditorRuntimeMode>))]
public enum EditorRuntimeMode
{
    Scene,
    Play,
}

[JsonConverter(typeof(JsonStringEnumConverter<EditorRuntimePlayState>))]
public enum EditorRuntimePlayState
{
    Editing,
    Playing,
    Paused,
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<EditorComponentModes>))]
public enum EditorComponentModes
{
    None = 0,
    Scene = 1,
    Play = 2,
    SceneAndPlay = Scene | Play,
}

[JsonConverter(typeof(JsonStringEnumConverter<EditorPropertyKind>))]
public enum EditorPropertyKind
{
    Boolean,
    Integer,
    Number,
    String,
    Enum,
    Flags,
    Vector2,
    Vector3,
    Vector4,
    Quaternion,
    Color,
    AssetReference,
    EntityReference,
}

[JsonConverter(typeof(JsonStringEnumConverter<EditorPropertyPresentation>))]
public enum EditorPropertyPresentation
{
    Auto,
    Slider,
    MultilineText,
}

public sealed record EditorPropertyDescriptor
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required EditorPropertyKind Kind { get; init; }
    public string? Description { get; init; }
    public string? Group { get; init; }
    public string? Units { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Increment { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsRequired { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public IReadOnlyList<string> AcceptedAssetKinds { get; init; } = [];
    public EditorPropertyPresentation Presentation { get; init; }
}

public sealed record EditorComponentDescriptor
{
    public required string TypeId { get; init; }
    public required int SchemaVersion { get; init; }
    public required string DisplayName { get; init; }
    public string Category { get; init; } = "Project";
    public string? Description { get; init; }
    public bool AllowMultiple { get; init; }
    public EditorComponentModes Modes { get; init; } = EditorComponentModes.SceneAndPlay;
    public IReadOnlyList<string> RequiredComponentTypeIds { get; init; } = [];
    public IReadOnlyList<string> ConflictingComponentTypeIds { get; init; } = [];
    public JsonElement DefaultData { get; init; } = JsonSerializer.SerializeToElement(new { });
    public IReadOnlyList<EditorPropertyDescriptor> Properties { get; init; } = [];
}

public sealed record EditorComponentMigration
{
    public required int FromVersion { get; init; }
    public required int ToVersion { get; init; }
    public required Func<JsonElement, JsonElement> Upgrade { get; init; }
}

public sealed record EditorComponentCatalog(
    string AdapterId,
    string AdapterDisplayName,
    string AdapterVersion,
    string SchemaHash,
    IReadOnlyList<EditorComponentDescriptor> Components)
{
    public IReadOnlyList<EditorBindingDescriptor> Bindings { get; init; } = [];
    public IReadOnlyList<EditorActionDescriptor> Actions { get; init; } = [];

    public static EditorComponentCatalog Create(
        string adapterId,
        string adapterDisplayName,
        string adapterVersion,
        IReadOnlyList<EditorComponentDescriptor> components,
        IReadOnlyList<EditorBindingDescriptor>? bindings = null,
        IReadOnlyList<EditorActionDescriptor>? actions = null)
    {
        var ordered = components.OrderBy(component => component.TypeId, StringComparer.Ordinal).ToArray();
        var orderedBindings = (bindings ?? []).OrderBy(binding => binding.Id, StringComparer.Ordinal).ToArray();
        var orderedActions = (actions ?? []).OrderBy(action => action.Id, StringComparer.Ordinal).ToArray();
        var canonical = JsonSerializer.Serialize(new
        {
            components = ordered,
            bindings = orderedBindings,
            actions = orderedActions,
        }, CatalogJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(adapterId, adapterDisplayName, adapterVersion, hash, ordered)
        {
            Bindings = orderedBindings,
            Actions = orderedActions,
        };
    }
}

public sealed record EditorTransformState(
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    double ScaleX,
    double ScaleY,
    double ScaleZ);

public sealed record EditorRuntimeAssetBounds(
    double CenterX,
    double CenterY,
    double CenterZ,
    double SizeX,
    double SizeY,
    double SizeZ);

public sealed record EditorRuntimeAsset(
    Guid AssetId,
    string Kind,
    string SourcePath,
    string ContentHash,
    EditorRuntimeAssetBounds? Bounds,
    IReadOnlyList<string> Diagnostics,
    JsonElement? Metadata = null,
    IReadOnlyList<Guid>? Dependencies = null)
{
    public IReadOnlyList<Guid> EffectiveDependencies => Dependencies ?? [];
}

/// <summary>
/// Resolves durable project asset IDs inside the isolated runtime. Scene data stores
/// IDs; only the runtime-facing catalog contains machine-specific absolute paths.
/// </summary>
public interface IEditorAssetResolver
{
    long CatalogVersion { get; }
    bool TryResolve(Guid assetId, out EditorRuntimeAsset asset);

    bool TryResolve(Guid assetId, string requiredKind, out EditorRuntimeAsset asset)
    {
        if (TryResolve(assetId, out asset)
            && string.Equals(asset.Kind, requiredKind, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        asset = null!;
        return false;
    }
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<EditorInteractionModes>))]
public enum EditorInteractionModes
{
    None = 0,
    ScenePreview = 1,
    Play = 2,
    ScenePreviewAndPlay = ScenePreview | Play,
}

[JsonConverter(typeof(JsonStringEnumConverter<EditorBindingValueKind>))]
public enum EditorBindingValueKind { Boolean, Number, String }

public sealed record EditorBindingDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required EditorBindingValueKind Kind { get; init; }
    public string? Description { get; init; }
    public EditorInteractionModes Modes { get; init; } = EditorInteractionModes.Play;
    public JsonElement DesignValue { get; init; } = JsonSerializer.SerializeToElement(string.Empty);
    public bool IsReadOnly { get; init; }
}

public sealed record EditorActionDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public EditorInteractionModes Modes { get; init; } = EditorInteractionModes.Play;
}

public sealed record EditorActionInvocation(
    string ActionId,
    Guid PanelEntityId,
    Guid SourceEntityId,
    EditorRuntimeMode Mode);

public interface IEditorInteractionResolver
{
    bool TryRead(string bindingId, out JsonElement value);
    bool TryWrite(string bindingId, JsonElement value, out string? error);
    bool TryInvoke(EditorActionInvocation invocation, out string? error);
}

public sealed record EditorProjectRuntimeContext(
    EditorRuntimeMode Mode,
    EditorRuntimePlayState PlayState,
    float DeltaTime,
    float SimulationTime,
    CancellationToken SessionCancellation);

public sealed record EditorComponentContext(
    EditorRuntimeMode Mode,
    EditorRuntimePlayState PlayState,
    Guid EntityId,
    Guid ComponentId,
    EditorTransformState Transform,
    float DeltaTime,
    float SimulationTime,
    CancellationToken SessionCancellation,
    IEditorAssetResolver Assets,
    IEditorInteractionResolver Interactions);

public readonly record struct EditorPickBounds(
    double CenterX,
    double CenterY,
    double CenterZ,
    double SizeX,
    double SizeY,
    double SizeZ);

public interface IEditorComponentRuntime
{
    void Create(EditorComponentContext context, JsonElement data);
    void Apply(EditorComponentContext context, JsonElement data);
    void Step(EditorComponentContext context);
    void Destroy(EditorComponentContext context);
}

/// <summary>
/// Optional runtime capability for project-rendered component content that should
/// select its owning entity in the Scene view. Bounds are in entity-local space.
/// </summary>
public interface IEditorComponentPickBoundsProvider
{
    EditorPickBounds? GetLocalPickBounds(EditorComponentContext context);
}

public interface IEditorProjectAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }

    void Configure(EditorAdapterBuilder builder);
    void Initialize(EditorProjectRuntimeContext context);
    void Step(EditorProjectRuntimeContext context);
    void Shutdown(EditorProjectRuntimeContext context);
}

public sealed class EditorAdapterBuilder
{
    private readonly Dictionary<string, EditorComponentRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorBindingRegistration> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorActionRegistration> _actions = new(StringComparer.Ordinal);

    public void RegisterComponent(
        EditorComponentDescriptor descriptor,
        Func<IEditorComponentRuntime> factory) =>
        RegisterComponent(descriptor, factory, []);

    public void RegisterComponent(
        EditorComponentDescriptor descriptor,
        Func<IEditorComponentRuntime> factory,
        IReadOnlyList<EditorComponentMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(migrations);
        ValidateDescriptor(descriptor);
        ValidateMigrations(descriptor, migrations);
        if (!_registrations.TryAdd(descriptor.TypeId, new(descriptor, factory, migrations.ToArray())))
        {
            throw new InvalidOperationException($"Component type ID '{descriptor.TypeId}' was registered more than once.");
        }
    }

    public IReadOnlyList<EditorComponentDescriptor> Descriptors =>
        _registrations.Values.Select(registration => registration.Descriptor).ToArray();

    public IReadOnlyList<EditorBindingDescriptor> BindingDescriptors =>
        _bindings.Values.Select(registration => registration.Descriptor).ToArray();

    public IReadOnlyList<EditorActionDescriptor> ActionDescriptors =>
        _actions.Values.Select(registration => registration.Descriptor).ToArray();

    public void RegisterBinding(
        EditorBindingDescriptor descriptor,
        Func<JsonElement> read,
        Action<JsonElement>? write = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(read);
        ValidateStableInteractionId(descriptor.Id, "Binding");
        if (descriptor.Modes == EditorInteractionModes.None)
        {
            throw new ArgumentException($"Binding '{descriptor.Id}' must support Scene Preview, Play, or both.", nameof(descriptor));
        }

        if (!IsCompatibleBindingValue(descriptor.Kind, descriptor.DesignValue))
        {
            throw new ArgumentException($"Binding '{descriptor.Id}' has an incompatible design value.", nameof(descriptor));
        }

        if (descriptor.IsReadOnly && write is not null)
        {
            throw new ArgumentException($"Read-only binding '{descriptor.Id}' cannot register a writer.", nameof(write));
        }

        if (!_bindings.TryAdd(descriptor.Id, new(descriptor, read, write)))
        {
            throw new InvalidOperationException($"Binding ID '{descriptor.Id}' was registered more than once.");
        }
    }

    public void RegisterAction(EditorActionDescriptor descriptor, Action<EditorActionInvocation> invoke)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(invoke);
        ValidateStableInteractionId(descriptor.Id, "Action");
        if (descriptor.Modes == EditorInteractionModes.None)
        {
            throw new ArgumentException($"Action '{descriptor.Id}' must support Scene Preview, Play, or both.", nameof(descriptor));
        }

        if (!_actions.TryAdd(descriptor.Id, new(descriptor, invoke)))
        {
            throw new InvalidOperationException($"Action ID '{descriptor.Id}' was registered more than once.");
        }
    }

    public bool TryGetBinding(string id, out EditorBindingRegistration registration) =>
        _bindings.TryGetValue(id, out registration!);

    public bool TryGetAction(string id, out EditorActionRegistration registration) =>
        _actions.TryGetValue(id, out registration!);

    public bool TryGetRegistration(string typeId, out EditorComponentRegistration registration) =>
        _registrations.TryGetValue(typeId, out registration!);

    public void ValidateRegistrations()
    {
        foreach (var registration in _registrations.Values)
        {
            var descriptor = registration.Descriptor;
            foreach (var requiredTypeId in descriptor.RequiredComponentTypeIds)
            {
                if (!_registrations.ContainsKey(requiredTypeId))
                {
                    throw new InvalidOperationException(
                        $"Component '{descriptor.TypeId}' requires unregistered component '{requiredTypeId}'.");
                }

                if (descriptor.ConflictingComponentTypeIds.Contains(requiredTypeId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Component '{descriptor.TypeId}' both requires and conflicts with '{requiredTypeId}'.");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeId in _registrations.Keys)
        {
            Visit(typeId);
        }

        void Visit(string typeId)
        {
            if (visited.Contains(typeId))
            {
                return;
            }

            if (!visiting.Add(typeId))
            {
                throw new InvalidOperationException($"Component dependency cycle includes '{typeId}'.");
            }

            foreach (var required in _registrations[typeId].Descriptor.RequiredComponentTypeIds)
            {
                Visit(required);
            }

            visiting.Remove(typeId);
            visited.Add(typeId);
        }
    }

    public bool TryMigrate(
        string typeId,
        int fromVersion,
        JsonElement data,
        out int targetVersion,
        out JsonElement migratedData,
        out string? error)
    {
        targetVersion = fromVersion;
        migratedData = data.Clone();
        error = null;
        if (!_registrations.TryGetValue(typeId, out var registration))
        {
            error = $"Component type '{typeId}' is not registered.";
            return false;
        }

        if (fromVersion == registration.Descriptor.SchemaVersion)
        {
            return true;
        }

        if (fromVersion < 1 || fromVersion > registration.Descriptor.SchemaVersion)
        {
            error = $"Stored schema {fromVersion} cannot be migrated to {registration.Descriptor.SchemaVersion}.";
            return false;
        }

        try
        {
            var current = data.Clone();
            var version = fromVersion;
            while (version < registration.Descriptor.SchemaVersion)
            {
                var migration = registration.Migrations.SingleOrDefault(candidate => candidate.FromVersion == version);
                if (migration is null)
                {
                    error = $"No migration is registered from schema {version}.";
                    return false;
                }

                current = migration.Upgrade(current).Clone();
                if (current.ValueKind != JsonValueKind.Object)
                {
                    error = $"Migration {migration.FromVersion} → {migration.ToVersion} did not return a JSON object.";
                    return false;
                }

                version = migration.ToVersion;
            }

            targetVersion = version;
            migratedData = current;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void ValidateDescriptor(EditorComponentDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.TypeId)
            || !descriptor.TypeId.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("Component TypeId must be a stable, dotted identifier.", nameof(descriptor));
        }

        if (descriptor.SchemaVersion < 1)
        {
            throw new ArgumentException("Component SchemaVersion must be at least 1.", nameof(descriptor));
        }

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
        {
            throw new ArgumentException("Component DisplayName is required.", nameof(descriptor));
        }

        if (descriptor.Modes == EditorComponentModes.None)
        {
            throw new ArgumentException(
                $"Component '{descriptor.TypeId}' must support Scene, Play, or both.",
                nameof(descriptor));
        }

        if (descriptor.DefaultData.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Component '{descriptor.TypeId}' DefaultData must be a JSON object.",
                nameof(descriptor));
        }

        ValidateTypeIdList(descriptor, descriptor.RequiredComponentTypeIds, "required");
        ValidateTypeIdList(descriptor, descriptor.ConflictingComponentTypeIds, "conflicting");

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in descriptor.Properties)
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !propertyNames.Add(property.Name))
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' has an empty or duplicate property name.",
                    nameof(descriptor));
            }

            if (property.Minimum is { } minimum
                && property.Maximum is { } maximum
                && minimum > maximum)
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' has a minimum greater than its maximum.",
                    nameof(descriptor));
            }

            if (property.Increment is <= 0)
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' must have a positive increment.",
                    nameof(descriptor));
            }

            if (property.Kind is EditorPropertyKind.Enum or EditorPropertyKind.Flags
                && (property.Options.Count == 0
                    || property.Options.Distinct(StringComparer.Ordinal).Count() != property.Options.Count))
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' requires unique options.",
                    nameof(descriptor));
            }

            if (property.Presentation == EditorPropertyPresentation.Slider
                && (property.Kind is not (EditorPropertyKind.Integer or EditorPropertyKind.Number)
                    || property.Minimum is null
                    || property.Maximum is null))
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' requires numeric minimum and maximum values for a Slider presentation.",
                    nameof(descriptor));
            }

            if (property.Presentation == EditorPropertyPresentation.MultilineText
                && property.Kind != EditorPropertyKind.String)
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' can use MultilineText only with String data.",
                    nameof(descriptor));
            }

            if (property.Kind != EditorPropertyKind.AssetReference && property.AcceptedAssetKinds.Count > 0)
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' can filter asset kinds only for AssetReference data.",
                    nameof(descriptor));
            }

            if (property.AcceptedAssetKinds.Any(string.IsNullOrWhiteSpace)
                || property.AcceptedAssetKinds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != property.AcceptedAssetKinds.Count)
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' has invalid or duplicate accepted asset kinds.",
                    nameof(descriptor));
            }

            if (descriptor.DefaultData.TryGetProperty(property.Name, out var defaultValue)
                && !IsCompatibleDefault(property, defaultValue))
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' property '{property.Name}' has an incompatible default JSON value.",
                    nameof(descriptor));
            }
        }
    }

    private static void ValidateTypeIdList(
        EditorComponentDescriptor descriptor,
        IReadOnlyList<string> typeIds,
        string label)
    {
        if (typeIds.Any(typeId => string.IsNullOrWhiteSpace(typeId)
                || !typeId.Contains('.', StringComparison.Ordinal)
                || string.Equals(typeId, descriptor.TypeId, StringComparison.Ordinal))
            || typeIds.Distinct(StringComparer.Ordinal).Count() != typeIds.Count)
        {
            throw new ArgumentException(
                $"Component '{descriptor.TypeId}' has invalid or duplicate {label} component type IDs.",
                nameof(descriptor));
        }
    }

    private static void ValidateMigrations(
        EditorComponentDescriptor descriptor,
        IReadOnlyList<EditorComponentMigration> migrations)
    {
        var duplicateFromVersion = migrations
            .GroupBy(migration => migration.FromVersion)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFromVersion is not null)
        {
            throw new ArgumentException(
                $"Component '{descriptor.TypeId}' registers schema {duplicateFromVersion.Key} more than once.",
                nameof(migrations));
        }

        foreach (var migration in migrations)
        {
            if (migration.FromVersion < 1
                || migration.ToVersion != migration.FromVersion + 1
                || migration.ToVersion > descriptor.SchemaVersion)
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' migrations must advance exactly one version and end no later than schema {descriptor.SchemaVersion}.",
                    nameof(migrations));
            }
        }

        if (descriptor.SchemaVersion > 1)
        {
            var expected = Enumerable.Range(1, descriptor.SchemaVersion - 1);
            if (!expected.SequenceEqual(migrations.Select(migration => migration.FromVersion).Order()))
            {
                throw new ArgumentException(
                    $"Component '{descriptor.TypeId}' must register a complete migration chain from schema 1 to {descriptor.SchemaVersion}.",
                    nameof(migrations));
            }
        }
    }

    private static bool IsCompatibleDefault(EditorPropertyDescriptor property, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return !property.IsRequired
                && property.Kind is EditorPropertyKind.AssetReference or EditorPropertyKind.EntityReference;
        }

        return property.Kind switch
    {
        EditorPropertyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        EditorPropertyKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        EditorPropertyKind.Number => value.ValueKind == JsonValueKind.Number,
        EditorPropertyKind.String or EditorPropertyKind.Enum or EditorPropertyKind.Flags
            or EditorPropertyKind.AssetReference or EditorPropertyKind.EntityReference =>
            value.ValueKind == JsonValueKind.String,
        EditorPropertyKind.Vector2 => IsNumberArray(value, 2),
        EditorPropertyKind.Vector3 => IsNumberArray(value, 3),
        EditorPropertyKind.Vector4 or EditorPropertyKind.Quaternion or EditorPropertyKind.Color =>
            IsNumberArray(value, 4),
        _ => false,
    };
    }

    private static bool IsNumberArray(JsonElement value, int count) =>
        value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() == count
        && value.EnumerateArray().All(element => element.ValueKind == JsonValueKind.Number);

    private static void ValidateStableInteractionId(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException($"{label} IDs must be stable, dotted identifiers.", nameof(id));
        }
    }

    private static bool IsCompatibleBindingValue(EditorBindingValueKind kind, JsonElement value) => kind switch
    {
        EditorBindingValueKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        EditorBindingValueKind.Number => value.ValueKind == JsonValueKind.Number,
        EditorBindingValueKind.String => value.ValueKind == JsonValueKind.String,
        _ => false,
    };
}

public sealed record EditorComponentRegistration(
    EditorComponentDescriptor Descriptor,
    Func<IEditorComponentRuntime> Factory,
    IReadOnlyList<EditorComponentMigration> Migrations)
{
    public EditorComponentRegistration(
        EditorComponentDescriptor descriptor,
        Func<IEditorComponentRuntime> factory)
        : this(descriptor, factory, [])
    {
    }
}

public sealed record EditorBindingRegistration(
    EditorBindingDescriptor Descriptor,
    Func<JsonElement> Read,
    Action<JsonElement>? Write);

public sealed record EditorActionRegistration(
    EditorActionDescriptor Descriptor,
    Action<EditorActionInvocation> Invoke);

public sealed class EmptyEditorProjectAdapter(string displayName = "StereoKit Project") : IEditorProjectAdapter
{
    public string Id => "stereokit.empty-adapter";
    public string DisplayName { get; } = displayName;
    public string Version => AdapterContractVersion.Current;

    public void Configure(EditorAdapterBuilder builder)
    {
    }

    public void Initialize(EditorProjectRuntimeContext context)
    {
    }

    public void Step(EditorProjectRuntimeContext context)
    {
    }

    public void Shutdown(EditorProjectRuntimeContext context)
    {
    }
}

internal static class CatalogJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
