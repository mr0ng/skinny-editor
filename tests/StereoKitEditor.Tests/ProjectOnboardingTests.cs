using System.Security.Cryptography;
using System.Text;
using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.Tests;

public sealed class ProjectOnboardingTests
{
    [Fact]
    public void Analysis_IsReadOnlyAndClassifiesConventionalStereoKitProject()
    {
        using var fixture = new OnboardingFixture();
        var before = fixture.Snapshot();

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        Assert.True(analysis.IsReadOnly);
        Assert.Equal(ExistingProjectCompatibility.DirectOptInSupported, analysis.Compatibility);
        Assert.Equal(OnboardingIntegrationShape.DirectOptIn, analysis.RecommendedIntegration);
        var project = Assert.Single(analysis.Projects);
        Assert.True(project.ReferencesStereoKit);
        Assert.Equal("0.4.0-preview.3557", project.StereoKitVersion);
        Assert.True(project.HasStereoKitInitialization);
        Assert.Equal(OrderSnapshot(before), OrderSnapshot(fixture.Snapshot()));
    }

    [Fact]
    public void Analysis_UnsupportedProjectProducesUsefulReportWithoutWriting()
    {
        using var fixture = new OnboardingFixture(includeStereoKit: false);
        var before = fixture.Snapshot();

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.Root);

