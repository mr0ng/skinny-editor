using System.Numerics;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Core;

public static class SceneEntityCloner
{
    public static SceneEntity CloneWithNewIds(SceneEntity source, string? rootName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var components = new EntityComponents();
        foreach (var existing in components.Records.ToArray())
        {
            components.Remove(existing.Id);
        }

        foreach (var component in source.Components.Records)
        {
            components.Add(new SceneComponentRecord
            {
                TypeId = component.TypeId,
                SchemaVersion = component.SchemaVersion,
                Enabled = component.Enabled,
                Data = component.Data.Clone(),
                ExtensionData = component.ExtensionData?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal),
            });
        }

        var clone = new SceneEntity
        {
            Name = rootName ?? source.Name,
            Enabled = source.Enabled,
            Components = components,
        };
        foreach (var child in source.Children)
        {
            clone.Children.Add(CloneWithNewIds(child));
        }

        return clone;
    }
}

public sealed class AddEntityCommand(Guid? parentId, SceneEntity entity) : ISceneCommand
{
    private int _index = -1;

    public string Description => parentId is null ? $"Create {entity.Name}" : $"Create Child {entity.Name}";

    public void Apply(SceneDocument document)
    {
        if (document.FindEntity(entity.Id) is not null)
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' already exists.");
        }

        var siblings = HierarchyCommandHelpers.GetChildren(document, parentId);
        _index = _index < 0 ? siblings.Count : Math.Clamp(_index, 0, siblings.Count);
        siblings.Insert(_index, entity);
    }

    public void Revert(SceneDocument document)
    {
        var siblings = HierarchyCommandHelpers.GetChildren(document, parentId);
        _index = siblings.FindIndex(candidate => candidate.Id == entity.Id);
        if (_index < 0)
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' no longer exists.");
        }

        siblings.RemoveAt(_index);
    }
}

public sealed class DeleteEntitiesCommand : ISceneCommand
{
    private readonly IReadOnlyList<Guid> _requestedIds;
    private IReadOnlyList<EntityPlacement>? _placements;

    public DeleteEntitiesCommand(IEnumerable<Guid> entityIds)
    {
        _requestedIds = entityIds.Distinct().ToArray();
        if (_requestedIds.Count == 0)
        {
            throw new ArgumentException("At least one entity must be deleted.", nameof(entityIds));
        }
    }

    public string Description => _requestedIds.Count == 1 ? "Delete Entity" : $"Delete {_requestedIds.Count} Entities";

    public void Apply(SceneDocument document)
    {
        _placements ??= HierarchyCommandHelpers.CaptureTopmost(document, _requestedIds);
        foreach (var placement in _placements.OrderByDescending(item => item.Index))
        {
            var siblings = HierarchyCommandHelpers.GetChildren(document, placement.ParentId);
            var index = siblings.FindIndex(entity => entity.Id == placement.Entity.Id);
            if (index >= 0)
            {
                siblings.RemoveAt(index);
            }
        }
    }

    public void Revert(SceneDocument document) => HierarchyCommandHelpers.Restore(document, _placements!);
}

public sealed class DuplicateEntitiesCommand : ISceneCommand
{
    private readonly IReadOnlyList<Guid> _sourceIds;
    private IReadOnlyList<EntityPlacement>? _duplicates;

    public DuplicateEntitiesCommand(IEnumerable<Guid> sourceIds)
    {
        _sourceIds = sourceIds.Distinct().ToArray();
        if (_sourceIds.Count == 0)
        {
            throw new ArgumentException("At least one entity must be duplicated.", nameof(sourceIds));
        }
    }

    public IReadOnlyList<Guid> DuplicateIds => _duplicates?.Select(item => item.Entity.Id).ToArray() ?? [];
    public string Description => _sourceIds.Count == 1 ? "Duplicate Entity" : $"Duplicate {_sourceIds.Count} Entities";

