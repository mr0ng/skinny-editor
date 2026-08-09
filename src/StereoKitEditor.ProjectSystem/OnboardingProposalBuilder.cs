using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StereoKitEditor.ProjectSystem;

public sealed class OnboardingProposalBuilder(string? runtimePackageVersion = null)
{
    public string RuntimePackageVersion { get; } = runtimePackageVersion ?? GetRuntimePackageVersion();

    public OnboardingProposal Create(
        ExistingProjectAnalysis analysis,
        OnboardingIntegrationShape integrationShape)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Compatibility is ExistingProjectCompatibility.ReadyToOpen
            or ExistingProjectCompatibility.Unsupported)
        {
            throw new InvalidOperationException(
                $"A scaffolding proposal is not available for a {analysis.Compatibility} analysis.");
        }

        var startupProject = SelectStartupProject(analysis);
        if (integrationShape == OnboardingIntegrationShape.DirectOptIn
            && !startupProject.TargetFrameworks.Any(
                ExistingStereoKitProjectAnalyzer.IsEditorRuntimeCompatibleFramework))
        {
            throw new InvalidOperationException(
                "Direct opt-in requires a net8.0-or-newer startup target. Choose a dedicated editor head.");
        }

        var proposalKey = $"{Path.GetFullPath(analysis.ProjectRoot)}|{startupProject.Path}|{integrationShape}|{RuntimePackageVersion}";
        var proposalId = CreateStableGuid($"proposal|{proposalKey}");
        var projectId = CreateStableGuid($"project|{proposalKey}");
        var sceneId = CreateStableGuid($"scene|{proposalKey}");
        var safeName = CreateSafeName(startupProject.Name);
        var onboardingDirectory = "SKinnyEditor";
        var descriptorRelativePath = Path.Combine(onboardingDirectory, $"{safeName}.skproject.json");
        var changes = new List<OnboardingProposedChange>();

        if (integrationShape == OnboardingIntegrationShape.DirectOptIn)
        {
            AddDirectOptInChanges(analysis, startupProject, changes);
        }
        else
        {
            AddDedicatedHeadChanges(analysis.ProjectRoot, startupProject, safeName, changes);
        }

        AddCreate(
            analysis.ProjectRoot,
            changes,
            descriptorRelativePath,
            "Create the explicit SKinny project descriptor.",
            CreateDescriptor(analysis, startupProject, integrationShape, safeName, projectId));
        AddCreate(
            analysis.ProjectRoot,
            changes,
            Path.Combine(onboardingDirectory, "Scenes", "Main.skscene.json"),
            "Create an empty, source-readable initial scene.",
            CreateScene(sceneId, startupProject.Name));
        AddCreate(
            analysis.ProjectRoot,
            changes,
            Path.Combine(onboardingDirectory, "Assets", ".gitkeep"),
            "Materialize the project-controlled authoring asset root.",
            string.Empty);

        var impact = integrationShape == OnboardingIntegrationShape.DirectOptIn
            ? new[]
            {
                "Adds one pinned runtime package reference and isolated onboarding source files.",
                "Does not replace the existing application entry point or normal launch path.",
                "The generated descriptor remains project-controlled and can be removed through rollback.",
            }
            : new[]
            {
                "Creates a separate editor-only executable that references the selected production project.",
                "Does not modify the selected production project or its composition root.",
                "The normal command-line and IDE launch path remains unchanged.",
            };
        var manualWork = integrationShape == OnboardingIntegrationShape.DirectOptIn
            ? new[]
            {
                "Review and add the generated EditorEntryPoint.TryRun call before normal StereoKit startup.",
                "Register project-specific component schemas in GeneratedProjectAdapter.",
                "Grant workspace trust before restore, build, Scene, or Play validation.",
            }
            : new[]
            {
                "Register project-specific component schemas in GeneratedProjectAdapter.",
                "Review which production references and assets the dedicated head may access.",
                "Grant workspace trust before restore, build, Scene, or Play validation.",
            };

        return new OnboardingProposal(
            proposalId,
            analysis.ProjectRoot,
            startupProject.Path,
            analysis.Compatibility,
            analysis.Summary,
            analysis.Reasons,
            analysis.Warnings,
            analysis.AuthorableContent,
            analysis.OpaqueContent,
            analysis.Prerequisites,
            integrationShape,
            RuntimePackageVersion,
            descriptorRelativePath,
            changes.OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            impact,
            manualWork);
    }

    private void AddDirectOptInChanges(
        ExistingProjectAnalysis analysis,
        InspectedDotnetProject startupProject,
        ICollection<OnboardingProposedChange> changes)
    {
        if (!startupProject.ReferencesEditorRuntime)
        {
            var packageVersionInProject = !startupProject.UsesCentralPackageManagement;
            AddPackageReferenceChange(
                analysis.ProjectRoot,
                startupProject.Path,
                packageVersionInProject ? RuntimePackageVersion : null,
                changes);

            if (!packageVersionInProject)
            {
                var centralFile = analysis.PackageConfigurationPaths
                    .Where(path => string.Equals(
                        Path.GetFileName(path),
                        "Directory.Packages.props",
                        StringComparison.OrdinalIgnoreCase))
                    .Where(path => ExistingStereoKitProjectAnalyzer.IsWithinRoot(
                        Path.GetDirectoryName(path)!,
                        startupProject.Path))
                    .OrderByDescending(path => Path.GetDirectoryName(path)!.Length)
                    .FirstOrDefault();
                if (centralFile is null)
                {
                    throw new InvalidOperationException(
                        "Direct opt-in uses central package management, but no in-scope Directory.Packages.props was found. Choose a dedicated editor head or configure the package version manually.");
                }

                AddCentralPackageVersionChange(analysis.ProjectRoot, centralFile, changes);
            }
        }

        AddCreate(
            analysis.ProjectRoot,
            changes,
            Path.Combine("SKinnyEditor", "EditorAdapter.cs"),
            "Add an isolated runtime entry helper and an empty project adapter.",
            CreateAdapterSource(includeMain: false));
        AddCreate(
            analysis.ProjectRoot,
            changes,
            Path.Combine("SKinnyEditor", "README.md"),
            "Record the normal/editor launch boundary and remaining manual integration.",
            CreateDirectReadme());
    }

    private void AddDedicatedHeadChanges(
        string projectRoot,
        InspectedDotnetProject startupProject,
        string safeName,
        ICollection<OnboardingProposedChange> changes)
    {
        var relativeProductionProject = NormalizeProjectPath(Path.GetRelativePath(
            Path.Combine(projectRoot, "SKinnyEditor"),
            startupProject.Path));
        var targetFramework = SelectDedicatedTargetFramework(startupProject.TargetFrameworks);
        AddCreate(
            projectRoot,
            changes,
            Path.Combine("SKinnyEditor", $"{safeName}.SKinny.Editor.csproj"),
            "Create an editor-only executable without changing the production composition root.",
            CreateDedicatedProject(targetFramework, relativeProductionProject, RuntimePackageVersion));
        AddCreate(
            projectRoot,
            changes,
            Path.Combine("SKinnyEditor", "Program.cs"),
            "Create the dedicated editor runtime entry point and empty adapter.",
            CreateAdapterSource(includeMain: true));
        AddCreate(
            projectRoot,
            changes,
            Path.Combine("SKinnyEditor", "README.md"),
            "Document the generated boundary and remaining adapter work.",
            CreateDedicatedReadme());
    }

    private void AddPackageReferenceChange(
        string projectRoot,
        string projectPath,
        string? version,
        ICollection<OnboardingProposedChange> changes)
    {
        var original = ReadUtf8Text(projectPath);
        var versionAttribute = version is null
            ? string.Empty
            : $" Version=\"{SecurityElement.Escape(version)}\"";
        var fragment =
            $"  <ItemGroup>{original.NewLine}" +
            $"    <PackageReference Include=\"SKinny.Editor.Runtime\"{versionAttribute} />{original.NewLine}" +
            $"  </ItemGroup>{original.NewLine}";
        var proposed = InsertBeforeProjectEnd(original.Text, fragment);
        AddModify(
            projectRoot,
            changes,
            projectPath,
            "Add the pinned SKinny runtime SDK reference.",
            original,
            proposed);
    }

    private void AddCentralPackageVersionChange(
        string projectRoot,
        string centralFile,
        ICollection<OnboardingProposedChange> changes)
    {
        var original = ReadUtf8Text(centralFile);
        if (Regex.IsMatch(
                original.Text,
                "(?:Include|Update)\\s*=\\s*['\"]SKinny\\.Editor\\.Runtime['\"]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return;
        }

        var fragment =
            $"  <ItemGroup>{original.NewLine}" +
            $"    <PackageVersion Include=\"SKinny.Editor.Runtime\" Version=\"{SecurityElement.Escape(RuntimePackageVersion)}\" />{original.NewLine}" +
            $"  </ItemGroup>{original.NewLine}";
        AddModify(
            projectRoot,
            changes,
            centralFile,
            "Pin the matching runtime SDK in central package management.",
            original,
            InsertBeforeProjectEnd(original.Text, fragment));
    }

    private static string CreateDedicatedProject(
        string targetFramework,
        string productionProjectPath,
        string runtimePackageVersion) =>
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{{targetFramework}}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="SKinny.Editor.Runtime" Version="{{runtimePackageVersion}}" />
            <ProjectReference Include="{{productionProjectPath}}" />
          </ItemGroup>
        </Project>
        """ + Environment.NewLine;

    private static string CreateAdapterSource(bool includeMain)
    {
        var main = includeMain
            ? """
              internal static class Program
              {
                  private static int Main(string[] args)
                  {
                      if (!EditorRuntimeHost.IsEditorLaunch(args))
                      {
                          Console.Error.WriteLine("This executable is an editor-only head. Run the production project for the normal application.");
                          return 2;
                      }

                      return EditorRuntimeHost.Run(args, new GeneratedProjectAdapter());
                  }
              }

              """
            : """
              public static class EditorEntryPoint
              {
                  public static bool TryRun(string[] args, out int exitCode)
                  {
                      if (!EditorRuntimeHost.IsEditorLaunch(args))
                      {
                          exitCode = 0;
                          return false;
                      }

                      exitCode = EditorRuntimeHost.Run(args, new GeneratedProjectAdapter());
                      return true;
                  }
              }

              """;
        return $$"""
        using StereoKitEditor.Adapter;
        using StereoKitEditor.Runtime;

        namespace SKinnyOnboarding;

        {{main}}
        internal sealed class GeneratedProjectAdapter : IEditorProjectAdapter
        {
            public string Id => "com.project.skinny-onboarding";
            public string DisplayName => "Onboarded StereoKit Project";
            public string Version => "0.1.0";

            public void Configure(EditorAdapterBuilder builder)
            {
                // Register project-owned component descriptors and runtimes here.
            }

            public void Initialize(EditorProjectRuntimeContext context) { }
            public void Step(EditorProjectRuntimeContext context) { }
            public void Shutdown(EditorProjectRuntimeContext context) { }
        }
        """ + Environment.NewLine;
    }

    private static string CreateDescriptor(
        ExistingProjectAnalysis analysis,
        InspectedDotnetProject startupProject,
        OnboardingIntegrationShape shape,
        string safeName,
        Guid projectId)
    {
        var descriptorDirectory = Path.Combine(analysis.ProjectRoot, "SKinnyEditor");
        var solution = analysis.SolutionPath ?? startupProject.Path;
        var runtimeProject = shape == OnboardingIntegrationShape.DedicatedEditorHead
            ? $"{safeName}.SKinny.Editor.csproj"
            : NormalizeProjectPath(Path.GetRelativePath(descriptorDirectory, startupProject.Path));
        var targetFramework = shape == OnboardingIntegrationShape.DedicatedEditorHead
            ? SelectDedicatedTargetFramework(startupProject.TargetFrameworks)
            : startupProject.TargetFrameworks.FirstOrDefault();
        var workingDirectory = shape == OnboardingIntegrationShape.DedicatedEditorHead
            ? "."
            : NormalizeProjectPath(Path.GetRelativePath(
                descriptorDirectory,
                Path.GetDirectoryName(startupProject.Path)!));
        var descriptor = new
        {
            formatVersion = 2,
            projectId,
            name = startupProject.Name,
            solution = NormalizeProjectPath(Path.GetRelativePath(descriptorDirectory, solution)),
            assetsRoot = "Assets",
            scenesRoot = "Scenes",
            startupScene = "Scenes/Main.skscene.json",
            defaultSceneProfile = "editor-desktop",
            defaultPlayProfile = "editor-desktop",
            runtimeProfiles = new[]
            {
                new
                {
                    id = "editor-desktop",
                    displayName = "Editor Desktop",
                    project = runtimeProject,
                    configuration = "Debug",
                    targetFramework,
                    workingDirectory,
                    arguments = Array.Empty<string>(),
                    environment = new Dictionary<string, string>(),
                    modes = new[] { "Scene", "Play" },
                },
            },
        };
        return JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true })
               + Environment.NewLine;
    }

    private static string CreateScene(Guid sceneId, string projectName) =>
        JsonSerializer.Serialize(
            new { formatVersion = 2, sceneId, name = $"{projectName} Main", roots = Array.Empty<object>() },
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static string CreateDirectReadme() =>
        """
        # SKinny Editor integration

        The normal application entry point has not been replaced. Before normal StereoKit startup,
        route explicit editor launches through the generated helper:

        ```csharp
        if (SKinnyOnboarding.EditorEntryPoint.TryRun(args, out var editorExitCode))
        {
            return editorExitCode;
        }
        ```

        Register only explicitly authorable project components in `GeneratedProjectAdapter.Configure`.
        Procedural runtime objects remain owned by the normal application and are not inferred by the editor.
        """ + Environment.NewLine;

    private static string CreateDedicatedReadme() =>
        """
        # SKinny Editor dedicated head

        This editor-only executable leaves the production composition root unchanged. It references the
        selected project so an adapter can expose a bounded set of production components and assets.

        Register only explicitly authorable project components in `GeneratedProjectAdapter.Configure`.
        Review project references and asset access before granting workspace trust and building the head.
        """ + Environment.NewLine;

    private static InspectedDotnetProject SelectStartupProject(ExistingProjectAnalysis analysis) =>
        analysis.Projects.FirstOrDefault(project =>
            project.ReferencesStereoKit && (project.OutputType is "Exe" or "WinExe"))
        ?? analysis.Projects.FirstOrDefault(project => project.ReferencesStereoKit)
        ?? throw new InvalidOperationException("The analysis has no StereoKit project to onboard.");

    private static string SelectDedicatedTargetFramework(IReadOnlyList<string> frameworks) =>
        frameworks.FirstOrDefault(ExistingStereoKitProjectAnalyzer.IsEditorRuntimeCompatibleFramework)
        ?? "net8.0";

    private static string InsertBeforeProjectEnd(string text, string fragment)
    {
        var index = text.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidDataException("The MSBuild file has no closing Project element.");
        }

        return text.Insert(index, fragment);
    }

    private static void AddCreate(
        string projectRoot,
        ICollection<OnboardingProposedChange> changes,
        string relativePath,
        string purpose,
        string proposedText)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var target = Path.GetFullPath(Path.Combine(projectRoot, normalized));
        if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(projectRoot, target))
        {
            throw new InvalidDataException($"Proposed path escapes the selected project root: {relativePath}");
        }

        var proposedBytes = EncodeUtf8(proposedText, writeBom: false);
        changes.Add(new OnboardingProposedChange(
            OnboardingChangeKind.Create,
            normalized,
            purpose,
            null,
            Hash(proposedBytes),
            null,
            proposedText,
            OnboardingTextDiff.Create(normalized, null, proposedText),
            false));
    }

    private static void AddModify(
        string projectRoot,
        ICollection<OnboardingProposedChange> changes,
        string path,
        string purpose,
        Utf8Text original,
        string proposedText)
    {
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(projectRoot, path));
        if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(projectRoot, path))
        {
            throw new InvalidDataException($"Modified path escapes the selected project root: {path}");
        }

        changes.Add(new OnboardingProposedChange(
            OnboardingChangeKind.Modify,
            relativePath,
            purpose,
            Hash(original.Bytes),
            Hash(EncodeUtf8(proposedText, original.HasBom)),
            original.Text,
            proposedText,
            OnboardingTextDiff.Create(relativePath, original.Text, proposedText),
            original.HasBom));
    }

    private static Utf8Text ReadUtf8Text(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(
                bytes.AsSpan(hasBom ? Encoding.UTF8.Preamble.Length : 0));
            var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return new(bytes, text, hasBom, newLine);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"'{path}' is not UTF-8. Onboarding will not rewrite an unsupported encoding.",
                exception);
        }
    }

    internal static byte[] EncodeUtf8(string text, bool writeBom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (!writeBom)
        {
            return body;
        }

        var preamble = Encoding.UTF8.Preamble;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string NormalizeRelativePath(string path) => path
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
        .TrimStart(Path.DirectorySeparatorChar);

    private static string NormalizeProjectPath(string path) => path.Replace('\\', '/');

    private static string CreateSafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "StereoKitProject" : safe;
    }

    private static Guid CreateStableGuid(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()), hash);
        return new Guid(hash[..16]);
    }

    private static string GetRuntimePackageVersion()
    {
        var assembly = typeof(OnboardingProposalBuilder).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version)
            ? assembly.GetName().Version?.ToString(3) ?? "0.3.0-preview.1"
            : version;
    }

    private sealed record Utf8Text(byte[] Bytes, string Text, bool HasBom, string NewLine);
}

internal static class OnboardingTextDiff
{
    public static string Create(string path, string? original, string proposed)
    {
        var oldLines = SplitLines(original ?? string.Empty);
        var newLines = SplitLines(proposed);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
               && string.Equals(
                   oldLines[oldLines.Length - suffix - 1],
                   newLines[newLines.Length - suffix - 1],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        var builder = new StringBuilder()
            .Append("--- a/").AppendLine(path.Replace('\\', '/'))
            .Append("+++ b/").AppendLine(path.Replace('\\', '/'))
            .Append("@@ -").Append(prefix + 1).Append(',').Append(oldLines.Length - prefix - suffix)
            .Append(" +").Append(prefix + 1).Append(',').Append(newLines.Length - prefix - suffix)
            .AppendLine(" @@");
        foreach (var line in oldLines.Skip(prefix).Take(oldLines.Length - prefix - suffix))
        {
            builder.Append('-').AppendLine(line);
        }

        foreach (var line in newLines.Skip(prefix).Take(newLines.Length - prefix - suffix))
        {
            builder.Append('+').AppendLine(line);
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n');
}
