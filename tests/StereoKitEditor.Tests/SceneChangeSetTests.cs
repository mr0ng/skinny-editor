using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class SceneChangeSetTests
{
    [Fact]
    public void CreateAndApply_DataAndHierarchyChanges_ReconstructsTargetScene()
    {
        var child = Entity("Child");
        var first = Entity("First", child);
        var second = Entity("Second");
        var previous = new SceneDocument
        {
            Name = "Before",
            Roots = { first, second },
        };
        var current = SceneSerializer.Clone(previous);
        var currentFirst = current.FindEntity(first.Id)!;
        var currentSecond = current.FindEntity(second.Id)!;
        current.Name = "After";
        currentFirst.Name = "First renamed";
        currentFirst.Children.Clear();
        current.Roots.Clear();
        current.Roots.Add(currentSecond);
        current.Roots.Add(currentFirst);
        currentSecond.Children.Add(Entity("New child"));

        Assert.True(SceneChangeSetBuilder.TryCreate(previous, 7, current, 8, out var changeSet));
        Assert.NotNull(changeSet);
        Assert.Contains(child.Id, changeSet.RemovedEntityIds);
        Assert.Contains(changeSet.UpsertedEntities, entity => entity.Id == first.Id);
        Assert.NotNull(changeSet.Hierarchy);

        Assert.True(SceneChangeSetApplier.TryApply(previous, 7, changeSet, out var applied, out var error), error);
        Assert.Equal(SceneSerializer.Serialize(current), SceneSerializer.Serialize(applied!));
    }

    [Fact]
    public void Create_DataOnlyChange_OmitsHierarchyAndUnchangedEntities()
    {
        var first = Entity("First");
        var second = Entity("Second");
        var previous = new SceneDocument { Roots = { first, second } };
        var current = SceneSerializer.Clone(previous);
        current.FindEntity(second.Id)!.Enabled = false;

        Assert.True(SceneChangeSetBuilder.TryCreate(previous, 2, current, 3, out var changeSet));
        Assert.Null(changeSet!.Hierarchy);
        Assert.Empty(changeSet.RemovedEntityIds);
        Assert.Equal(second.Id, Assert.Single(changeSet.UpsertedEntities).Id);

        Assert.True(SceneChangeSetApplier.TryApply(previous, 2, changeSet, out var applied, out var error), error);
        Assert.Equal(SceneSerializer.Serialize(current), SceneSerializer.Serialize(applied!));
    }

    [Fact]
    public void Apply_RevisionGap_RequestsSnapshotInsteadOfMutatingScene()
    {
        var previous = new SceneDocument { Roots = { Entity("First") } };
        var current = SceneSerializer.Clone(previous);
        current.Roots[0].Name = "Changed";
        Assert.True(SceneChangeSetBuilder.TryCreate(previous, 10, current, 11, out var changeSet));

        Assert.False(SceneChangeSetApplier.TryApply(previous, 9, changeSet!, out var applied, out var error));
        Assert.Null(applied);
        Assert.Contains("Expected base revision 9", error, StringComparison.Ordinal);
        Assert.Equal("First", previous.Roots[0].Name);
    }

    private static SceneEntity Entity(string name, params SceneEntity[] children)
    {
        var entity = new SceneEntity { Name = name };
        entity.Children.AddRange(children);
        return entity;
    }
}
