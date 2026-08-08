using System.Text.Json;
using System.Text.Json.Serialization;

namespace StereoKitEditor.Scene;

public static partial class BuiltInComponentTypes
{
    public const string Transform = "stereokit.transform";
    public const string PrimitiveMeshRenderer = "stereokit.primitive-mesh-renderer";
    public const string ModelRenderer = "stereokit.model-renderer";
    public const string EnvironmentSettings = "stereokit.environment-settings";
    public const string EditorAnnotation = "stereokit.editor-annotation";
}

public sealed class SceneDocument
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public Guid SceneId { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Main";
    public List<SceneEntity> Roots { get; init; } = [];

    public SceneEntity? FindEntity(Guid id)
    {
        foreach (var root in Roots)
        {
            var match = root.Find(id);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public IEnumerable<SceneEntity> Traverse()
    {
        foreach (var root in Roots)
        {
            foreach (var entity in root.Traverse())
            {
                yield return entity;
            }
        }
    }
}

public sealed class SceneEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Entity";
    public bool Enabled { get; set; } = true;
    public EntityComponents Components { get; init; } = new();
    public List<SceneEntity> Children { get; init; } = [];

    public SceneEntity? Find(Guid id)
    {
        if (Id == id)
        {
            return this;
        }

        foreach (var child in Children)
        {
            var match = child.Find(id);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public IEnumerable<SceneEntity> Traverse()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Traverse())
            {
                yield return descendant;
            }
        }
    }
}

[JsonConverter(typeof(EntityComponentsJsonConverter))]
public sealed partial class EntityComponents
{
    private readonly List<SceneComponentRecord> _records;

    public EntityComponents()
        : this([SceneComponentRecord.Create(BuiltInComponentTypes.Transform, TransformComponent.Identity)])
    {
    }

    private EntityComponents(IEnumerable<SceneComponentRecord> records) => _records = [.. records];

    public IReadOnlyList<SceneComponentRecord> Records => _records;

    public TransformComponent Transform
    {
        get => GetData<TransformComponent>(BuiltInComponentTypes.Transform) ?? TransformComponent.Identity;
        set => SetData(BuiltInComponentTypes.Transform, value);
    }

    public PrimitiveMeshRendererComponent? PrimitiveMeshRenderer
    {
        get => GetData<PrimitiveMeshRendererComponent>(BuiltInComponentTypes.PrimitiveMeshRenderer);
        set
        {
            if (value is null)
            {
                RemoveByType(BuiltInComponentTypes.PrimitiveMeshRenderer);
            }
            else
            {
                SetData(BuiltInComponentTypes.PrimitiveMeshRenderer, value, schemaVersion: 2);
            }
        }
    }

    public ModelRendererComponent? ModelRenderer
    {
        get => GetData<ModelRendererComponent>(BuiltInComponentTypes.ModelRenderer);
        set
        {
            if (value is null)
            {
                RemoveByType(BuiltInComponentTypes.ModelRenderer);
            }
            else
            {
                SetData(BuiltInComponentTypes.ModelRenderer, value);
            }
        }
    }

    public EnvironmentSettingsComponent? EnvironmentSettings
    {
        get => GetData<EnvironmentSettingsComponent>(BuiltInComponentTypes.EnvironmentSettings);
        set
        {
            if (value is null)
            {
                RemoveByType(BuiltInComponentTypes.EnvironmentSettings);
            }
            else
            {
                SetData(BuiltInComponentTypes.EnvironmentSettings, value);
            }
        }
    }

    public EditorAnnotationComponent? EditorAnnotation
    {
        get => GetData<EditorAnnotationComponent>(BuiltInComponentTypes.EditorAnnotation);
        set
        {
            if (value is null)
            {
                RemoveByType(BuiltInComponentTypes.EditorAnnotation);
            }
            else
            {
                SetData(BuiltInComponentTypes.EditorAnnotation, value);
            }
        }
    }

    public SceneComponentRecord? Find(Guid componentId) =>
        _records.FirstOrDefault(component => component.Id == componentId);

    public SceneComponentRecord? FindByType(string typeId) =>
        _records.FirstOrDefault(component => string.Equals(component.TypeId, typeId, StringComparison.Ordinal));

    public void Add(SceneComponentRecord component, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Id == Guid.Empty || Find(component.Id) is not null)
        {
            throw new InvalidOperationException($"Component ID '{component.Id}' is empty or duplicated on the entity.");
        }

        var insertAt = index is null ? _records.Count : Math.Clamp(index.Value, 0, _records.Count);
        _records.Insert(insertAt, component);
    }

    public bool Remove(Guid componentId) =>
        _records.RemoveAll(component => component.Id == componentId) > 0;

    internal static EntityComponents FromRecords(IEnumerable<SceneComponentRecord> records) => new(records);

    private T? GetData<T>(string typeId)
    {
        var record = FindByType(typeId);
        return record is null ? default : record.Data.Deserialize<T>(SceneSerializer.Options);
    }

    private void SetData<T>(string typeId, T value, int schemaVersion = 1)
    {
        var data = JsonSerializer.SerializeToElement(value, SceneSerializer.Options);
        var record = FindByType(typeId);
        if (record is null)
        {
            _records.Add(new SceneComponentRecord
            {
                TypeId = typeId,
                SchemaVersion = schemaVersion,
                Data = data,
            });
        }
        else
        {
            record.SchemaVersion = schemaVersion;
            record.Data = data;
        }
    }

    private void RemoveByType(string typeId) =>
        _records.RemoveAll(component => string.Equals(component.TypeId, typeId, StringComparison.Ordinal));
}

