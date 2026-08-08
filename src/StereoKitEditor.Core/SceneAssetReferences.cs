using System.Text.Json;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Core;

public static class SceneAssetReferences
{
    public static IReadOnlyList<SceneEntity> Find(SceneDocument document, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Traverse()
            .Where(entity => entity.Components.Records.Any(component => ContainsGuid(component.Data, assetId)))
            .ToArray();
    }

    private static bool ContainsGuid(JsonElement element, Guid value) => element.ValueKind switch
    {
        JsonValueKind.String => Guid.TryParse(element.GetString(), out var parsed) && parsed == value,
        JsonValueKind.Array => element.EnumerateArray().Any(child => ContainsGuid(child, value)),
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsGuid(property.Value, value)),
        _ => false,
    };
}
