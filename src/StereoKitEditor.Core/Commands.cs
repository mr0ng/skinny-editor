using StereoKitEditor.Scene;
using System.Text.Json;

namespace StereoKitEditor.Core;

public interface ISceneCommand
{
    string Description { get; }
    void Apply(SceneDocument document);
    void Revert(SceneDocument document);
}

public sealed class CommandHistory
{
    private readonly Stack<ISceneCommand> _undo = new();
    private readonly Stack<ISceneCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoDescription => _undo.TryPeek(out var command) ? command.Description : null;
    public string? NextRedoDescription => _redo.TryPeek(out var command) ? command.Description : null;

    public void Execute(SceneDocument document, ISceneCommand command)
    {
        command.Apply(document);
        _undo.Push(command);
        _redo.Clear();
    }

    public bool Undo(SceneDocument document)
    {
        if (!_undo.TryPop(out var command))
        {
            return false;
        }

        command.Revert(document);
        _redo.Push(command);
        return true;
    }

    public bool Redo(SceneDocument document)
    {
        if (!_redo.TryPop(out var command))
        {
            return false;
        }

        command.Apply(document);
        _undo.Push(command);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void CombineLastExecuted(int commandCount, ISceneCommand combined)
    {
        if (commandCount < 2 || _undo.Count < commandCount)
        {
            throw new InvalidOperationException("The requested command group is not available.");
        }

        for (var index = 0; index < commandCount; index++)
        {
            _undo.Pop();
        }

        _undo.Push(combined);
    }
}

public sealed class CompositeSceneCommand(
    string description,
    IReadOnlyList<ISceneCommand> commands) : ISceneCommand
{
    public string Description { get; } = description;

    public void Apply(SceneDocument document)
    {
        foreach (var command in commands)
        {
            command.Apply(document);
        }
    }

    public void Revert(SceneDocument document)
    {
        for (var index = commands.Count - 1; index >= 0; index--)
        {
            commands[index].Revert(document);
        }
    }
}

public sealed class AddRootEntityCommand(SceneEntity entity) : ISceneCommand
{
    private int _index = -1;

    public string Description => $"Create {entity.Name}";

    public void Apply(SceneDocument document)
    {
        if (document.FindEntity(entity.Id) is not null)
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' already exists.");
        }

        if (_index < 0 || _index > document.Roots.Count)
        {
            _index = document.Roots.Count;
        }

        document.Roots.Insert(_index, entity);
    }

    public void Revert(SceneDocument document)
    {
        _index = document.Roots.FindIndex(candidate => candidate.Id == entity.Id);
        if (_index < 0)
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' no longer exists.");
        }

        document.Roots.RemoveAt(_index);
    }
}

public sealed class RenameEntityCommand(Guid entityId, string oldName, string newName) : ISceneCommand
{
    public string Description => $"Rename {oldName}";
    public void Apply(SceneDocument document) => Find(document).Name = newName;
    public void Revert(SceneDocument document) => Find(document).Name = oldName;

