using System.Text.Json.Serialization;

namespace StereoKitEditor.ProjectSystem;

[JsonConverter(typeof(JsonStringEnumConverter<ExistingProjectCompatibility>))]
public enum ExistingProjectCompatibility
{
    ReadyToOpen,
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
    IReadOnlyList<string> Warnings)
{
    public bool IsReadOnly => true;

    public InspectedDotnetProject? RecommendedStartupProject => Projects.FirstOrDefault(project =>
        project.ReferencesStereoKit
        && project.OutputType is "Exe" or "WinExe");
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
    bool HasEditorLaunchHook)
{
    public bool ReferencesStereoKit => PackageReferences.Keys.Any(package =>
        string.Equals(package, "StereoKit", StringComparison.OrdinalIgnoreCase));

    public string? StereoKitVersion => PackageReferences.FirstOrDefault(package =>
        string.Equals(package.Key, "StereoKit", StringComparison.OrdinalIgnoreCase)).Value;

    public bool ReferencesEditorRuntime => PackageReferences.Keys.Any(package =>
        string.Equals(package, "SKinny.Editor.Runtime", StringComparison.OrdinalIgnoreCase));
}

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
    string ProposedText,
    string Diff,
    bool WriteUtf8Bom);

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
