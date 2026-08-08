using StereoKitEditor.Core;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class SceneTemplateLibraryTests
{
    [Fact]
    public async Task SaveDiscoverAndInstantiate_PreservesDataAndRegeneratesAllIds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-template-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var child = new SceneEntity { Name = "Child" };
            child.Components.PrimitiveMeshRenderer = new();
            var source = new SceneEntity { Name = "Reusable", Children = { child } };
            var library = new SceneTemplateLibrary(root);

            var saved = await library.SaveAsync(source, "Reusable", TestContext.Current.CancellationToken);
            var discovered = Assert.Single(library.Discover());
            var first = library.Instantiate(saved.Path);
            var second = library.Instantiate(saved.Path);

            Assert.Equal(saved.TemplateId, discovered.TemplateId);
            Assert.Equal("Reusable", first.Name);
            Assert.NotEqual(source.Id, first.Id);
            Assert.NotEqual(first.Id, second.Id);
            Assert.NotEqual(first.Children[0].Id, second.Children[0].Id);
            Assert.Empty(first.Traverse().SelectMany(entity => entity.Components.Records).Select(component => component.Id)
                .Intersect(second.Traverse().SelectMany(entity => entity.Components.Records).Select(component => component.Id)));
            Assert.NotNull(first.Children[0].Components.PrimitiveMeshRenderer);
            Assert.Empty(Directory.EnumerateFiles(library.Root, "*.tmp"));
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
