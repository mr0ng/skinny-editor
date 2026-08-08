using StereoKitEditor.App.Services;

namespace StereoKitEditor.Tests;

public sealed class RecentProjectsServiceTests
{
    [Fact]
    public void RecordOpened_DeduplicatesAndPromotesTheLatestProject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-recent-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var first = Path.Combine(root, "First.skproject.json");
            var second = Path.Combine(root, "Second.skproject.json");
            File.WriteAllText(first, "{}");
            File.WriteAllText(second, "{}");
            var service = new RecentProjectsService(Path.Combine(root, "recent.json"));

            service.RecordOpened(first, "First");
            service.RecordOpened(second, "Second");
            service.RecordOpened(first, "First renamed");

            var entries = service.Load();
            Assert.Equal(2, entries.Count);
            Assert.Equal(Path.GetFullPath(first), entries[0].Path);
            Assert.Equal("First renamed", entries[0].Name);
            Assert.True(entries[0].Exists);
            Assert.Equal(Path.GetFullPath(second), entries[1].Path);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
