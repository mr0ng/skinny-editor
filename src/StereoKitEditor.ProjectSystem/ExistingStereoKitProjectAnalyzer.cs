using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StereoKitEditor.ProjectSystem;

/// <summary>
/// Performs a safe, read-only inspection of solution and project files. It deliberately does not
/// invoke MSBuild, restore packages, load project assemblies, or execute application code.
/// </summary>
public sealed partial class ExistingStereoKitProjectAnalyzer
{
    private const int MaximumProjects = 512;
    private const int MaximumSourceFilesPerProject = 4096;
    private static readonly string[] ExcludedDirectoryNames = [".git", "bin", "obj", "packages"];

    public ExistingProjectAnalysis Analyze(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);

        var canonicalSelection = Path.GetFullPath(selectedPath);
        if (!File.Exists(canonicalSelection) && !Directory.Exists(canonicalSelection))
        {
            throw new FileNotFoundException(
                $"The selected project, solution, or directory was not found: {canonicalSelection}",
                canonicalSelection);
        }

        var warnings = new List<string>();
        var (projectRoot, solutionPath, projectPaths) = DiscoverProjects(canonicalSelection, warnings);
        var projects = new List<InspectedDotnetProject>();
        foreach (var projectPath in projectPaths.Take(MaximumProjects))
        {
            try
            {
                projects.Add(InspectProject(projectPath, projectRoot, warnings));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or XmlException or InvalidDataException)
            {
                warnings.Add($"Could not safely inspect '{projectPath}': {exception.Message}");
            }
        }

        if (projectPaths.Count > MaximumProjects)
        {
            warnings.Add($"Only the first {MaximumProjects} project files were inspected.");
        }

