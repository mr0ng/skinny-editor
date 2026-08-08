using StereoKitEditor.App.Services;

namespace StereoKitEditor.Tests;

public sealed class DebouncedFileWatcherTests
{
    [Theory]
    [InlineData("Source/Program.cs", true)]
    [InlineData("Source/App.csproj", true)]
    [InlineData("Source/obj/Generated.cs", false)]
    [InlineData("Source/bin/Debug/App.dll", false)]
    [InlineData("Assets/model.glb", false)]
    public void ProjectSourceFilter_ExcludesGeneratedAndAssetFiles(string path, bool expected) =>
        Assert.Equal(expected, DebouncedFileWatcher.IsProjectSourcePath(Path.Combine(Path.GetTempPath(), path)));

    [Fact]
    public async Task Watcher_CoalescesRapidChangesIntoOneBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var watcher = new DebouncedFileWatcher(
                root,
                DebouncedFileWatcher.IsProjectSourcePath,
                TimeSpan.FromMilliseconds(100));
            var received = new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.FilesChanged += (_, paths) => received.TrySetResult(paths);
            watcher.IsEnabled = true;
            var source = Path.Combine(root, "Program.cs");

            await File.WriteAllTextAsync(source, "// first", TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(source, "\n// second", TestContext.Current.CancellationToken);
            var paths = await received.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(source), paths[0], ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
