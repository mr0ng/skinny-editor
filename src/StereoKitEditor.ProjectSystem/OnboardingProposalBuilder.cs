using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StereoKitEditor.ProjectSystem;

public sealed class OnboardingProposalBuilder(
    string? runtimePackageVersion = null,
    string? sdkPackageDirectory = null,
    string? testedStereoKitVersion = null)
{
    private readonly string? _sdkPackageDirectory = sdkPackageDirectory;
    private readonly string? _testedStereoKitVersion = testedStereoKitVersion;

    public string RuntimePackageVersion { get; } = runtimePackageVersion ?? GetRuntimePackageVersion();

    public OnboardingProposal Create(
        ExistingProjectAnalysis analysis,
        OnboardingIntegrationShape integrationShape,
        bool alignStereoKitVersion = false)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Compatibility is ExistingProjectCompatibility.ReadyToOpen
            or ExistingProjectCompatibility.ManualIntegrationRequired
            or ExistingProjectCompatibility.Unsupported)
        {
            throw new InvalidOperationException(
                $"A scaffolding proposal is not available for a {analysis.Compatibility} analysis.");
        }

        var startupProject = SelectStartupProject(analysis);
        var stereoKitCompatibility = ResolveStereoKitCompatibility(analysis, startupProject);
        ValidateStereoKitCompatibility(stereoKitCompatibility, alignStereoKitVersion);
        if (integrationShape == OnboardingIntegrationShape.DirectOptIn
            && !startupProject.TargetFrameworks.Any(
                ExistingStereoKitProjectAnalyzer.IsEditorRuntimeCompatibleFramework))
        {
            throw new InvalidOperationException(
                "Direct opt-in requires a net8.0-or-newer startup target. Choose a dedicated editor head.");
        }

        if (integrationShape == OnboardingIntegrationShape.DedicatedEditorHead
            && !startupProject.TargetFrameworks.Any(
                ExistingStereoKitProjectAnalyzer.CanReferenceFromDedicatedHeadFramework))
        {
            throw new InvalidOperationException(
                "A desktop net8.0 editor head cannot safely reference the selected project's target framework.");
        }

        var proposalKey = $"{Path.GetFullPath(analysis.ProjectRoot)}|{startupProject.Path}|{integrationShape}|{RuntimePackageVersion}";
        var proposalId = CreateStableGuid($"proposal|{proposalKey}");
        var projectId = CreateStableGuid($"project|{proposalKey}");
        var sceneId = CreateStableGuid($"scene|{proposalKey}");
        var safeName = CreateSafeName(startupProject.Name);
        var onboardingDirectory = "SKinnyEditor";
        var isResume = analysis.Compatibility == ExistingProjectCompatibility.IncompleteOnboarding;
        var descriptorRelativePath = isResume && integrationShape == OnboardingIntegrationShape.DirectOptIn
                                     && analysis.ValidDescriptorPaths.FirstOrDefault() is { } existingDescriptor
            ? NormalizeRelativePath(Path.GetRelativePath(analysis.ProjectRoot, existingDescriptor))
            : Path.Combine(
                onboardingDirectory,
                isResume && integrationShape == OnboardingIntegrationShape.DedicatedEditorHead
                    ? $"{safeName}.editor.skproject.json"
                    : $"{safeName}.skproject.json");
        var changes = new List<OnboardingProposedChange>();

        AddSdkFeedChanges(analysis, startupProject, changes);

        if (integrationShape == OnboardingIntegrationShape.DirectOptIn)
        {
            AddDirectOptInChanges(analysis, startupProject, changes, isResume);
        }
        else
        {
            AddDedicatedHeadChanges(analysis.ProjectRoot, startupProject, safeName, changes, isResume);
        }

        if (stereoKitCompatibility?.Compatibility == StereoKitProjectCompatibility.UpgradeRequired)
        {
            AddStereoKitVersionAlignmentChange(
                analysis.ProjectRoot,
                startupProject,
                stereoKitCompatibility,
                changes);
        }

        AddGeneratedText(
            analysis.ProjectRoot,
            changes,
            descriptorRelativePath,
            "Create the explicit SKinny project descriptor.",
            CreateDescriptor(analysis, startupProject, integrationShape, safeName, projectId),
            reuseExisting: isResume && integrationShape == OnboardingIntegrationShape.DirectOptIn);
        AddGeneratedText(
            analysis.ProjectRoot,
            changes,
            Path.Combine(onboardingDirectory, "Scenes", "Main.skscene.json"),
            "Create an empty, source-readable initial scene.",
            CreateScene(sceneId, startupProject.Name),
            reuseExisting: isResume);
        AddGeneratedText(
            analysis.ProjectRoot,
            changes,
            Path.Combine(onboardingDirectory, "Assets", ".gitkeep"),
            "Materialize the project-controlled authoring asset root.",
            string.Empty,
            reuseExisting: isResume);

        var impact = (integrationShape == OnboardingIntegrationShape.DirectOptIn
            ? new[]
            {
                "Adds one pinned runtime package reference, a project-local SDK feed, and isolated onboarding source files.",
                "Adds a small editor-launch guard before normal application startup.",
                "Does not replace the existing application entry point or normal launch path.",
                "The generated descriptor remains project-controlled and can be removed through rollback.",
            }
            : new[]
            {
                "Creates a separate editor-only executable and project-local SDK feed.",
                "Does not modify the selected production project or its composition root.",
                "The normal command-line and IDE launch path remains unchanged.",
            }).ToList();
        if (stereoKitCompatibility?.Compatibility == StereoKitProjectCompatibility.UpgradeRequired)
        {
            impact.Insert(
                0,
                $"Updates StereoKit from {stereoKitCompatibility.ProjectVersion} to the tested {stereoKitCompatibility.TestedVersion}; NuGet downloads it during the trusted restore.");
        }
        var manualWork = integrationShape == OnboardingIntegrationShape.DirectOptIn
            ? new[]
            {
                "Optionally register project-specific component schemas in GeneratedProjectAdapter.",
                "Grant workspace trust before restore, build, Scene, or Play validation.",
            }
            : new[]
            {
                "Optionally register project-specific component schemas in GeneratedProjectAdapter.",
                "Grant workspace trust before restore, build, Scene, or Play validation.",
            };

        return new OnboardingProposal(
            proposalId,
            analysis.ProjectRoot,
            startupProject.Path,
            analysis.Compatibility,
            analysis.Summary,
            analysis.Reasons,
            AppendCompatibilityWarning(analysis.Warnings, stereoKitCompatibility),
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

    public OnboardingProposal CreateStereoKitAlignment(ExistingProjectAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Compatibility != ExistingProjectCompatibility.ReadyToOpen)
        {
            throw new InvalidOperationException(
                "A compatibility-only proposal requires an existing valid SKinny project.");
        }

        var startupProject = SelectStartupProject(analysis);
        var compatibility = ResolveStereoKitCompatibility(analysis, startupProject)
                            ?? throw new InvalidOperationException(
                                "StereoKit compatibility was not evaluated for this project.");
        ValidateStereoKitCompatibility(compatibility, alignStereoKitVersion: true);
        if (compatibility.Compatibility != StereoKitProjectCompatibility.UpgradeRequired)
        {
            throw new InvalidOperationException("This project does not require a StereoKit version upgrade.");
        }

        var descriptorPath = analysis.ValidDescriptorPaths.FirstOrDefault()
                             ?? throw new InvalidOperationException(
                                 "A compatibility-only proposal requires a valid project descriptor.");
        var descriptorRelativePath = NormalizeRelativePath(
            Path.GetRelativePath(analysis.ProjectRoot, descriptorPath));
        var changes = new List<OnboardingProposedChange>();
        AddStereoKitVersionAlignmentChange(
            analysis.ProjectRoot,
            startupProject,
            compatibility,
            changes);
        var integrationShape = startupProject.HasEditorLaunchHook
            ? OnboardingIntegrationShape.DirectOptIn
            : OnboardingIntegrationShape.DedicatedEditorHead;
        var proposalKey = $"{Path.GetFullPath(analysis.ProjectRoot)}|{startupProject.Path}|stereokit|{compatibility.TestedVersion}|{RuntimePackageVersion}";

        return new(
            CreateStableGuid($"proposal|{proposalKey}"),
            analysis.ProjectRoot,
            startupProject.Path,
            analysis.Compatibility,
            analysis.Summary,
            analysis.Reasons,
            AppendCompatibilityWarning(analysis.Warnings, compatibility),
            analysis.AuthorableContent,
            analysis.OpaqueContent,
            analysis.Prerequisites,
            integrationShape,
            RuntimePackageVersion,
            descriptorRelativePath,
            changes,
            [
                $"Updates StereoKit from {compatibility.ProjectVersion} to the tested {compatibility.TestedVersion}.",
                "Leaves the existing SKinny descriptor, scene, adapter, and application entry point unchanged.",
                "NuGet downloads the selected StereoKit package during the trusted restore.",
            ],
            [
                "Review any StereoKit API migration diagnostics produced by the project build.",
                "Grant workspace trust before restore, build, Scene, or Play validation.",
            ]);
    }

    private StereoKitProjectCompatibilityAssessment? ResolveStereoKitCompatibility(
        ExistingProjectAnalysis analysis,
        InspectedDotnetProject startupProject) =>
        analysis.StereoKitCompatibility
        ?? (string.IsNullOrWhiteSpace(_testedStereoKitVersion)
            ? null
            : new StereoKitProjectCompatibilityEvaluator(_testedStereoKitVersion).Evaluate(startupProject));

    private static void ValidateStereoKitCompatibility(
        StereoKitProjectCompatibilityAssessment? compatibility,
        bool alignStereoKitVersion)
    {
        if (compatibility is null)
        {
            return;
        }

        if (compatibility.Compatibility == StereoKitProjectCompatibility.Unresolved)
        {
            throw new InvalidOperationException(compatibility.Message);
        }

        if (compatibility.Compatibility == StereoKitProjectCompatibility.UpgradeRequired
            && (!alignStereoKitVersion || !compatibility.CanUpgradeAutomatically))
        {
            throw new InvalidOperationException(compatibility.Message);
        }
    }

    private static IReadOnlyList<string> AppendCompatibilityWarning(
        IReadOnlyList<string> warnings,
        StereoKitProjectCompatibilityAssessment? compatibility) =>
        compatibility is null || compatibility.Compatibility == StereoKitProjectCompatibility.Tested
            ? warnings
            : [.. warnings, compatibility.Message];

    private static void AddStereoKitVersionAlignmentChange(
        string projectRoot,
        InspectedDotnetProject startupProject,
        StereoKitProjectCompatibilityAssessment compatibility,
        IList<OnboardingProposedChange> changes)
    {
        var source = startupProject.StereoKitVersionSource
                     ?? throw new InvalidOperationException(
                         "The effective StereoKit version declaration could not be changed automatically.");
        if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(projectRoot, source.Path))
        {
            throw new InvalidDataException(
                $"The StereoKit version declaration escapes the selected project root: {source.Path}");
        }

        var original = ReadUtf8Text(source.Path);
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(projectRoot, source.Path));
        var pendingChanges = changes
            .Select((change, index) => (Change: change, Index: index))
            .Where(candidate => candidate.Change.Kind == OnboardingChangeKind.Modify
                                && string.Equals(
                                    candidate.Change.RelativePath,
                                    relativePath,
                                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pendingChanges.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple onboarding changes target the StereoKit version file '{source.Path}'.");
        }

        var pendingChange = pendingChanges.SingleOrDefault();
        var currentText = pendingChange.Change?.ProposedText ?? original.Text;
        var proposed = source.Kind switch
        {
            PackageVersionSourceKind.MsBuildProperty when !string.IsNullOrWhiteSpace(source.PropertyName) =>
                ReplaceMsBuildPropertyValue(
                    currentText,
                    source.PropertyName,
                    source.DeclaredValue,
                    compatibility.TestedVersion,
                    source.Path),
            PackageVersionSourceKind.ProjectPackageReference => ReplacePackageVersionValue(
                currentText,
                "PackageReference",
                source.ValueName,
                source.DeclaredValue,
                compatibility.TestedVersion,
                source.Path),
            PackageVersionSourceKind.CentralPackageVersion => ReplacePackageVersionValue(
                currentText,
                "PackageVersion",
                "Version",
                source.DeclaredValue,
                compatibility.TestedVersion,
                source.Path),
            _ => throw new InvalidOperationException(
                "The effective StereoKit version declaration could not be changed automatically."),
        };
        if (pendingChange.Change is not null)
        {
            changes.RemoveAt(pendingChange.Index);
        }

        var purpose = $"Upgrade StereoKit to {compatibility.TestedVersion}, the version tested with this runtime bridge.";
        AddModify(
            projectRoot,
            changes,
            source.Path,
            pendingChange.Change is null
                ? purpose
                : $"{pendingChange.Change.Purpose} {purpose}",
            original,
            proposed);
    }

    private static string ReplaceMsBuildPropertyValue(
        string text,
        string propertyName,
        string expectedValue,
        string replacementValue,
        string path)
    {
        var pattern = new Regex(
            $"(?<open><{Regex.Escape(propertyName)}\\b[^>]*>)(?<value>.*?)(?<close></{Regex.Escape(propertyName)}\\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return ReplaceSingleXmlValue(text, pattern, expectedValue, replacementValue, path);
    }

    private static string ReplacePackageVersionValue(
        string text,
        string elementName,
        string valueName,
        string expectedValue,
        string replacementValue,
        string path)
    {
        var elementPattern = new Regex(
            $"<{elementName}\\b(?:(?!<{elementName}\\b).)*?(?:/>|</{elementName}\\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        var candidates = elementPattern.Matches(text)
            .Where(match => Regex.IsMatch(
                match.Value,
                "(?:Include|Update)\\s*=\\s*['\"]StereoKit['\"]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one editable StereoKit {elementName} in '{path}', but found {candidates.Length}.");
        }

        var candidate = candidates[0];
        var attribute = Regex.Match(
            candidate.Value,
            $"(?<open>\\b{Regex.Escape(valueName)}\\s*=\\s*['\"])(?<value>[^'\"]+)(?<close>['\"])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Match valueMatch;
        if (attribute.Success)
        {
            valueMatch = attribute;
        }
        else
        {
            valueMatch = Regex.Match(
                candidate.Value,
                $"(?<open><{Regex.Escape(valueName)}\\b[^>]*>)(?<value>.*?)(?<close></{Regex.Escape(valueName)}\\s*>)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        if (!valueMatch.Success)
        {
            throw new InvalidOperationException(
                $"The StereoKit {elementName} in '{path}' has no editable version value.");
        }

        var valueGroup = valueMatch.Groups["value"];
        var absoluteStart = candidate.Index + valueGroup.Index;
        return ReplaceSingleValue(
            text,
            absoluteStart,
            valueGroup.Length,
            valueGroup.Value,
            expectedValue,
            replacementValue,
            path);
    }

    private static string ReplaceSingleXmlValue(
        string text,
        Regex pattern,
        string expectedValue,
        string replacementValue,
        string path)
    {
        var matches = pattern.Matches(text)
            .Where(match => string.Equals(
                match.Groups["value"].Value.Trim(),
                expectedValue,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one version value '{expectedValue}' in '{path}', but found {matches.Length}.");
        }

        var group = matches[0].Groups["value"];
        return ReplaceSingleValue(
            text,
            group.Index,
            group.Length,
            group.Value,
            expectedValue,
            replacementValue,
            path);
    }

    private static string ReplaceSingleValue(
        string text,
        int start,
        int length,
        string currentValue,
        string expectedValue,
        string replacementValue,
        string path)
    {
        var leading = currentValue.Length - currentValue.TrimStart().Length;
        var trailing = currentValue.Length - currentValue.TrimEnd().Length;
        if (!string.Equals(currentValue.Trim(), expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The StereoKit version changed while onboarding was being prepared: '{path}'.");
        }

        var replacement = currentValue[..leading]
                          + replacementValue
                          + currentValue[(currentValue.Length - trailing)..];
        return text[..start] + replacement + text[(start + length)..];
    }

    private void AddDirectOptInChanges(
        ExistingProjectAnalysis analysis,
        InspectedDotnetProject startupProject,
        ICollection<OnboardingProposedChange> changes,
        bool isResume)
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

        var hook = CSharpEntryPointHookPlanner.Analyze(startupProject.Path);
        if (hook.Status == EditorLaunchHookPlanStatus.Ready
            && hook.SourcePath is not null
            && hook.OriginalText is not null
            && hook.ProposedText is not null)
        {
            var original = ReadUtf8Text(hook.SourcePath);
            if (!string.Equals(original.Text, hook.OriginalText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The entry point changed while onboarding was being prepared: {hook.SourcePath}");
            }

            AddModify(
                analysis.ProjectRoot,
                changes,
                hook.SourcePath,
                "Route explicit editor launches before normal application startup.",
                original,
                hook.ProposedText);
        }
        else if (hook.Status != EditorLaunchHookPlanStatus.AlreadyPresent)
        {
            throw new InvalidOperationException(
                $"The production entry point cannot be changed automatically. Choose a dedicated editor head. {hook.Message}");
        }

        AddGeneratedText(
            analysis.ProjectRoot,
            changes,
            Path.Combine("SKinnyEditor", "EditorAdapter.cs"),
            "Add an isolated runtime entry helper and an empty project adapter.",
            CreateAdapterSource(includeMain: false),
            reuseExisting: isResume);
        AddGeneratedText(
            analysis.ProjectRoot,
            changes,
            Path.Combine("SKinnyEditor", "README.md"),
            "Document the automatic normal/editor launch boundary.",
            CreateDirectReadme(),
            reuseExisting: isResume,
            knownPreviousText: CreateLegacyDirectReadme());
    }

    private void AddDedicatedHeadChanges(
        string projectRoot,
        InspectedDotnetProject startupProject,
        string safeName,
        ICollection<OnboardingProposedChange> changes,
        bool isResume)
    {
        var relativeProductionProject = NormalizeProjectPath(Path.GetRelativePath(
            Path.Combine(projectRoot, "SKinnyEditor"),
            startupProject.Path));
        var targetFramework = SelectDedicatedTargetFramework(startupProject.TargetFrameworks);
        AddGeneratedText(
            projectRoot,
            changes,
            Path.Combine("SKinnyEditor", $"{safeName}.SKinny.Editor.csproj"),
            "Create an editor-only executable without changing the production composition root.",
            CreateDedicatedProject(targetFramework, relativeProductionProject, RuntimePackageVersion),
            reuseExisting: isResume);
        AddGeneratedText(
            projectRoot,
            changes,
            Path.Combine("SKinnyEditor", "Program.cs"),
            "Create the dedicated editor runtime entry point and empty adapter.",
            CreateAdapterSource(includeMain: true),
            reuseExisting: isResume);
        AddGeneratedText(
            projectRoot,
            changes,
            Path.Combine("SKinnyEditor", "README.md"),
            "Document the generated boundary and remaining adapter work.",
            CreateDedicatedReadme(),
            reuseExisting: isResume);
    }

    private void AddSdkFeedChanges(
        ExistingProjectAnalysis analysis,
        InspectedDotnetProject startupProject,
        ICollection<OnboardingProposedChange> changes)
    {
        var packages = BundledSdkPackages.FindRequired(
            RuntimePackageVersion,
            _sdkPackageDirectory,
            allowGlobalPackageCache: true);
        var sdkDirectory = Path.Combine(analysis.ProjectRoot, ".skinny", "sdk");
        foreach (var package in packages)
        {
            AddBinaryCreate(
                analysis.ProjectRoot,
                changes,
                Path.Combine(".skinny", "sdk", Path.GetFileName(package)),
                "Copy a matching SKinny SDK package into the project-local feed.",
                File.ReadAllBytes(package));
        }

        var configPath = analysis.PackageConfigurationPaths
            .Where(path => string.Equals(
                               Path.GetFileName(path),
                               "NuGet.config",
                               StringComparison.OrdinalIgnoreCase)
                           || string.Equals(
                               Path.GetFileName(path),
                               "nuget.config",
                               StringComparison.OrdinalIgnoreCase))
            .Where(path => ExistingStereoKitProjectAnalyzer.IsWithinRoot(
                Path.GetDirectoryName(path)!,
                startupProject.Path))
            .OrderByDescending(path => Path.GetDirectoryName(path)!.Length)
            .FirstOrDefault()
            ?? Path.Combine(analysis.ProjectRoot, "NuGet.config");
        AddNuGetConfigSource(analysis.ProjectRoot, configPath, sdkDirectory, changes);
    }

    private static void AddNuGetConfigSource(
        string projectRoot,
        string configPath,
        string sdkDirectory,
        ICollection<OnboardingProposedChange> changes)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        var relativeSource = NormalizeProjectPath(Path.GetRelativePath(configDirectory, sdkDirectory));
        if (!File.Exists(configPath))
        {
            AddCreate(
                projectRoot,
                changes,
                Path.GetRelativePath(projectRoot, configPath),
                "Configure the project-local SKinny SDK package source.",
                $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="skinny-project-sdk" value="{{relativeSource}}" />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                  </packageSources>
                </configuration>
                """ + Environment.NewLine);
            return;
        }

        var original = ReadUtf8Text(configPath);
        var document = LoadSafeXml(original.Text, configPath);
        var packageSources = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "packageSources");
        var existingLocalSource = packageSources?.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "add"
            && ResolvesToDirectory(
                element.Attribute("value")?.Value,
                configDirectory,
                sdkDirectory));
        if (existingLocalSource is not null)
        {
            var existingKey = existingLocalSource.Attribute("key")?.Value;
            if (string.IsNullOrWhiteSpace(existingKey))
            {
                throw new InvalidDataException(
                    $"The project-local source in '{configPath}' has no package-source key.");
            }

            var mapped = EnsurePackageSourceMapping(
                original.Text,
                original.NewLine,
                configPath,
                existingKey);
            if (!string.Equals(mapped, original.Text, StringComparison.Ordinal))
            {
                AddModify(
                    projectRoot,
                    changes,
                    configPath,
                    "Allow matching SKinny SDK packages through NuGet package-source mapping.",
                    original,
                    mapped);
            }

            return;
        }

        var existingKeys = document.Descendants()
            .Where(element => element.Name.LocalName == "add")
            .Select(element => element.Attribute("key")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = "skinny-project-sdk";
        for (var suffix = 2; existingKeys.Contains(key); suffix++)
        {
            key = $"skinny-project-sdk-{suffix}";
        }

        string proposed;
        if (packageSources is not null)
        {
            if (packageSources.IsEmpty)
            {
                var match = Regex.Match(
                    original.Text,
                    "<packageSources\\b(?<attributes>[^>]*)/\\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    throw new InvalidDataException(
                        $"NuGet configuration '{configPath}' has an unsupported empty packageSources element.");
                }

                proposed = original.Text.Remove(match.Index, match.Length).Insert(
                    match.Index,
                    $"<packageSources{match.Groups["attributes"].Value}>{original.NewLine}" +
                    $"    <add key=\"{SecurityElement.Escape(key)}\" value=\"{SecurityElement.Escape(relativeSource)}\" />{original.NewLine}" +
                    "  </packageSources>");
            }
            else
            {
                var closeTag = $"</{packageSources.Name.LocalName}>";
                var index = original.Text.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    throw new InvalidDataException($"NuGet configuration '{configPath}' has no packageSources closing element.");
                }

                var lineStart = original.Text.LastIndexOf('\n', Math.Max(0, index - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                var closeIndentation = original.Text[lineStart..index];
                if (closeIndentation.Any(character => !char.IsWhiteSpace(character)))
                {
                    closeIndentation = "  ";
                    lineStart = index;
                }

                proposed = original.Text.Insert(
                    lineStart,
                    $"{closeIndentation}  <add key=\"{SecurityElement.Escape(key)}\" value=\"{SecurityElement.Escape(relativeSource)}\" />{original.NewLine}");
            }
        }
        else
        {
            var index = original.Text.LastIndexOf("</configuration>", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                var match = Regex.Match(
                    original.Text,
                    "<configuration\\b(?<attributes>[^>]*)/\\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    throw new InvalidDataException($"NuGet configuration '{configPath}' has no configuration closing element.");
                }

                proposed = original.Text.Remove(match.Index, match.Length).Insert(
                    match.Index,
                    $"<configuration{match.Groups["attributes"].Value}>{original.NewLine}" +
                    $"  <packageSources>{original.NewLine}" +
                    $"    <add key=\"{SecurityElement.Escape(key)}\" value=\"{SecurityElement.Escape(relativeSource)}\" />{original.NewLine}" +
                    $"  </packageSources>{original.NewLine}" +
                    "</configuration>");
            }
            else
            {
                proposed = original.Text.Insert(
                    index,
                    $"  <packageSources>{original.NewLine}" +
                    $"    <add key=\"{SecurityElement.Escape(key)}\" value=\"{SecurityElement.Escape(relativeSource)}\" />{original.NewLine}" +
                    $"  </packageSources>{original.NewLine}");
            }
        }

        proposed = EnsurePackageSourceMapping(proposed, original.NewLine, configPath, key);
        AddModify(
            projectRoot,
            changes,
            configPath,
            "Configure the project-local SKinny SDK package source.",
            original,
            proposed);
    }

    private static string EnsurePackageSourceMapping(
        string text,
        string newLine,
        string configPath,
        string sourceKey)
    {
        var document = LoadSafeXml(text, configPath);
        var mapping = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "packageSourceMapping");
        if (mapping is null)
        {
            return text;
        }

        var mappedSource = mapping.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "packageSource"
            && string.Equals(
                element.Attribute("key")?.Value,
                sourceKey,
                StringComparison.OrdinalIgnoreCase));
        if (mappedSource is not null)
        {
            var coversSdk = mappedSource.Elements().Any(element =>
                element.Name.LocalName == "package"
                && PackagePatternCoversSdk(element.Attribute("pattern")?.Value));
            if (coversSdk)
            {
                return text;
            }

            throw new InvalidDataException(
                $"NuGet package-source mapping in '{configPath}' already defines '{sourceKey}' without a pattern that covers SKinny.Editor.* packages.");
        }

        const string closeTag = "</packageSourceMapping>";
        var index = text.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidDataException(
                $"NuGet configuration '{configPath}' has an unsupported packageSourceMapping element.");
        }

        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var closeIndentation = text[lineStart..index];
        if (closeIndentation.Any(character => !char.IsWhiteSpace(character)))
        {
            closeIndentation = "  ";
            lineStart = index;
        }

        return text.Insert(
            lineStart,
            $"{closeIndentation}  <packageSource key=\"{SecurityElement.Escape(sourceKey)}\">{newLine}" +
            $"{closeIndentation}    <package pattern=\"SKinny.Editor.*\" />{newLine}" +
            $"{closeIndentation}  </packageSource>{newLine}");
    }

    private static bool PackagePatternCoversSdk(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return BundledSdkPackages.RequiredPackageIds.All(packageId =>
            Regex.IsMatch(
                packageId,
                expression,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static XDocument LoadSafeXml(string text, string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, settings);
        try
        {
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"NuGet configuration '{path}' is not well-formed XML.", exception);
        }
    }

    private static bool ResolvesToDirectory(string? value, string configDirectory, string expectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return false;
        }

        try
        {
            var resolved = Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(configDirectory, value));
            return string.Equals(
                Path.TrimEndingDirectorySeparator(resolved),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
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

        SKinny added a guarded editor-launch route before normal application startup. The guard only
        handles explicit SKinny Scene and Play launches; ordinary application launches are unchanged.

        Optionally register explicitly authorable project components in `GeneratedProjectAdapter.Configure`.
        Procedural runtime objects remain owned by the normal application and are not inferred by the editor.
        """ + Environment.NewLine;

    private static string CreateLegacyDirectReadme() =>
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
        analysis.RecommendedStartupProject
        ?? throw new InvalidOperationException("The analysis has no StereoKit project to onboard.");

    private static string SelectDedicatedTargetFramework(IReadOnlyList<string> frameworks) =>
        frameworks.FirstOrDefault(framework =>
            ExistingStereoKitProjectAnalyzer.IsEditorRuntimeCompatibleFramework(framework))
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
            proposedBytes,
            OnboardingTextDiff.Create(normalized, null, proposedText),
            false));
    }

    private static void AddGeneratedText(
        string projectRoot,
        ICollection<OnboardingProposedChange> changes,
        string relativePath,
        string purpose,
        string proposedText,
        bool reuseExisting,
        string? knownPreviousText = null)
    {
        var target = Path.GetFullPath(Path.Combine(projectRoot, NormalizeRelativePath(relativePath)));
        if (!reuseExisting || !File.Exists(target))
        {
            AddCreate(projectRoot, changes, relativePath, purpose, proposedText);
            return;
        }

        var original = ReadUtf8Text(target);
        if (string.Equals(original.Text, proposedText, StringComparison.Ordinal))
        {
            return;
        }

        if (knownPreviousText is not null
            && string.Equals(original.Text, knownPreviousText, StringComparison.Ordinal))
        {
            AddModify(projectRoot, changes, target, purpose, original, proposedText);
        }
    }

    private static void AddBinaryCreate(
        string projectRoot,
        ICollection<OnboardingProposedChange> changes,
        string relativePath,
        string purpose,
        byte[] proposedBytes)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var target = Path.GetFullPath(Path.Combine(projectRoot, normalized));
        if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(projectRoot, target))
        {
            throw new InvalidDataException($"Proposed path escapes the selected project root: {relativePath}");
        }

        var proposedHash = Hash(proposedBytes);
        if (File.Exists(target))
        {
            using var current = File.OpenRead(target);
            var currentHash = Convert.ToHexString(SHA256.HashData(current));
            if (string.Equals(currentHash, proposedHash, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"The project-local SDK package already exists with different content: {normalized}");
        }

        changes.Add(new OnboardingProposedChange(
            OnboardingChangeKind.Create,
            normalized,
            purpose,
            null,
            proposedHash,
            null,
            null,
            proposedBytes,
            $"Binary file: {normalized.Replace('\\', '/')} ({proposedBytes.Length:N0} bytes, SHA-256 {proposedHash})",
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
            EncodeUtf8(proposedText, original.HasBom),
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
