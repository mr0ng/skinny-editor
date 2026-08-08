using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace StereoKitEditor.Assets;

internal sealed record GlbInspection(
    AssetBounds? Bounds,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<AssetDiagnostic> Diagnostics,
    ModelAssetMetadata? Model = null);

internal static class GlbMetadataReader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunkType = 0x4E4F534A;

    public static GlbInspection Inspect(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != GlbMagic)
        {
            throw new InvalidDataException("The file does not have a glTF binary header.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != 2)
        {
            throw new InvalidDataException($"Unsupported GLB version {version}; expected glTF 2.0.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (declaredLength < 20 || (stream.CanSeek && declaredLength > stream.Length))
        {
            throw new InvalidDataException("The GLB declares an invalid file length.");
        }

        byte[]? json = null;
        var consumed = 12u;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (consumed + 8 <= declaredLength)
        {
            ReadExactly(stream, chunkHeader);
            consumed += 8;
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            if (chunkLength > declaredLength - consumed || chunkLength > int.MaxValue)
            {
                throw new InvalidDataException("The GLB contains a chunk with an invalid length.");
            }

            var data = new byte[(int)chunkLength];
            ReadExactly(stream, data);
            consumed += chunkLength;
            if (chunkType == JsonChunkType && json is null)
            {
                json = data;
            }
        }

        if (json is null)
        {
            throw new InvalidDataException("The GLB does not contain a JSON scene chunk.");
        }

        var jsonLength = json.Length;
        while (jsonLength > 0 && json[jsonLength - 1] is 0 or 0x20)
        {
            jsonLength--;
        }

        using var document = JsonDocument.Parse(json.AsMemory(0, jsonLength));
        var root = document.RootElement;
        if (!root.TryGetProperty("asset", out var asset)
            || !asset.TryGetProperty("version", out var assetVersion)
            || assetVersion.GetString() is not { } versionText
            || !versionText.StartsWith("2", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The GLB JSON does not declare glTF asset version 2.");
        }

        var dependencies = ReadDependencies(root);
        var bounds = ReadSceneBounds(root);
        var diagnostics = bounds is null
            ? new[]
            {
                new AssetDiagnostic(
                    AssetDiagnosticSeverity.Warning,
                    "SKINNY-ASSET-BOUNDS-MISSING",
                    "The model did not provide POSITION accessor bounds.",
                    "Re-export the GLB with accessor min/max metadata for accurate picking and framing."),
            }
            : [];
        return new(bounds, dependencies, diagnostics, ReadModelMetadata(root));
    }

    private static ModelAssetMetadata ReadModelMetadata(JsonElement root)
    {
        var materialNames = root.TryGetProperty("materials", out var materials)
            && materials.ValueKind == JsonValueKind.Array
            ? materials.EnumerateArray()
                .Select((material, index) => material.TryGetProperty("name", out var name)
                    && !string.IsNullOrWhiteSpace(name.GetString())
                        ? name.GetString()!
                        : $"Material {index + 1}")
                .ToArray()
            : [];
        var slots = new List<ModelMaterialSlot>();
        if (root.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
        {
            foreach (var mesh in meshes.EnumerateArray())
            {
                if (!mesh.TryGetProperty("primitives", out var primitives)
                    || primitives.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var primitive in primitives.EnumerateArray())
                {
                    var materialIndex = primitive.TryGetProperty("material", out var material)
                        && material.TryGetInt32(out var index)
                        ? index
                        : -1;
                    var name = materialIndex >= 0 && materialIndex < materialNames.Length
                        ? materialNames[materialIndex]
                        : $"Visual {slots.Count + 1}";
                    slots.Add(new(slots.Count, name));
                }
            }
        }

        return new() { MaterialSlots = slots };
    }

    private static IReadOnlyList<string> ReadDependencies(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddUris(root, "buffers", result);
        AddUris(root, "images", result);
        return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddUris(JsonElement root, string collectionName, ISet<string> result)
    {
        if (!root.TryGetProperty(collectionName, out var collection)
            || collection.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in collection.EnumerateArray())
        {
            if (!item.TryGetProperty("uri", out var uriElement)
                || uriElement.GetString() is not { Length: > 0 } uri
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || Uri.TryCreate(uri, UriKind.Absolute, out _))
            {
                continue;
            }

            result.Add(Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
        }
    }

    private static AssetBounds? ReadSceneBounds(JsonElement root)
    {
        var accessors = ReadAccessorBounds(root);
        var meshBounds = ReadMeshBounds(root, accessors);
        if (meshBounds.Count == 0)
        {
            return null;
        }

        if (!root.TryGetProperty("nodes", out var nodesElement)
            || nodesElement.ValueKind != JsonValueKind.Array
            || nodesElement.GetArrayLength() == 0)
        {
            return ToAssetBounds(meshBounds.Values.Aggregate(Bounds3.Empty, (sum, value) => sum.Union(value)));
        }

        var nodes = nodesElement.EnumerateArray().Select(ReadNode).ToArray();
        var roots = ReadRootNodes(root, nodes);
        var worldBounds = Bounds3.Empty;
        foreach (var rootIndex in roots)
        {
            worldBounds = TraverseNode(rootIndex, Matrix4x4.Identity, nodes, meshBounds, [], worldBounds);
        }

        return worldBounds.IsEmpty
            ? ToAssetBounds(meshBounds.Values.Aggregate(Bounds3.Empty, (sum, value) => sum.Union(value)))
            : ToAssetBounds(worldBounds);
    }

    private static IReadOnlyDictionary<int, Bounds3> ReadAccessorBounds(JsonElement root)
    {
        var result = new Dictionary<int, Bounds3>();
        if (!root.TryGetProperty("accessors", out var accessors)
            || accessors.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var index = 0;
        foreach (var accessor in accessors.EnumerateArray())
        {
            if (accessor.TryGetProperty("min", out var minimum)
                && accessor.TryGetProperty("max", out var maximum)
                && TryReadVector3(minimum, out var min)
                && TryReadVector3(maximum, out var max))
            {
                result[index] = new(min, max);
            }

            index++;
        }

        return result;
    }

    private static IReadOnlyDictionary<int, Bounds3> ReadMeshBounds(
        JsonElement root,
        IReadOnlyDictionary<int, Bounds3> accessors)
    {
        var result = new Dictionary<int, Bounds3>();
        if (!root.TryGetProperty("meshes", out var meshes)
            || meshes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var meshIndex = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            var bounds = Bounds3.Empty;
            if (mesh.TryGetProperty("primitives", out var primitives)
                && primitives.ValueKind == JsonValueKind.Array)
            {
                foreach (var primitive in primitives.EnumerateArray())
                {
                    if (primitive.TryGetProperty("attributes", out var attributes)
                        && attributes.TryGetProperty("POSITION", out var position)
                        && position.TryGetInt32(out var accessorIndex)
                        && accessors.TryGetValue(accessorIndex, out var primitiveBounds))
                    {
                        bounds = bounds.Union(primitiveBounds);
                    }
                }
            }

            if (!bounds.IsEmpty)
            {
                result[meshIndex] = bounds;
            }

            meshIndex++;
        }

        return result;
    }

    private static NodeDefinition ReadNode(JsonElement node)
    {
        var mesh = node.TryGetProperty("mesh", out var meshElement) && meshElement.TryGetInt32(out var meshIndex)
            ? meshIndex
            : (int?)null;
        var children = node.TryGetProperty("children", out var childrenElement)
            && childrenElement.ValueKind == JsonValueKind.Array
            ? childrenElement.EnumerateArray().Select(child => child.GetInt32()).ToArray()
            : [];
        return new(mesh, children, ReadLocalMatrix(node));
    }

    private static Matrix4x4 ReadLocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrixElement)
            && matrixElement.ValueKind == JsonValueKind.Array
            && matrixElement.GetArrayLength() == 16)
        {
            var values = matrixElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            return new(
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
                values[12], values[13], values[14], values[15]);
        }

        var scale = node.TryGetProperty("scale", out var scaleElement) && TryReadVector3(scaleElement, out var scaleValue)
            ? scaleValue
            : Vector3.One;
        var translation = node.TryGetProperty("translation", out var translationElement)
            && TryReadVector3(translationElement, out var translationValue)
            ? translationValue
            : Vector3.Zero;
        var rotation = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out var rotationElement)
            && rotationElement.ValueKind == JsonValueKind.Array
            && rotationElement.GetArrayLength() == 4)
        {
            var values = rotationElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            rotation = Quaternion.Normalize(new(values[0], values[1], values[2], values[3]));
        }

        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
    }

    private static IReadOnlyList<int> ReadRootNodes(JsonElement root, IReadOnlyList<NodeDefinition> nodes)
    {
        if (root.TryGetProperty("scenes", out var scenes)
            && scenes.ValueKind == JsonValueKind.Array
            && scenes.GetArrayLength() > 0)
        {
            var sceneIndex = root.TryGetProperty("scene", out var sceneElement)
                && sceneElement.TryGetInt32(out var selectedScene)
                ? Math.Clamp(selectedScene, 0, scenes.GetArrayLength() - 1)
                : 0;
            var scene = scenes[sceneIndex];
            if (scene.TryGetProperty("nodes", out var sceneNodes)
                && sceneNodes.ValueKind == JsonValueKind.Array)
            {
                return sceneNodes.EnumerateArray().Select(item => item.GetInt32()).ToArray();
            }
        }

        var children = nodes.SelectMany(node => node.Children).ToHashSet();
        return Enumerable.Range(0, nodes.Count).Where(index => !children.Contains(index)).ToArray();
    }

    private static Bounds3 TraverseNode(
        int nodeIndex,
        Matrix4x4 parent,
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyDictionary<int, Bounds3> meshBounds,
        HashSet<int> stack,
        Bounds3 aggregate)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Count || !stack.Add(nodeIndex))
        {
            return aggregate;
        }

        var node = nodes[nodeIndex];
        var world = node.LocalTransform * parent;
        if (node.Mesh is { } mesh && meshBounds.TryGetValue(mesh, out var localBounds))
        {
            aggregate = aggregate.Union(localBounds.Transform(world));
        }

        foreach (var child in node.Children)
        {
            aggregate = TraverseNode(child, world, nodes, meshBounds, stack, aggregate);
        }

        stack.Remove(nodeIndex);
        return aggregate;
    }

    private static AssetBounds? ToAssetBounds(Bounds3 bounds)
    {
        if (bounds.IsEmpty)
        {
            return null;
        }

        var center = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var size = bounds.Maximum - bounds.Minimum;
        return new(center.X, center.Y, center.Z, size.X, size.Y, size.Z);
    }

    private static bool TryReadVector3(JsonElement value, out Vector3 result)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 3)
        {
            var numbers = value.EnumerateArray().Take(3).Select(item => item.GetSingle()).ToArray();
            result = new(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        result = default;
        return false;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException("The GLB ended unexpectedly.");
            }

            total += read;
        }
    }

    private sealed record NodeDefinition(int? Mesh, IReadOnlyList<int> Children, Matrix4x4 LocalTransform);

    private readonly record struct Bounds3(Vector3 Minimum, Vector3 Maximum)
    {
        public static Bounds3 Empty => new(
            new Vector3(float.PositiveInfinity),
            new Vector3(float.NegativeInfinity));

        public bool IsEmpty => float.IsPositiveInfinity(Minimum.X);

        public Bounds3 Union(Bounds3 other) => other.IsEmpty
            ? this
            : IsEmpty
                ? other
                : new(Vector3.Min(Minimum, other.Minimum), Vector3.Max(Maximum, other.Maximum));

        public Bounds3 Transform(Matrix4x4 matrix)
        {
            var result = Empty;
            foreach (var x in new[] { Minimum.X, Maximum.X })
            foreach (var y in new[] { Minimum.Y, Maximum.Y })
            foreach (var z in new[] { Minimum.Z, Maximum.Z })
            {
                var point = Vector3.Transform(new Vector3(x, y, z), matrix);
                result = result.Union(new(point, point));
            }

            return result;
        }
    }
}