    public void Apply(SceneDocument document)
    {
        if (_duplicates is null)
        {
            var sources = HierarchyCommandHelpers.CaptureTopmost(document, _sourceIds);
            _duplicates = sources.Select(source => new EntityPlacement(
                source.ParentId,
                source.Index + 1,
                SceneEntityCloner.CloneWithNewIds(source.Entity, source.Entity.Name + " Copy"))).ToArray();
        }

        foreach (var group in _duplicates.GroupBy(item => item.ParentId))
        {
            var siblings = HierarchyCommandHelpers.GetChildren(document, group.Key);
            var offset = 0;
            foreach (var placement in group.OrderBy(item => item.Index))
            {
                siblings.Insert(Math.Clamp(placement.Index + offset, 0, siblings.Count), placement.Entity);
                offset++;
            }
        }
    }

    public void Revert(SceneDocument document)
    {
        foreach (var placement in _duplicates!.Reverse())
        {
            HierarchyCommandHelpers.GetChildren(document, placement.ParentId)
                .RemoveAll(entity => entity.Id == placement.Entity.Id);
        }
    }
}

public sealed class ReparentEntitiesCommand : ISceneCommand
{
    private readonly IReadOnlyList<Guid> _requestedIds;
    private readonly Guid? _newParentId;
    private IReadOnlyList<EntityPlacement>? _originalPlacements;
    private IReadOnlyDictionary<Guid, TransformComponent>? _originalTransforms;
    private IReadOnlyDictionary<Guid, TransformComponent>? _reparentedTransforms;

    public ReparentEntitiesCommand(IEnumerable<Guid> entityIds, Guid? newParentId)
    {
        _requestedIds = entityIds.Distinct().ToArray();
        _newParentId = newParentId;
        if (_requestedIds.Count == 0)
        {
            throw new ArgumentException("At least one entity must be reparented.", nameof(entityIds));
        }
    }

    public string Description => _requestedIds.Count == 1 ? "Reparent Entity" : $"Reparent {_requestedIds.Count} Entities";

    public void Apply(SceneDocument document)
    {
        _originalPlacements ??= HierarchyCommandHelpers.CaptureTopmost(document, _requestedIds);
        if (_newParentId is { } parentId)
        {
            var parent = document.FindEntity(parentId)
                ?? throw new InvalidOperationException($"Target parent '{parentId}' was not found.");
            if (_originalPlacements.Any(placement => placement.Entity.Id == parentId
                    || placement.Entity.Traverse().Any(descendant => descendant.Id == parentId)))
            {
                throw new InvalidOperationException("An entity cannot be parented to itself or one of its descendants.");
            }
        }

        if (_reparentedTransforms is null)
        {
            _originalTransforms = _originalPlacements.ToDictionary(
                placement => placement.Entity.Id,
                placement => placement.Entity.Components.Transform);
            var parentWorld = _newParentId is { } targetParentId
                ? SceneTransformMath.GetWorldMatrix(document, targetParentId)
                : Matrix4x4.Identity;
            if (!Matrix4x4.Invert(parentWorld, out var inverseParentWorld))
            {
                throw new InvalidOperationException("The target parent's world transform cannot be inverted.");
            }

            _reparentedTransforms = _originalPlacements.ToDictionary(
                placement => placement.Entity.Id,
                placement => SceneTransformMath.FromMatrix(
                    SceneTransformMath.GetWorldMatrix(document, placement.Entity.Id) * inverseParentWorld));
        }

        foreach (var placement in _originalPlacements.OrderByDescending(item => item.Index))
        {
            HierarchyCommandHelpers.GetChildren(document, placement.ParentId)
                .RemoveAll(entity => entity.Id == placement.Entity.Id);
        }

        var target = HierarchyCommandHelpers.GetChildren(document, _newParentId);
        foreach (var placement in _originalPlacements)
        {
            placement.Entity.Components.Transform = _reparentedTransforms[placement.Entity.Id];
            target.Add(placement.Entity);
        }
    }