    private SceneEntity Find(SceneDocument document) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed class SetEntityEnabledCommand(Guid entityId, bool oldValue, bool newValue) : ISceneCommand
{
    public string Description => newValue ? "Enable Entity" : "Disable Entity";
    public void Apply(SceneDocument document) => Find(document).Enabled = newValue;
    public void Revert(SceneDocument document) => Find(document).Enabled = oldValue;

    private SceneEntity Find(SceneDocument document) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed class SetTransformCommand(
    Guid entityId,
    TransformComponent oldValue,
    TransformComponent newValue) : ISceneCommand
{
    public string Description => "Change Transform";
    public void Apply(SceneDocument document) => Find(document).Components.Transform = newValue;
    public void Revert(SceneDocument document) => Find(document).Components.Transform = oldValue;

    private SceneEntity Find(SceneDocument document) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed record EntityTransformChange(
    Guid EntityId,
    TransformComponent OldValue,
    TransformComponent NewValue);

public sealed class SetTransformsCommand(IReadOnlyList<EntityTransformChange> changes) : ISceneCommand
{
    public string Description => $"Change {changes.Count} Transforms";

    public void Apply(SceneDocument document)
    {
        foreach (var change in changes)
        {
            Find(document, change.EntityId).Components.Transform = change.NewValue;
        }
    }

    public void Revert(SceneDocument document)
    {
        foreach (var change in changes)
        {
            Find(document, change.EntityId).Components.Transform = change.OldValue;
        }
    }

    private static SceneEntity Find(SceneDocument document, Guid entityId) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed class AddComponentCommand(Guid entityId, SceneComponentRecord component) : ISceneCommand
{
    private int _index = -1;

    public string Description => $"Add {component.TypeId}";

    public void Apply(SceneDocument document)
    {
        var components = Find(document).Components;
        if (components.Find(component.Id) is not null)
        {
            throw new InvalidOperationException($"Component '{component.Id}' already exists.");
        }

        components.Add(component, _index < 0 ? null : _index);
        _index = components.Records.ToList().FindIndex(candidate => candidate.Id == component.Id);
    }

    public void Revert(SceneDocument document)
    {
        var components = Find(document).Components;
        _index = components.Records.ToList().FindIndex(candidate => candidate.Id == component.Id);
        if (_index < 0 || !components.Remove(component.Id))
        {
            throw new InvalidOperationException($"Component '{component.Id}' no longer exists.");
        }
    }

    private SceneEntity Find(SceneDocument document) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed class AddComponentsCommand(Guid entityId, IReadOnlyList<SceneComponentRecord> components) : ISceneCommand
{
    private readonly IReadOnlyList<SceneComponentRecord> _components = components.Count > 0
        ? components.ToArray()
        : throw new ArgumentException("At least one component is required.", nameof(components));

    public string Description => _components.Count == 1
        ? $"Add {_components[0].TypeId}"
        : $"Add {_components.Count} Components";

    public void Apply(SceneDocument document)
    {
        var target = Find(document).Components;
        foreach (var component in _components)
        {
            if (target.Find(component.Id) is not null)
            {
                throw new InvalidOperationException($"Component '{component.Id}' already exists.");
            }

            target.Add(component);
        }
    }

    public void Revert(SceneDocument document)
    {
        var target = Find(document).Components;
        foreach (var component in _components.Reverse())
        {
            if (!target.Remove(component.Id))
            {
                throw new InvalidOperationException($"Component '{component.Id}' no longer exists.");
            }
        }
    }

    private SceneEntity Find(SceneDocument document) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed class RemoveComponentCommand(Guid entityId, SceneComponentRecord component) : ISceneCommand
{
    private int _index = -1;

    public string Description => $"Remove {component.TypeId}";

    public void Apply(SceneDocument document)
    {
        var components = Find(document).Components;
        _index = components.Records.ToList().FindIndex(candidate => candidate.Id == component.Id);
        if (_index < 0 || !components.Remove(component.Id))
        {
            throw new InvalidOperationException($"Component '{component.Id}' was not found.");
        }
    }

    public void Revert(SceneDocument document) => Find(document).Components.Add(component, _index);

    private SceneEntity Find(SceneDocument document) => document.FindEntity(entityId)
        ?? throw new InvalidOperationException($"Entity '{entityId}' was not found.");
}

public sealed class SetComponentEnabledCommand(
    Guid entityId,
    Guid componentId,
    bool oldValue,
    bool newValue) : ISceneCommand
{
    public string Description => newValue ? "Enable Component" : "Disable Component";
    public void Apply(SceneDocument document) => Find(document).Enabled = newValue;
    public void Revert(SceneDocument document) => Find(document).Enabled = oldValue;

    private SceneComponentRecord Find(SceneDocument document) =>
        document.FindEntity(entityId)?.Components.Find(componentId)
        ?? throw new InvalidOperationException($"Component '{componentId}' was not found.");
}

public sealed class SetComponentDataCommand(
    Guid entityId,
    Guid componentId,
    JsonElement oldValue,
    JsonElement newValue,
    string propertyDisplayName) : ISceneCommand
{
    private readonly JsonElement _oldValue = oldValue.Clone();
    private readonly JsonElement _newValue = newValue.Clone();

    public string Description => $"Change {propertyDisplayName}";
    public void Apply(SceneDocument document) => Find(document).Data = _newValue.Clone();
    public void Revert(SceneDocument document) => Find(document).Data = _oldValue.Clone();

    private SceneComponentRecord Find(SceneDocument document) =>
        document.FindEntity(entityId)?.Components.Find(componentId)
        ?? throw new InvalidOperationException($"Component '{componentId}' was not found.");
}

public sealed record ComponentSchemaUpgrade(
    Guid EntityId,
    Guid ComponentId,
    int OldSchemaVersion,
    JsonElement OldData,
    int NewSchemaVersion,
    JsonElement NewData);

public sealed class UpgradeComponentSchemasCommand : ISceneCommand
{
    private readonly IReadOnlyList<ComponentSchemaUpgrade> _upgrades;

    public UpgradeComponentSchemasCommand(IReadOnlyList<ComponentSchemaUpgrade> upgrades)
    {
        ArgumentNullException.ThrowIfNull(upgrades);
        if (upgrades.Count == 0)
        {
            throw new ArgumentException("At least one component schema upgrade is required.", nameof(upgrades));
        }

        _upgrades = upgrades.Select(upgrade => upgrade with
        {
            OldData = upgrade.OldData.Clone(),
            NewData = upgrade.NewData.Clone(),
        }).ToArray();
    }

    public string Description => _upgrades.Count == 1
        ? "Upgrade Component Schema"
        : $"Upgrade {_upgrades.Count} Component Schemas";

    public void Apply(SceneDocument document)
    {
        foreach (var upgrade in _upgrades)
        {
            Set(document, upgrade, upgrade.NewSchemaVersion, upgrade.NewData);
        }
    }

    public void Revert(SceneDocument document)
    {
        foreach (var upgrade in _upgrades.Reverse())
        {
            Set(document, upgrade, upgrade.OldSchemaVersion, upgrade.OldData);
        }
    }

    private static void Set(
        SceneDocument document,
        ComponentSchemaUpgrade upgrade,
        int schemaVersion,
        JsonElement data)
    {
        var component = document.FindEntity(upgrade.EntityId)?.Components.Find(upgrade.ComponentId)
            ?? throw new InvalidOperationException($"Component '{upgrade.ComponentId}' was not found.");
        component.SchemaVersion = schemaVersion;
        component.Data = data.Clone();
    }
}
