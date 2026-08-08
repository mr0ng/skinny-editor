using System.Text.Json;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Protocol;

public sealed record SceneHierarchyPlacement(Guid EntityId, Guid? ParentId, int SiblingIndex);

public sealed record LoadSceneChangeSetMessage(
    Guid SceneId,
    string SceneName,
    long BaseRevision,
    long Revision,
    IReadOnlyList<Guid> RemovedEntityIds,
    IReadOnlyList<SceneEntity> UpsertedEntities,
    IReadOnlyList<SceneHierarchyPlacement>? Hierarchy);

public sealed record SceneResyncRequiredMessage(
    long ExpectedBaseRevision,
    long ReceivedBaseRevision,
    string Reason);

public static class SceneChangeSetBuilder
{
    public static bool TryCreate(
        SceneDocument previous,
        long baseRevision,
        SceneDocument current,
        long revision,
        out LoadSceneChangeSetMessage? changeSet)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        changeSet = null;
        if (previous.SceneId != current.SceneId || revision <= baseRevision)
        {
            return false;
        }

        var previousEntities = previous.Traverse().ToDictionary(entity => entity.Id);
        var currentEntities = current.Traverse().ToDictionary(entity => entity.Id);
        var removed = previousEntities.Keys
            .Except(currentEntities.Keys)
            .Order()
            .ToArray();
        var upserts = currentEntities.Values
            .Where(entity => !previousEntities.TryGetValue(entity.Id, out var oldEntity)
                || !EntityDataEquals(oldEntity, entity))
            .OrderBy(entity => entity.Id)
            .Select(CloneWithoutChildren)
            .ToArray();

        var previousHierarchy = FlattenHierarchy(previous);
        var currentHierarchy = FlattenHierarchy(current);
        IReadOnlyList<SceneHierarchyPlacement>? hierarchy = previousHierarchy.SequenceEqual(currentHierarchy)
            ? null
            : currentHierarchy;