public sealed record SceneComponentRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TypeId { get; init; }
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public JsonElement Data { get; set; } = JsonSerializer.SerializeToElement(new { });

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public static SceneComponentRecord Create<T>(string typeId, T data) => new()
    {
        TypeId = typeId,
        Data = JsonSerializer.SerializeToElement(data, SceneSerializer.Options),
    };
}

public sealed record TransformComponent(
    Vector3Value Position,
    QuaternionValue Rotation,
    Vector3Value Scale)
{
    public static TransformComponent Identity { get; } = new(
        Vector3Value.Zero,
        QuaternionValue.Identity,
        Vector3Value.One);
}

public sealed record PrimitiveMeshRendererComponent
{
    public PrimitiveKind Primitive { get; init; } = PrimitiveKind.Cube;
    public Guid? MaterialAssetId { get; init; }
    public Guid? BaseColorTextureOverrideId { get; init; }
    public ColorValue Color { get; init; } = new(0.22, 0.64, 0.92, 1);
    public Vector2Value UvScale { get; init; } = Vector2Value.One;
    public Vector2Value UvOffset { get; init; } = Vector2Value.Zero;
    public bool Visible { get; init; } = true;
}

public sealed record ModelRendererComponent
{
    public Guid AssetId { get; init; }
    public Guid? MaterialAssetId { get; init; }
    public Dictionary<string, Guid> MaterialOverrides { get; init; } = new(StringComparer.Ordinal);
    public bool Visible { get; init; } = true;
    public bool FitToBounds { get; init; } = true;
    public double MaximumSize { get; init; } = 0.5;
}

public sealed record EnvironmentSettingsComponent
{
    public ColorValue ClearColor { get; init; } = new(0.08, 0.09, 0.11, 1);
    public bool FloorVisible { get; init; } = true;
    public ColorValue FloorColor { get; init; } = new(0.15, 0.17, 0.20, 1);
}

public sealed record EditorAnnotationComponent
{
    public string Label { get; init; } = "Note";
    public ColorValue Color { get; init; } = new(1, 0.72, 0.22, 1);
    public bool Visible { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<PrimitiveKind>))]
public enum PrimitiveKind
{
    Cube,
    Sphere,
    Quad,
}

[JsonConverter(typeof(Vector3ValueJsonConverter))]
public readonly record struct Vector3Value(double X, double Y, double Z)
{
    public static Vector3Value Zero => new(0, 0, 0);
    public static Vector3Value One => new(1, 1, 1);
}

[JsonConverter(typeof(QuaternionValueJsonConverter))]
public readonly record struct QuaternionValue(double X, double Y, double Z, double W)
{
    public static QuaternionValue Identity => new(0, 0, 0, 1);
}

[JsonConverter(typeof(ColorValueJsonConverter))]
public readonly record struct ColorValue(double R, double G, double B, double A);

internal sealed class EntityComponentsJsonConverter : JsonConverter<EntityComponents>
{
    public override EntityComponents Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Scene format 2 components must be an array.");
        }

        var records = JsonSerializer.Deserialize<List<SceneComponentRecord>>(ref reader, options)
            ?? throw new JsonException("The component array was empty.");
        return EntityComponents.FromRecords(records);
    }

    public override void Write(Utf8JsonWriter writer, EntityComponents value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Records, options);
}

internal sealed class Vector3ValueJsonConverter : JsonConverter<Vector3Value>
{
    public override Vector3Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = ReadNumbers(ref reader, 3);
        return new(values[0], values[1], values[2]);
    }

    public override void Write(Utf8JsonWriter writer, Vector3Value value, JsonSerializerOptions options) =>
        WriteNumbers(writer, value.X, value.Y, value.Z);

    internal static double[] ReadNumbers(ref Utf8JsonReader reader, int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array.");
        }

        var result = new double[count];
        for (var index = 0; index < count; index++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Expected {count} numeric values.");
            }

            result[index] = reader.GetDouble();
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException($"Expected exactly {count} numeric values.");
        }

        return result;
    }

    internal static void WriteNumbers(Utf8JsonWriter writer, params double[] values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }
}

internal sealed class QuaternionValueJsonConverter : JsonConverter<QuaternionValue>
{
    public override QuaternionValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = Vector3ValueJsonConverter.ReadNumbers(ref reader, 4);
        return new(values[0], values[1], values[2], values[3]);
    }

    public override void Write(Utf8JsonWriter writer, QuaternionValue value, JsonSerializerOptions options) =>
        Vector3ValueJsonConverter.WriteNumbers(writer, value.X, value.Y, value.Z, value.W);
}

internal sealed class ColorValueJsonConverter : JsonConverter<ColorValue>
{
    public override ColorValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = Vector3ValueJsonConverter.ReadNumbers(ref reader, 4);
        return new(values[0], values[1], values[2], values[3]);
    }

    public override void Write(Utf8JsonWriter writer, ColorValue value, JsonSerializerOptions options) =>
        Vector3ValueJsonConverter.WriteNumbers(writer, value.R, value.G, value.B, value.A);
}
