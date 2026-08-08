using StereoKitEditor.Core;
using StereoKitEditor.Scene;
using System.Text.Json;

namespace StereoKitEditor.Tests;

public sealed class CommandHistoryTests
{
    [Fact]
    public void ExecuteUndoRedo_RestoresTransformAndRevisionState()
    {
        var entity = new SceneEntity { Name = "Cube" };
        var scene = new SceneDocument { Roots = [entity] };
        var history = new CommandHistory();
        var before = entity.Components.Transform;
        var after = before with { Position = new Vector3Value(1, 2, 3) };

        history.Execute(scene, new SetTransformCommand(entity.Id, before, after));
        Assert.Equal(after, entity.Components.Transform);
        Assert.True(history.CanUndo);

        Assert.True(history.Undo(scene));
        Assert.Equal(before, entity.Components.Transform);
        Assert.True(history.CanRedo);

        Assert.True(history.Redo(scene));
        Assert.Equal(after, entity.Components.Transform);
    }

    [Fact]
    public void AddRootEntity_UndoAndRedo_PreservesStableIdentityAndOrder()
    {
        var first = new SceneEntity { Name = "First" };
        var added = new SceneEntity { Name = "Added" };
        var scene = new SceneDocument { Roots = [first] };
        var history = new CommandHistory();

        history.Execute(scene, new AddRootEntityCommand(added));
        Assert.Equal([first.Id, added.Id], scene.Roots.Select(entity => entity.Id));

        history.Undo(scene);
        Assert.Equal([first.Id], scene.Roots.Select(entity => entity.Id));

        history.Redo(scene);
        Assert.Equal([first.Id, added.Id], scene.Roots.Select(entity => entity.Id));
    }

    [Fact]
    public void ProjectComponentCommands_UndoDataEnableAndRemoval()
    {
        var entity = new SceneEntity { Name = "Component host" };
        var scene = new SceneDocument { Roots = [entity] };
        var history = new CommandHistory();
        var component = SceneComponentRecord.Create(
            "com.example.marker",
            new { size = 0.1, visible = true });

        history.Execute(scene, new AddComponentCommand(entity.Id, component));
        Assert.Same(component, entity.Components.Find(component.Id));

        var updated = JsonSerializer.SerializeToElement(
            new { size = 0.25, visible = true },
            SceneSerializer.Options);
        history.Execute(scene, new SetComponentDataCommand(
            entity.Id,
            component.Id,
            component.Data,
            updated,
            "Size"));
        Assert.Equal(0.25, component.Data.GetProperty("size").GetDouble());

        history.Execute(scene, new SetComponentEnabledCommand(entity.Id, component.Id, true, false));
        Assert.False(component.Enabled);
        Assert.True(history.Undo(scene));
        Assert.True(component.Enabled);

        history.Execute(scene, new RemoveComponentCommand(entity.Id, component));
        Assert.Null(entity.Components.Find(component.Id));
        Assert.True(history.Undo(scene));
        Assert.Same(component, entity.Components.Find(component.Id));
    }

    [Fact]
    public void ComponentSchemaUpgrade_IsOneUndoableCommand()
    {
        var entity = new SceneEntity { Name = "Migrated host" };
        var component = SceneComponentRecord.Create("com.example.marker", new { size = 0.1 });
        entity.Components.Add(component);
        var scene = new SceneDocument { Roots = [entity] };
        var history = new CommandHistory();
        var migrated = JsonSerializer.SerializeToElement(new { size = 0.1, label = "Marker" });

        history.Execute(scene, new UpgradeComponentSchemasCommand([
            new ComponentSchemaUpgrade(
                entity.Id,
                component.Id,
                1,
                component.Data,
                2,
                migrated),
        ]));

        Assert.Equal(2, component.SchemaVersion);
        Assert.Equal("Marker", component.Data.GetProperty("label").GetString());
        Assert.True(history.Undo(scene));
        Assert.Equal(1, component.SchemaVersion);
        Assert.False(component.Data.TryGetProperty("label", out _));
        Assert.True(history.Redo(scene));
        Assert.Equal(2, component.SchemaVersion);
    }

    [Fact]
    public void AddComponents_AddsDependenciesAsOneUndoableCommand()
    {
        var entity = new SceneEntity { Name = "Dependent host" };
        var scene = new SceneDocument { Roots = [entity] };
        var history = new CommandHistory();
        var dependency = SceneComponentRecord.Create("com.example.service", new { });
        var consumer = SceneComponentRecord.Create("com.example.consumer", new { });

        history.Execute(scene, new AddComponentsCommand(entity.Id, [dependency, consumer]));
        Assert.Equal([dependency.Id, consumer.Id], entity.Components.Records.TakeLast(2).Select(component => component.Id));
        Assert.True(history.Undo(scene));
        Assert.Null(entity.Components.Find(dependency.Id));
        Assert.Null(entity.Components.Find(consumer.Id));
        Assert.True(history.Redo(scene));
        Assert.NotNull(entity.Components.Find(dependency.Id));
        Assert.NotNull(entity.Components.Find(consumer.Id));
    }

    [Fact]
    public void DuplicateAndTransform_CanBeCombinedIntoOneUndoStep()
    {
        var source = new SceneEntity { Name = "Source" };
        var scene = new SceneDocument { Roots = [source] };
        var history = new CommandHistory();
        var duplicate = new DuplicateEntitiesCommand([source.Id]);

        history.Execute(scene, duplicate);
        var duplicateEntity = scene.FindEntity(Assert.Single(duplicate.DuplicateIds))!;
        var before = duplicateEntity.Components.Transform;
        var after = before with { Position = new Vector3Value(2, 3, 4) };
        var transform = new SetTransformsCommand([
            new EntityTransformChange(duplicateEntity.Id, before, after),
        ]);
        history.Execute(scene, transform);
        history.CombineLastExecuted(
            2,
            new CompositeSceneCommand("Duplicate and Transform", [duplicate, transform]));

        Assert.Equal(after, duplicateEntity.Components.Transform);
        Assert.True(history.Undo(scene));
        Assert.Null(scene.FindEntity(duplicateEntity.Id));
        Assert.False(history.CanUndo);
        Assert.True(history.Redo(scene));
        Assert.Equal(after, scene.FindEntity(duplicateEntity.Id)!.Components.Transform);
    }
}
