using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StereoKitEditor.Scene;

public static class SceneSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(SceneDocument document) =>
        JsonSerializer.Serialize(document, Options) + Environment.NewLine;

    public static SceneDocument Deserialize(string json) => DeserializeWithMetadata(json).Document;

    public static SceneDeserializationResult DeserializeWithMetadata(string json)
    {
        using var source = JsonDocument.Parse(json);
        if (!source.RootElement.TryGetProperty("formatVersion", out var formatProperty)
            || !formatProperty.TryGetInt32(out var formatVersion))
        {
            throw new JsonException("The scene document does not declare formatVersion.");
        }

        var migrated = formatVersion == 1;
        var effectiveJson = migrated ? MigrateFormat1(json) : json;
        if (formatVersion is not (1 or SceneDocument.CurrentFormatVersion))
        {
            throw new NotSupportedException(
                $"Scene format {formatVersion} is not supported; expected 1 or {SceneDocument.CurrentFormatVersion}.");
        }

        var document = JsonSerializer.Deserialize<SceneDocument>(effectiveJson, Options)
            ?? throw new JsonException("The scene document was empty.");
        if (document.FormatVersion != SceneDocument.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"Scene format {document.FormatVersion} is not supported; expected {SceneDocument.CurrentFormatVersion}.");
        }

        EnsureUniqueIds(document);
        return new(document, migrated);
    }

    public static SceneDocument Clone(SceneDocument document) => Deserialize(Serialize(document));

    public static async Task SaveAtomicAsync(
        SceneDocument document,
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("A scene path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, Serialize(document), cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<SceneDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        (await LoadWithMetadataAsync(path, cancellationToken)).Document;

    public static async Task<SceneDeserializationResult> LoadWithMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return DeserializeWithMetadata(json);
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string MigrateFormat1(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new JsonException("The scene document was empty.");
        root["formatVersion"] = SceneDocument.CurrentFormatVersion;
        if (root["roots"] is JsonArray roots)
        {
            foreach (var entity in roots.OfType<JsonObject>())
            {
                MigrateEntity(entity);
            }
        }

        return root.ToJsonString(Options);
    }

    private static void MigrateEntity(JsonObject entity)
    {
        var entityId = entity["id"]?.GetValue<Guid>()
            ?? throw new JsonException("A legacy scene entity is missing its ID.");
        if (entity["components"] is not JsonObject components)
        {
            throw new JsonException($"Legacy entity '{entityId}' has no component object.");
        }

        var migrated = new JsonArray();
        foreach (var property in components)
        {
            if (property.Value is null)
            {
                continue;
            }

            var typeId = property.Key switch
            {
                "transform" => BuiltInComponentTypes.Transform,
                "primitiveMeshRenderer" => BuiltInComponentTypes.PrimitiveMeshRenderer,
                _ => property.Key,
            };
            migrated.Add(new JsonObject
            {
                ["id"] = CreateStableComponentId(entityId, typeId),
                ["typeId"] = typeId,
                ["schemaVersion"] = 1,
                ["enabled"] = true,
                ["data"] = property.Value.DeepClone(),
            });
        }

        entity["components"] = migrated;
        if (entity["children"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                MigrateEntity(child);
            }
        }
    }

    private static Guid CreateStableComponentId(Guid entityId, string typeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{entityId:N}:{typeId}"));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static void EnsureUniqueIds(SceneDocument document)
    {
        var entityIds = new HashSet<Guid>();
        var componentIds = new HashSet<Guid>();
        foreach (var entity in document.Traverse())
        {
            if (entity.Id == Guid.Empty)
            {
                throw new JsonException("Entity IDs cannot be empty.");
            }

            if (!entityIds.Add(entity.Id))
            {
                throw new JsonException($"Duplicate entity ID '{entity.Id}'.");
            }

            var transformCount = 0;
            foreach (var component in entity.Components.Records)
            {
                if (component.Id == Guid.Empty || !componentIds.Add(component.Id))
                {
                    throw new JsonException($"Component ID '{component.Id}' is empty or duplicated.");
                }

                if (string.IsNullOrWhiteSpace(component.TypeId) || component.SchemaVersion < 1)
                {
                    throw new JsonException($"Component '{component.Id}' has an invalid type ID or schema version.");
                }

                if (component.TypeId == BuiltInComponentTypes.Transform)
                {
                    transformCount++;
                }
            }

            if (transformCount != 1)
            {
                throw new JsonException($"Entity '{entity.Id}' must contain exactly one Transform component.");
            }
        }
    }
}

public sealed record SceneDeserializationResult(SceneDocument Document, bool MigratedFromFormat1);
