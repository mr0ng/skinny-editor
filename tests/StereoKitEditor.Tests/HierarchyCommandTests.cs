using StereoKitEditor.Core;
using StereoKitEditor.Scene;
using System.Numerics;

namespace StereoKitEditor.Tests;

public sealed class HierarchyCommandTests
{
    [Fact]
    public void Reparent_UndoRedoAndCyclePrevention_PreserveSubtreeIdentity()
    {
        var child = new SceneEntity { Name = "Child" };
        var first = new SceneEntity { Name = "First", Children = { child } };
        var second = new SceneEntity { Name = "Second" };
        var scene = new SceneDocument { Roots = [first, second] };
        var history = new CommandHistory();

        history.Execute(scene, new ReparentEntitiesCommand([child.Id], second.Id));
        Assert.Empty(first.Children);
        Assert.Same(child, Assert.Single(second.Children));
        Assert.True(history.Undo(scene));
        Assert.Same(child, Assert.Single(first.Children));
        Assert.Empty(second.Children);
        Assert.True(history.Redo(scene));
        Assert.Same(child, Assert.Single(second.Children));

        Assert.Throws<InvalidOperationException>(() =>
            new ReparentEntitiesCommand([second.Id], child.Id).Apply(scene));
        Assert.Same(child, Assert.Single(second.Children));
    }

    [Fact]
    public void DuplicateSubtree_RegeneratesEveryEntityAndComponentId_AndIsUndoable()
    {
        var child = new SceneEntity { Name = "Child" };
        child.Components.PrimitiveMeshRenderer = new();
        var source = new SceneEntity { Name = "Source", Children = { child } };
        var scene = new SceneDocument { Roots = [source] };
        var history = new CommandHistory();
        var command = new DuplicateEntitiesCommand([source.Id]);

        history.Execute(scene, command);
        var duplicate = scene.Roots[1];
        Assert.Equal("Source Copy", duplicate.Name);
        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(child.Id, duplicate.Children[0].Id);
        Assert.Empty(source.Traverse().SelectMany(entity => entity.Components.Records).Select(component => component.Id)
            .Intersect(duplicate.Traverse().SelectMany(entity => entity.Components.Records).Select(component => component.Id)));
        var duplicateId = duplicate.Id;

        Assert.True(history.Undo(scene));
        Assert.Single(scene.Roots);
        Assert.True(history.Redo(scene));
        Assert.Equal(duplicateId, scene.Roots[1].Id);
    }

    [Fact]
    public void DeleteParentAndSelectedChild_DeletesAndRestoresSubtreeOnce()
    {
        var child = new SceneEntity { Name = "Child" };
        var parent = new SceneEntity { Name = "Parent", Children = { child } };
        var sibling = new SceneEntity { Name = "Sibling" };
        var scene = new SceneDocument { Roots = [parent, sibling] };
        var history = new CommandHistory();

        history.Execute(scene, new DeleteEntitiesCommand([parent.Id, child.Id]));
        Assert.Equal([sibling.Id], scene.Roots.Select(entity => entity.Id));
        Assert.True(history.Undo(scene));
        Assert.Equal([parent.Id, sibling.Id], scene.Roots.Select(entity => entity.Id));
        Assert.Same(child, Assert.Single(parent.Children));
    }

    [Fact]
    public void Reparent_PreservesWorldTransformAcrossDifferentParentTransforms()
    {
        var child = new SceneEntity { Name = "Child" };
        child.Components.Transform = new(
            new Vector3Value(0.5, 0.25, -1),
            QuaternionValue.Identity,
            new Vector3Value(1, 1, 1));
        var first = new SceneEntity { Name = "First", Children = { child } };
        first.Components.Transform = new(
            new Vector3Value(2, 0, 0),
            QuaternionValue.Identity,
            new Vector3Value(2, 2, 2));
        var second = new SceneEntity { Name = "Second" };
        second.Components.Transform = new(
            new Vector3Value(-3, 1, 0),
            QuaternionValue.Identity,
            new Vector3Value(0.5, 0.5, 0.5));
        var scene = new SceneDocument { Roots = [first, second] };
        var history = new CommandHistory();
        var originalWorld = SceneTransformMath.GetWorldMatrix(scene, child.Id);

        history.Execute(scene, new ReparentEntitiesCommand([child.Id], second.Id));
        AssertMatrixNear(originalWorld, SceneTransformMath.GetWorldMatrix(scene, child.Id));
        Assert.True(history.Undo(scene));
        AssertMatrixNear(originalWorld, SceneTransformMath.GetWorldMatrix(scene, child.Id));
        Assert.True(history.Redo(scene));
        AssertMatrixNear(originalWorld, SceneTransformMath.GetWorldMatrix(scene, child.Id));
    }

    private static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44,
        };
        var actualValues = new[]
        {
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44,
        };
        for (var index = 0; index < expectedValues.Length; index++)
        {
            Assert.InRange(actualValues[index], expectedValues[index] - 0.0001f, expectedValues[index] + 0.0001f);
        }
    }
}
