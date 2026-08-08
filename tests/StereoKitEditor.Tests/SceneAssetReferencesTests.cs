using StereoKitEditor.Core;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class SceneAssetReferencesTests
{
    [Fact]
    public void Find_DetectsBuiltInAndNestedCustomGuidReferencesWithoutSubstringMatches()
    {
        var assetId = Guid.NewGuid();
        var custom = new SceneEntity { Name = "Custom" };
        custom.Components.Add(SceneComponentRecord.Create(
            "example.custom",
            new { nested = new[] { assetId.ToString("D") } }));
        var unrelated = new SceneEntity { Name = "Unrelated" };
        unrelated.Components.Add(SceneComponentRecord.Create(
            "example.text",
            new { value = $"prefix-{assetId:D}" }));
        var document = new SceneDocument
        {
            Roots =
            [
                new SceneEntity
                {
                    Name = "Built in",
                    Components = { ModelRenderer = new() { AssetId = assetId } },
                },
                custom,
                unrelated,
            ],
        };

        var matches = SceneAssetReferences.Find(document, assetId);

        Assert.Equal(["Built in", "Custom"], matches.Select(entity => entity.Name));
    }
}
