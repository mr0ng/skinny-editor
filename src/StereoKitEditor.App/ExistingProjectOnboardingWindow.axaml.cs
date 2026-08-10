using Avalonia.Controls;
using Avalonia.Interactivity;
using StereoKitEditor.ProjectSystem;
using StereoKitEditor.Protocol;

namespace StereoKitEditor.App;

public sealed record ExistingProjectOnboardingResult(
    string? DescriptorPath,
    string? ManifestPath,
    string Message);

public partial class ExistingProjectOnboardingWindow : Window
{
    private readonly ExistingProjectAnalysis _analysis;
    private readonly OnboardingProposalBuilder _proposalBuilder;
    private readonly OnboardingTransactionService _transactions = new();
    private readonly StereoKitProjectCompatibilityAssessment? _stereoKitCompatibility;
    private OnboardingProposal? _proposal;

    public ExistingProjectOnboardingWindow()
        : this(CreateEmptyAnalysis())
    {
    }

    public ExistingProjectOnboardingWindow(ExistingProjectAnalysis analysis)
    {
        InitializeComponent();
        _stereoKitCompatibility = analysis.StereoKitCompatibility
                                  ?? new StereoKitProjectCompatibilityEvaluator(
                                          StereoKitCompatibility.TestedVersions)
                                      .Evaluate(analysis.RecommendedStartupProject);
        _analysis = analysis with { StereoKitCompatibility = _stereoKitCompatibility };
        _proposalBuilder = new(testedStereoKitVersion: StereoKitCompatibility.PreferredVersion);
        PopulateReport();
    }

    private void PopulateReport()
    {
        ClassificationText.Text = FormatClassification(_analysis.Compatibility);
        SummaryText.Text = _analysis.Summary;
        SelectedPathText.Text = _analysis.SelectedPath;
        ProjectRootText.Text = _analysis.ProjectRoot;
        ProjectsList.ItemsSource = _analysis.Projects.Select(project => new ProjectLine(
            project.Name,
            $"{string.Join(", ", project.TargetFrameworks.DefaultIfEmpty("target unresolved"))} · {project.OutputType} · " +
            $"StereoKit {project.StereoKitVersion ?? "not directly referenced"} · {project.Path}"));
        PopulateStereoKitCompatibility();
        ReasonsList.ItemsSource = Lines(_analysis.Reasons);
        AuthorableList.ItemsSource = Lines(_analysis.AuthorableContent);
        OpaqueList.ItemsSource = Lines(_analysis.OpaqueContent);
        PrerequisitesList.ItemsSource = Lines(
            _analysis.Prerequisites
                .Concat(_analysis.PackageConfigurationPaths.Select(path =>
                    $"Package configuration detected: {path}"))
                .Concat(_analysis.BuildCustomizationPaths.Select(path =>
                    $"Build customization detected but not evaluated: {path}"))
                .Concat(_analysis.Warnings));

        var choices = CreateIntegrationChoices(_analysis).ToArray();
        IntegrationChoiceBox.ItemsSource = choices;
        IntegrationChoiceBox.SelectedItem = choices.FirstOrDefault(choice =>
            choice.Shape == _analysis.RecommendedIntegration) ?? choices.FirstOrDefault();

        if (_analysis.Compatibility == ExistingProjectCompatibility.ReadyToOpen)
        {
            var descriptor = _analysis.ValidDescriptorPaths.FirstOrDefault();
            if (descriptor is not null)
            {
                if (_stereoKitCompatibility?.Compatibility
                    == StereoKitProjectCompatibility.UpgradeRequired)
                {
                    PrepareProposal();
                }
                else if (_stereoKitCompatibility?.Compatibility
                         == StereoKitProjectCompatibility.Unresolved)
                {
                    ApplyButton.IsEnabled = false;
                    StatusText.Text = _stereoKitCompatibility.Message;
                }
                else
                {
                    ApplyButton.Content = "Open existing project";
                    ApplyButton.IsEnabled = true;
                    ApplyButton.Tag = descriptor;
                    StatusText.Text = _stereoKitCompatibility?.Compatibility
                                      == StereoKitProjectCompatibility.UntestedNewer
                        ? "No files will be changed. Running this newer StereoKit version requires the explicit experimental override."
                        : "No files will be changed.";
                }
            }
        }
        else if (choices.Length == 0)
        {
            StatusText.Text = "No safe automatic proposal is available. Review the compatibility report for manual work.";
        }
        else
        {
            PrepareProposal();
        }
    }

    private void HandleIntegrationChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (IntegrationChoiceBox.SelectedItem is IntegrationChoice choice)
        {
            IntegrationDescriptionText.Text = choice.Description;
        }

