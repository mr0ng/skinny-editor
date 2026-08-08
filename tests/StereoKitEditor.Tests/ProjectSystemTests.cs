using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.Tests;

public sealed class ProjectSystemTests
{
    [Fact]
    public void StartupProjectLocator_PrefersExplicitArgumentAndSupportsRelativePaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"skinny-project-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "Projects"));
        var descriptor = Path.Combine(directory, "Projects", "Chosen.skproject.json");
        File.WriteAllText(descriptor, "{}");
        try
        {
            var resolved = EditorProjectLocator.ResolveStartupProject(
                directory,
                ["skinny.exe", "--project", "Projects/Chosen.skproject.json"],
                "ignored.skproject.json");

            Assert.Equal(Path.GetFullPath(descriptor), resolved);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Definition_ResolvesNormalExternalProject()
    {
        var root = FindWorkspaceRoot();
        var definition = EditorProjectDefinition.Load(
            Path.Combine(root, "samples", "HelloEditor", "HelloEditor.skproject.json"));

        var runtime = definition.CreateRuntimeProjectSpec();

        Assert.Equal("Hello Editor", definition.Name);
        Assert.Equal("Debug", runtime.Configuration);
        Assert.EndsWith(
            Path.Combine("samples", "HelloStereoKitProject", "HelloStereoKitProject.csproj"),
            runtime.ProjectPath);
        Assert.EndsWith(
            Path.Combine("samples", "HelloEditor", "Scenes", "Main.skscene.json"),
            definition.ResolveStartupScenePath());
    }

    [Fact]
    public async Task Builder_BuildsNormalProjectAndFindsRunnableAssembly()
    {
        var root = FindWorkspaceRoot();
        var definition = EditorProjectDefinition.Load(
            Path.Combine(root, "samples", "HelloEditor", "HelloEditor.skproject.json"));
        var output = new List<DotnetBuildOutput>();
        var cache = Path.Combine(Path.GetTempPath(), $"skeditor-cache-test-{Guid.NewGuid():N}");

        try
        {
            var result = await new DotnetProjectBuilder(cache).BuildAsync(
                definition.CreateRuntimeProjectSpec(),
                output.Add,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(result.TargetPath));
            Assert.EndsWith("HelloStereoKitProject.dll", result.TargetPath);
            Assert.StartsWith(cache, result.GenerationDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(result.BuildId, Path.GetFileName(result.GenerationDirectory));
            Assert.NotEqual(result.SourceTargetPath, result.TargetPath);
            Assert.Contains(output, line => line.Text.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Builder_SurfacesCompilerFailureOutput()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"skeditor-build-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var projectPath = Path.Combine(temporaryDirectory, "BrokenProject.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, "Broken.cs"),
                "internal static class Broken { private static void Fail() { MissingType value; } }",
                TestContext.Current.CancellationToken);
            var output = new List<DotnetBuildOutput>();

            var exception = await Assert.ThrowsAsync<DotnetBuildException>(() =>
                new DotnetProjectBuilder().BuildAsync(
                    new RuntimeProjectSpec(projectPath, "Debug"),
                    output.Add,
                    TestContext.Current.CancellationToken));

            Assert.NotEqual(0, exception.ExitCode);
            Assert.Contains(
                output,
                line => line.IsError && line.Text.Contains("error CS", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DiagnosticBundles_AreRedactedAndRetentionIsBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-diagnostics-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        try
        {
            var writer = new DiagnosticBundleWriter(root, retainedBundleCount: 2);
            string? latest = null;
            for (var index = 0; index < 3; index++)
            {
                latest = await writer.WriteAsync(new DiagnosticBundleInput(
                    projectId,
                    "Fixture",
                    Path.Combine(root, "Fixture.skproject.json"),
                    "Scene",
                    $"Crash {index}",
                    "editor",
                    "build-id",
                    -1,
                    "{\"formatVersion\":2}",
                    Enumerable.Range(0, 510).Select(line => $"line {line}").ToArray(),
                    ["SECRET_TOKEN"],
                    DateTimeOffset.UtcNow.AddMilliseconds(index)),
                    TestContext.Current.CancellationToken);
            }

            var projectDirectory = Path.Combine(root, projectId.ToString("N"));
            Assert.Equal(2, Directory.EnumerateDirectories(projectDirectory).Count());
            var manifest = await File.ReadAllTextAsync(
                Path.Combine(latest!, "manifest.json"),
                TestContext.Current.CancellationToken);
            Assert.Contains("SECRET_TOKEN", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", manifest, StringComparison.Ordinal);
            Assert.Equal(500, (await File.ReadAllLinesAsync(
                Path.Combine(latest!, "runtime.log"),
                TestContext.Current.CancellationToken)).Length);
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
    public void GenerationRetention_PreservesProtectedAndNewestUnused()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-generations-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(root, "Fixture.csproj");
        Directory.CreateDirectory(root);
        File.WriteAllText(projectPath, "<Project />");
        var project = new RuntimeProjectSpec(projectPath, "Debug");
        var profileDirectory = Path.Combine(root, project.ProjectId.ToString("N"), project.ProfileId);
        Directory.CreateDirectory(profileDirectory);
        try
        {
            var generations = Enumerable.Range(0, 5)
                .Select(index => Path.Combine(profileDirectory, $"generation-{index}"))
                .ToArray();
            for (var index = 0; index < generations.Length; index++)
            {
                Directory.CreateDirectory(generations[index]);
                Directory.SetLastWriteTimeUtc(generations[index], DateTime.UtcNow.AddMinutes(index));
            }

            var removed = new DotnetProjectBuilder(root).PruneGenerations(
                project,
                [generations[0]],
                retainedUnusedGenerations: 2);

            Assert.True(Directory.Exists(generations[0]));
            Assert.True(Directory.Exists(generations[4]));
            Assert.True(Directory.Exists(generations[3]));
            Assert.Equal(2, removed.Count);
            Assert.False(Directory.Exists(generations[1]));
            Assert.False(Directory.Exists(generations[2]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AndroidDeploymentPlan_TargetsSelectedDeviceAndProjectRelativeApk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profile = new EditorProjectDefinition.DeploymentProfileDefinition
            {
                Id = "quest",
                DisplayName = "Test headset",
                Project = "App/App.csproj",
                ApkPath = "App/bin/Release/net8.0-android/publish/app.apk",
                PackageName = "com.example.skinnyfixture",
                DeviceSerial = "DEVICE-123",
            };

            var arguments = AndroidAdbDeploymentProvider.CreateInstallArguments(profile, root);

            Assert.Equal(["-s", "DEVICE-123", "install", "-r"], arguments.Take(4));
            Assert.Equal(
                Path.Combine(root, "App", "bin", "Release", "net8.0-android", "publish", "app.apk"),
                arguments[4]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
