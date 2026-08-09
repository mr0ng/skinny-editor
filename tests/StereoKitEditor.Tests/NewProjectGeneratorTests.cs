using System.Text.Json;
using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.Tests;

public sealed class NewProjectGeneratorTests
{
    private static readonly string[] SdkPackageIds =
    [
        "SKinny.Editor.Adapter",
        "SKinny.Editor.Scene",
        "SKinny.Editor.Protocol",
        "SKinny.Editor.Runtime",
    ];

    [Fact]
    public void Create_GeneratesCompleteRenamedProjectsWithFreshIds()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstParent = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var secondParent = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            var sdkDirectory = CreateSdkPackages(root);
            var generator = new NewProjectGenerator(FindTemplateDirectory(), sdkDirectory);

            var first = generator.Create(new NewProjectRequest("StarGarden", firstParent));
            var second = generator.Create(new NewProjectRequest("StarGarden", secondParent));

            Assert.Equal(Path.Combine(firstParent, "StarGarden"), first.ProjectDirectory);
            Assert.True(File.Exists(Path.Combine(first.ProjectDirectory, "StarGarden.sln")));
            Assert.True(File.Exists(Path.Combine(first.ProjectDirectory, "StarGarden.csproj")));
            Assert.True(File.Exists(Path.Combine(first.ProjectDirectory, "Program.cs")));
            Assert.True(File.Exists(Path.Combine(first.ProjectDirectory, "EditorAdapter.cs")));
            Assert.True(File.Exists(Path.Combine(first.ProjectDirectory, "Assets", ".gitkeep")));
            Assert.True(File.Exists(Path.Combine(first.ProjectDirectory, "Scenes", "Main.skscene.json")));
            Assert.Equal(SdkPackageIds.Length, Directory.EnumerateFiles(
                Path.Combine(first.ProjectDirectory, ".skinny", "sdk"),
                "*.nupkg").Count());

            var definition = EditorProjectDefinition.Load(first.DescriptorPath);
            Assert.Equal("StarGarden", definition.Name);
            Assert.Equal(Path.Combine(first.ProjectDirectory, "StarGarden.csproj"),
                definition.CreateRuntimeProjectSpec().ProjectPath);

            var firstDescriptorId = ReadGuid(first.DescriptorPath, "projectId");
            var secondDescriptorId = ReadGuid(second.DescriptorPath, "projectId");
            var firstSceneId = ReadGuid(
                Path.Combine(first.ProjectDirectory, "Scenes", "Main.skscene.json"),
                "sceneId");
            var secondSceneId = ReadGuid(
                Path.Combine(second.ProjectDirectory, "Scenes", "Main.skscene.json"),
                "sceneId");
            Assert.NotEqual(firstDescriptorId, secondDescriptorId);
            Assert.NotEqual(firstSceneId, secondSceneId);

            foreach (var file in Directory.EnumerateFiles(
                         first.ProjectDirectory,
                         "*",
                         SearchOption.AllDirectories)
                         .Where(path => !path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.DoesNotContain("__PROJECT_NAME__", File.ReadAllText(file), StringComparison.Ordinal);
            }

            Assert.Contains(
                "namespace StarGarden;",
                File.ReadAllText(Path.Combine(first.ProjectDirectory, "Program.cs")),
                StringComparison.Ordinal);
            Assert.Contains(
                $"Version=\"{NewProjectGenerator.CurrentSdkVersion}\"",
                File.ReadAllText(Path.Combine(first.ProjectDirectory, "StarGarden.csproj")),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("9Lives")]
    [InlineData("Hello World")]
    [InlineData("Project-Name")]
    [InlineData("namespace")]
    public void ValidateProjectName_RejectsNamesThatCannotBeUsedAsANamespace(string name)
    {
        Assert.Throws<ArgumentException>(() => NewProjectGenerator.ValidateProjectName(name));
    }

    [Fact]
    public void Create_RefusesToOverwriteAnExistingDestination()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var destination = Directory.CreateDirectory(Path.Combine(root, "ExistingProject")).FullName;
            var sentinel = Path.Combine(destination, "keep.txt");
            File.WriteAllText(sentinel, "keep me");
            var generator = new NewProjectGenerator(FindTemplateDirectory(), CreateSdkPackages(root));

            var exception = Assert.Throws<IOException>(() => generator.Create(
                new NewProjectRequest("ExistingProject", root)));

            Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep me", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_RemovesStagingDirectoryWhenTemplateExpansionFails()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var template = Directory.CreateDirectory(Path.Combine(root, "template")).FullName;
            File.WriteAllText(Path.Combine(template, "broken.txt"), "__UNKNOWN_TOKEN__");
            var generator = new NewProjectGenerator(template, CreateSdkPackages(root));

            var exception = Assert.Throws<InvalidDataException>(() => generator.Create(
                new NewProjectRequest("BrokenProject", root)));

            Assert.Contains("unknown token", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "BrokenProject")));
            Assert.Empty(Directory.EnumerateDirectories(root, ".skinny-new-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skinny-new-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSdkPackages(string root)
    {
        var path = Directory.CreateDirectory(Path.Combine(root, $"sdk-{Guid.NewGuid():N}")).FullName;
        foreach (var packageId in SdkPackageIds)
        {
            File.WriteAllText(
                Path.Combine(path, $"{packageId}.{NewProjectGenerator.CurrentSdkVersion}.nupkg"),
                string.Empty);
        }

        return path;
    }

    private static Guid ReadGuid(string path, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(propertyName).GetGuid();
    }

    private static string FindTemplateDirectory()
    {
        var workspaceRoot = FindWorkspaceRoot();
        return Path.Combine(workspaceRoot, "templates", "StereoKitApp", "1");
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StereoKitEditor.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate StereoKitEditor.sln from the test output directory.");
    }
}
