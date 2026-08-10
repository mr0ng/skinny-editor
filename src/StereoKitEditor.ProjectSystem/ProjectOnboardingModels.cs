using System.Text.Json.Serialization;

namespace StereoKitEditor.ProjectSystem;

[JsonConverter(typeof(JsonStringEnumConverter<ExistingProjectCompatibility>))]
public enum ExistingProjectCompatibility
{
    ReadyToOpen,
    IncompleteOnboarding,
    DirectOptInSupported,
    DedicatedEditorHeadRecommended,
    ManualIntegrationRequired,
    Unsupported,
}

[JsonConverter(typeof(JsonStringEnumConverter<OnboardingIntegrationShape>))]
public enum OnboardingIntegrationShape
{
    DirectOptIn,
    DedicatedEditorHead,
}

public sealed record ExistingProjectAnalysis(
    string SelectedPath,
    string ProjectRoot,
    string? SolutionPath,
    IReadOnlyList<InspectedDotnetProject> Projects,
    IReadOnlyList<string> DescriptorPaths,
    IReadOnlyList<string> ValidDescriptorPaths,
    IReadOnlyList<string> PackageConfigurationPaths,
    IReadOnlyList<string> BuildCustomizationPaths,
    ExistingProjectCompatibility Compatibility,
    OnboardingIntegrationShape? RecommendedIntegration,
    string Summary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> AuthorableContent,
    IReadOnlyList<string> OpaqueContent,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> Warnings,
    StereoKitProjectCompatibilityAssessment? StereoKitCompatibility = null)
{
    public bool IsReadOnly => true;

    public InspectedDotnetProject? RecommendedStartupProject
    {
        get
        {
            var selected = Projects.FirstOrDefault(project =>
                project.ReferencesStereoKit
                && string.Equals(project.Path, SelectedPath, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }

            var descriptorRuntimePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptorPath in ValidDescriptorPaths)
            {
                try
                {
                    var definition = EditorProjectDefinition.Load(descriptorPath);
                    descriptorRuntimePaths.Add(
                        definition.CreateRuntimeProjectSpec(RuntimeProfileMode.Scene).ProjectPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Text.Json.JsonException or InvalidDataException)
                {
                    // The analyzer already reports invalid descriptors. Do not let a later file change
                    // turn startup selection into an unsafe arbitrary fallback.
                }
            }

            var descriptorProjects = Projects.Where(project => descriptorRuntimePaths.Contains(project.Path))
                .ToArray();
            if (descriptorProjects.Length == 1)
            {
                return descriptorProjects[0];
            }

            var executables = Projects.Where(project =>
                    project.ReferencesStereoKit
                    && (project.OutputType is "Exe" or "WinExe"))
                .ToArray();
            if (executables.Length == 1)
            {
                return executables[0];
            }

            var candidates = Projects.Where(project => project.ReferencesStereoKit).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
}

public sealed record InspectedDotnetProject(
    string Path,
    string Name,
    string Sdk,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> RuntimeIdentifiers,
    string OutputType,
    IReadOnlyDictionary<string, string?> PackageReferences,
    IReadOnlyList<string> ProjectReferences,
    bool UsesCentralPackageManagement,
    bool HasStereoKitInitialization,
    bool HasEditorLaunchHook,
    bool CanAddEditorLaunchHook,
    string EditorLaunchHookAssessment,
    PackageVersionSource? StereoKitVersionSource = null)
{
    public bool ReferencesStereoKit => PackageReferences.Keys.Any(package =>
        string.Equals(package, "StereoKit", StringComparison.OrdinalIgnoreCase));

    public string? StereoKitVersion => PackageReferences.FirstOrDefault(package =>
        string.Equals(package.Key, "StereoKit", StringComparison.OrdinalIgnoreCase)).Value;

    public bool ReferencesEditorRuntime => PackageReferences.Keys.Any(package =>
        string.Equals(package, "SKinny.Editor.Runtime", StringComparison.OrdinalIgnoreCase));
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageVersionSourceKind>))]
public enum PackageVersionSourceKind
{
    ProjectPackageReference,
    CentralPackageVersion,
    MsBuildProperty,
}

public sealed record PackageVersionSource(
    string Path,
    PackageVersionSourceKind Kind,
    string DeclaredValue,
    string? PropertyName = null,
    string ValueName = "Version");

[JsonConverter(typeof(JsonStringEnumConverter<StereoKitProjectCompatibility>))]
public enum StereoKitProjectCompatibility
{
    Tested,
    UpgradeRequired,
    UntestedNewer,
    Unresolved,
}

public sealed record StereoKitProjectCompatibilityAssessment(
    StereoKitProjectCompatibility Compatibility,
    string? ProjectVersion,
    string TestedVersion,
    bool CanUpgradeAutomatically,
    string Message);

[JsonConverter(typeof(JsonStringEnumConverter<OnboardingChangeKind>))]
public enum OnboardingChangeKind
{
    Create,
    Modify,
}

public sealed record OnboardingProposedChange(
    OnboardingChangeKind Kind,
    string RelativePath,
    string Purpose,
    string? OriginalSha256,
    string ProposedSha256,
    string? OriginalText,
    string? ProposedText,
    byte[] ProposedBytes,
    string Diff,
    bool WriteUtf8Bom)
{
    public bool IsBinary => ProposedText is null;
}

public sealed record OnboardingProposal(
    Guid ProposalId,
    string ProjectRoot,
    string SourceProjectPath,
    ExistingProjectCompatibility Compatibility,
    string AnalysisSummary,
    IReadOnlyList<string> AnalysisReasons,
    IReadOnlyList<string> AnalysisWarnings,
    IReadOnlyList<string> AuthorableContent,
    IReadOnlyList<string> OpaqueContent,
    IReadOnlyList<string> Prerequisites,
    OnboardingIntegrationShape IntegrationShape,
    string RuntimePackageVersion,
    string DescriptorRelativePath,
    IReadOnlyList<OnboardingProposedChange> Changes,
    IReadOnlyList<string> ExpectedImpact,
    IReadOnlyList<string> RemainingManualWork);

[JsonConverter(typeof(JsonStringEnumConverter<OnboardingTransactionStatus>))]
public enum OnboardingTransactionStatus
{
    Prepared,
    Applying,
    Applied,
    RollingBack,
    RolledBack,
    RollbackConflict,
    Failed,
}

public sealed record OnboardingTransactionChange(
    OnboardingChangeKind Kind,
    string RelativePath,
    string? OriginalSha256,
    string ProposedSha256,
    string? BackupRelativePath,
    bool Applied);

public sealed record OnboardingTransactionManifest(
    Guid TransactionId,
    Guid ProposalId,
    string ProjectRoot,
    OnboardingIntegrationShape IntegrationShape,
    DateTimeOffset CreatedAt,
    OnboardingTransactionStatus Status,
    IReadOnlyList<OnboardingTransactionChange> Changes,
    string? Failure);

public sealed record OnboardingApplyResult(
    string ManifestPath,
    string ReportPath,
    string DescriptorPath,
    OnboardingValidationResult Validation);

public sealed record OnboardingPersistentReport(
    Guid TransactionId,
    string ProjectRoot,
    ExistingProjectCompatibility Compatibility,
    OnboardingIntegrationShape IntegrationShape,
    string AnalysisSummary,
    IReadOnlyList<string> AnalysisReasons,
    IReadOnlyList<string> AnalysisWarnings,
    IReadOnlyList<string> AuthorableContent,
    IReadOnlyList<string> OpaqueContent,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> ExpectedImpact,
    IReadOnlyList<string> RemainingManualWork,
    IReadOnlyList<OnboardingValidationCheck> ValidationChecks,
    IReadOnlyList<string> ChangedPaths,
    OnboardingTransactionStatus Status,
    IReadOnlyList<string> RollbackConflicts,
    string? Failure);

public sealed record OnboardingRollbackResult(
    string ManifestPath,
    OnboardingTransactionStatus Status,
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> Conflicts);

public sealed record OnboardingValidationCheck(string Name, bool Succeeded, string Message);

public sealed record OnboardingValidationResult(IReadOnlyList<OnboardingValidationCheck> Checks)
{
    public bool Succeeded => Checks.All(check => check.Succeeded);
}

public sealed class OnboardingPreflightException(string message) : InvalidOperationException(message);

public sealed class OnboardingTransactionException(
    string message,
    string manifestPath,
    Exception innerException) : IOException(message, innerException)
{
    public string ManifestPath { get; } = manifestPath;
}
