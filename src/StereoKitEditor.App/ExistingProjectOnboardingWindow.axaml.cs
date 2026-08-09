using Avalonia.Controls;
using Avalonia.Interactivity;
using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.App;

public sealed record ExistingProjectOnboardingResult(
    string? DescriptorPath,
    string? ManifestPath,
    string Message);

public partial class ExistingProjectOnboardingWindow : Window
{
    private readonly ExistingProjectAnalysis _analysis;
    private readonly OnboardingProposalBuilder _proposalBuilder = new();
    private readonly OnboardingTransactionService _transactions = new();
    private OnboardingProposal? _proposal;

    public ExistingProjectOnboardingWindow()
        : this(CreateEmptyAnalysis())
    {
    }

    public ExistingProjectOnboardingWindow(ExistingProjectAnalysis analysis)
    {
        InitializeComponent();
        _analysis = analysis;
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
        PreviewButton.IsEnabled = choices.Length > 0;

        if (_analysis.Compatibility == ExistingProjectCompatibility.ReadyToOpen)
        {
            var descriptor = _analysis.ValidDescriptorPaths.FirstOrDefault();
            if (descriptor is not null)
            {
                ApplyButton.Content = "Open existing project";
                ApplyButton.IsEnabled = true;
                ApplyButton.Tag = descriptor;
                StatusText.Text = "No files will be changed.";
            }
        }
        else if (choices.Length == 0)
        {
            StatusText.Text = "No safe automatic proposal is available. Review the compatibility report for manual work.";
        }
    }

    private void HandleIntegrationChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (IntegrationChoiceBox.SelectedItem is IntegrationChoice choice)
        {
            IntegrationDescriptionText.Text = choice.Description;
        }

        _proposal = null;
        ChangesList.ItemsSource = null;
        DiffText.Text = string.Empty;
        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "Apply reviewed changes";
        StatusText.Text = "Preview again after changing the integration shape.";
    }

    private void HandlePreview(object? sender, RoutedEventArgs args)
    {
        if (IntegrationChoiceBox.SelectedItem is not IntegrationChoice choice)
        {
            return;
        }

        try
        {
            _proposal = _proposalBuilder.Create(_analysis, choice.Shape);
            var changes = _proposal.Changes.Select(change => new ChangeLine(
                change.Kind.ToString().ToUpperInvariant(),
                change.RelativePath,
                change.Purpose,
                change)).ToArray();
            ChangesList.ItemsSource = changes;
            ChangesList.SelectedItem = changes.FirstOrDefault();
            ApplyButton.IsEnabled = changes.Length > 0;
            StatusText.Text = $"Review all {changes.Length} create/modify actions before applying. No build or project code will run.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            _proposal = null;
            ApplyButton.IsEnabled = false;
            StatusText.Text = exception.Message;
        }
    }

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
        if (ApplyButton.Tag is ExistingProjectOnboardingResult completed)
        {
            Close(completed);
            return;
        }

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
        PreviewButton.IsEnabled = false;
        IntegrationChoiceBox.IsEnabled = false;
        StatusText.Text = "Applying the reviewed transaction…";
        try
        {
            var result = await _transactions.ApplyAsync(_proposal);
            var message = _proposal.IntegrationShape == OnboardingIntegrationShape.DirectOptIn
                ? $"Scaffolding applied. Complete the startup hook described in SKinnyEditor/README.md. Persistent report: {result.ReportPath}"
                : $"Dedicated editor head applied and safely validated. Persistent report: {result.ReportPath}";
            ApplyButton.Tag = new ExistingProjectOnboardingResult(
                _proposal.IntegrationShape == OnboardingIntegrationShape.DedicatedEditorHead
                    ? result.DescriptorPath
                    : null,
                result.ManifestPath,
                message);
            ApplyButton.Content = _proposal.IntegrationShape == OnboardingIntegrationShape.DedicatedEditorHead
                ? "Open project"
                : "Done";
            ApplyButton.IsEnabled = true;
            RollbackButton.IsVisible = true;
            CancelButton.Content = "Close";
            StatusText.Text = message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException
                                           or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
            ApplyButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            IntegrationChoiceBox.IsEnabled = true;
        }
    }

    private async void HandleRollback(object? sender, RoutedEventArgs args)
    {
        if (ApplyButton.Tag is not ExistingProjectOnboardingResult { ManifestPath: not null } completed)
        {
            return;
        }

        RollbackButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Rolling back files that still match the onboarding transaction…";
        try
        {
            var result = await _transactions.RollbackAsync(completed.ManifestPath);
            RollbackButton.IsVisible = false;
            ApplyButton.Tag = new ExistingProjectOnboardingResult(
                null,
                completed.ManifestPath,
                result.Status == OnboardingTransactionStatus.RolledBack
                    ? "Onboarding was rolled back. The transaction manifest and backups were retained."
                    : $"Rollback preserved conflicting edits: {string.Join(" ", result.Conflicts)}");
            ApplyButton.Content = "Close";
            ApplyButton.IsEnabled = true;
            StatusText.Text = ((ExistingProjectOnboardingResult)ApplyButton.Tag).Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException
                                           or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
            RollbackButton.IsEnabled = true;
            ApplyButton.IsEnabled = true;
        }
    }

    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(null);

    private static IEnumerable<IntegrationChoice> CreateIntegrationChoices(ExistingProjectAnalysis analysis)
    {
        if (analysis.Compatibility is ExistingProjectCompatibility.DirectOptInSupported
            or ExistingProjectCompatibility.DedicatedEditorHeadRecommended)
        {
            yield return new(
                OnboardingIntegrationShape.DirectOptIn,
                "Main-project opt-in",
                "Adds a runtime package and isolated adapter helper; you explicitly connect the startup hook.");
        }

        if (analysis.Compatibility is ExistingProjectCompatibility.DirectOptInSupported
            or ExistingProjectCompatibility.DedicatedEditorHeadRecommended
            or ExistingProjectCompatibility.ManualIntegrationRequired
            && analysis.RecommendedIntegration == OnboardingIntegrationShape.DedicatedEditorHead)
        {
            yield return new(
                OnboardingIntegrationShape.DedicatedEditorHead,
                "Dedicated editor head",
                "Creates a separate editor-only executable and leaves the production composition root untouched.");
        }
    }

    private static IReadOnlyList<ReportLine> Lines(IEnumerable<string> values) => values
        .Select(value => new ReportLine($"• {value}"))
        .ToArray();

    private static string FormatClassification(ExistingProjectCompatibility compatibility) => compatibility switch
    {
        ExistingProjectCompatibility.ReadyToOpen => "Ready to open",
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
