using StereoKitEditor.App.Services;

namespace StereoKitEditor.Tests;

public sealed class EditorLayoutSettingsTests
{
    [Fact]
    public async Task LayoutSettings_RoundTripAtomicallyAndClampUnsafeDimensions()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"skinny-layout-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(root, "layout.json");
        try
        {
            var service = new EditorLayoutSettingsService(path);
            await service.SaveAsync(
                new EditorLayoutSettings(10, 4_000, 20, 5_000),
                TestContext.Current.CancellationToken);

            var loaded = service.Load();
            Assert.Equal(140, loaded.HierarchyWidth);
            Assert.Equal(700, loaded.InspectorWidth);
            Assert.Equal(100, loaded.BottomHeight);
            Assert.Equal(1_200, loaded.ProjectWidth);
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
