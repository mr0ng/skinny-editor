using System.Text.Json;
using StereoKitEditor.Core;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class SceneSerializerTests
{
    [Fact]
    public void FutureSceneFormat_IsRejectedWithoutMutatingItsData()
    {
        var json = SceneSerializer.Serialize(CreateScene()).Replace(
            "\"formatVersion\": 2",
            "\"formatVersion\": 3",
            StringComparison.Ordinal);

        var error = Assert.Throws<NotSupportedException>(() => SceneSerializer.Deserialize(json));

        Assert.Contains("Scene format 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("expected 1 or 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_IsDeterministic_AndUsesCompactVectorArrays()
    {
        var original = CreateScene();

        var first = SceneSerializer.Serialize(original);
        var roundTripped = SceneSerializer.Deserialize(first);
        var second = SceneSerializer.Serialize(roundTripped);

        Assert.Equal(first, second);
        Assert.Contains("\"position\": [", first, StringComparison.Ordinal);
        Assert.Equal(original.SceneId, roundTripped.SceneId);
        Assert.Equal(original.Roots[0].Id, roundTripped.Roots[0].Id);
    }

    [Fact]
    public void RoundTrip_PreservesUnknownComponents()
    {
        var knownJson = SceneSerializer.Serialize(CreateScene());
        using var knownDocument = JsonDocument.Parse(knownJson);
        var root = knownDocument.RootElement;
        var components = root.GetProperty("roots")[0].GetProperty("components");
        var transform = components.EnumerateArray().Single(component =>
            component.GetProperty("typeId").GetString() == BuiltInComponentTypes.Transform);

        var customJson = $$"""
        {
          "formatVersion": 1,
          "sceneId": "{{root.GetProperty("sceneId").GetGuid()}}",
          "name": "Unknown component fixture",
          "roots": [
            {
              "id": "{{root.GetProperty("roots")[0].GetProperty("id").GetGuid()}}",
              "name": "Fixture",
              "enabled": true,
              "components": {
                "transform": {{transform.GetProperty("data").GetRawText()}},
                "customBehavior": { "speed": 7, "mode": "orbit" }
              },
              "children": []
            }
          ]
        }
        """;

        var result = SceneSerializer.DeserializeWithMetadata(customJson);
        var roundTripped = SceneSerializer.Serialize(result.Document);

        Assert.True(result.MigratedFromFormat1);
        Assert.Equal(SceneDocument.CurrentFormatVersion, result.Document.FormatVersion);
        Assert.Contains("\"typeId\": \"customBehavior\"", roundTripped, StringComparison.Ordinal);
        Assert.Contains("\"speed\": 7", roundTripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsDuplicateEntityIds()
    {
        var scene = CreateScene();
        scene.Roots.Add(new SceneEntity { Id = scene.Roots[0].Id, Name = "Duplicate" });
        var json = SceneSerializer.Serialize(scene);

        var exception = Assert.Throws<JsonException>(() => SceneSerializer.Deserialize(json));

        Assert.Contains("Duplicate entity ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_CreatesIndependentPlaySnapshot()
    {
        var editDocument = CreateScene();

        var playDocument = SceneSerializer.Clone(editDocument);
        playDocument.Roots[0].Components.Transform = playDocument.Roots[0].Components.Transform with
        {
            Position = new Vector3Value(9, 8, 7),
        };

        Assert.NotSame(editDocument, playDocument);
        Assert.Equal(new Vector3Value(1.25, -2.5, -0.75), editDocument.Roots[0].Components.Transform.Position);
        Assert.Equal(new Vector3Value(9, 8, 7), playDocument.Roots[0].Components.Transform.Position);
    }

    [Fact]
    public async Task MigratedFormat1_FirstSaveCreatesBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"skeditor-scene-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Legacy.skscene.json");
        var legacyJson = """
        {
          "formatVersion": 1,
          "sceneId": "e42bdab6-7ca4-403d-b166-b062f3392c80",
          "name": "Legacy",
          "roots": [
            {
              "id": "da868f9d-c1b0-4ed0-9aab-44b1e3e6e26e",
              "name": "Cube",
              "enabled": true,
              "components": {
                "transform": {
                  "position": [0, 0, -0.5],
                  "rotation": [0, 0, 0, 1],
                  "scale": [1, 1, 1]
                }
              },
              "children": []
            }
          ]
        }
        """;

        try
        {
            await File.WriteAllTextAsync(path, legacyJson, TestContext.Current.CancellationToken);
            var result = SceneSerializer.DeserializeWithMetadata(legacyJson);
            var session = new EditorSession(result.Document, path, result.MigratedFromFormat1);

            await session.SaveAsync(TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path + ".format1.bak"));
            Assert.Equal(legacyJson, await File.ReadAllTextAsync(path + ".format1.bak", TestContext.Current.CancellationToken));
            Assert.Contains("\"formatVersion\": 2", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.False(session.IsDirty);
            Assert.False(session.RequiresMigrationBackup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SceneDocument CreateScene() => new()
    {
        SceneId = Guid.Parse("E42BDAB6-7CA4-403D-B166-B062F3392C80"),
        Name = "Fixture",
        Roots =
        [
            new SceneEntity
            {
                Id = Guid.Parse("DA868F9D-C1B0-4ED0-9AAB-44B1E3E6E26E"),
                Name = "Cube",
                Components =
                {
                    Transform = new(
                        new Vector3Value(1.25, -2.5, -0.75),
                        QuaternionValue.Identity,
                        Vector3Value.One),
                    PrimitiveMeshRenderer = new(),
                },
            },
        ],
    };
}