        PrepareProposal();
    }

    private void HandleStereoKitUpgradeChanged(object? sender, RoutedEventArgs args) => PrepareProposal();

    private void PopulateStereoKitCompatibility()
    {
        if (_stereoKitCompatibility is null)
        {
            return;
        }

        StereoKitCompatibilityPanel.IsVisible = true;
        StereoKitCompatibilityTitle.Text = _stereoKitCompatibility.Compatibility switch
        {
            StereoKitProjectCompatibility.Tested => "StereoKit compatibility · Tested",
            StereoKitProjectCompatibility.UpgradeRequired => "StereoKit compatibility · Upgrade required",
            StereoKitProjectCompatibility.UntestedNewer => "StereoKit compatibility · Newer and untested",
            _ => "StereoKit compatibility · Version unresolved",
        };
        StereoKitCompatibilityText.Text = _stereoKitCompatibility.Message;
        if (_stereoKitCompatibility.Compatibility == StereoKitProjectCompatibility.UpgradeRequired
            && _stereoKitCompatibility.CanUpgradeAutomatically)
        {
            UpgradeStereoKitCheckBox.IsVisible = true;
            UpgradeStereoKitCheckBox.Content =
                $"Upgrade StereoKit to {_stereoKitCompatibility.TestedVersion} (recommended and reversible)";
            UpgradeStereoKitCheckBox.IsChecked = true;
        }
    }

    private void PrepareProposal()
    {
        _proposal = null;
        ChangesList.ItemsSource = null;
        DiffText.Text = string.Empty;
        ApplyButton.Tag = null;
        ApplyButton.IsEnabled = false;
        ReviewButton.IsVisible = false;
        ApplyButton.Content = _analysis.Compatibility == ExistingProjectCompatibility.IncompleteOnboarding
            ? "Finish setup & open"
            : "Import & Open";
        if (_stereoKitCompatibility?.Compatibility == StereoKitProjectCompatibility.Unresolved)
        {
            StatusText.Text = _stereoKitCompatibility.Message;
            return;
        }

        if (_stereoKitCompatibility?.Compatibility == StereoKitProjectCompatibility.UpgradeRequired
            && !_stereoKitCompatibility.CanUpgradeAutomatically)
        {
            StatusText.Text = _stereoKitCompatibility.Message;
            return;
        }

        if (_stereoKitCompatibility?.Compatibility == StereoKitProjectCompatibility.UpgradeRequired
            && UpgradeStereoKitCheckBox.IsChecked != true)
        {
            StatusText.Text = "The bundled runtime cannot restore against this older StereoKit version. Select the upgrade to continue.";
            return;
        }

        if (_analysis.Compatibility == ExistingProjectCompatibility.ReadyToOpen)
        {
            try
            {
                BindProposal(
                    _proposalBuilder.CreateStereoKitAlignment(_analysis),
                    "Upgrade & Open");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException or InvalidOperationException)
            {
                StatusText.Text = exception.Message;
            }

            return;
        }

        if (IntegrationChoiceBox.SelectedItem is not IntegrationChoice choice)
        {
            return;
        }

        try
        {
            BindProposal(
                _proposalBuilder.Create(
                    _analysis,
                    choice.Shape,
                    alignStereoKitVersion: _stereoKitCompatibility?.Compatibility
                                               == StereoKitProjectCompatibility.UpgradeRequired),
                ApplyButton.Content?.ToString() ?? "Import & Open");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            _proposal = null;
            ApplyButton.IsEnabled = false;
            StatusText.Text = exception.Message;
        }
    }

    private void BindProposal(OnboardingProposal proposal, string buttonText)
    {
        _proposal = proposal;
        var changes = proposal.Changes.Select(change => new ChangeLine(
            change.Kind.ToString().ToUpperInvariant(),
            change.RelativePath,
            change.Purpose,
            change)).ToArray();
        ChangesList.ItemsSource = changes;
        ChangesList.SelectedItem = changes.FirstOrDefault();
        ApplyButton.Content = buttonText;
        ApplyButton.IsEnabled = changes.Length > 0;
        ReviewButton.IsVisible = changes.Length > 0;
        StatusText.Text = $"Ready to apply {changes.Length} reversible change{(changes.Length == 1 ? string.Empty : "s")}. Review is optional; restore and project code run only after workspace trust.";
    }

    private void HandleReview(object? sender, RoutedEventArgs args) => OnboardingTabs.SelectedIndex = 1;

    private void HandleChangeSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (ChangesList.SelectedItem is not ChangeLine selected || _proposal is null)
        {
            return;
        }

        string[] details =
        [
            selected.Change.Diff,
            "Expected impact:",
            .. _proposal.ExpectedImpact.Select(line => $"  - {line}"),
            string.Empty,
            "Remaining manual work:",
            .. _proposal.RemainingManualWork.Select(line => $"  - {line}"),
        ];
        DiffText.Text = string.Join(Environment.NewLine, details);
    }

    private async void HandleApply(object? sender, RoutedEventArgs args)
    {
        if (ApplyButton.Tag is string existingDescriptor)
        {
            Close(new ExistingProjectOnboardingResult(
                existingDescriptor,
                null,
                "Opened the existing descriptor without changing project files."));
            return;
        }

        if (_proposal is null)
        {
            return;
        }

        ApplyButton.IsEnabled = false;
        ReviewButton.IsEnabled = false;
        IntegrationChoiceBox.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Applying the onboarding transaction…";
        try
        {
            var result = await _transactions.ApplyAsync(_proposal);
            var message = $"Import completed and safely validated. Persistent report: {result.ReportPath}";
            Close(new ExistingProjectOnboardingResult(
                result.DescriptorPath,
                result.ManifestPath,
                message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException
                                           or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
            ApplyButton.IsEnabled = true;
            ReviewButton.IsEnabled = true;
            IntegrationChoiceBox.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(null);

    private static IEnumerable<IntegrationChoice> CreateIntegrationChoices(ExistingProjectAnalysis analysis)
    {
        var startup = analysis.RecommendedStartupProject;
        var canUseDirect =
            (analysis.Compatibility is ExistingProjectCompatibility.DirectOptInSupported
                or ExistingProjectCompatibility.IncompleteOnboarding)
            && startup is not null
            && startup.TargetFrameworks.Any(
                ExistingStereoKitProjectAnalyzer.IsEditorRuntimeCompatibleFramework)
            && (startup.HasEditorLaunchHook || startup.CanAddEditorLaunchHook);
        var canUseDedicated = startup is not null
                              && (analysis.Compatibility is
                                  ExistingProjectCompatibility.DirectOptInSupported
                                  or ExistingProjectCompatibility.DedicatedEditorHeadRecommended
                                  or ExistingProjectCompatibility.IncompleteOnboarding)
                              && startup.TargetFrameworks.Any(
                                  ExistingStereoKitProjectAnalyzer.CanReferenceFromDedicatedHeadFramework);
        var shapes = new[]
            {
                OnboardingIntegrationShape.DirectOptIn,
                OnboardingIntegrationShape.DedicatedEditorHead,
            }
            .Where(shape => shape == OnboardingIntegrationShape.DirectOptIn
                ? canUseDirect
                : canUseDedicated)
            .OrderByDescending(shape => shape == analysis.RecommendedIntegration);
        foreach (var shape in shapes)
        {
            var recommended = shape == analysis.RecommendedIntegration;
            yield return shape == OnboardingIntegrationShape.DirectOptIn
                ? new(
                    shape,
                    recommended ? "Automatic (recommended) — Main project" : "Main-project integration",
                    "Adds a guarded editor launch to the existing entry point; normal launches remain unchanged.")
                : new(
                    shape,
                    recommended ? "Automatic (recommended) — Isolated head" : "Dedicated editor head",
                    "Creates a separate editor-only executable and leaves the production entry point untouched.");
        }
    }

    private static IReadOnlyList<ReportLine> Lines(IEnumerable<string> values) => values
        .Select(value => new ReportLine($"• {value}"))
        .ToArray();

    private static string FormatClassification(ExistingProjectCompatibility compatibility) => compatibility switch
    {
        ExistingProjectCompatibility.ReadyToOpen => "Ready to open",
        ExistingProjectCompatibility.IncompleteOnboarding => "Ready to finish automatically",
        ExistingProjectCompatibility.DirectOptInSupported => "Direct opt-in supported",
        ExistingProjectCompatibility.DedicatedEditorHeadRecommended => "Dedicated editor head recommended",
        ExistingProjectCompatibility.ManualIntegrationRequired => "Manual integration required",
        _ => "Run-only or unsupported",
    };

    private static ExistingProjectAnalysis CreateEmptyAnalysis() => new(
        string.Empty,
        Environment.CurrentDirectory,
        null,
        [],
        [],
        [],
        [],
        [],
        ExistingProjectCompatibility.Unsupported,
        null,
        "No project was analyzed.",
        [],
        [],
        [],
        [],
        []);

    private sealed record ProjectLine(string Name, string Details);
    private sealed record ReportLine(string Text);
    private sealed record ChangeLine(
        string Action,
        string Path,
        string Purpose,
        OnboardingProposedChange Change);
    private sealed record IntegrationChoice(
        OnboardingIntegrationShape Shape,
        string Label,
        string Description)
    {
        public override string ToString() => Label;
    }
}