    public void Revert(SceneDocument document)
    {
        var target = HierarchyCommandHelpers.GetChildren(document, _newParentId);
        foreach (var placement in _originalPlacements!)
        {
            target.RemoveAll(entity => entity.Id == placement.Entity.Id);
        }

        HierarchyCommandHelpers.Restore(document, _originalPlacements!);
        foreach (var placement in _originalPlacements!)
        {
            placement.Entity.Components.Transform = _originalTransforms![placement.Entity.Id];
        }
    }
}

public static class SceneTransformMath
{
    public static Matrix4x4 GetWorldMatrix(SceneDocument document, Guid entityId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryFind(document.Roots, Matrix4x4.Identity, entityId, out var result))
        {
            throw new InvalidOperationException($"Entity '{entityId}' was not found.");
        }

        return result;
    }

    public static Matrix4x4 ToMatrix(TransformComponent transform)
    {
        var rotation = Quaternion.Normalize(new Quaternion(
            (float)transform.Rotation.X,
            (float)transform.Rotation.Y,
            (float)transform.Rotation.Z,
            (float)transform.Rotation.W));
        return Matrix4x4.CreateScale(
                   (float)transform.Scale.X,
                   (float)transform.Scale.Y,
                   (float)transform.Scale.Z)
               * Matrix4x4.CreateFromQuaternion(rotation)
               * Matrix4x4.CreateTranslation(
                   (float)transform.Position.X,
                   (float)transform.Position.Y,
                   (float)transform.Position.Z);
    }

    public static TransformComponent FromMatrix(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position))
        {
            throw new InvalidOperationException("The world transform cannot be represented as position, rotation, and scale.");
        }

        rotation = Quaternion.Normalize(rotation);
        return new(
            new Vector3Value(position.X, position.Y, position.Z),
            new QuaternionValue(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new Vector3Value(scale.X, scale.Y, scale.Z));
    }

    private static bool TryFind(
        IReadOnlyList<SceneEntity> siblings,
        Matrix4x4 parentWorld,
        Guid entityId,
        out Matrix4x4 result)
    {
        foreach (var entity in siblings)
        {
            var world = ToMatrix(entity.Components.Transform) * parentWorld;
            if (entity.Id == entityId)
            {
                result = world;
                return true;
            }

            if (TryFind(entity.Children, world, entityId, out result))
            {
                return true;
            }
        }

        result = default;
        return false;
    }
}

internal sealed record EntityPlacement(Guid? ParentId, int Index, SceneEntity Entity);

internal static class HierarchyCommandHelpers
{
    public static List<SceneEntity> GetChildren(SceneDocument document, Guid? parentId) => parentId is { } id
        ? document.FindEntity(id)?.Children
            ?? throw new InvalidOperationException($"Parent entity '{id}' was not found.")
        : document.Roots;

    public static IReadOnlyList<EntityPlacement> CaptureTopmost(
        SceneDocument document,
        IEnumerable<Guid> requestedIds)
    {
        var requested = requestedIds.ToHashSet();
        var result = new List<EntityPlacement>();
        Capture(document.Roots, null, ancestorSelected: false);
        if (result.Count == 0)
        {
            throw new InvalidOperationException("None of the requested entities exist in the scene.");
        }

        return result;

        void Capture(IReadOnlyList<SceneEntity> siblings, Guid? parentId, bool ancestorSelected)
        {
            for (var index = 0; index < siblings.Count; index++)
            {
                var entity = siblings[index];
                var selected = requested.Contains(entity.Id);
                if (selected && !ancestorSelected)
                {
                    result.Add(new EntityPlacement(parentId, index, entity));
                }

                Capture(entity.Children, entity.Id, ancestorSelected || selected);
            }
        }
    }

    public static void Restore(SceneDocument document, IReadOnlyList<EntityPlacement> placements)
    {
        foreach (var group in placements.GroupBy(item => item.ParentId))
        {
            var siblings = GetChildren(document, group.Key);
            foreach (var placement in group.OrderBy(item => item.Index))
            {
                siblings.Insert(Math.Clamp(placement.Index, 0, siblings.Count), placement.Entity);
            }
        }
    }
}