        Assert.Equal(ExistingProjectCompatibility.Unsupported, analysis.Compatibility);
        Assert.Contains(analysis.Reasons, reason => reason.Contains("StereoKit", StringComparison.Ordinal));
        Assert.Equal(OrderSnapshot(before), OrderSnapshot(fixture.Snapshot()));
    }

    [Fact]
    public void DirectProposal_RespectsCentralPackageManagement()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            fixture.ProjectPath,
            File.ReadAllText(fixture.ProjectPath).Replace(
                " Version=\"0.4.0-preview.3557\"",
                string.Empty,
                StringComparison.Ordinal));
        var centralPath = Path.Combine(fixture.Root, "Directory.Packages.props");
        File.WriteAllText(
            centralPath,
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include='StereoKit' Version='0.4.0-preview.3557' />
              </ItemGroup>
            </Project>
            """ + Environment.NewLine);

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1")
            .Create(analysis, OnboardingIntegrationShape.DirectOptIn);

        Assert.True(Assert.Single(analysis.Projects).UsesCentralPackageManagement);
        Assert.Equal("0.4.0-preview.3557", Assert.Single(analysis.Projects).StereoKitVersion);
        var centralChange = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SKinny.Editor.Runtime", centralChange.ProposedText, StringComparison.Ordinal);
        var projectChange = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "Fixture.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            "SKinny.Editor.Runtime\" Version=",
            projectChange.ProposedText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DedicatedHeadProposal_IsDeterministicAndRollsBackCreatedFiles()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var builder = new OnboardingProposalBuilder("0.3.0-preview.1");

        var first = builder.Create(analysis, OnboardingIntegrationShape.DedicatedEditorHead);
        var second = builder.Create(analysis, OnboardingIntegrationShape.DedicatedEditorHead);

        Assert.Equal(first.ProposalId, second.ProposalId);
        Assert.Equal(
            first.Changes.Select(change => (change.RelativePath, change.ProposedSha256)),
            second.Changes.Select(change => (change.RelativePath, change.ProposedSha256)));
        Assert.All(first.Changes, change => Assert.Equal(OnboardingChangeKind.Create, change.Kind));

        var sourceBefore = File.ReadAllText(fixture.ProjectPath);
        var transactions = new OnboardingTransactionService();
        var applied = await transactions.ApplyAsync(first, TestContext.Current.CancellationToken);

        Assert.True(applied.Validation.Succeeded);
        Assert.True(File.Exists(applied.DescriptorPath));
        Assert.Equal(
            OnboardingTransactionStatus.Applied,
            OnboardingTransactionService.LoadManifest(applied.ManifestPath).Status);
        var persistentReport = OnboardingTransactionService.LoadReport(applied.ReportPath);
        Assert.Equal(OnboardingTransactionStatus.Applied, persistentReport.Status);
        Assert.NotEmpty(persistentReport.AuthorableContent);
        Assert.NotEmpty(persistentReport.OpaqueContent);
        Assert.Equal(sourceBefore, File.ReadAllText(fixture.ProjectPath));

        var rollback = await transactions.RollbackAsync(
            applied.ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingTransactionStatus.RolledBack, rollback.Status);
        Assert.All(first.Changes, change =>
            Assert.False(File.Exists(Path.Combine(fixture.Root, change.RelativePath))));
        Assert.Equal(sourceBefore, File.ReadAllText(fixture.ProjectPath));
        Assert.True(File.Exists(applied.ManifestPath));
        Assert.Equal(
            OnboardingTransactionStatus.RolledBack,
            OnboardingTransactionService.LoadReport(applied.ReportPath).Status);
    }

    [Fact]
    public async Task DirectOptIn_RestoresModifiedProjectAndRemovesOnlyGeneratedFiles()
    {
        using var fixture = new OnboardingFixture();
        var originalProject = File.ReadAllText(fixture.ProjectPath);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1")
            .Create(analysis, OnboardingIntegrationShape.DirectOptIn);
        var transactions = new OnboardingTransactionService();

        var applied = await transactions.ApplyAsync(proposal, TestContext.Current.CancellationToken);

        Assert.Contains("SKinny.Editor.Runtime", File.ReadAllText(fixture.ProjectPath), StringComparison.Ordinal);
        var rollback = await transactions.RollbackAsync(
            applied.ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingTransactionStatus.RolledBack, rollback.Status);
        Assert.Equal(originalProject, File.ReadAllText(fixture.ProjectPath));
        Assert.Contains(
            Path.GetRelativePath(fixture.Root, fixture.ProjectPath),
            rollback.RestoredPaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analysis_RecognizesAppliedDescriptorAsReadyWithoutWriting()
    {
        using var fixture = new OnboardingFixture();
        var analyzer = new ExistingStereoKitProjectAnalyzer();
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1").Create(
            analyzer.Analyze(fixture.ProjectPath),
            OnboardingIntegrationShape.DedicatedEditorHead);
        var transactions = new OnboardingTransactionService();
        var applied = await transactions.ApplyAsync(proposal, TestContext.Current.CancellationToken);
        var before = fixture.Snapshot();

        var ready = analyzer.Analyze(fixture.Root);

        Assert.Equal(ExistingProjectCompatibility.ReadyToOpen, ready.Compatibility);
        Assert.Contains(applied.DescriptorPath, ready.ValidDescriptorPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(OrderSnapshot(before), OrderSnapshot(fixture.Snapshot()));
    }

    [Fact]
    public async Task Rollback_PreservesFilesEditedAfterOnboardingAndReportsConflict()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1")
            .Create(analysis, OnboardingIntegrationShape.DirectOptIn);
        var transactions = new OnboardingTransactionService();
        var applied = await transactions.ApplyAsync(proposal, TestContext.Current.CancellationToken);
        var adapterPath = Path.Combine(fixture.Root, "SKinnyEditor", "EditorAdapter.cs");
        await File.AppendAllTextAsync(
            adapterPath,
            "// user edit" + Environment.NewLine,
            TestContext.Current.CancellationToken);

        var rollback = await transactions.RollbackAsync(
            applied.ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingTransactionStatus.RollbackConflict, rollback.Status);
        Assert.Contains(rollback.Conflicts, conflict => conflict.Contains("EditorAdapter.cs", StringComparison.Ordinal));
        Assert.Contains("// user edit", File.ReadAllText(adapterPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSafeValidation_AutomaticallyRestoresTheTransaction()
    {
        using var fixture = new OnboardingFixture();
        var originalProject = File.ReadAllText(fixture.ProjectPath);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1")
            .Create(analysis, OnboardingIntegrationShape.DirectOptIn);
        const string invalidDescriptor = "{";
        proposal = proposal with
        {
            Changes = proposal.Changes.Select(change =>
                    string.Equals(
                        change.RelativePath,
                        proposal.DescriptorRelativePath,
                        StringComparison.OrdinalIgnoreCase)
                        ? change with
                        {
                            ProposedText = invalidDescriptor,
                            ProposedSha256 = Convert.ToHexString(
                                SHA256.HashData(Encoding.UTF8.GetBytes(invalidDescriptor))),
                        }
                        : change)
                .ToArray(),
        };

        var exception = await Assert.ThrowsAsync<OnboardingTransactionException>(() =>
            new OnboardingTransactionService().ApplyAsync(
                proposal,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            OnboardingTransactionStatus.RolledBack,
            OnboardingTransactionService.LoadManifest(exception.ManifestPath).Status);
        Assert.Equal(originalProject, File.ReadAllText(fixture.ProjectPath));
        Assert.All(proposal.Changes.Where(change => change.Kind == OnboardingChangeKind.Create), change =>
            Assert.False(File.Exists(Path.Combine(fixture.Root, change.RelativePath))));
    }

    [Fact]
    public async Task Apply_BlocksCollisionBeforeCreatingTransaction()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1")
            .Create(analysis, OnboardingIntegrationShape.DedicatedEditorHead);
        var collision = proposal.Changes.First(change => change.Kind == OnboardingChangeKind.Create);
        var collisionPath = Path.Combine(fixture.Root, collision.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(collisionPath)!);
        await File.WriteAllTextAsync(collisionPath, "existing", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<OnboardingPreflightException>(() =>
            new OnboardingTransactionService().ApplyAsync(proposal, TestContext.Current.CancellationToken));

        Assert.Contains("will not overwrite", exception.Message, StringComparison.Ordinal);
        Assert.Equal("existing", File.ReadAllText(collisionPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".skinny", "onboarding")));
    }

    [Fact]
    public async Task Apply_BlocksPathEscapeBeforeWriting()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = new OnboardingProposalBuilder("0.3.0-preview.1")
            .Create(analysis, OnboardingIntegrationShape.DedicatedEditorHead);
        var first = proposal.Changes[0];
        var escapedName = $"{Path.GetFileName(fixture.Root)}-escaped.txt";
        proposal = proposal with
        {
            Changes = [first with { RelativePath = Path.Combine("..", escapedName) }, .. proposal.Changes.Skip(1)],
        };

        var exception = await Assert.ThrowsAsync<OnboardingPreflightException>(() =>
            new OnboardingTransactionService().ApplyAsync(proposal, TestContext.Current.CancellationToken));

        Assert.Contains("escapes the project root", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Root)!, escapedName)));
    }

    private sealed class OnboardingFixture : IDisposable
    {
        public OnboardingFixture(bool includeStereoKit = true)
        {
            Root = Path.Combine(Path.GetTempPath(), $"skinny-onboarding-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "Fixture.csproj");
            var package = includeStereoKit
                ? """
                    <ItemGroup>
                      <PackageReference Include="StereoKit" Version="0.4.0-preview.3557" />
                    </ItemGroup>
                  """
                : string.Empty;
            File.WriteAllText(
                ProjectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                {{package}}
                </Project>
                """ + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(Root, "Program.cs"),
                "internal static class Program { private static int Main(string[] args) { return StereoKit.SK.Initialize(default) ? 0 : 1; } }" + Environment.NewLine);
        }

        public string Root { get; }
        public string ProjectPath { get; }

        public IReadOnlyDictionary<string, string> Snapshot() => Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(Root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> OrderSnapshot(
        IReadOnlyDictionary<string, string> snapshot) => snapshot.OrderBy(
        item => item.Key,
        StringComparer.OrdinalIgnoreCase);
}
