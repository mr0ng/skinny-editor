using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class LargeProjectTests
{
    [Fact]
    public void FiveThousandEntityEdit_ProducesOnePatchAndRoundTrips()
    {
        const int count = 5_000;
        var original = new SceneDocument
        {
            Name = "Large fixture",
            Roots = Enumerable.Range(0, count)
                .Select(index => new SceneEntity { Name = $"Entity {index:00000}" })
                .ToList(),
        };
        var current = SceneSerializer.Clone(original);
        current.Roots[count - 1].Name = "Renamed tail";

        Assert.True(SceneChangeSetBuilder.TryCreate(original, 10, current, 11, out var changeSet));
        Assert.NotNull(changeSet);
        Assert.Null(changeSet.Hierarchy);
        Assert.Empty(changeSet.RemovedEntityIds);
        Assert.Equal(current.Roots[count - 1].Id, Assert.Single(changeSet.UpsertedEntities).Id);
        Assert.True(SceneChangeSetApplier.TryApply(original, 10, changeSet, out var applied, out var error), error);
        Assert.Equal(count, applied!.Roots.Count);
        Assert.Equal("Renamed tail", applied.Roots[count - 1].Name);

        var json = SceneSerializer.Serialize(applied);
        Assert.Equal(json, SceneSerializer.Serialize(SceneSerializer.Deserialize(json)));
    }
}
