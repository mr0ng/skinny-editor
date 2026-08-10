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
    private static readonly string[] ExcludedDirectoryNames = [".git", ".skinny", "bin", "obj", "packages"];
    private readonly IReadOnlyList<string>? _testedStereoKitVersions;

    public ExistingStereoKitProjectAnalyzer()
    {
    }

    public ExistingStereoKitProjectAnalyzer(string testedStereoKitVersion)
        : this([testedStereoKitVersion])
    {
    }

    public ExistingStereoKitProjectAnalyzer(IEnumerable<string> testedStereoKitVersions)
    {
        ArgumentNullException.ThrowIfNull(testedStereoKitVersions);
        _testedStereoKitVersions = testedStereoKitVersions
            .Select(version => version?.Trim())
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_testedStereoKitVersions.Count == 0)
        {
            throw new ArgumentException(
                "At least one tested StereoKit version is required.",
                nameof(testedStereoKitVersions));
        }
    }

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
        var validDescriptorRuntimeProjects = new List<ValidDescriptorRuntime>();
        foreach (var descriptorPath in descriptorPaths)
        {
            try
            {
                var definition = EditorProjectDefinition.Load(descriptorPath);
                ValidateExistingDescriptorFiles(definition);
                validDescriptors.Add(descriptorPath);
                validDescriptorRuntimeProjects.Add(new(
                    descriptorPath,
                    definition.CreateRuntimeProjectSpec(RuntimeProfileMode.Scene).ProjectPath,
                    HasGeneratedDirectAdapter(descriptorPath)));
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

        var classification = Classify(
            projects,
            descriptorPaths,
            validDescriptors,
            validDescriptorRuntimeProjects);
        var analysis = new ExistingProjectAnalysis(
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
        return _testedStereoKitVersions is null
            ? analysis
            : analysis with
            {
                StereoKitCompatibility = new StereoKitProjectCompatibilityEvaluator(_testedStereoKitVersions)
                    .Evaluate(analysis.RecommendedStartupProject),
            };
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
        var buildPropertiesFile = FindNearestFile(
            projectDirectory,
            projectRoot,
            "Directory.Build.props");
        var centralPackageFile = FindNearestFile(
            projectDirectory,
            projectRoot,
            "Directory.Packages.props");
        var propertyDefinitions = ReadPropertyDefinitions(
            [buildPropertiesFile, centralPackageFile, projectPath],
            warnings);
        var properties = propertyDefinitions.ToDictionary(
            pair => pair.Key,
            pair => ExpandProperties(pair.Value.Value, propertyDefinitions),
            StringComparer.OrdinalIgnoreCase);
        var centralVersions = ReadCentralPackageVersions(centralPackageFile, warnings);

        var targetFrameworks = Split(properties.GetValueOrDefault("TargetFrameworks"))
            .Concat(Split(properties.GetValueOrDefault("TargetFramework")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runtimeIdentifiers = Split(properties.GetValueOrDefault("RuntimeIdentifiers"))
            .Concat(Split(properties.GetValueOrDefault("RuntimeIdentifier")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packageReferences = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        PackageVersionSource? stereoKitVersionSource = null;
        foreach (var reference in root.Descendants().Where(element =>
                     element.Name.LocalName == "PackageReference"))
        {
            var name = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var versionOverride = reference.Attribute("VersionOverride")?.Value
                                  ?? reference.Elements().FirstOrDefault(element =>
                                      element.Name.LocalName == "VersionOverride")?.Value;
            var versionExpression = versionOverride
                                    ?? reference.Attribute("Version")?.Value
                                    ?? reference.Elements().FirstOrDefault(element =>
                                        element.Name.LocalName == "Version")?.Value;
            var versionValueName = versionOverride is null ? "Version" : "VersionOverride";
            PackageVersionDefinition? versionDefinition = null;
            if (string.IsNullOrWhiteSpace(versionExpression))
            {
                centralVersions.TryGetValue(name, out versionDefinition);
                versionExpression = versionDefinition?.Value;
                versionValueName = "Version";
            }

            var effectiveVersion = string.IsNullOrWhiteSpace(versionExpression)
                ? null
                : ExpandProperties(versionExpression.Trim(), propertyDefinitions);
            packageReferences[name] = effectiveVersion;
            if (string.Equals(name, "StereoKit", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(versionExpression))
            {
                stereoKitVersionSource = ResolvePackageVersionSource(
                    versionDefinition?.Path ?? projectPath,
                    versionDefinition is null
                        ? PackageVersionSourceKind.ProjectPackageReference
                        : PackageVersionSourceKind.CentralPackageVersion,
                    versionExpression.Trim(),
                    versionValueName,
                    propertyDefinitions);
            }
        }

        var projectReferences = root.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.Combine(projectDirectory, value!)))
            .ToArray();
        var sourceShape = InspectSourceShape(projectPath, projectDirectory, warnings);
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
            sourceShape.HasEditorLaunchHook,
            sourceShape.CanAddEditorLaunchHook,
            sourceShape.EditorLaunchHookAssessment,
            stereoKitVersionSource);
    }

    private static (
        bool HasStereoKitInitialization,
        bool HasEditorLaunchHook,
        bool CanAddEditorLaunchHook,
        string EditorLaunchHookAssessment) InspectSourceShape(
        string projectPath,
        string directory,
        ICollection<string> warnings)
    {
        var hasInitialization = false;
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
            if (hasInitialization)
            {
                break;
            }
        }

        try
        {
            var hook = CSharpEntryPointHookPlanner.Analyze(projectPath);
            return (
                hasInitialization,
                hook.Status == EditorLaunchHookPlanStatus.AlreadyPresent,
                hook.Status == EditorLaunchHookPlanStatus.Ready,
                hook.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not safely inspect the C# entry point in '{projectPath}': {exception.Message}");
            return (
                hasInitialization,
                false,
                false,
                "The C# entry point could not be inspected safely.");
        }
    }

    private static Classification Classify(
        IReadOnlyList<InspectedDotnetProject> projects,
        IReadOnlyList<string> descriptors,
        IReadOnlyList<string> validDescriptors,
        IReadOnlyList<ValidDescriptorRuntime> validDescriptorRuntimeProjects)
    {
        if (validDescriptors.Count > 0)
        {
            var incomplete = projects.FirstOrDefault(project =>
                validDescriptorRuntimeProjects.Any(runtime =>
                    runtime.HasGeneratedDirectAdapter
                    && string.Equals(runtime.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase))
                && project.ReferencesEditorRuntime
                && !project.HasEditorLaunchHook);
            if (incomplete is not null)
            {
                var integration = incomplete.CanAddEditorLaunchHook
                    ? OnboardingIntegrationShape.DirectOptIn
                    : OnboardingIntegrationShape.DedicatedEditorHead;
                return new(
                    ExistingProjectCompatibility.IncompleteOnboarding,
                    integration,
                    "This workspace contains a valid descriptor from an incomplete onboarding transaction.",
                    [
                        incomplete.CanAddEditorLaunchHook
                            ? "SKinny can finish the entry-point integration automatically and preserve the existing descriptor."
                            : "The production entry point is ambiguous, so SKinny will finish setup with an isolated editor head.",
                    ]);
            }

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
                null,
                "StereoKit is present, but no conventional desktop startup project was identified.",
            ["A user must select the production code and assets that a dedicated editor head may reference."]);
        }

        if (desktopExecutables.Length == 1 && stereoKitProjects.Length == 1)
        {
            var startup = desktopExecutables[0];
            if (!startup.TargetFrameworks.Any(IsEditorRuntimeCompatibleFramework))
            {
                if (!startup.TargetFrameworks.Any(CanReferenceFromDedicatedHeadFramework))
                {
                    return new(
                        ExistingProjectCompatibility.ManualIntegrationRequired,
                        null,
                        "This project's target framework cannot be referenced safely by a desktop editor head.",
                        [
                            $"'{startup.Name}' targets {string.Join(", ", startup.TargetFrameworks)}.",
                            "Import requires an editor-compatible desktop bridge project selected by the user.",
                        ]);
                }

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
            if (!startup.HasEditorLaunchHook && !startup.CanAddEditorLaunchHook)
            {
                return new(
                    ExistingProjectCompatibility.DedicatedEditorHeadRecommended,
                    OnboardingIntegrationShape.DedicatedEditorHead,
                    "A separate editor head avoids an ambiguous production entry-point rewrite.",
                    [.. reasons, startup.EditorLaunchHookAssessment]);
            }

            if (!startup.HasEditorLaunchHook)
            {
                reasons.Add(startup.EditorLaunchHookAssessment);
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

    private static bool HasGeneratedDirectAdapter(string descriptorPath)
    {
        var adapterPath = Path.Combine(Path.GetDirectoryName(descriptorPath)!, "EditorAdapter.cs");
        if (!File.Exists(adapterPath))
        {
            return false;
        }

        try
        {
            var source = File.ReadAllText(adapterPath);
            return source.Contains("namespace SKinnyOnboarding", StringComparison.Ordinal)
                   && source.Contains("class EditorEntryPoint", StringComparison.Ordinal)
                   && source.Contains("class GeneratedProjectAdapter", StringComparison.Ordinal)
                   && source.Contains("EditorRuntimeHost.IsEditorLaunch", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
            content.Add("Unrecognized startup composition remains application-owned and is isolated behind the generated editor entry point.");
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

    private static IReadOnlyDictionary<string, PackageVersionDefinition> ReadCentralPackageVersions(
        string? path,
        ICollection<string> warnings)
    {
        if (path is null)
        {
            return new Dictionary<string, PackageVersionDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var document = LoadXml(path);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "PackageVersion")
                .Select(element => new
                {
                    Name = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
                    Version = element.Attribute("Version")?.Value
                              ?? element.Elements().FirstOrDefault(child =>
                                  child.Name.LocalName == "Version")?.Value,
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                               && !string.IsNullOrWhiteSpace(item.Version))
                .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new PackageVersionDefinition(
                        path,
                        group.Last().Version!.Trim()),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            warnings.Add($"Could not inspect central package versions in '{path}': {exception.Message}");
            return new Dictionary<string, PackageVersionDefinition>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyDictionary<string, PropertyDefinition> ReadPropertyDefinitions(
        IEnumerable<string?> paths,
        ICollection<string> warnings)
    {
        var result = new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = LoadXml(path!);
                var root = document.Root;
                if (root is null)
                {
                    continue;
                }

                foreach (var group in root.Elements().Where(element =>
                             element.Name.LocalName == "PropertyGroup"
                             && element.Attribute("Condition") is null))
                {
                    foreach (var property in group.Elements().Where(element =>
                                 element.Attribute("Condition") is null))
                    {
                        result[property.Name.LocalName] = new(
                            path!,
                            property.Name.LocalName,
                            property.Value.Trim());
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
            {
                warnings.Add($"Could not inspect MSBuild properties in '{path}': {exception.Message}");
            }
        }

        return result;
    }

    private static string ExpandProperties(
        string value,
        IReadOnlyDictionary<string, PropertyDefinition> properties,
        ISet<string>? expansionStack = null)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains("$(", StringComparison.Ordinal))
        {
            return value.Trim();
        }

        expansionStack ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return PropertyPattern().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (!properties.TryGetValue(name, out var definition) || !expansionStack.Add(name))
            {
                return match.Value;
            }

            try
            {
                return ExpandProperties(definition.Value, properties, expansionStack);
            }
            finally
            {
                expansionStack.Remove(name);
            }
        }).Trim();
    }

    private static PackageVersionSource ResolvePackageVersionSource(
        string path,
        PackageVersionSourceKind kind,
        string value,
        string valueName,
        IReadOnlyDictionary<string, PropertyDefinition> properties)
    {
        var currentValue = value;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? sourcePropertyName = null;
        while (TryReadSingleProperty(currentValue, out var propertyName)
               && visited.Add(propertyName)
               && properties.TryGetValue(propertyName, out var property))
        {
            path = property.Path;
            kind = PackageVersionSourceKind.MsBuildProperty;
            sourcePropertyName = propertyName;
            currentValue = property.Value;
        }

        return new(
            path,
            kind,
            currentValue,
            sourcePropertyName,
            valueName);
    }

    private static bool TryReadSingleProperty(string value, out string propertyName)
    {
        var match = PropertyPattern().Match(value.Trim());
        if (match.Success && match.Length == value.Trim().Length)
        {
            propertyName = match.Groups[1].Value;
            return true;
        }

        propertyName = string.Empty;
        return false;
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
        && !framework.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase)
        && !framework.Contains("tvos", StringComparison.OrdinalIgnoreCase)
        && !framework.Contains("browser", StringComparison.OrdinalIgnoreCase)
        && !framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase);

    public static bool IsEditorRuntimeCompatibleFramework(string framework)
    {
        if (!IsDesktopTargetFramework(framework)
            || !TryReadNetMajorVersion(framework, out var major))
        {
            return false;
        }

        return major >= 8;
    }

    public static bool CanReferenceFromDedicatedHeadFramework(string framework)
    {
        if (!IsDesktopTargetFramework(framework))
        {
            return false;
        }

        return framework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
               || TryReadNetMajorVersion(framework, out var major) && major >= 5;
    }

    private static bool TryReadNetMajorVersion(string framework, out int major)
    {
        major = 0;
        if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = framework.AsSpan(3);
        var separator = version.IndexOfAny('.', '-');
        return separator > 0 && int.TryParse(version[..separator], out major);
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

    private sealed record ValidDescriptorRuntime(
        string DescriptorPath,
        string ProjectPath,
        bool HasGeneratedDirectAdapter);

    private sealed record PropertyDefinition(string Path, string Name, string Value);

    private sealed record PackageVersionDefinition(string Path, string Value);

    [GeneratedRegex("^Project\\(.*\\)\\s*=\\s*.*?,\\s*\"([^\"]+\\.csproj)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionProjectPattern();

    [GeneratedRegex("\\$\\(([A-Za-z_][A-Za-z0-9_.-]*)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPattern();
}