        changeSet = new LoadSceneChangeSetMessage(
            current.SceneId,
            current.Name,
            baseRevision,
            revision,
            removed,
            upserts,
            hierarchy);
        return true;
    }

    private static bool EntityDataEquals(SceneEntity left, SceneEntity right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            || left.Enabled != right.Enabled
            || left.Components.Records.Count != right.Components.Records.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Components.Records.Count; index++)
        {
            var leftComponent = left.Components.Records[index];
            var rightComponent = right.Components.Records[index];
            if (leftComponent.Id != rightComponent.Id
                || !string.Equals(leftComponent.TypeId, rightComponent.TypeId, StringComparison.Ordinal)
                || leftComponent.SchemaVersion != rightComponent.SchemaVersion
                || leftComponent.Enabled != rightComponent.Enabled
                || !JsonValueEquals(leftComponent.Data, rightComponent.Data)
                || !ExtensionDataEquals(leftComponent.ExtensionData, rightComponent.ExtensionData))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExtensionDataEquals(
        IReadOnlyDictionary<string, JsonElement>? left,
        IReadOnlyDictionary<string, JsonElement>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || !JsonValueEquals(pair.Value, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool JsonValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => PropertiesEqual(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                                   && left.EnumerateArray().Zip(right.EnumerateArray())
                                       .All(pair => JsonValueEquals(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => false,
        };

        static bool PropertiesEqual(JsonElement leftObject, JsonElement rightObject)
        {
            var leftEnumerator = leftObject.EnumerateObject();
            var rightEnumerator = rightObject.EnumerateObject();
            while (true)
            {
                var hasLeft = leftEnumerator.MoveNext();
                var hasRight = rightEnumerator.MoveNext();
                if (hasLeft != hasRight)
                {
                    return false;
                }

                if (!hasLeft)
                {
                    return true;
                }

                if (!string.Equals(leftEnumerator.Current.Name, rightEnumerator.Current.Name, StringComparison.Ordinal)
                    || !JsonValueEquals(leftEnumerator.Current.Value, rightEnumerator.Current.Value))
                {
                    return false;
                }
            }
        }
    }

    private static SceneEntity CloneWithoutChildren(SceneEntity entity) =>
        JsonSerializer.Deserialize<SceneEntity>(SerializeWithoutChildren(entity), SceneSerializer.Options)
        ?? throw new InvalidDataException($"Entity '{entity.Id}' could not be cloned for a scene change set.");

    private static string SerializeWithoutChildren(SceneEntity entity) =>
        JsonSerializer.Serialize(
            new SceneEntity
            {
                Id = entity.Id,
                Name = entity.Name,
                Enabled = entity.Enabled,
                Components = entity.Components,
            },
            SceneSerializer.Options);

    internal static IReadOnlyList<SceneHierarchyPlacement> FlattenHierarchy(SceneDocument scene)
    {
        var result = new List<SceneHierarchyPlacement>();
        Add(scene.Roots, null, result);
        return result;

        static void Add(
            IReadOnlyList<SceneEntity> entities,
            Guid? parentId,
            ICollection<SceneHierarchyPlacement> placements)
        {
            for (var index = 0; index < entities.Count; index++)
            {
                var entity = entities[index];
                placements.Add(new SceneHierarchyPlacement(entity.Id, parentId, index));
                Add(entity.Children, entity.Id, placements);
            }
        }
    }
}

public static class SceneChangeSetApplier
{
    public static bool TryApply(
        SceneDocument current,
        long currentRevision,
        LoadSceneChangeSetMessage changeSet,
        out SceneDocument? updated,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changeSet);

        updated = null;
        error = null;
        if (changeSet.BaseRevision != currentRevision)
        {
            error = $"Expected base revision {currentRevision}, received {changeSet.BaseRevision}.";
            return false;
        }

        if (changeSet.Revision <= changeSet.BaseRevision)
        {
            error = "The target revision must be newer than the base revision.";
            return false;
        }

        if (changeSet.SceneId == Guid.Empty || changeSet.SceneId != current.SceneId)
        {
            error = "The change set belongs to a different scene.";
            return false;
        }

        try
        {
            var entityData = current.Traverse().ToDictionary(entity => entity.Id);
            foreach (var removedId in changeSet.RemovedEntityIds.Distinct())
            {
                entityData.Remove(removedId);
            }

            foreach (var entity in changeSet.UpsertedEntities)
            {
                if (entity.Id == Guid.Empty || entity.Children.Count != 0)
                {
                    throw new InvalidDataException("Change-set entity patches must have a non-empty ID and no children.");
                }

                entityData[entity.Id] = entity;
            }

            var hierarchy = changeSet.Hierarchy
                ?? SceneChangeSetBuilder.FlattenHierarchy(current)
                    .Where(placement => entityData.ContainsKey(placement.EntityId))
                    .ToArray();
            ValidateHierarchy(entityData, hierarchy);

            var rebuilt = entityData.ToDictionary(
                pair => pair.Key,
                pair => new SceneEntity
                {
                    Id = pair.Value.Id,
                    Name = pair.Value.Name,
                    Enabled = pair.Value.Enabled,
                    Components = pair.Value.Components,
                });
            var scene = new SceneDocument
            {
                FormatVersion = current.FormatVersion,
                SceneId = current.SceneId,
                Name = changeSet.SceneName,
            };

            foreach (var placement in hierarchy)
            {
                var entity = rebuilt[placement.EntityId];
                if (placement.ParentId is { } parentId)
                {
                    rebuilt[parentId].Children.Add(entity);
                }
                else
                {
                    scene.Roots.Add(entity);
                }
            }

            if (scene.Traverse().Select(entity => entity.Id).Distinct().Count() != rebuilt.Count)
            {
                throw new InvalidDataException("The hierarchy contains a cycle or unreachable entity.");
            }

            updated = scene;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void ValidateHierarchy(
        IReadOnlyDictionary<Guid, SceneEntity> entities,
        IReadOnlyList<SceneHierarchyPlacement> hierarchy)
    {
        if (hierarchy.Count != entities.Count
            || hierarchy.Select(placement => placement.EntityId).Distinct().Count() != hierarchy.Count
            || hierarchy.Any(placement => !entities.ContainsKey(placement.EntityId)))
        {
            throw new InvalidDataException("The hierarchy must place every live entity exactly once.");
        }

        foreach (var placement in hierarchy)
        {
            if (placement.ParentId == placement.EntityId
                || placement.ParentId is { } parentId && !entities.ContainsKey(parentId))
            {
                throw new InvalidDataException($"Entity '{placement.EntityId}' has an invalid parent.");
            }
        }

        foreach (var siblingGroup in hierarchy.GroupBy(placement => placement.ParentId))
        {
            var indexes = siblingGroup.Select(placement => placement.SiblingIndex).Order().ToArray();
            if (!indexes.SequenceEqual(Enumerable.Range(0, indexes.Length)))
            {
                throw new InvalidDataException("Sibling indexes must be unique and contiguous.");
            }
        }
    }
}
