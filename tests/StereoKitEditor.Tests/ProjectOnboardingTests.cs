using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    public void Analysis_DoesNotSelectAnArbitraryStartupFromMultipleStereoKitExecutables()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "Second.csproj"),
            File.ReadAllText(fixture.ProjectPath).Replace("Fixture", "Second", StringComparison.Ordinal));

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.Root);

        Assert.Equal(ExistingProjectCompatibility.DedicatedEditorHeadRecommended, analysis.Compatibility);
        Assert.Null(analysis.RecommendedStartupProject);
        Assert.Throws<InvalidOperationException>(() => fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DedicatedEditorHead));
    }

    [Fact]
    public void Analysis_HonorsAnExplicitlySelectedStereoKitProject()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "Second.csproj"),
            File.ReadAllText(fixture.ProjectPath).Replace("Fixture", "Second", StringComparison.Ordinal));

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        Assert.Equal(fixture.ProjectPath, analysis.RecommendedStartupProject?.Path);
    }

    [Fact]
    public void Analysis_UsesDedicatedHeadForOlderCompatibleDesktopTarget()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            fixture.ProjectPath,
            File.ReadAllText(fixture.ProjectPath).Replace("net8.0", "net7.0", StringComparison.Ordinal));

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DedicatedEditorHead);

        Assert.Equal(ExistingProjectCompatibility.DedicatedEditorHeadRecommended, analysis.Compatibility);
        var project = Assert.Single(proposal.Changes, change =>
            change.RelativePath.EndsWith(".SKinny.Editor.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", project.ProposedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("net8.0-android")]
    [InlineData("net472")]
    public void Analysis_DoesNotOfferUnsafeDedicatedHeadForIncompatibleTarget(string targetFramework)
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            fixture.ProjectPath,
            File.ReadAllText(fixture.ProjectPath).Replace("net8.0", targetFramework, StringComparison.Ordinal));

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        Assert.Equal(ExistingProjectCompatibility.ManualIntegrationRequired, analysis.Compatibility);
        Assert.Null(analysis.RecommendedIntegration);
        Assert.Throws<InvalidOperationException>(() => fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DedicatedEditorHead));
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
        var proposal = fixture.CreateBuilder()
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
    public async Task DirectOptIn_AutomatesVoidMainAndCreatesPortableSdkFeed()
    {
        const string source = """
                              using StereoKit;

                              internal static class Program
                              {
                                  private static void Main(string[] args)
                                  {
                              #if SPACES
                                      StereoKit.Backend.OpenXR.ExcludeExt("XR_example");
                              #endif
                                      if (!SK.Initialize(default))
                                      {
                                          return;
                                      }
                                  }
                              }
                              """;
        using var fixture = new OnboardingFixture(programSource: source);
        var originalSource = File.ReadAllText(Path.Combine(fixture.Root, "Program.cs"));
        var analyzer = new ExistingStereoKitProjectAnalyzer();
        var analysis = analyzer.Analyze(fixture.ProjectPath);

        var project = Assert.Single(analysis.Projects);
        Assert.True(project.CanAddEditorLaunchHook);
        Assert.False(project.HasEditorLaunchHook);
        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);
        var programChange = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "SKinnyOnboarding.EditorEntryPoint.TryRun(args, out var skinnyEditorExitCode)",
            programChange.ProposedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "System.Environment.ExitCode = skinnyEditorExitCode;",
            programChange.ProposedText,
            StringComparison.Ordinal);
        AssertValidCSharp(programChange.ProposedText);
        Assert.Equal(4, proposal.Changes.Count(change => change.IsBinary));
        Assert.Contains(proposal.Changes, change =>
            string.Equals(change.RelativePath, "NuGet.config", StringComparison.OrdinalIgnoreCase));

        var transactions = new OnboardingTransactionService();
        var applied = await transactions.ApplyAsync(proposal, TestContext.Current.CancellationToken);

        Assert.True(applied.Validation.Succeeded);
        Assert.Contains(
            "SKinnyOnboarding.EditorEntryPoint.TryRun",
            File.ReadAllText(Path.Combine(fixture.Root, "Program.cs")),
            StringComparison.Ordinal);
        Assert.All(
            proposal.Changes.Where(change => change.IsBinary),
            change => Assert.True(File.Exists(Path.Combine(fixture.Root, change.RelativePath))));
        Assert.Equal(
            ExistingProjectCompatibility.ReadyToOpen,
            analyzer.Analyze(fixture.Root).Compatibility);

        var rollback = await transactions.RollbackAsync(
            applied.ManifestPath,
            TestContext.Current.CancellationToken);
        Assert.Equal(OnboardingTransactionStatus.RolledBack, rollback.Status);
        Assert.Equal(originalSource, File.ReadAllText(Path.Combine(fixture.Root, "Program.cs")));
    }

    [Theory]
    [InlineData("void", "_ = StereoKit.SK.Initialize(default);", "System.Environment.ExitCode = skinnyEditorExitCode;")]
    [InlineData("int", "return StereoKit.SK.Initialize(default) ? 0 : 1;", "return skinnyEditorExitCode;")]
    [InlineData("async Task", "await Task.Yield(); _ = StereoKit.SK.Initialize(default);", "System.Environment.ExitCode = skinnyEditorExitCode;")]
    [InlineData("async Task<int>", "await Task.Yield(); return StereoKit.SK.Initialize(default) ? 0 : 1;", "return skinnyEditorExitCode;")]
    public void DirectOptIn_SupportsConventionalMainReturnShapes(
        string signature,
        string body,
        string expectedCompletion)
    {
        var source = $$"""
                       using System.Threading.Tasks;

                       internal static class Program
                       {
                           private static {{signature}} Main(string[] args)
                           {
                               {{body}}
                           }
                       }
                       """;
        using var fixture = new OnboardingFixture(programSource: source);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);

        var program = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(expectedCompletion, program.ProposedText, StringComparison.Ordinal);
        AssertValidCSharp(program.ProposedText);
    }

    [Fact]
    public void Analysis_FallsBackToDedicatedHeadForAmbiguousEntryPoints()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "AlternateProgram.cs"),
            "internal static class AlternateProgram { private static void Main() { } }" + Environment.NewLine);

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        Assert.Equal(ExistingProjectCompatibility.DedicatedEditorHeadRecommended, analysis.Compatibility);
        Assert.Equal(OnboardingIntegrationShape.DedicatedEditorHead, analysis.RecommendedIntegration);
        var project = Assert.Single(analysis.Projects);
        Assert.False(project.CanAddEditorLaunchHook);
        Assert.Contains("2 possible", project.EditorLaunchHookAssessment, StringComparison.Ordinal);
        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DedicatedEditorHead);
        Assert.DoesNotContain(proposal.Changes, change =>
            string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(proposal.Changes, change => string.Equals(
            change.RelativePath,
            Path.Combine("SKinnyEditor", "Program.cs"),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analysis_DoesNotTreatHookTextInACommentAsAnInstalledHook()
    {
        const string source = """
                              internal static class Program
                              {
                                  // SKinnyOnboarding.EditorEntryPoint.TryRun(args, out var exitCode);
                                  private static int Main(string[] args)
                                  {
                                      return StereoKit.SK.Initialize(default) ? 0 : 1;
                                  }
                              }
                              """;
        using var fixture = new OnboardingFixture(programSource: source);

        var project = Assert.Single(new ExistingStereoKitProjectAnalyzer()
            .Analyze(fixture.ProjectPath)
            .Projects);

        Assert.False(project.HasEditorLaunchHook);
        Assert.True(project.CanAddEditorLaunchHook);
    }

    [Fact]
    public void Analysis_FallsBackToDedicatedHeadForNonUtf8EntryPointSource()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "Program.cs"),
            "internal static class Program { private static void Main() { } }",
            Encoding.Unicode);

        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        Assert.Equal(ExistingProjectCompatibility.DedicatedEditorHeadRecommended, analysis.Compatibility);
        Assert.False(Assert.Single(analysis.Projects).CanAddEditorLaunchHook);
    }

    [Fact]
    public void DirectOptIn_SupportsTopLevelStatements()
    {
        const string source = """
                              using StereoKit;

                              if (!SK.Initialize(default))
                              {
                                  return;
                              }
                              """;
        using var fixture = new OnboardingFixture(programSource: source);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);

        var program = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "System.Environment.GetCommandLineArgs()[1..]",
            program.ProposedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "System.Environment.ExitCode = skinnyEditorExitCode;",
            program.ProposedText,
            StringComparison.Ordinal);
        AssertValidCSharp(program.ProposedText);
    }

    [Fact]
    public void DirectProposal_MergesProjectLocalFeedIntoExistingNuGetConfiguration()
    {
        using var fixture = new OnboardingFixture();
        var configPath = Path.Combine(fixture.Root, "NuGet.config");
        File.WriteAllText(
            configPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="https://packages.example.test/v3/index.json" />
              </packageSources>
            </configuration>
            """ + Environment.NewLine);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);

        var config = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "NuGet.config", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("packages.example.test", config.ProposedText, StringComparison.Ordinal);
        Assert.Contains(".skinny/sdk", config.ProposedText, StringComparison.Ordinal);
        _ = System.Xml.Linq.XDocument.Parse(config.ProposedText!);
    }

    [Fact]
    public void DirectProposal_ExpandsEmptyNuGetPackageSources()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "NuGet.config"),
            "<configuration><packageSources /></configuration>" + Environment.NewLine);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);

        var config = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "NuGet.config", StringComparison.OrdinalIgnoreCase));
        var document = System.Xml.Linq.XDocument.Parse(config.ProposedText!);
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "add"
            && (string?)element.Attribute("value") == ".skinny/sdk");
    }

    [Fact]
    public void DirectProposal_ExpandsEmptyNuGetConfiguration()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "NuGet.config"),
            "<configuration />" + Environment.NewLine);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);

        var config = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "NuGet.config", StringComparison.OrdinalIgnoreCase));
        var document = System.Xml.Linq.XDocument.Parse(config.ProposedText!);
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "add"
            && (string?)element.Attribute("value") == ".skinny/sdk");
    }

    [Fact]
    public void DirectProposal_AddsProjectLocalFeedToNuGetPackageSourceMapping()
    {
        using var fixture = new OnboardingFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "NuGet.config"),
            """
            <configuration>
              <packageSources>
                <add key="private" value="https://packages.example.test/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """ + Environment.NewLine);
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);

        var proposal = fixture.CreateBuilder().Create(
            analysis,
            OnboardingIntegrationShape.DirectOptIn);

        var config = Assert.Single(proposal.Changes, change =>
            string.Equals(change.RelativePath, "NuGet.config", StringComparison.OrdinalIgnoreCase));
        var document = System.Xml.Linq.XDocument.Parse(config.ProposedText!);
        var localSource = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "add"
            && (string?)element.Attribute("value") == ".skinny/sdk");
        var localKey = (string?)localSource.Attribute("key");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "packageSource"
            && (string?)element.Attribute("key") == localKey
            && element.Elements().Any(pattern =>
                (string?)pattern.Attribute("pattern") == "SKinny.Editor.*"));
    }

    [Fact]
    public async Task Analysis_ResumesIncompleteDirectOnboardingAndFinishesIdempotently()
    {
        using var fixture = new OnboardingFixture();
        var analyzer = new ExistingStereoKitProjectAnalyzer();
        var initial = fixture.CreateBuilder().Create(
            analyzer.Analyze(fixture.ProjectPath),
            OnboardingIntegrationShape.DirectOptIn);
        var legacy = initial with
        {
            Changes = initial.Changes.Where(change =>
                    !string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        var transactions = new OnboardingTransactionService();
        _ = await transactions.ApplyAsync(legacy, TestContext.Current.CancellationToken);
        var readmePath = Path.Combine(fixture.Root, "SKinnyEditor", "README.md");
        File.WriteAllText(
            readmePath,
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
            """ + Environment.NewLine);

        var incomplete = analyzer.Analyze(fixture.Root);

        Assert.Equal(ExistingProjectCompatibility.IncompleteOnboarding, incomplete.Compatibility);
        Assert.Equal(OnboardingIntegrationShape.DirectOptIn, incomplete.RecommendedIntegration);
        Assert.Equal(fixture.ProjectPath, incomplete.RecommendedStartupProject?.Path);
        Assert.True(incomplete.RecommendedStartupProject?.CanAddEditorLaunchHook);
        var completion = fixture.CreateBuilder().Create(
            incomplete,
            OnboardingIntegrationShape.DirectOptIn);
        Assert.Equal(2, completion.Changes.Count);
        Assert.Contains(completion.Changes, change =>
            string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase));
        var readme = Assert.Single(completion.Changes, change => string.Equals(
            change.RelativePath,
            Path.Combine("SKinnyEditor", "README.md"),
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("return editorExitCode", readme.ProposedText, StringComparison.Ordinal);

        var applied = await transactions.ApplyAsync(completion, TestContext.Current.CancellationToken);

        Assert.True(applied.Validation.Succeeded);
        Assert.Equal(
            ExistingProjectCompatibility.ReadyToOpen,
            analyzer.Analyze(fixture.Root).Compatibility);
    }

    [Fact]
    public async Task Analysis_DoesNotReclassifyAHandAuthoredValidDescriptorAsIncomplete()
    {
        using var fixture = new OnboardingFixture();
        var analyzer = new ExistingStereoKitProjectAnalyzer();
        var initial = fixture.CreateBuilder().Create(
            analyzer.Analyze(fixture.ProjectPath),
            OnboardingIntegrationShape.DirectOptIn);
        var withoutHook = initial with
        {
            Changes = initial.Changes.Where(change =>
                    !string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        _ = await new OnboardingTransactionService().ApplyAsync(
            withoutHook,
            TestContext.Current.CancellationToken);
        File.WriteAllText(
            Path.Combine(fixture.Root, "SKinnyEditor", "EditorAdapter.cs"),
            "namespace CustomIntegration; internal sealed class HandAuthoredAdapter { }" + Environment.NewLine);

        var analysis = analyzer.Analyze(fixture.Root);

        Assert.Equal(ExistingProjectCompatibility.ReadyToOpen, analysis.Compatibility);
    }

    [Fact]
    public async Task DedicatedHeadProposal_IsDeterministicAndRollsBackCreatedFiles()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var builder = fixture.CreateBuilder();

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
        var proposal = fixture.CreateBuilder()
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
        var proposal = fixture.CreateBuilder().Create(
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
        var proposal = fixture.CreateBuilder()
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
        var proposal = fixture.CreateBuilder()
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
                            ProposedBytes = Encoding.UTF8.GetBytes(invalidDescriptor),
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
    public async Task InvalidGeneratedCSharp_AutomaticallyRestoresTheTransaction()
    {
        using var fixture = new OnboardingFixture();
        var originalProgram = File.ReadAllText(Path.Combine(fixture.Root, "Program.cs"));
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = fixture.CreateBuilder()
            .Create(analysis, OnboardingIntegrationShape.DirectOptIn);
        const string invalidSource = "{";
        var invalidBytes = Encoding.UTF8.GetBytes(invalidSource);
        proposal = proposal with
        {
            Changes = proposal.Changes.Select(change =>
                    string.Equals(change.RelativePath, "Program.cs", StringComparison.OrdinalIgnoreCase)
                        ? change with
                        {
                            ProposedText = invalidSource,
                            ProposedBytes = invalidBytes,
                            ProposedSha256 = Convert.ToHexString(SHA256.HashData(invalidBytes)),
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
        Assert.Equal(originalProgram, File.ReadAllText(Path.Combine(fixture.Root, "Program.cs")));
    }

    [Fact]
    public async Task InvalidGeneratedNuGetConfiguration_AutomaticallyRestoresTheTransaction()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = fixture.CreateBuilder()
            .Create(analysis, OnboardingIntegrationShape.DirectOptIn);
        const string invalidXml = "<configuration>";
        var invalidBytes = Encoding.UTF8.GetBytes(invalidXml);
        proposal = proposal with
        {
            Changes = proposal.Changes.Select(change =>
                    string.Equals(change.RelativePath, "NuGet.config", StringComparison.OrdinalIgnoreCase)
                        ? change with
                        {
                            ProposedText = invalidXml,
                            ProposedBytes = invalidBytes,
                            ProposedSha256 = Convert.ToHexString(SHA256.HashData(invalidBytes)),
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
        Assert.False(File.Exists(Path.Combine(fixture.Root, "NuGet.config")));
    }

    [Fact]
    public async Task Apply_BlocksCollisionBeforeCreatingTransaction()
    {
        using var fixture = new OnboardingFixture();
        var analysis = new ExistingStereoKitProjectAnalyzer().Analyze(fixture.ProjectPath);
        var proposal = fixture.CreateBuilder()
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
        var proposal = fixture.CreateBuilder()
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
        public OnboardingFixture(bool includeStereoKit = true, string? programSource = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"skinny-onboarding-{Guid.NewGuid():N}");
            SdkDirectory = Path.Combine(Path.GetTempPath(), $"skinny-onboarding-sdk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(SdkDirectory);
            foreach (var packageId in new[]
                     {
                         "SKinny.Editor.Adapter",
                         "SKinny.Editor.Scene",
                         "SKinny.Editor.Protocol",
                         "SKinny.Editor.Runtime",
                     })
            {
                File.WriteAllBytes(
                    Path.Combine(SdkDirectory, $"{packageId}.0.3.0-preview.1.nupkg"),
                    Encoding.UTF8.GetBytes($"test package: {packageId}"));
            }

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
                programSource
                ?? "internal static class Program { private static int Main(string[] args) { return StereoKit.SK.Initialize(default) ? 0 : 1; } }" + Environment.NewLine);
        }

        public string Root { get; }
        public string ProjectPath { get; }
        public string SdkDirectory { get; }

        public OnboardingProposalBuilder CreateBuilder() =>
            new("0.3.0-preview.1", SdkDirectory);

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

            if (Directory.Exists(SdkDirectory))
            {
                Directory.Delete(SdkDirectory, recursive: true);
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> OrderSnapshot(
        IReadOnlyDictionary<string, string> snapshot) => snapshot.OrderBy(
            item => item.Key,
            StringComparer.OrdinalIgnoreCase);

    private static void AssertValidCSharp(string? source)
    {
        Assert.NotNull(source);
        var diagnostics = CSharpSyntaxTree.ParseText(source).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(diagnostics);
    }
}
