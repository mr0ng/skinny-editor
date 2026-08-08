using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using StereoKitEditor.Assets;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class AssetDatabaseTests
{
    [Fact]
    public async Task Refresh_ImportsGlbWithStableIdentityBoundsAndThumbnail()
    {
        var directory = CreateWorkspace();
        try
        {
            var source = Path.Combine(directory, "Assets", "Fixture.glb");
            await File.WriteAllBytesAsync(source, CreateBoundsGlb(), TestContext.Current.CancellationToken);
            var database = CreateDatabase(directory);

            var first = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));
            var second = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));

            Assert.Equal(first.Metadata.AssetId, second.Metadata.AssetId);
            Assert.NotEqual(Guid.Empty, first.Metadata.AssetId);
            Assert.Equal("Fixture.glb", first.Metadata.SourcePath);
            Assert.Equal(64, first.Metadata.ContentHash.Length);
            Assert.False(first.HasErrors);
            Assert.True(File.Exists(source + ".skmeta"));
            Assert.True(File.Exists(first.ThumbnailFullPath));
            Assert.Equal(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                (await File.ReadAllBytesAsync(first.ThumbnailFullPath, TestContext.Current.CancellationToken))[..8]);

            var bounds = Assert.IsType<AssetBounds>(first.Metadata.Bounds);
            Assert.InRange(bounds.CenterX, 3.99999, 4.00001);
            Assert.InRange(bounds.CenterY, -0.00001, 0.00001);
            Assert.InRange(bounds.CenterZ, -0.00001, 0.00001);
            Assert.InRange(bounds.SizeX, 1.99999, 2.00001);
            Assert.InRange(bounds.SizeY, 3.99999, 4.00001);
            Assert.InRange(bounds.SizeZ, 5.99999, 6.00001);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Move_PreservesStableIdentityAndSceneReference()
    {
        var directory = CreateWorkspace();
        try
        {
            var source = Path.Combine(directory, "Assets", "Fixture.glb");
            await File.WriteAllBytesAsync(source, CreateBoundsGlb(), TestContext.Current.CancellationToken);
            var database = CreateDatabase(directory);
            var original = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));

            var moved = await database.MoveAsync(
                original.Metadata.AssetId,
                "Models/Renamed.glb",
                TestContext.Current.CancellationToken);
            var scene = new SceneDocument
            {
                Roots =
                [
                    new SceneEntity
                    {
                        Name = "Model",
                        Components =
                        {
                            ModelRenderer = new() { AssetId = moved.Metadata.AssetId },
                        },
                    },
                ],
            };
            var roundTripped = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));

            Assert.Equal(original.Metadata.AssetId, moved.Metadata.AssetId);
            Assert.Equal("Models/Renamed.glb", moved.Metadata.SourcePath);
            Assert.False(File.Exists(source));
            Assert.True(File.Exists(moved.SourceFullPath));
            Assert.True(File.Exists(moved.MetadataFullPath));
            Assert.Equal(
                original.Metadata.AssetId,
                roundTripped.Roots[0].Components.ModelRenderer?.AssetId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidGlb_ProducesActionableDiagnosticAndErrorThumbnail()
    {
        var directory = CreateWorkspace();
        try
        {
            var source = Path.Combine(directory, "Assets", "Broken.glb");
            await File.WriteAllTextAsync(source, "not a glb", TestContext.Current.CancellationToken);
            var database = CreateDatabase(directory);

            var record = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));

            Assert.True(record.HasErrors);
            var diagnostic = Assert.Single(record.Metadata.Diagnostics);
            Assert.Equal("SKINNY-ASSET-GLB-INVALID", diagnostic.Code);
            Assert.NotEmpty(diagnostic.Message);
            Assert.NotEmpty(diagnostic.SuggestedAction ?? string.Empty);
            Assert.True(File.Exists(record.ThumbnailFullPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_MovesSourceAndMetadataToRecoverableProjectTrash()
    {
        var directory = CreateWorkspace();
        try
        {
            var source = Path.Combine(directory, "Assets", "Fixture.glb");
            await File.WriteAllBytesAsync(source, CreateBoundsGlb(), TestContext.Current.CancellationToken);
            var database = CreateDatabase(directory);
            var record = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));

            var trashed = await database.DeleteAsync(record.Metadata.AssetId, TestContext.Current.CancellationToken);

            Assert.Empty(database.Records);
            Assert.False(File.Exists(source));
            Assert.False(File.Exists(source + ".skmeta"));
            Assert.Equal("Fixture.glb", trashed.OriginalRelativePath);
            Assert.StartsWith(
                Path.Combine(directory, ".skinny", "Trash", "Assets"),
                trashed.TrashDirectory,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(trashed.TrashDirectory, "Fixture.glb")));
            Assert.True(File.Exists(Path.Combine(trashed.TrashDirectory, "Fixture.glb.skmeta")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Refresh_LargeLibraryReusesUnchangedImportsAndTouchesOnlyChangedSidecar()
    {
        var directory = CreateWorkspace();
        try
        {
            var bytes = CreateBoundsGlb();
            for (var index = 0; index < 250; index++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, "Assets", $"Model-{index:000}.glb"),
                    bytes,
                    TestContext.Current.CancellationToken);
            }

            var database = CreateDatabase(directory);
            var imported = await database.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(250, imported.Count);
            var sidecarTimes = imported.ToDictionary(
                record => record.Metadata.AssetId,
                record => File.GetLastWriteTimeUtc(record.MetadataFullPath));
            var changed = imported[137];
            File.SetLastWriteTimeUtc(changed.SourceFullPath, DateTime.UtcNow.AddMinutes(2));

            var stopwatch = Stopwatch.StartNew();
            var refreshed = await database.RefreshAsync(TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.Equal(250, refreshed.Count);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Incremental refresh took {stopwatch.Elapsed.TotalSeconds:0.00}s.");
            foreach (var record in refreshed)
            {
                if (record.Metadata.AssetId == changed.Metadata.AssetId)
                {
                    Assert.NotEqual(sidecarTimes[record.Metadata.AssetId], File.GetLastWriteTimeUtc(record.MetadataFullPath));
                }
                else
                {
                    Assert.Equal(sidecarTimes[record.Metadata.AssetId], File.GetLastWriteTimeUtc(record.MetadataFullPath));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AssetDatabase CreateDatabase(string directory) => new(
        Guid.Parse("FC72CC3A-7106-4FA7-8364-D07667381C6E"),
        directory,
        "Assets",
        Path.Combine(directory, "Cache"));

    private static string CreateWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"skinny-assets-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        return directory;
    }

    private static byte[] CreateBoundsGlb()
    {
        const string json = """
        {
          "asset": { "version": "2.0" },
          "accessors": [
            { "componentType": 5126, "count": 8, "type": "VEC3", "min": [-1, -2, -3], "max": [1, 2, 3] }
          ],
          "meshes": [
            { "primitives": [ { "attributes": { "POSITION": 0 } } ] }
          ],
          "nodes": [
            { "mesh": 0, "translation": [4, 0, 0] }
          ],
          "scenes": [ { "nodes": [0] } ],
          "scene": 0
        }
        """;
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var paddedJsonLength = (jsonBytes.Length + 3) & ~3;
        var result = new byte[12 + 8 + paddedJsonLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 0x4E4F534A);
        jsonBytes.CopyTo(result.AsSpan(20));
        result.AsSpan(20 + jsonBytes.Length).Fill(0x20);
        return result;
    }
}