        var descriptorPaths = EnumerateFiles(projectRoot, "*.skproject.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var validDescriptors = new List<string>();
        foreach (var descriptorPath in descriptorPaths)
        {
            try
            {
                var definition = EditorProjectDefinition.Load(descriptorPath);
                ValidateExistingDescriptorFiles(definition);
                validDescriptors.Add(descriptorPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Text.Json.JsonException or InvalidDataException)
            {
                warnings.Add($"Existing descriptor '{descriptorPath}' is not valid: {exception.Message}");
            }
        }

        var packageConfigurationPaths = EnumerateNamedFiles(
            projectRoot,
            ["NuGet.config", "nuget.config", "Directory.Packages.props"]);
        var buildCustomizationPaths = EnumerateNamedFiles(
            projectRoot,
            ["Directory.Build.props", "Directory.Build.targets"]);

        var classification = Classify(projects, descriptorPaths, validDescriptors);
        return new ExistingProjectAnalysis(
            canonicalSelection,
            projectRoot,
            solutionPath,
            projects,
            descriptorPaths,
            validDescriptors,
            packageConfigurationPaths,
            buildCustomizationPaths,
            classification.Compatibility,
            classification.Integration,
            classification.Summary,
            classification.Reasons,
            CreateAuthorableContent(projects, validDescriptors),
            CreateOpaqueContent(projects),
            CreatePrerequisites(projects),
            warnings);
    }

    private static void ValidateExistingDescriptorFiles(EditorProjectDefinition definition)
    {
        if (!File.Exists(definition.ResolveSolutionPath()))
        {
            throw new InvalidDataException(
                $"Configured solution was not found: {definition.ResolveSolutionPath()}");
        }

        if (!File.Exists(definition.ResolveStartupScenePath()))
        {
            throw new InvalidDataException(
                $"Configured startup scene was not found: {definition.ResolveStartupScenePath()}");
        }

        var sceneProject = definition.CreateRuntimeProjectSpec(RuntimeProfileMode.Scene).ProjectPath;
        if (!File.Exists(sceneProject))
        {
            throw new InvalidDataException($"Configured Scene runtime project was not found: {sceneProject}");
        }

        var playProject = definition.CreateRuntimeProjectSpec(RuntimeProfileMode.Play).ProjectPath;
        if (!File.Exists(playProject))
        {
            throw new InvalidDataException($"Configured Play runtime project was not found: {playProject}");
        }
    }

    private static (string Root, string? Solution, IReadOnlyList<string> Projects) DiscoverProjects(
        string selectedPath,
        ICollection<string> warnings)
    {
        var isDirectory = Directory.Exists(selectedPath);
        var extension = isDirectory ? string.Empty : Path.GetExtension(selectedPath);
        if (!isDirectory
            && !string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
            && !selectedPath.EndsWith(".skproject.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a .sln, .csproj, .skproject.json, or project directory.");
        }

        var root = isDirectory
            ? selectedPath
            : Path.GetDirectoryName(selectedPath)
              ?? throw new InvalidDataException("The selected path has no parent directory.");
        string? solution = string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            ? selectedPath
            : Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        IReadOnlyList<string> projects;
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            projects = [selectedPath];
        }
        else if (solution is not null)
        {
            projects = ReadSolutionProjects(solution, root, warnings);
        }
        else
        {
            projects = EnumerateFiles(root, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return (Path.GetFullPath(root), solution, projects);
    }

    private static IReadOnlyList<string> ReadSolutionProjects(
        string solutionPath,
        string projectRoot,
        ICollection<string> warnings)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(solutionPath))
        {
            var match = SolutionProjectPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var relativePath = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solutionPath)!, relativePath));
            if (!IsWithinRoot(projectRoot, resolved))
            {
                warnings.Add($"Solution project outside the selected root was not inspected: {resolved}");
                continue;
            }

            if (File.Exists(resolved))
            {
                paths.Add(resolved);
            }
            else
            {
                warnings.Add($"Solution project was not found: {resolved}");
            }
        }

        return paths.ToArray();
    }

    private static InspectedDotnetProject InspectProject(
        string projectPath,
        string projectRoot,
        ICollection<string> warnings)
    {
        var document = LoadXml(projectPath);
        var root = document.Root ?? throw new InvalidDataException("The project XML is empty.");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var centralPackageFile = FindNearestFile(
            projectDirectory,
            projectRoot,
            "Directory.Packages.props");
        var centralVersions = ReadCentralPackageVersions(centralPackageFile, warnings);
        var properties = root.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        var targetFrameworks = Split(properties.GetValueOrDefault("TargetFrameworks"))
            .Concat(Split(properties.GetValueOrDefault("TargetFramework")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runtimeIdentifiers = Split(properties.GetValueOrDefault("RuntimeIdentifiers"))
            .Concat(Split(properties.GetValueOrDefault("RuntimeIdentifier")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packageReferences = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in root.Descendants().Where(element =>
                     element.Name.LocalName == "PackageReference"))
        {
            var name = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var version = reference.Attribute("Version")?.Value
                          ?? reference.Elements().FirstOrDefault(element =>
                              element.Name.LocalName == "Version")?.Value;
            if (string.IsNullOrWhiteSpace(version))
            {
                centralVersions.TryGetValue(name, out version);
            }

            packageReferences[name] = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }

        var projectReferences = root.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.Combine(projectDirectory, value!)))
            .ToArray();
        var sourceShape = InspectSourceShape(projectDirectory, warnings);
        var centralManagement = string.Equals(
            properties.GetValueOrDefault("ManagePackageVersionsCentrally"),
            "true",
            StringComparison.OrdinalIgnoreCase)
            || centralPackageFile is not null;

        return new InspectedDotnetProject(
            projectPath,
            Path.GetFileNameWithoutExtension(projectPath),
            root.Attribute("Sdk")?.Value ?? string.Empty,
            targetFrameworks,
            runtimeIdentifiers,
            properties.GetValueOrDefault("OutputType", "Library"),
            packageReferences,
            projectReferences,
            centralManagement,
            sourceShape.HasStereoKitInitialization,
            sourceShape.HasEditorLaunchHook);
    }

    private static (bool HasStereoKitInitialization, bool HasEditorLaunchHook) InspectSourceShape(
        string directory,
        ICollection<string> warnings)
    {
        var hasInitialization = false;
        var hasHook = false;
        var count = 0;
        foreach (var sourcePath in EnumerateFiles(directory, "*.cs"))
        {
            count++;
            if (count > MaximumSourceFilesPerProject)
            {
                warnings.Add($"Source inspection was capped at {MaximumSourceFilesPerProject} files under '{directory}'.");
                break;
            }

            string source;
            try
            {
                source = File.ReadAllText(sourcePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read source metadata from '{sourcePath}': {exception.Message}");
                continue;
            }

            hasInitialization |= source.Contains("SK.Initialize", StringComparison.Ordinal)
                                 || source.Contains("StereoKit.SK.Initialize", StringComparison.Ordinal);
            hasHook |= source.Contains("EditorRuntimeHost.IsEditorLaunch", StringComparison.Ordinal)
                       && source.Contains("EditorRuntimeHost.Run", StringComparison.Ordinal);
            if (hasInitialization && hasHook)
            {
                break;
            }
        }

        return (hasInitialization, hasHook);
    }

    private static Classification Classify(
        IReadOnlyList<InspectedDotnetProject> projects,
        IReadOnlyList<string> descriptors,
        IReadOnlyList<string> validDescriptors)
    {
        if (validDescriptors.Count > 0)
        {
            return new(
                ExistingProjectCompatibility.ReadyToOpen,
                null,
                "This workspace already contains a valid SKinny Editor project.",
                ["A descriptor passed format and path validation; no scaffolding is required."]);
        }

        if (descriptors.Count > 0)
        {
            return new(
                ExistingProjectCompatibility.ManualIntegrationRequired,
                null,
                "Existing SKinny metadata needs repair before the project can be opened.",
                ["At least one descriptor was found, but none passed validation."]);
        }

        var stereoKitProjects = projects.Where(project => project.ReferencesStereoKit).ToArray();
        if (stereoKitProjects.Length == 0)
        {
            var reason = projects.Count == 0
                ? "No readable .NET projects were found."
                : "No inspected project has a direct StereoKit package reference.";
            return new(
                ExistingProjectCompatibility.Unsupported,
                null,
                "This selection cannot be identified as a StereoKit project through safe inspection.",
                [reason]);
        }

        var desktopExecutables = stereoKitProjects.Where(project =>
                IsExecutable(project.OutputType)
                && project.TargetFrameworks.Any(IsDesktopTargetFramework))
            .ToArray();
        if (desktopExecutables.Length == 0)
        {
            return new(
                ExistingProjectCompatibility.ManualIntegrationRequired,
                OnboardingIntegrationShape.DedicatedEditorHead,
                "StereoKit is present, but no conventional desktop startup project was identified.",
                ["A user must select the production code and assets that a dedicated editor head may reference."]);
        }

        if (desktopExecutables.Length == 1 && stereoKitProjects.Length == 1)
        {
            var startup = desktopExecutables[0];
            if (!startup.TargetFrameworks.Any(IsEditorRuntimeCompatibleFramework))
            {
                return new(
                    ExistingProjectCompatibility.DedicatedEditorHeadRecommended,
                    OnboardingIntegrationShape.DedicatedEditorHead,
                    "A separate net8.0-or-newer editor head is required for this project's target framework.",
                    [
                        $"'{startup.Name}' targets {string.Join(", ", startup.TargetFrameworks)}, which cannot directly reference the current editor runtime package.",
                        "The production target and normal launch path can remain unchanged.",
                    ]);
            }

            var reasons = new List<string>
            {
                $"'{startup.Name}' is a single desktop executable with a StereoKit package reference.",
                "Safe inspection did not evaluate MSBuild targets or execute project code.",
            };
            if (!startup.HasStereoKitInitialization)
            {
                reasons.Add("StereoKit initialization was not recognized in source, so the startup hook must be reviewed manually.");
            }

            return new(
                ExistingProjectCompatibility.DirectOptInSupported,
                OnboardingIntegrationShape.DirectOptIn,
                "This conventional desktop project can opt in while retaining its normal launch path.",
                reasons);
        }

        return new(
            ExistingProjectCompatibility.DedicatedEditorHeadRecommended,
            OnboardingIntegrationShape.DedicatedEditorHead,
            "A separate editor head is the safer integration for this workspace.",
            [
                $"Safe inspection found {stereoKitProjects.Length} StereoKit projects and {desktopExecutables.Length} desktop startup candidates.",
                "A dedicated head avoids modifying production composition roots.",
            ]);
    }

    private static IReadOnlyList<string> CreateAuthorableContent(
        IReadOnlyList<InspectedDotnetProject> projects,
        IReadOnlyList<string> validDescriptors)
    {
        if (validDescriptors.Count > 0 && projects.Any(project => project.HasEditorLaunchHook))
        {
            return ["Components explicitly registered by the existing runtime adapter.", "Scene entities and indexed assets already described by the project."];
        }

        return ["New scene entities and built-in SKinny components after opt-in.", "Assets under the selected authoring root can be indexed without executing project code."];
    }

    private static IReadOnlyList<string> CreateOpaqueContent(IReadOnlyList<InspectedDotnetProject> projects)
    {
        var content = new List<string>
        {
            "Procedural draw calls, dynamic objects, services, and UI created only by application code remain opaque.",
            "Project-specific components are not editable until an adapter registers their schemas.",
        };
        if (projects.Any(project => !project.HasStereoKitInitialization))
        {
            content.Add("Unrecognized startup composition must be connected manually or isolated in a dedicated head.");
        }

        return content;
    }

    private static IReadOnlyList<string> CreatePrerequisites(IReadOnlyList<InspectedDotnetProject> projects)
    {
        var frameworks = projects.SelectMany(project => project.TargetFrameworks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var prerequisites = new List<string>();
        if (frameworks.Length > 0)
        {
            prerequisites.Add($"A .NET SDK supporting {string.Join(", ", frameworks)}.");
        }

        var versions = projects.Where(project => project.ReferencesStereoKit)
            .Select(project => project.StereoKitVersion ?? "an unresolved StereoKit version")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        prerequisites.Add(versions.Length == 0
            ? "Restore access for the project's configured StereoKit and SKinny runtime packages."
            : $"Restore access for {string.Join(", ", versions)}.");
        prerequisites.Add("Workspace trust before restore, build, adapter handshake, Scene, or Play validation.");
        return prerequisites;
    }

    private static IReadOnlyDictionary<string, string?> ReadCentralPackageVersions(
        string? path,
        ICollection<string> warnings)
    {
        if (path is null)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var document = LoadXml(path);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "PackageVersion")
                .Select(element => new
                {
                    Name = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
                    Version = element.Attribute("Version")?.Value,
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Version, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            warnings.Add($"Could not inspect central package versions in '{path}': {exception.Message}");
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreWhitespace = false,
        };
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }

    private static IEnumerable<string> Split(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsDesktopTargetFramework(string framework) =>
        framework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && !framework.Contains("android", StringComparison.OrdinalIgnoreCase)
        && !framework.Contains("ios", StringComparison.OrdinalIgnoreCase)
        && !framework.Contains("browser", StringComparison.OrdinalIgnoreCase)
        && !framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEditorRuntimeCompatibleFramework(string framework)
    {
        if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = framework.AsSpan(3);
        var separator = version.IndexOfAny('.', '-');
        if (separator <= 0 || !int.TryParse(version[..separator], out var major))
        {
            return false;
        }

        return major >= 8;
    }

    private static bool IsExecutable(string outputType) =>
        string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> EnumerateNamedFiles(string root, IReadOnlyCollection<string> names) =>
        EnumerateFiles(root, "*")
            .Where(path => names.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsReparsePoint(file))
                {
                    yield return file;
                }
            }

            foreach (var child in directories.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (ExcludedDirectoryNames.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)
                    || IsReparsePoint(child))
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? FindNearestFile(string directory, string root, string fileName)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = new DirectoryInfo(directory);
             current is not null
             && (string.Equals(
                     canonicalRoot,
                     Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.FullName)),
                     StringComparison.OrdinalIgnoreCase)
                 || IsWithinRoot(canonicalRoot, current.FullName));
             current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool IsWithinRoot(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        return !string.Equals(canonicalRoot, canonicalPath, StringComparison.OrdinalIgnoreCase)
               && canonicalPath.StartsWith(
                   canonicalRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Classification(
        ExistingProjectCompatibility Compatibility,
        OnboardingIntegrationShape? Integration,
        string Summary,
        IReadOnlyList<string> Reasons);

    [GeneratedRegex("^Project\\(.*\\)\\s*=\\s*.*?,\\s*\"([^\"]+\\.csproj)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionProjectPattern();
}
