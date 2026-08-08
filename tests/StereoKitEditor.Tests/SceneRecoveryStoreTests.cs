using StereoKitEditor.Core;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class SceneRecoveryStoreTests
{
    [Fact]
    public async Task Recovery_round_trips_and_clear_removes_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-recovery-{Guid.NewGuid():N}");
        try
        {
            var scenePath = Path.Combine(root, "project", "Scenes", "Main.skscene.json");
            var document = CreateScene("Recovered name");
            await SceneSerializer.SaveAtomicAsync(CreateScene("Saved name"), scenePath, TestContext.Current.CancellationToken);
            using var store = new SceneRecoveryStore(Guid.NewGuid(), scenePath, Path.Combine(root, "local"));

            store.Schedule(document, revision: 42);
            store.Flush();

            var recovery = store.TryLoad();
            Assert.NotNull(recovery);
            Assert.Equal(42, recovery.Revision);
            Assert.Equal("Recovered name", recovery.Document.Name);
            Assert.False(recovery.SourceChangedSinceCapture);

            store.Clear();
            Assert.Null(store.TryLoad());
            Assert.False(File.Exists(store.RecoveryPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_reports_when_saved_scene_changed_after_capture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-recovery-{Guid.NewGuid():N}");
        try
        {
            var scenePath = Path.Combine(root, "project", "Scenes", "Main.skscene.json");
            await SceneSerializer.SaveAtomicAsync(CreateScene("Initial"), scenePath, TestContext.Current.CancellationToken);
            using var store = new SceneRecoveryStore(Guid.NewGuid(), scenePath, Path.Combine(root, "local"));
            store.Schedule(CreateScene("Unsaved"), revision: 2);
            store.Flush();

            File.SetLastWriteTimeUtc(scenePath, File.GetLastWriteTimeUtc(scenePath).AddSeconds(5));

            Assert.True(store.TryLoad()!.SourceChangedSinceCapture);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SceneDocument CreateScene(string name) => new()
    {
        Name = name,
        Roots =
        [
            new SceneEntity
            {
                Name = "Object",
            },
        ],
    };
}
