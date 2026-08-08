using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StereoKitEditor.Adapter;
using StereoKitEditor.Assets;
using StereoKitEditor.App.Infrastructure;
using StereoKitEditor.App.Services;
using StereoKitEditor.Core;
using StereoKitEditor.ProjectSystem;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly EditorSession _session;
    private readonly EditorProjectDefinition _project;
    private RuntimeProjectSpec _sceneRuntimeProject;
    private RuntimeProjectSpec _playRuntimeProject;
    private readonly WorkspaceTrustService _workspaceTrust = new();
    private readonly DotnetProjectBuilder _projectBuilder = new();
    private readonly AndroidAdbDeploymentProvider _androidDeployment = new();
    private readonly DiagnosticBundleWriter _diagnosticBundleWriter = new();
    private readonly EditorPreferencesService _preferencesService = new();
    private readonly SceneRecoveryStore _recoveryStore;
    private readonly string _workspaceDisplayRoot;
    private readonly AssetDatabase _assetDatabase;
    private readonly SceneTemplateLibrary _templateLibrary;
    private readonly RuntimeSession _sceneHost = new(RuntimeSessionMode.Scene);
    private readonly RuntimeSession _playHost = new(RuntimeSessionMode.Play);
    private readonly DebouncedFileWatcher _sourceWatcher;
    private readonly DebouncedFileWatcher _assetWatcher;
    private HierarchyItemViewModel? _selectedItem;
    private readonly HashSet<Guid> _selectedEntityIds = [];
    private bool _refreshingHierarchy;
    private string _sceneHostStatus = "Scene host starting…";
    private string _playHostStatus = "Play stopped";
    private string _statusMessage = "Ready";
    private long _lastSceneRevision = -1;
    private long _lastPlayRevision = -1;
    private RuntimeSessionMode _activeViewport = RuntimeSessionMode.Scene;
    private readonly Dictionary<string, DotnetBuildResult> _runtimeBuilds = new(StringComparer.Ordinal);
    private DotnetBuildResult? _sceneBuild;
    private EditorComponentCatalog? _sceneCatalog;
    private ComponentOptionViewModel? _selectedComponentToAdd;
    private readonly SceneCrashRecoveryPolicy _sceneCrashRecovery = new();
    private string? _playBuildId;
    private string? _playProfileId;
    private string? _playGenerationDirectory;
    private bool _isPlayStale;
    private bool _sceneRecoveryInProgress;
    private SceneCameraState _sceneCamera = SceneCameraState.Default;
    private SceneToolSettings _sceneToolSettings = SceneToolSettings.Default;
    private bool _initialized;
    private IReadOnlyList<AssetRecord> _assets = [];
    private ComponentMigrationProposalMessage? _pendingMigrationProposal;
    private string? _blockedStereoKitVersion;
    private RuntimeSessionMode _blockedCompatibilityMode;
    private bool _allowUntestedStereoKit;
    private RuntimeSession? _unresponsiveRuntime;
    private bool _autoRebuildEnabled;
    private bool _autoRefreshAssetsEnabled;
    private bool _sourceRefreshInProgress;
    private bool _assetRefreshInProgress;
    private readonly List<ProjectItemViewModel> _allProjectItems = [];
    private string _projectSearchText = string.Empty;
    private RuntimeProfileOptionViewModel? _selectedSceneProfile;
    private RuntimeProfileOptionViewModel? _selectedPlayProfile;
    private RuntimeTelemetryMessage? _sceneTelemetry;
    private RuntimeTelemetryMessage? _playTelemetry;
    private bool _showRuntimeInspection = true;
    private DeploymentProfileOptionViewModel? _selectedDeploymentProfile;
    private PendingDuplicateDrag? _pendingDuplicateDrag;
    private SceneRecoveryRecord? _pendingRecovery;

    public MainViewModel(string? explicitProjectPath = null)
    {
        var root = WorkspaceLocator.FindRoot();
        var projectPath = string.IsNullOrWhiteSpace(explicitProjectPath)
            ? EditorProjectLocator.ResolveStartupProject(
                root,
                Environment.GetCommandLineArgs(),
                Environment.GetEnvironmentVariable("SKINNY_PROJECT"))
            : Path.GetFullPath(explicitProjectPath);
        _project = EditorProjectDefinition.Load(projectPath);
        _assetDatabase = new AssetDatabase(
            _project.ProjectId,
            _project.ProjectDirectory,
            _project.AssetsRoot);
        _templateLibrary = new SceneTemplateLibrary(_project.ProjectDirectory);
        _sceneRuntimeProject = _project.CreateRuntimeProjectSpec(RuntimeProfileMode.Scene);
        _playRuntimeProject = _project.CreateRuntimeProjectSpec(RuntimeProfileMode.Play);
        foreach (var profile in GetProfileOptions(RuntimeProfileMode.Scene))
        {
            SceneProfiles.Add(profile);
        }

        foreach (var profile in GetProfileOptions(RuntimeProfileMode.Play))
        {
            PlayProfiles.Add(profile);
        }

        _selectedSceneProfile = SceneProfiles.Single(profile => profile.Id == _sceneRuntimeProject.ProfileId);
        _selectedPlayProfile = PlayProfiles.Single(profile => profile.Id == _playRuntimeProject.ProfileId);
        foreach (var profile in _project.DeploymentProfiles)
        {
            DeploymentProfiles.Add(new(
                profile,
                string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName));
        }

        _selectedDeploymentProfile = DeploymentProfiles.FirstOrDefault();
        var profileDirectories = SceneProfiles
            .Select(profile => _project.CreateRuntimeProjectSpec(RuntimeProfileMode.Scene, profile.Id).ProjectDirectory)
            .Concat(PlayProfiles.Select(profile =>
                _project.CreateRuntimeProjectSpec(RuntimeProfileMode.Play, profile.Id).ProjectDirectory));
        var sourceRoot = FindCommonDirectory(new[] { _project.ProjectDirectory }.Concat(profileDirectories).ToArray());
        _workspaceDisplayRoot = Path.GetDirectoryName(_project.ResolveSolutionPath()) ?? sourceRoot;
        _sourceWatcher = new DebouncedFileWatcher(sourceRoot, DebouncedFileWatcher.IsProjectSourcePath);
        _assetWatcher = new DebouncedFileWatcher(_assetDatabase.AssetsRoot, DebouncedFileWatcher.IsAssetSourcePath);
        _sourceWatcher.FilesChanged += HandleSourceFilesChanged;
        _assetWatcher.FilesChanged += HandleAssetFilesChanged;
        _sourceWatcher.Error += HandleWatcherError;
        _assetWatcher.Error += HandleWatcherError;
        var preferences = _preferencesService.Load();
        _autoRebuildEnabled = preferences.AutoRebuildSource;
        _autoRefreshAssetsEnabled = preferences.AutoRefreshAssets;
        _showRuntimeInspection = preferences.ShowRuntimeInspection;
        _sourceWatcher.IsEnabled = _autoRebuildEnabled;
        _assetWatcher.IsEnabled = _autoRefreshAssetsEnabled;
        var scenePath = _project.ResolveStartupScenePath();
        var sceneLoad = File.Exists(scenePath)
            ? SceneSerializer.DeserializeWithMetadata(File.ReadAllText(scenePath))
            : new SceneDeserializationResult(CreateFallbackScene(), false);

        _recoveryStore = new SceneRecoveryStore(_project.ProjectId, scenePath);
        _pendingRecovery = _recoveryStore.TryLoad();
        _recoveryStore.WriteFailed += HandleRecoveryWriteFailed;
        _session = new EditorSession(sceneLoad.Document, scenePath, sceneLoad.MigratedFromFormat1);
        _session.Changed += HandleSessionChanged;
        _sceneHost.EventReceived += HandleRuntimeEvent;
        _playHost.EventReceived += HandleRuntimeEvent;

        AddCubeCommand = new RelayCommand(() => AddPrimitive(PrimitiveKind.Cube));
        AddSphereCommand = new RelayCommand(() => AddPrimitive(PrimitiveKind.Sphere));
        InitializePhase5Commands();
        SaveCommand = new AsyncRelayCommand(() => RunSafelyAsync(SaveAsync), () => _session.IsDirty);
        ReloadCommand = new AsyncRelayCommand(() => RunSafelyAsync(ReloadAsync), () => !_session.IsDirty);
        UndoCommand = new RelayCommand(Undo, () => _session.History.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _session.History.CanRedo);
        RestartSceneHostCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(StartSceneHostAsync),
            () => !_sceneHost.IsRunning);
        StartPlayCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(StartPlayAsync),
            () => !_playHost.IsRunning);
        StopPlayCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(StopPlayAsync),
            () => _playHost.IsRunning);
        TogglePauseCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(TogglePauseAsync),
            () => _playHost.IsRunning && _playHost.IsReady);
        StepPlayCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(StepPlayAsync),
            () => _playHost.IsRunning && _playHost.IsReady);
        ShowSceneCommand = new RelayCommand(() => ActiveViewport = RuntimeSessionMode.Scene);
        ShowGameCommand = new RelayCommand(() => ActiveViewport = RuntimeSessionMode.Play, () => _playHost.IsRunning);
        FrameSelectionCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(FrameSelectionAsync),
            () => SelectedEntity is not null && _sceneHost.IsReady);
        SelectMoveToolCommand = new RelayCommand(() => SetTransformTool(SceneTransformTool.Move));
        SelectRotateToolCommand = new RelayCommand(() => SetTransformTool(SceneTransformTool.Rotate));
        SelectScaleToolCommand = new RelayCommand(() => SetTransformTool(SceneTransformTool.Scale));
        ToggleGizmoSpaceCommand = new RelayCommand(ToggleGizmoSpace);
        ToggleActiveSnapCommand = new RelayCommand(ToggleActiveSnap);
        ToggleProjectionCommand = new RelayCommand(ToggleProjection);
        ToggleGridCommand = new RelayCommand(ToggleGrid);
        TogglePivotModeCommand = new RelayCommand(TogglePivotMode);
        ViewFrontCommand = new RelayCommand(() => SetSceneView(0, 0, "Front"));
        ViewRightCommand = new RelayCommand(() => SetSceneView(90, 0, "Right"));
        ViewTopCommand = new RelayCommand(() => SetSceneView(0, -89, "Top"));
        ViewIsometricCommand = new RelayCommand(() => SetSceneView(45, 28, "Isometric"));
        AddSelectedComponentCommand = new RelayCommand(AddSelectedComponent, CanAddSelectedComponent);
        RefreshAssetsCommand = new AsyncRelayCommand(() => RunSafelyAsync(() => RefreshAssetsAsync()));
        ClearConsoleCommand = new RelayCommand(ConsoleEntries.Clear);
        ApplyMigrationProposalCommand = new RelayCommand(ApplyMigrationProposal, CanApplyMigrationProposal);
        DismissMigrationProposalCommand = new RelayCommand(ClearMigrationProposal);
        RunUntestedStereoKitCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(RunUntestedStereoKitAsync),
            () => HasCompatibilityBlock);
        DismissCompatibilityBlockCommand = new RelayCommand(ClearCompatibilityBlock);
        WaitForRuntimeCommand = new RelayCommand(WaitForRuntime, () => HasUnresponsiveRuntime);
        RestartUnresponsiveRuntimeCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(RestartUnresponsiveRuntimeAsync),
            () => HasUnresponsiveRuntime);
        StopUnresponsiveRuntimeCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(StopUnresponsiveRuntimeAsync),
            () => HasUnresponsiveRuntime);
        CreateChildCommand = new RelayCommand(CreateChildEntity);
        DuplicateEntitiesCommand = new RelayCommand(DuplicateSelectedEntities, () => SelectedEntityIds.Count > 0);
        DeleteEntitiesCommand = new RelayCommand(DeleteSelectedEntities, () => SelectedEntityIds.Count > 0);
        BeginHierarchyRenameCommand = new RelayCommand(() => BeginHierarchyRename(), () => SelectedItem is not null);
        DeployCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(DeployAsync),
            () => SelectedDeploymentProfile is not null && IsWorkspaceTrusted);

        RefreshProjectItems();
        RefreshComponentInspector();

        RefreshHierarchy();
        var initialSelection = _session.Document.Roots.FirstOrDefault()?.Id;
        SelectOnly(initialSelection);
        RefreshHierarchy();
        AddConsole("Info", $"Opened {_session.ScenePath}");
    }

    public event EventHandler<RuntimeWindowChangedEventArgs>? RuntimeWindowChanged;

    public ObservableCollection<HierarchyItemViewModel> Entities { get; } = [];
    public ObservableCollection<ProjectItemViewModel> ProjectFiles { get; } = [];
    public ObservableCollection<ConsoleEntryViewModel> ConsoleEntries { get; } = [];
    public ObservableCollection<ComponentOptionViewModel> AvailableComponents { get; } = [];
    public ObservableCollection<ComponentInspectorViewModel> InspectorComponents { get; } = [];
    public ObservableCollection<ModelMaterialSlotViewModel> ModelMaterialSlots { get; } = [];
    public ObservableCollection<RuntimeProfileOptionViewModel> SceneProfiles { get; } = [];
    public ObservableCollection<RuntimeProfileOptionViewModel> PlayProfiles { get; } = [];
    public ObservableCollection<RuntimeComponentStatusViewModel> RuntimeComponentStates { get; } = [];
    public ObservableCollection<DeploymentProfileOptionViewModel> DeploymentProfiles { get; } = [];

    public RelayCommand AddCubeCommand { get; }
    public RelayCommand AddSphereCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public AsyncRelayCommand RestartSceneHostCommand { get; }
    public AsyncRelayCommand StartPlayCommand { get; }
    public AsyncRelayCommand StopPlayCommand { get; }
    public AsyncRelayCommand TogglePauseCommand { get; }
    public AsyncRelayCommand StepPlayCommand { get; }
    public RelayCommand ShowSceneCommand { get; }
    public RelayCommand ShowGameCommand { get; }
    public AsyncRelayCommand FrameSelectionCommand { get; }
    public RelayCommand SelectMoveToolCommand { get; }
    public RelayCommand SelectRotateToolCommand { get; }
    public RelayCommand SelectScaleToolCommand { get; }
    public RelayCommand ToggleGizmoSpaceCommand { get; }
    public RelayCommand ToggleActiveSnapCommand { get; }
    public RelayCommand ToggleProjectionCommand { get; }
    public RelayCommand ToggleGridCommand { get; }
    public RelayCommand TogglePivotModeCommand { get; }
    public RelayCommand ViewFrontCommand { get; }
    public RelayCommand ViewRightCommand { get; }
    public RelayCommand ViewTopCommand { get; }
    public RelayCommand ViewIsometricCommand { get; }
    public RelayCommand AddSelectedComponentCommand { get; }
    public AsyncRelayCommand RefreshAssetsCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }
    public RelayCommand ApplyMigrationProposalCommand { get; }
    public RelayCommand DismissMigrationProposalCommand { get; }
    public AsyncRelayCommand RunUntestedStereoKitCommand { get; }
    public RelayCommand DismissCompatibilityBlockCommand { get; }
    public RelayCommand WaitForRuntimeCommand { get; }
    public AsyncRelayCommand RestartUnresponsiveRuntimeCommand { get; }
    public AsyncRelayCommand StopUnresponsiveRuntimeCommand { get; }
    public RelayCommand CreateChildCommand { get; }
    public RelayCommand DuplicateEntitiesCommand { get; }
    public RelayCommand DeleteEntitiesCommand { get; }
    public RelayCommand BeginHierarchyRenameCommand { get; }
    public AsyncRelayCommand DeployCommand { get; }

    public string WindowTitle => $"{_session.Document.Name}{(_session.IsDirty ? " *" : string.Empty)} — SKinny Editor";
    public string ProjectDefinitionPath => _project.DefinitionPath;
    public string ProjectName => _project.Name;
    public bool HasUnsavedChanges => _session.IsDirty;
    public bool HasPendingRecovery => _pendingRecovery is not null;
    public string PendingRecoveryDescription => _pendingRecovery is { } recovery
        ? $"An unsaved recovery snapshot from {recovery.CapturedUtc.ToLocalTime():g} was found" +
          (recovery.SourceChangedSinceCapture
              ? ". The scene file also changed after that snapshot, so review the restored scene before saving."
              : ". Restore it to continue from those unsaved changes, or discard it to open the saved scene.")
        : string.Empty;
    public string SceneStatus => $"{Path.GetFileName(_session.ScenePath)} · revision {_session.Revision}{(_session.IsDirty ? " · unsaved" : string.Empty)}";
    public string SceneHostStatus { get => _sceneHostStatus; private set => SetProperty(ref _sceneHostStatus, value); }
    public string PlayHostStatus { get => _playHostStatus; private set => SetProperty(ref _playHostStatus, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasSelection => SelectedEntity is not null;
    public IReadOnlyList<Guid> SelectedEntityIds => _selectedEntityIds.Count > 0
        ? _selectedEntityIds.ToArray()
        : SelectedEntity is { } entity ? [entity.Id] : [];
    public bool IsSceneHostRunning => _sceneHost.IsRunning;
    public bool IsPlayRunning => _playHost.IsRunning;
    public bool IsPlayPaused => _playHost.PlayState == RuntimePlayState.Paused;
    public bool IsPlayStale => _isPlayStale;
    public string PauseLabel => IsPlayPaused ? "▶ Resume" : "Ⅱ Pause";
    public bool IsSceneViewActive => ActiveViewport == RuntimeSessionMode.Scene;
    public bool IsGameViewActive => ActiveViewport == RuntimeSessionMode.Play;
    public string SceneRenderRevision => _lastSceneRevision < 0 ? "Scene not rendered" : $"Scene r{_lastSceneRevision}";
    public string PlayRenderRevision => _lastPlayRevision < 0 ? "Game not rendered" : $"Game r{_lastPlayRevision}";
    public bool HasRuntimeTelemetry => DisplayTelemetry is not null;
    public bool HasRuntimeInspection => DisplayTelemetry?.InspectedEntity is not null;
    public string RuntimePerformanceSummary => DisplayTelemetry is { } telemetry
        ? $"{telemetry.FramesPerSecond:0.0} FPS · {telemetry.FrameTimeMilliseconds:0.00} ms · frame {telemetry.Frame}"
        : "Waiting for runtime telemetry…";
    public string RuntimeCountsSummary => DisplayTelemetry is { } telemetry
        ? $"{telemetry.EnabledEntityCount}/{telemetry.EntityCount} active objects · {telemetry.LiveComponentCount}/{telemetry.ComponentCount} live components"
        : string.Empty;
    public string RuntimeMemorySummary => DisplayTelemetry is { } telemetry
        ? $"Runtime memory {FormatBytes(telemetry.WorkingSetBytes)} · managed {FormatBytes(telemetry.ManagedMemoryBytes)}"
        : string.Empty;
    public string RuntimeInspectionTitle => DisplayTelemetry?.InspectedEntity is { } inspected
        ? $"Live: {inspected.Name}"
        : "No live object selected";
    public bool HasDeploymentProfiles => DeploymentProfiles.Count > 0;
    public string GizmoSpaceLabel => _sceneToolSettings.Tool == SceneTransformTool.Scale
        ? "Local"
        : _sceneToolSettings.GizmoSpace == SceneGizmoSpace.Global ? "Global" : "Local";
    public bool IsMoveTool => _sceneToolSettings.Tool == SceneTransformTool.Move;
    public bool IsRotateTool => _sceneToolSettings.Tool == SceneTransformTool.Rotate;
    public bool IsScaleTool => _sceneToolSettings.Tool == SceneTransformTool.Scale;
    public string ActiveSnapLabel => IsActiveSnapEnabled ? "Snap On" : "Snap Off";
    public string ProjectionLabel => _sceneCamera.Projection == SceneProjection.Orthographic ? "Ortho" : "Persp";
    public string GridLabel => _sceneToolSettings.ShowGrid ? "Grid On" : "Grid Off";
    public string PivotModeLabel => _sceneToolSettings.PivotMode == ScenePivotMode.Center ? "Center" : "Active";
    public string ActiveSnapUnits => _sceneToolSettings.Tool switch
    {
        SceneTransformTool.Move => "m",
        SceneTransformTool.Rotate => "°",
        _ => "step",
    };
    public decimal ActiveSnapAmount
    {
        get => (decimal)(_sceneToolSettings.Tool switch
        {
            SceneTransformTool.Move => _sceneToolSettings.TranslationSnap,
            SceneTransformTool.Rotate => _sceneToolSettings.RotationSnapDegrees,
            _ => _sceneToolSettings.ScaleSnap,
        });
        set
        {
            var maximum = _sceneToolSettings.Tool == SceneTransformTool.Rotate ? 180 : 10;
            var clamped = Math.Clamp((double)value, 0.001, maximum);
            var current = (double)ActiveSnapAmount;
            if (Math.Abs(clamped - current) < 0.000001)
            {
                return;
            }

            _sceneToolSettings = _sceneToolSettings.Tool switch
            {
                SceneTransformTool.Move => _sceneToolSettings with { TranslationSnap = clamped },
                SceneTransformTool.Rotate => _sceneToolSettings with { RotationSnapDegrees = clamped },
                _ => _sceneToolSettings with { ScaleSnap = clamped },
            };
            OnPropertyChanged(nameof(ActiveSnapAmount));
            _ = PushSceneToolSettingsSafelyAsync();
        }
    }
    private bool IsActiveSnapEnabled => _sceneToolSettings.Tool switch
    {
        SceneTransformTool.Move => _sceneToolSettings.TranslationSnapEnabled,
        SceneTransformTool.Rotate => _sceneToolSettings.RotationSnapEnabled,
        _ => _sceneToolSettings.ScaleSnapEnabled,
    };
    public bool IsWorkspaceTrusted => _workspaceTrust.IsTrusted(_project.ProjectId, _project.DefinitionPath);
    public WorkspaceTrustSummary WorkspaceTrust => new(
        _project.ProjectId,
        _project.Name,
        _project.DefinitionPath,
        string.Join(Environment.NewLine, new[] { _sceneRuntimeProject.ProjectPath, _playRuntimeProject.ProjectPath }.Distinct(StringComparer.OrdinalIgnoreCase)),
        string.Join(Environment.NewLine, new[] { _sceneRuntimeProject.WorkingDirectory, _playRuntimeProject.WorkingDirectory }.Distinct(StringComparer.OrdinalIgnoreCase)),
        string.Join(Environment.NewLine, new[] { CreateBuildCommandSummary(_sceneRuntimeProject), CreateBuildCommandSummary(_playRuntimeProject) }.Distinct(StringComparer.Ordinal)),
        string.Join(" ", _sceneRuntimeProject.Arguments.Concat(_playRuntimeProject.Arguments)),
        _sceneRuntimeProject.Environment.Count + _playRuntimeProject.Environment.Count == 0
            ? "None"
            : string.Join(", ", _sceneRuntimeProject.Environment.Keys
                .Concat(_playRuntimeProject.Environment.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)));
    public bool HasComponentCatalog => _sceneCatalog is not null;
    public bool HasMigrationProposal => _pendingMigrationProposal is { Upgrades.Count: > 0 };
    public string MigrationProposalSummary => _pendingMigrationProposal is { } proposal
        ? $"{proposal.Upgrades.Count} component schema upgrade{(proposal.Upgrades.Count == 1 ? string.Empty : "s")} available"
        : string.Empty;
    public bool HasCompatibilityBlock => !string.IsNullOrWhiteSpace(_blockedStereoKitVersion);
    public string CompatibilityBlockSummary => HasCompatibilityBlock
        ? $"StereoKit {_blockedStereoKitVersion} is untested"
        : string.Empty;
    public bool HasUnresponsiveRuntime => _unresponsiveRuntime is not null;
    public string UnresponsiveRuntimeSummary => _unresponsiveRuntime is { } runtime
        ? $"{(runtime.Mode == RuntimeSessionMode.Scene ? "Scene" : "Game")} is not responding"
        : string.Empty;
    public bool AutoRebuildEnabled
    {
        get => _autoRebuildEnabled;
        set
        {
            if (SetProperty(ref _autoRebuildEnabled, value))
            {
                _sourceWatcher.IsEnabled = value;
                SavePreferences();
                StatusMessage = value ? "Automatic source rebuild enabled" : "Automatic source rebuild disabled";
            }
        }
    }
    public bool AutoRefreshAssetsEnabled
    {
        get => _autoRefreshAssetsEnabled;
        set
        {
            if (SetProperty(ref _autoRefreshAssetsEnabled, value))
            {
                _assetWatcher.IsEnabled = value;
                SavePreferences();
                StatusMessage = value ? "Automatic asset refresh enabled" : "Automatic asset refresh disabled";
            }
        }
    }
    public bool ShowRuntimeInspection
    {
        get => _showRuntimeInspection;
        set
        {
            if (SetProperty(ref _showRuntimeInspection, value))
            {
                SavePreferences();
            }
        }
    }
    public string ProjectSearchText
    {
        get => _projectSearchText;
        set
        {
            if (SetProperty(ref _projectSearchText, value))
            {
                ApplyProjectFilter();
            }
        }
    }
    public RuntimeProfileOptionViewModel? SelectedSceneProfile
    {
        get => _selectedSceneProfile;
        set
        {
            if (value is null || !SetProperty(ref _selectedSceneProfile, value))
            {
                return;
            }

            _sceneRuntimeProject = _project.CreateRuntimeProjectSpec(RuntimeProfileMode.Scene, value.Id);
            RefreshProjectItems();
            StatusMessage = $"Scene profile changed to {value.DisplayName}";
            if (_initialized && IsWorkspaceTrusted)
            {
                _ = RunSafelyAsync(StartSceneHostAsync);
            }
        }
    }
    public RuntimeProfileOptionViewModel? SelectedPlayProfile
    {
        get => _selectedPlayProfile;
        set
        {
            if (value is null || !SetProperty(ref _selectedPlayProfile, value))
            {
                return;
            }

            _playRuntimeProject = _project.CreateRuntimeProjectSpec(RuntimeProfileMode.Play, value.Id);
            RefreshProjectItems();
            _isPlayStale = _playHost.IsRunning;
            OnPropertyChanged(nameof(IsPlayStale));
            StatusMessage = _playHost.IsRunning
                ? $"Play profile changed to {value.DisplayName}; restart Play to apply"
                : $"Play profile changed to {value.DisplayName}";
        }
    }
    public DeploymentProfileOptionViewModel? SelectedDeploymentProfile
    {
        get => _selectedDeploymentProfile;
        set
        {
            if (SetProperty(ref _selectedDeploymentProfile, value))
            {
                DeployCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string AddComponentHint => _sceneCatalog is null
        ? "Waiting for the project adapter…"
        : AvailableComponents.Count == 0
            ? "No more project components can be added to this entity."
            : "Choose a component registered by this project.";

    public ComponentOptionViewModel? SelectedComponentToAdd
    {
        get => _selectedComponentToAdd;
        set
        {
            if (SetProperty(ref _selectedComponentToAdd, value))
            {
                AddSelectedComponentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RuntimeSessionMode ActiveViewport
    {
        get => _activeViewport;
        private set
        {
            if (!SetProperty(ref _activeViewport, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSceneViewActive));
            OnPropertyChanged(nameof(IsGameViewActive));
            NotifyRuntimeTelemetry();
        }
    }

    public HierarchyItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            if (_refreshingHierarchy)
            {
                return;
            }

            _selectedEntityIds.Clear();
            if (value is not null)
            {
                _selectedEntityIds.Add(value.Id);
            }
            _session.Select(value?.Id);

            NotifyInspector();
            NotifyHierarchyCommands();
        }
    }

    public void SelectHierarchyItems(IEnumerable<HierarchyItemViewModel> items)
    {
        var selected = items.DistinctBy(item => item.Id).ToArray();
        var primary = _selectedItem is not null && selected.Any(item => item.Id == _selectedItem.Id)
            ? _selectedItem.Id
            : selected.LastOrDefault()?.Id;
        _selectedEntityIds.Clear();
        foreach (var item in selected)
        {
            _selectedEntityIds.Add(item.Id);
        }
        _session.Select(primary);

        foreach (var item in Entities)
        {
            item.IsSelected = _selectedEntityIds.Contains(item.Id);
        }

        NotifyInspector();
        NotifyHierarchyCommands();
    }

    public void BeginHierarchyRename(HierarchyItemViewModel? item = null)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        foreach (var candidate in Entities)
        {
            candidate.IsRenaming = ReferenceEquals(candidate, item);
        }

        item.EditName = item.Name;
    }

    public void CommitHierarchyRename(HierarchyItemViewModel item)
    {
        var trimmed = item.EditName.Trim();
        item.IsRenaming = false;
        var entity = _session.Document.FindEntity(item.Id);
        if (entity is null || trimmed.Length == 0 || entity.Name == trimmed)
        {
            item.EditName = entity?.Name ?? item.Name;
            return;
        }

        _session.Execute(new RenameEntityCommand(entity.Id, entity.Name, trimmed));
    }

    public void CancelHierarchyRename(HierarchyItemViewModel item)
    {
        item.EditName = item.Name;
        item.IsRenaming = false;
    }

    public void ReparentSelectedEntities(Guid? newParentId)
    {
        var ids = SelectedEntityIds;
        if (ids.Count == 0 || newParentId is { } parentId && ids.Contains(parentId))
        {
            return;
        }

        try
        {
            _session.Execute(new ReparentEntitiesCommand(ids, newParentId));
            StatusMessage = newParentId is { } id
                ? $"Reparented {ids.Count} object{(ids.Count == 1 ? string.Empty : "s")} under {_session.Document.FindEntity(id)?.Name}"
                : $"Moved {ids.Count} object{(ids.Count == 1 ? string.Empty : "s")} to scene root";
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
            AddConsole("Warning", $"[Hierarchy] {exception.Message}");
        }
    }

    public string SelectedName
    {
        get => SelectedEntity?.Name ?? string.Empty;
        set
        {
            var entity = SelectedEntity;
            var trimmed = value.Trim();
            if (entity is null || trimmed.Length == 0 || entity.Name == trimmed)
            {
                return;
            }

            _session.Execute(new RenameEntityCommand(entity.Id, entity.Name, trimmed));
        }
    }

    public bool SelectedEnabled
    {
        get => SelectedEntity?.Enabled ?? false;
        set
        {
            var entity = SelectedEntity;
            if (entity is null || entity.Enabled == value)
            {
                return;
            }

            _session.Execute(new SetEntityEnabledCommand(entity.Id, entity.Enabled, value));
        }
    }

    public string SelectedKind => SelectedEntity switch
    {
        { Components.UiPanel: not null } => "Spatial UI Panel",
        { Components.ImageRenderer: not null } => "Image",
        { Components.TextRenderer: not null } => "Text",
        { Components.UiText: not null } => "UI Text",
        { Components.UiImage: not null } => "UI Image",
        { Components.UiButton: not null } => "UI Button",
        { Components.UiToggle: not null } => "UI Toggle",
        { Components.UiSlider: not null } => "UI Slider",
        { Components.UiTextInput: not null } => "UI Text Input",
        { Components.UiSeparator: not null } => "UI Separator",
        { Components.UiSpacer: not null } => "UI Spacer",
        { Components.ModelRenderer: not null } => "Model",
        { Components.PrimitiveMeshRenderer: { } primitive } => primitive.Primitive.ToString(),
        _ => "Empty",
    };

    public decimal PositionX { get => (decimal)PositionValue.X; set => SetPosition(new((double)value, PositionValue.Y, PositionValue.Z)); }
    public decimal PositionY { get => (decimal)PositionValue.Y; set => SetPosition(new(PositionValue.X, (double)value, PositionValue.Z)); }
    public decimal PositionZ { get => (decimal)PositionValue.Z; set => SetPosition(new(PositionValue.X, PositionValue.Y, (double)value)); }
    public decimal RotationX { get => (decimal)RotationValue.X; set => SetRotation(new((double)value, RotationValue.Y, RotationValue.Z, RotationValue.W)); }
    public decimal RotationY { get => (decimal)RotationValue.Y; set => SetRotation(new(RotationValue.X, (double)value, RotationValue.Z, RotationValue.W)); }
    public decimal RotationZ { get => (decimal)RotationValue.Z; set => SetRotation(new(RotationValue.X, RotationValue.Y, (double)value, RotationValue.W)); }
    public decimal RotationW { get => (decimal)RotationValue.W; set => SetRotation(new(RotationValue.X, RotationValue.Y, RotationValue.Z, (double)value)); }
    public decimal ScaleX { get => (decimal)ScaleValue.X; set => SetScale(new((double)value, ScaleValue.Y, ScaleValue.Z)); }
    public decimal ScaleY { get => (decimal)ScaleValue.Y; set => SetScale(new(ScaleValue.X, (double)value, ScaleValue.Z)); }
    public decimal ScaleZ { get => (decimal)ScaleValue.Z; set => SetScale(new(ScaleValue.X, ScaleValue.Y, (double)value)); }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        if (!IsWorkspaceTrusted)
        {
            throw new InvalidOperationException("Workspace trust is required before building or running project code.");
        }

        _initialized = true;
        await RefreshAssetsAsync(pushToRuntime: false);
        await RunSafelyAsync(StartSceneHostAsync);
    }

    public async Task<bool> TrySaveNowAsync()
    {
        try
        {
            await SaveAsync();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Scene save failed";
            AddConsole("Error", exception.Message);
            return false;
        }
    }

    public void RestorePendingRecovery()
    {
        if (_pendingRecovery is not { } recovery)
        {
            return;
        }

        _pendingRecovery = null;
        _session.Recover(recovery.Document);
        StatusMessage = "Recovered unsaved scene changes";
        AddConsole("Recovery", $"Restored local recovery snapshot from {recovery.CapturedUtc.ToLocalTime():g}.");
        OnPropertyChanged(nameof(HasPendingRecovery));
        OnPropertyChanged(nameof(PendingRecoveryDescription));
    }

    public void DiscardPendingRecovery()
    {
        _pendingRecovery = null;
        _recoveryStore.Clear();
        OnPropertyChanged(nameof(HasPendingRecovery));
        OnPropertyChanged(nameof(PendingRecoveryDescription));
    }

    public void DiscardUnsavedRecovery() => _recoveryStore.Clear();

    public Task ImportGlbAsync(string path) => ImportAssetAsync(path);

    public async Task ImportAssetAsync(string path)
    {
        try
        {
            var record = await _assetDatabase.ImportAsync(path);
            await RefreshAssetsAsync();
            StatusMessage = record.HasErrors
                ? $"Imported {Path.GetFileName(record.SourceFullPath)} with errors"
                : $"Imported {Path.GetFileName(record.SourceFullPath)}";
        }
        catch (Exception exception)
        {
            StatusMessage = "Asset import failed";
            AddConsole("Error", $"Could not import {Path.GetFileName(path)}: {exception.Message}");
        }
    }

    public void BeginProjectRename(ProjectItemViewModel item)
    {
        if (item.AssetId is null)
        {
            return;
        }

        item.EditName = item.Name;
        item.IsRenaming = true;
    }

    public void CancelProjectRename(ProjectItemViewModel item)
    {
        item.EditName = item.Name;
        item.IsRenaming = false;
    }

    public async Task CommitProjectRenameAsync(ProjectItemViewModel item)
    {
        if (!item.IsRenaming || item.AssetId is not { } assetId)
        {
            return;
        }

        item.IsRenaming = false;
        var newName = item.EditName.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(newName)
            || !string.Equals(Path.GetFileName(newName), newName, StringComparison.Ordinal)
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusMessage = "Asset rename failed";
            AddConsole("Error", "An asset name must be a valid file name without folder separators.");
            return;
        }

        var directory = Path.GetDirectoryName(item.RelativePath?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty);
        var relativePath = string.IsNullOrWhiteSpace(directory)
            ? newName
            : Path.Combine(directory, newName).Replace('\\', '/');
        await MoveAssetAsync(assetId, relativePath);
    }

    public async Task MoveAssetAsync(Guid assetId, string relativePath)
    {
        try
        {
            var moved = await _assetDatabase.MoveAsync(assetId, relativePath.Trim());
            await RefreshAssetsAsync();
            StatusMessage = $"Moved asset to {moved.Metadata.SourcePath}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            StatusMessage = "Asset move failed";
            AddConsole("Error", exception.Message);
        }
    }

    public Task CreateAssetFolderAsync(string relativePath)
    {
        try
        {
            var folder = _assetDatabase.CreateFolder(relativePath);
            RefreshProjectItems();
            StatusMessage = $"Created asset folder {Path.GetRelativePath(_assetDatabase.AssetsRoot, folder).Replace('\\', '/')}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusMessage = "Could not create asset folder";
            AddConsole("Error", exception.Message);
        }

        return Task.CompletedTask;
    }

    public async Task DeleteAssetAsync(Guid assetId)
    {
        var references = FindAssetReferences(assetId);
        if (references.Count > 0)
        {
            StatusMessage = "Asset is still referenced";
            AddConsole(
                "Warning",
                $"Delete blocked: asset is referenced by {string.Join(", ", references)}. Remove those references first.");
            return;
        }

        try
        {
            var trashed = await _assetDatabase.DeleteAsync(assetId);
            await RefreshAssetsAsync();
            StatusMessage = $"Moved {trashed.OriginalRelativePath} to project trash";
            AddConsole("Info", $"Recoverable asset delete: {trashed.TrashDirectory}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            StatusMessage = "Asset delete failed";
            AddConsole("Error", exception.Message);
        }
    }

    public async Task SaveSelectedAsTemplateAsync(string name)
    {
        if (SelectedEntity is not { } entity)
        {
            return;
        }

        try
        {
            var template = await _templateLibrary.SaveAsync(entity, name);
            RefreshProjectItems();
            StatusMessage = $"Saved scene template {template.Name}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusMessage = "Could not save scene template";
            AddConsole("Error", exception.Message);
        }
    }

    public void InstantiateTemplate(string path)
    {
        try
        {
            var entity = _templateLibrary.Instantiate(path);
            _session.Execute(new AddEntityCommand(null, entity));
            SelectOnly(entity.Id);
            StatusMessage = $"Instantiated template {entity.Name}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            StatusMessage = "Could not instantiate scene template";
            AddConsole("Error", exception.Message);
        }
    }

    public void CreateModelEntity(Guid assetId)
    {
        var asset = _assets.FirstOrDefault(record => record.Metadata.AssetId == assetId);
        if (asset is null)
        {
            StatusMessage = "The selected model asset is no longer available";
            return;
        }

        var entity = new SceneEntity
        {
            Name = Path.GetFileNameWithoutExtension(asset.SourceFullPath),
            Components =
            {
                Transform = new(
                    new Vector3Value(0, 0, -0.65),
                    QuaternionValue.Identity,
                    Vector3Value.One),
                ModelRenderer = new()
                {
                    AssetId = assetId,
                    Visible = true,
                    FitToBounds = true,
                    MaximumSize = 0.5,
                },
            },
        };
        _session.Execute(new AddRootEntityCommand(entity));
        SelectOnly(entity.Id);
        StatusMessage = $"Created model entity {entity.Name}";
    }

    public async Task TrustWorkspaceAsync()
    {
        await _workspaceTrust.TrustAsync(_project.ProjectId, _project.DefinitionPath);
        OnPropertyChanged(nameof(IsWorkspaceTrusted));
        DeployCommand.RaiseCanExecuteChanged();
        StatusMessage = "Workspace trusted";
    }

    public void DeclineWorkspaceTrust()
    {
        SceneHostStatus = "Scene host not started";
        StatusMessage = "Workspace not trusted; no project code was built or run";
        AddConsole("Warning", "Workspace trust was declined. Scene and Play remain disabled until the editor is reopened and trusted.");
    }

    private SceneEntity? SelectedEntity => _session.SelectedEntityId is { } id
        ? _session.Document.FindEntity(id)
        : null;

    private Vector3Value PositionValue =>
        SelectedEntity?.Components.Transform.Position ?? Vector3Value.Zero;

    private QuaternionValue RotationValue =>
        SelectedEntity?.Components.Transform.Rotation ?? QuaternionValue.Identity;

    private Vector3Value ScaleValue =>
        SelectedEntity?.Components.Transform.Scale ?? Vector3Value.One;

    private void AddPrimitive(PrimitiveKind kind)
    {
        var number = _session.Document.Traverse().Count() + 1;
        var entity = new SceneEntity
        {
            Name = $"{kind} {number}",
            Components =
            {
                Transform = new(
                    new Vector3Value((number - 2) * 0.25, 0, -0.65),
                    QuaternionValue.Identity,
                    Vector3Value.One),
                PrimitiveMeshRenderer = new()
                {
                    Primitive = kind,
                    Color = kind == PrimitiveKind.Cube
                        ? new ColorValue(0.20, 0.62, 0.92, 1)
                        : new ColorValue(0.62, 0.35, 0.92, 1),
                },
            },
        };

        _session.Execute(new AddRootEntityCommand(entity));
        SelectOnly(entity.Id);
        StatusMessage = $"Created {entity.Name}";
    }

    private void CreateChildEntity()
    {
        var parentId = SelectedEntity?.Id;
        var entity = new SceneEntity { Name = parentId is null ? "GameObject" : "Child GameObject" };
        _session.Execute(new AddEntityCommand(parentId, entity));
        SelectOnly(entity.Id);
        StatusMessage = parentId is null ? "Created GameObject" : "Created child GameObject";
    }

    private void DuplicateSelectedEntities()
    {
        _pendingDuplicateDrag = null;
        var ids = SelectedEntityIds;
        if (ids.Count == 0)
        {
            return;
        }

        var command = new DuplicateEntitiesCommand(ids);
        _session.Execute(command);
        _selectedEntityIds.Clear();
        foreach (var id in command.DuplicateIds)
        {
            _selectedEntityIds.Add(id);
        }

        _session.Select(command.DuplicateIds.FirstOrDefault() is { } first && first != Guid.Empty ? first : null);
        StatusMessage = $"Duplicated {command.DuplicateIds.Count} object{(command.DuplicateIds.Count == 1 ? string.Empty : "s")}";
    }

    private void BeginDuplicateDrag(IReadOnlyList<Guid>? entityIds)
    {
        if (entityIds is not { Count: > 0 })
        {
            return;
        }

        var command = new DuplicateEntitiesCommand(entityIds);
        _session.Execute(command);
        _selectedEntityIds.Clear();
        foreach (var id in command.DuplicateIds)
        {
            _selectedEntityIds.Add(id);
        }

        _pendingDuplicateDrag = new(command, DateTimeOffset.UtcNow);
        _session.Select(command.DuplicateIds.FirstOrDefault() is { } first && first != Guid.Empty ? first : null);
        StatusMessage = $"Duplicated {command.DuplicateIds.Count} object{(command.DuplicateIds.Count == 1 ? string.Empty : "s")} for drag";
    }

    private void DeleteSelectedEntities()
    {
        var ids = SelectedEntityIds;
        if (ids.Count == 0)
        {
            return;
        }

        _session.Execute(new DeleteEntitiesCommand(ids));
        _selectedEntityIds.Clear();
        var next = _session.Document.Roots.FirstOrDefault()?.Id;
        SelectOnly(next);

        StatusMessage = $"Deleted {ids.Count} selected object{(ids.Count == 1 ? string.Empty : "s")}";
    }

    private bool CanAddSelectedComponent() => SelectedEntity is not null && SelectedComponentToAdd is not null;

    private void AddSelectedComponent()
    {
        var entity = SelectedEntity;
        var option = SelectedComponentToAdd;
        if (entity is null || option is null)
        {
            return;
        }

        var descriptor = option.Descriptor;
        if (!descriptor.AllowMultiple && entity.Components.FindByType(descriptor.TypeId) is not null)
        {
            StatusMessage = $"{descriptor.DisplayName} is already on {entity.Name}";
            return;
        }

        var descriptors = _sceneCatalog!.Components.ToDictionary(
            candidate => candidate.TypeId,
            StringComparer.Ordinal);
        var components = new List<SceneComponentRecord>();
        var scheduled = new HashSet<string>(StringComparer.Ordinal);
        AddWithDependencies(descriptor);
        _session.Execute(new AddComponentsCommand(entity.Id, components));
        StatusMessage = components.Count == 1
            ? $"Added {descriptor.DisplayName}"
            : $"Added {descriptor.DisplayName} and {components.Count - 1} required component{(components.Count == 2 ? string.Empty : "s")}";
        return;

        void AddWithDependencies(EditorComponentDescriptor candidate)
        {
            if (entity.Components.FindByType(candidate.TypeId) is not null || !scheduled.Add(candidate.TypeId))
            {
                return;
            }

            foreach (var requiredTypeId in candidate.RequiredComponentTypeIds)
            {
                AddWithDependencies(descriptors[requiredTypeId]);
            }

            components.Add(new SceneComponentRecord
            {
                TypeId = candidate.TypeId,
                SchemaVersion = candidate.SchemaVersion,
                Data = candidate.DefaultData.Clone(),
            });
        }
    }

    private void Undo()
    {
        if (_session.Undo())
        {
            StatusMessage = "Undo";
        }
    }

    private void Redo()
    {
        if (_session.Redo())
        {
            StatusMessage = "Redo";
        }
    }

    private async Task SaveAsync()
    {
        await _session.SaveAsync();
        _recoveryStore.Clear();
        AddConsole("Info", $"Saved {_session.ScenePath}");
        StatusMessage = "Scene saved";
    }

    private async Task DeployAsync()
    {
        if (SelectedDeploymentProfile is not { } selected)
        {
            return;
        }

        StatusMessage = $"Publishing {selected.DisplayName}…";
        AddConsole("Deploy", $"[{selected.Profile.Id}] Android publish and ADB deployment started.");
        var result = await _androidDeployment.DeployAsync(
            selected.Profile,
            _project.ProjectDirectory,
            line => Dispatcher.UIThread.Post(() =>
                AddConsole(line.IsError ? "Error" : "Deploy", $"[Deploy] {line.Text}")));
        StatusMessage = $"Deployed {selected.DisplayName} in {result.Duration.TotalSeconds:0.0}s";
        AddConsole("Deploy", $"Installed and launched {result.ApkPath}");
    }

    private async Task ReloadAsync()
    {
        var result = await SceneSerializer.LoadWithMetadataAsync(_session.ScenePath);
        _session.Replace(result.Document, _session.ScenePath, result.MigratedFromFormat1);
        RefreshHierarchy();
        AddConsole("Info", "Reloaded scene from disk.");
        StatusMessage = "Scene reloaded";
    }

    private async Task StartSceneHostAsync()
    {
        SceneHostStatus = $"Building {Path.GetFileName(_sceneRuntimeProject.ProjectPath)}…";
        NotifyCommandStates();
        var runtimeAssemblyPath = await BuildRuntimeProjectAsync(_sceneRuntimeProject, force: true);
        _sceneBuild = runtimeAssemblyPath;
        SceneHostStatus = "Starting embedded Scene host…";
        await _sceneHost.StartAsync(
            runtimeAssemblyPath.TargetPath,
            _sceneRuntimeProject.WorkingDirectory,
            _session.Document,
            _session.Revision,
            _session.SelectedEntityId,
            CreateRuntimeIdentity(_sceneRuntimeProject, runtimeAssemblyPath),
            _sceneRuntimeProject.Arguments,
            _sceneRuntimeProject.Environment,
            _sceneCamera,
            _sceneToolSettings,
            runtimeAssets: CreateRuntimeAssets(),
            allowUntestedStereoKit: _allowUntestedStereoKit,
            selectedEntityIds: SelectedEntityIds);
        NotifyCommandStates();
    }

    private async Task StartPlayAsync()
    {
        PlayHostStatus = "Starting isolated Play session…";
        var runtimeAssemblyPath = await BuildRuntimeProjectAsync(_playRuntimeProject, force: false);
        var playSnapshot = SceneSerializer.Clone(_session.Document);
        await _playHost.StartAsync(
            runtimeAssemblyPath.TargetPath,
            _playRuntimeProject.WorkingDirectory,
            playSnapshot,
            _session.Revision,
            selectedEntityId: null,
            CreateRuntimeIdentity(_playRuntimeProject, runtimeAssemblyPath),
            _playRuntimeProject.Arguments,
            _playRuntimeProject.Environment,
            runtimeAssets: CreateRuntimeAssets(),
            allowUntestedStereoKit: _allowUntestedStereoKit);
        _playBuildId = runtimeAssemblyPath.BuildId;
        _playProfileId = _playRuntimeProject.ProfileId;
        _playGenerationDirectory = runtimeAssemblyPath.GenerationDirectory;
        _isPlayStale = false;
        OnPropertyChanged(nameof(IsPlayStale));
        ActiveViewport = RuntimeSessionMode.Play;
        NotifyCommandStates();
    }

    private async Task<DotnetBuildResult> BuildRuntimeProjectAsync(RuntimeProjectSpec project, bool force)
    {
        if (!IsWorkspaceTrusted)
        {
            throw new InvalidOperationException("Trust this workspace before building or running its project code.");
        }

        if (!force
            && _runtimeBuilds.TryGetValue(project.ProfileId, out var existingBuild)
            && File.Exists(existingBuild.TargetPath))
        {
            return existingBuild;
        }

        AddConsole("Info", $"[Build:{project.ProfileId}] dotnet build {project.ProjectPath}");
        var result = await _projectBuilder.BuildAsync(
            project,
            line => Dispatcher.UIThread.Post(() =>
                AddConsole(line.IsError ? "Error" : "Build", $"[Build] {line.Text}")));
        _runtimeBuilds[project.ProfileId] = result;
        var removedGenerations = _projectBuilder.PruneGenerations(
            project,
            new[]
            {
                result.GenerationDirectory,
                _sceneBuild?.GenerationDirectory,
                _playGenerationDirectory,
            }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!),
            retainedUnusedGenerations: 2);
        if (removedGenerations.Count > 0)
        {
            AddConsole("Info", $"[Build] Removed {removedGenerations.Count} old unused generation{(removedGenerations.Count == 1 ? string.Empty : "s")}.");
        }
        if (_playHost.IsRunning
            && _playBuildId is not null
            && string.Equals(_playProfileId, project.ProfileId, StringComparison.Ordinal)
            && !string.Equals(_playBuildId, result.BuildId, StringComparison.Ordinal))
        {
            _isPlayStale = true;
            PlayHostStatus = "Playing · stale code (restart Play to update)";
            OnPropertyChanged(nameof(IsPlayStale));
        }

        AddConsole(
            "Info",
            $"[Build] Ready in {result.Duration.TotalSeconds:0.0}s: {result.TargetPath} · build {result.BuildId}");
        return result;
    }

    private RuntimeLaunchIdentity CreateRuntimeIdentity(RuntimeProjectSpec project, DotnetBuildResult build) => new(
        _project.ProjectId,
        _project.Name,
        project.ProfileId,
        build.BuildId);

    private static string CreateBuildCommandSummary(RuntimeProjectSpec project)
    {
        var parts = new List<string>
        {
            "dotnet build",
            $"\"{project.ProjectPath}\"",
            "--configuration",
            project.Configuration,
        };
        if (!string.IsNullOrWhiteSpace(project.TargetFramework))
        {
            parts.Add("--framework");
            parts.Add(project.TargetFramework);
        }

        if (!string.IsNullOrWhiteSpace(project.RuntimeIdentifier))
        {
            parts.Add("--runtime");
            parts.Add(project.RuntimeIdentifier);
        }

        return string.Join(" ", parts);
    }

    private async Task StopPlayAsync()
    {
        await _playHost.StopAsync("Play stopped by user");
        PlayHostStatus = "Play stopped";
        _lastPlayRevision = -1;
        _playBuildId = null;
        _playProfileId = null;
        _playGenerationDirectory = null;
        _isPlayStale = false;
        RuntimeWindowChanged?.Invoke(this, new(RuntimeSessionMode.Play, 0));
        ActiveViewport = RuntimeSessionMode.Scene;
        OnPropertyChanged(nameof(PlayRenderRevision));
        OnPropertyChanged(nameof(IsPlayStale));
        NotifyCommandStates();
    }

    private async Task TogglePauseAsync()
    {
        var next = _playHost.PlayState == RuntimePlayState.Paused
            ? RuntimePlayState.Playing
            : RuntimePlayState.Paused;
        await _playHost.SetPlayStateAsync(next);
    }

    private async Task StepPlayAsync()
    {
        await _playHost.StepPlayAsync();
        StatusMessage = "Advanced Play by one frame";
    }

    private async Task FrameSelectionAsync()
    {
        var entity = SelectedEntity;
        if (entity is null)
        {
            return;
        }

        await _sceneHost.FrameSelectionAsync(entity.Id);
        ActiveViewport = RuntimeSessionMode.Scene;
        StatusMessage = $"Framed {entity.Name}";
    }

    private void ToggleGizmoSpace()
    {
        if (_sceneToolSettings.Tool == SceneTransformTool.Scale)
        {
            StatusMessage = "Scale uses local axes because the scene format stores TRS without shear";
            return;
        }

        _sceneToolSettings = _sceneToolSettings with
        {
            GizmoSpace = _sceneToolSettings.GizmoSpace == SceneGizmoSpace.Global
                ? SceneGizmoSpace.Local
                : SceneGizmoSpace.Global,
        };
        OnPropertyChanged(nameof(GizmoSpaceLabel));
        _ = PushSceneToolSettingsSafelyAsync();
    }

    private void SetTransformTool(SceneTransformTool tool)
    {
        if (_sceneToolSettings.Tool == tool)
        {
            // ToggleButton changes its visual state before invoking the command.
            // Re-publish the authoritative one-way value so the active tool
            // cannot be visually toggled off by clicking it again.
            NotifySceneToolSettings();
            return;
        }

        _sceneToolSettings = _sceneToolSettings with { Tool = tool };
        NotifySceneToolSettings();
        _ = PushSceneToolSettingsSafelyAsync();
    }

    private void ToggleActiveSnap()
    {
        _sceneToolSettings = _sceneToolSettings.Tool switch
        {
            SceneTransformTool.Move => _sceneToolSettings with
            {
                TranslationSnapEnabled = !_sceneToolSettings.TranslationSnapEnabled,
            },
            SceneTransformTool.Rotate => _sceneToolSettings with
            {
                RotationSnapEnabled = !_sceneToolSettings.RotationSnapEnabled,
            },
            _ => _sceneToolSettings with
            {
                ScaleSnapEnabled = !_sceneToolSettings.ScaleSnapEnabled,
            },
        };
        NotifySceneToolSettings();
        _ = PushSceneToolSettingsSafelyAsync();
    }

    private void ToggleProjection()
    {
        _sceneCamera = _sceneCamera with
        {
            Projection = _sceneCamera.Projection == SceneProjection.Perspective
                ? SceneProjection.Orthographic
                : SceneProjection.Perspective,
        };
        OnPropertyChanged(nameof(ProjectionLabel));
        StatusMessage = $"Scene projection: {ProjectionLabel}";
        _ = PushSceneCameraSafelyAsync();
    }

    private void ToggleGrid()
    {
        _sceneToolSettings = _sceneToolSettings with { ShowGrid = !_sceneToolSettings.ShowGrid };
        NotifySceneToolSettings();
        _ = PushSceneToolSettingsSafelyAsync();
    }

    private void TogglePivotMode()
    {
        _sceneToolSettings = _sceneToolSettings with
        {
            PivotMode = _sceneToolSettings.PivotMode == ScenePivotMode.Center
                ? ScenePivotMode.Active
                : ScenePivotMode.Center,
        };
        NotifySceneToolSettings();
        _ = PushSceneToolSettingsSafelyAsync();
    }

    private void SetSceneView(double yawDegrees, double pitchDegrees, string displayName)
    {
        _sceneCamera = _sceneCamera with
        {
            YawDegrees = yawDegrees,
            PitchDegrees = pitchDegrees,
        };
        StatusMessage = $"Scene view: {displayName}";
        _ = PushSceneCameraSafelyAsync();
    }

    private void SetPosition(Vector3Value value)
    {
        var entity = SelectedEntity;
        if (entity is null || entity.Components.Transform.Position == value)
        {
            return;
        }

        var current = entity.Components.Transform;
        _session.Execute(new SetTransformCommand(entity.Id, current, current with { Position = value }));
    }

    private void SetRotation(QuaternionValue value)
    {
        var entity = SelectedEntity;
        if (entity is null || entity.Components.Transform.Rotation == value)
        {
            return;
        }

        var current = entity.Components.Transform;
        _session.Execute(new SetTransformCommand(entity.Id, current, current with { Rotation = value }));
    }

    private void SetScale(Vector3Value value)
    {
        var entity = SelectedEntity;
        if (entity is null || entity.Components.Transform.Scale == value)
        {
            return;
        }

        var current = entity.Components.Transform;
        _session.Execute(new SetTransformCommand(entity.Id, current, current with { Scale = value }));
    }

    private void HandleSessionChanged(object? sender, SessionChangedEventArgs args)
    {
        if (args.Kind is SessionChangeKind.Document or SessionChangeKind.Reloaded or SessionChangeKind.Recovered)
        {
            if (args.Kind == SessionChangeKind.Reloaded)
            {
                ClearMigrationProposal();
            }

            RefreshHierarchy();
            _ = PushSceneSafelyAsync();
            if (args.Kind is SessionChangeKind.Document or SessionChangeKind.Recovered)
            {
                _recoveryStore.Schedule(_session.Document, _session.Revision);
            }
        }
        else if (args.Kind == SessionChangeKind.Selection)
        {
            SyncHierarchySelection();
            _ = SetSceneSelectionSafelyAsync();
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SceneStatus));
        NotifyInspector();
        NotifyCommandStates();
    }

    private void HandleRuntimeEvent(object? sender, RuntimeEventArgs args)
    {
        var runtime = (RuntimeSession)sender!;
        Dispatcher.UIThread.Post(() => ApplyRuntimeEvent(runtime, args));
    }

    private void HandleRecoveryWriteFailed(object? sender, Exception exception) =>
        Dispatcher.UIThread.Post(() =>
            AddConsole("Warning", $"Could not update the local scene recovery snapshot: {exception.Message}"));

    private void ApplyRuntimeEvent(RuntimeSession runtime, RuntimeEventArgs args)
    {
        var label = runtime.Mode == RuntimeSessionMode.Scene ? "Scene" : "Play";
        switch (args.Kind)
        {
            case RuntimeEventKind.Ready:
                ClearCompatibilityBlock();
                SetRuntimeStatus(runtime, args.Message);
                StatusMessage = $"{label} host ready";
                AddConsole("Info", args.Message);
                break;
            case RuntimeEventKind.CatalogReady:
                if (runtime.Mode == RuntimeSessionMode.Scene && args.Catalog is { } catalog)
                {
                    _sceneCatalog = catalog;
                    RefreshComponentInspector();
                    OnPropertyChanged(nameof(HasComponentCatalog));
                }

                AddConsole("Adapter", $"[{label}] {args.Message}");
                break;
            case RuntimeEventKind.WindowReady:
                RuntimeWindowChanged?.Invoke(this, new(runtime.Mode, args.WindowHandle));
                break;
            case RuntimeEventKind.RevisionApplied:
                if (runtime.Mode == RuntimeSessionMode.Scene)
                {
                    _lastSceneRevision = args.Revision ?? -1;
                    OnPropertyChanged(nameof(SceneRenderRevision));
                }
                else
                {
                    _lastPlayRevision = args.Revision ?? -1;
                    OnPropertyChanged(nameof(PlayRenderRevision));
                }

                break;
            case RuntimeEventKind.EntityPicked:
                if (runtime.Mode == RuntimeSessionMode.Scene)
                {
                    SelectOnly(args.EntityId);
                    SyncHierarchySelection();
                    StatusMessage = args.EntityId is null ? "Selection cleared" : "Selected from Scene view";
                }

                break;
            case RuntimeEventKind.TransformCommitted:
                CommitSceneTransform(args);
                break;
            case RuntimeEventKind.TransformsCommitted:
                CommitSceneTransforms(args.Transforms);
                break;
            case RuntimeEventKind.ComponentDataCommitted:
                CommitSceneComponentData(args);
                break;
            case RuntimeEventKind.DuplicateSelectionRequested:
                if (runtime.Mode == RuntimeSessionMode.Scene)
                {
                    BeginDuplicateDrag(args.EntityIds);
                }

                break;
            case RuntimeEventKind.SceneCameraChanged:
                if (args.Camera is { } camera)
                {
                    _sceneCamera = camera;
                    OnPropertyChanged(nameof(ProjectionLabel));
                }

                break;
            case RuntimeEventKind.SceneToolSettingsChanged:
                if (args.ToolSettings is { } settings)
                {
                    _sceneToolSettings = settings;
                    NotifySceneToolSettings();
                }

                break;
            case RuntimeEventKind.ComponentMigrationProposed:
                if (runtime.Mode == RuntimeSessionMode.Scene && args.MigrationProposal is { } proposal)
                {
                    _pendingMigrationProposal = proposal;
                    OnPropertyChanged(nameof(HasMigrationProposal));
                    OnPropertyChanged(nameof(MigrationProposalSummary));
                    ApplyMigrationProposalCommand.RaiseCanExecuteChanged();
                    AddConsole("Adapter", $"[Scene] {args.Message} Review and apply the proposal from the top bar.");
                }

                break;
            case RuntimeEventKind.PlayStateChanged:
                PlayHostStatus = _isPlayStale ? $"{args.Message} · stale code" : args.Message;
                OnPropertyChanged(nameof(IsPlayPaused));
                OnPropertyChanged(nameof(PauseLabel));
                break;
            case RuntimeEventKind.Log:
                AddConsole(args.Level ?? "Info", $"[{label}] {args.Message}");
                break;
            case RuntimeEventKind.Diagnostic:
                ApplyDiagnostic(label, args);
                break;
            case RuntimeEventKind.Telemetry:
                if (runtime.Mode == RuntimeSessionMode.Scene)
                {
                    _sceneTelemetry = args.Telemetry;
                }
                else
                {
                    _playTelemetry = args.Telemetry;
                }

                if (runtime.Mode == ActiveViewport)
                {
                    NotifyRuntimeTelemetry();
                }

                break;
            case RuntimeEventKind.Unresponsive:
                _unresponsiveRuntime = runtime;
                NotifyUnresponsiveRuntime();
                SetRuntimeStatus(runtime, $"{label} unresponsive");
                AddConsole("Warning", $"[{label}] {args.Message} Use Wait, Restart, or Stop from the top bar.");
                PersistDiagnosticBundle(runtime, "Unresponsive runtime", args);
                break;
            case RuntimeEventKind.Responsive:
                ClearUnresponsiveRuntime(runtime);
                SetRuntimeStatus(runtime, $"{label} connected");
                AddConsole("Info", $"[{label}] {args.Message}");
                break;
            case RuntimeEventKind.CompatibilityBlocked:
                _blockedStereoKitVersion = args.StereoKitVersion ?? "unknown";
                _blockedCompatibilityMode = runtime.Mode;
                OnPropertyChanged(nameof(HasCompatibilityBlock));
                OnPropertyChanged(nameof(CompatibilityBlockSummary));
                RunUntestedStereoKitCommand.RaiseCanExecuteChanged();
                AddConsole("Warning", $"[{label}] {args.Message} Running anyway is experimental and only affects this editor session.");
                break;
            case RuntimeEventKind.Error:
                SetRuntimeStatus(runtime, $"{label} error");
                AddConsole("Error", $"[{label}] {args.Message}");
                break;
            case RuntimeEventKind.Stopped:
                ClearUnresponsiveRuntime(runtime);
                SetRuntimeStatus(runtime, runtime.Mode == RuntimeSessionMode.Scene ? "Scene host disconnected" : "Play stopped");
                RuntimeWindowChanged?.Invoke(this, new(runtime.Mode, 0));
                AddConsole("Info", args.Message);
                if (runtime.Mode == RuntimeSessionMode.Play)
                {
                    ActiveViewport = RuntimeSessionMode.Scene;
                    _playBuildId = null;
                    _playProfileId = null;
                    _playGenerationDirectory = null;
                    _isPlayStale = false;
                    OnPropertyChanged(nameof(IsPlayStale));
                }
                else if (args.Unexpected)
                {
                    PersistDiagnosticBundle(runtime, "Unexpected runtime exit", args);
                    TryScheduleSceneRecovery(args.BuildId);
                }

                break;
            case RuntimeEventKind.Status:
                SetRuntimeStatus(runtime, args.Message);
                break;
        }

        OnPropertyChanged(nameof(IsSceneHostRunning));
        OnPropertyChanged(nameof(IsPlayRunning));
        OnPropertyChanged(nameof(IsPlayPaused));
        OnPropertyChanged(nameof(PauseLabel));
        NotifyCommandStates();
    }

    private void CommitSceneTransform(RuntimeEventArgs args)
    {
        if (args.EntityId is not { } entityId || args.Transform is null)
        {
            return;
        }

        CommitSceneTransforms([new EntityTransformValue(entityId, args.Transform)]);
    }

    private void CommitSceneTransforms(IReadOnlyList<EntityTransformValue>? transforms)
    {
        if (transforms is not { Count: > 0 })
        {
            return;
        }

        var changes = new List<EntityTransformChange>();
        foreach (var value in transforms.DistinctBy(value => value.EntityId))
        {
            var entity = _session.Document.FindEntity(value.EntityId);
            if (entity is not null && entity.Components.Transform != value.Transform)
            {
                changes.Add(new(value.EntityId, entity.Components.Transform, value.Transform));
            }
        }

        if (changes.Count == 0)
        {
            return;
        }

        var transformCommand = new SetTransformsCommand(changes);
        _session.Execute(transformCommand);
        if (_pendingDuplicateDrag is { } pending
            && DateTimeOffset.UtcNow - pending.StartedAt < TimeSpan.FromSeconds(15)
            && changes.All(change => pending.Command.DuplicateIds.Contains(change.EntityId)))
        {
            _session.History.CombineLastExecuted(
                2,
                new CompositeSceneCommand(
                    "Duplicate and Transform",
                    [pending.Command, transformCommand]));
            _pendingDuplicateDrag = null;
        }
        else
        {
            _pendingDuplicateDrag = null;
        }

        StatusMessage = $"Changed {changes.Count} object transform{(changes.Count == 1 ? string.Empty : "s")} as one undo step";
    }

    private void CommitSceneComponentData(RuntimeEventArgs args)
    {
        if (args.EntityId is not { } entityId
            || args.ComponentId is not { } componentId
            || args.ComponentData is not { } data
            || _session.Document.FindEntity(entityId)?.Components.Find(componentId) is not { } component)
        {
            return;
        }

        if (string.Equals(component.Data.GetRawText(), data.GetRawText(), StringComparison.Ordinal))
        {
            return;
        }

        _session.Execute(new SetComponentDataCommand(
            entityId,
            componentId,
            component.Data,
            data,
            string.IsNullOrWhiteSpace(args.Message) ? "UI Layout" : args.Message));
        StatusMessage = args.Message;
    }

    private void SetRuntimeStatus(RuntimeSession runtime, string status)
    {
        if (runtime.Mode == RuntimeSessionMode.Scene)
        {
            SceneHostStatus = status;
        }
        else
        {
            PlayHostStatus = _isPlayStale ? $"{status} · stale code" : status;
        }
    }

    private void ApplyDiagnostic(string label, RuntimeEventArgs args)
    {
        var diagnostic = args.Diagnostic;
        if (diagnostic is null)
        {
            AddConsole(args.Level ?? "Warning", $"[{label}] {args.Message}");
            return;
        }

        var target = diagnostic.EntityId is { } entityId
            ? $" · entity {entityId.ToString()[..8]}"
            : string.Empty;
        var component = diagnostic.ComponentTypeId is { Length: > 0 } typeId
            ? $" · {typeId}"
            : string.Empty;
        AddConsole(
            diagnostic.Severity.ToString(),
            $"[{label}] {diagnostic.Code}: {diagnostic.Message}{target}{component}");
        if (!string.IsNullOrWhiteSpace(diagnostic.SuggestedAction))
        {
            AddConsole("Help", $"[{label}] {diagnostic.SuggestedAction}");
        }
    }

    private void TryScheduleSceneRecovery(string? buildId)
    {
        if (_sceneRecoveryInProgress || string.IsNullOrWhiteSpace(buildId))
        {
            return;
        }

        if (!_sceneCrashRecovery.ShouldRestart(buildId, DateTimeOffset.UtcNow))
        {
            SceneHostStatus = "Scene crashed twice · automatic restart stopped";
            AddConsole(
                "Error",
                "[Scene] Crash-loop protection stopped automatic recovery. Use Rebuild Scene after inspecting the runtime log.");
            return;
        }

        _sceneRecoveryInProgress = true;
        _ = RunSafelyAsync(async () =>
        {
            try
            {
                await RecoverSceneHostAsync(buildId);
            }
            finally
            {
                _sceneRecoveryInProgress = false;
            }
        });
    }

    private async Task RecoverSceneHostAsync(string buildId)
    {
        var build = _sceneBuild;
        if (build is null
            || !string.Equals(build.BuildId, buildId, StringComparison.Ordinal)
            || !File.Exists(build.TargetPath))
        {
            throw new InvalidOperationException("The crashed Scene build generation is no longer available.");
        }

        SceneHostStatus = "Scene crashed · restoring unsaved scene…";
        AddConsole("Warning", $"[Scene] Restarting build {buildId} once from editor-owned state.");
        await Task.Delay(250);
        await _sceneHost.StartAsync(
            build.TargetPath,
            _sceneRuntimeProject.WorkingDirectory,
            _session.Document,
            _session.Revision,
            _session.SelectedEntityId,
            CreateRuntimeIdentity(_sceneRuntimeProject, build),
            _sceneRuntimeProject.Arguments,
            _sceneRuntimeProject.Environment,
            _sceneCamera,
            _sceneToolSettings,
            runtimeAssets: CreateRuntimeAssets(),
            allowUntestedStereoKit: _allowUntestedStereoKit,
            selectedEntityIds: SelectedEntityIds);
    }

    private void RefreshHierarchy()
    {
        var selectedId = _session.SelectedEntityId;
        _refreshingHierarchy = true;
        try
        {
            Entities.Clear();
            foreach (var entity in _session.Document.Roots)
            {
                AddHierarchyItem(entity, 0);
            }

            _selectedItem = selectedId is { } id ? Entities.FirstOrDefault(item => item.Id == id) : null;
            OnPropertyChanged(nameof(SelectedItem));
        }
        finally
        {
            _refreshingHierarchy = false;
        }
    }

    private void SyncHierarchySelection()
    {
        var selectedId = _session.SelectedEntityId;
        _selectedEntityIds.Clear();
        if (selectedId is { } selected)
        {
            _selectedEntityIds.Add(selected);
        }

        _selectedItem = selectedId is { } id ? Entities.FirstOrDefault(item => item.Id == id) : null;
        foreach (var item in Entities)
        {
            item.IsSelected = selectedId == item.Id;
        }

        OnPropertyChanged(nameof(SelectedItem));
        NotifyInspector();
        NotifyHierarchyCommands();
    }

    private void SelectOnly(Guid? entityId)
    {
        _selectedEntityIds.Clear();
        if (entityId is { } id)
        {
            _selectedEntityIds.Add(id);
        }

        _session.Select(entityId);
    }

    private void AddHierarchyItem(SceneEntity entity, int depth)
    {
        Entities.Add(new(
            entity.Id,
            entity.Name,
            entity.Enabled,
            depth,
            _selectedEntityIds.Contains(entity.Id)));
        foreach (var child in entity.Children)
        {
            AddHierarchyItem(child, depth + 1);
        }
    }

    private async Task PushSceneSafelyAsync()
    {
        if (!_sceneHost.IsRunning)
        {
            return;
        }

        try
        {
            await _sceneHost.PushSceneAsync(_session.Document, _session.Revision);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            AddConsole("Warning", $"Scene update was not delivered: {exception.Message}");
        }
    }

    private async Task RefreshAssetsAsync(bool pushToRuntime = true)
    {
        _assets = await _assetDatabase.RefreshAsync();
        RefreshProjectItems();
        RefreshComponentInspector();
        foreach (var asset in _assets)
        {
            foreach (var diagnostic in asset.Metadata.Diagnostics)
            {
                AddConsole(
                    diagnostic.Severity.ToString(),
                    $"[Asset:{asset.Metadata.SourcePath}] {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        if (pushToRuntime)
        {
            var runtimeAssets = CreateRuntimeAssets();
            if (_sceneHost.IsReady)
            {
                await _sceneHost.PushAssetCatalogAsync(runtimeAssets);
                await _sceneHost.PushSceneAsync(_session.Document, _session.Revision);
            }

            if (_playHost.IsReady)
            {
                await _playHost.PushAssetCatalogAsync(runtimeAssets);
            }
        }

        StatusMessage = $"Assets refreshed · {_assets.Count} asset{(_assets.Count == 1 ? string.Empty : "s")}";
    }

    private IReadOnlyList<RuntimeAssetDescriptor> CreateRuntimeAssets() => _assets
        .Select(asset => new RuntimeAssetDescriptor(
            asset.Metadata.AssetId,
            asset.Metadata.Kind.ToString(),
            asset.SourceFullPath,
            asset.Metadata.ContentHash,
            asset.Metadata.Bounds is { } bounds
                ? new RuntimeAssetBounds(
                    bounds.CenterX,
                    bounds.CenterY,
                    bounds.CenterZ,
                    bounds.SizeX,
                    bounds.SizeY,
                    bounds.SizeZ)
                : null,
            asset.Metadata.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray(),
            JsonSerializer.SerializeToElement(new
            {
                importerSettings = asset.Metadata.ImporterSettings,
                model = asset.Metadata.Model,
                texture = asset.Metadata.Texture,
                font = asset.Metadata.Font,
                material = asset.Metadata.Material,
                textStyle = asset.Metadata.TextStyle,
            }, SceneSerializer.Options),
            asset.Metadata.AssetDependencies))
        .ToArray();

    private void RefreshProjectItems()
    {
        foreach (var item in _allProjectItems)
        {
            item.Thumbnail?.Dispose();
        }

        _allProjectItems.Clear();
        _allProjectItems.Add(new("Scenes", Path.GetFileName(_session.ScenePath), "Scene"));
        foreach (var template in _templateLibrary.Discover())
        {
            _allProjectItems.Add(new(
                "Templates",
                template.Name,
                "Scene Template",
                details: "Reusable object hierarchy · double-click to instantiate",
                templatePath: template.Path));
        }
        foreach (var folder in Directory.EnumerateDirectories(_assetDatabase.AssetsRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(_assetDatabase.AssetsRoot, folder).Replace('\\', '/');
            var parent = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
            _allProjectItems.Add(new(
                string.IsNullOrWhiteSpace(parent) ? "Assets" : $"Assets / {parent}",
                Path.GetFileName(folder),
                "Folder",
                relativePath: relativePath,
                isFolder: true));
        }

        foreach (var asset in _assets.OrderBy(asset => asset.Metadata.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var bounds = asset.Metadata.Bounds;
            var details = asset.Metadata.Kind switch
            {
                AssetKind.Texture when asset.Metadata.Texture is { } texture =>
                    $"{texture.Width} × {texture.Height} · {asset.Metadata.ImporterSettings.ColorSpace} · {asset.Metadata.ImporterSettings.TextureUsage}",
                AssetKind.Font when asset.Metadata.Font is { } font =>
                    string.IsNullOrWhiteSpace(font.FamilyName) ? font.SourceFormat : $"{font.FamilyName} · {font.SourceFormat}",
                AssetKind.Material when asset.Metadata.Material is { } material =>
                    $"{material.ShaderFamily} · {material.Transparency} · {asset.Metadata.AssetDependencies.Count} texture reference(s)",
                AssetKind.TextStyle when asset.Metadata.TextStyle is { } style =>
                    $"{style.CharacterHeight:0.###} m · {asset.Metadata.AssetDependencies.Count} dependency reference(s)",
                _ when bounds is not null => $"{bounds.SizeX:0.###} × {bounds.SizeY:0.###} × {bounds.SizeZ:0.###} m",
                _ => "Metadata unavailable",
            };
            var relativeDirectory = Path.GetDirectoryName(asset.Metadata.SourcePath)?.Replace('\\', '/');
            _allProjectItems.Add(new(
                string.IsNullOrWhiteSpace(relativeDirectory) ? "Assets" : $"Assets / {relativeDirectory}",
                Path.GetFileName(asset.Metadata.SourcePath),
                asset.HasErrors ? $"{asset.Metadata.Kind} · Error" : AssetKindLabel(asset.Metadata.Kind),
                asset.Metadata.AssetId,
                TryLoadThumbnail(asset.ThumbnailFullPath),
                details,
                asset.HasErrors,
                asset.Metadata.SourcePath));
        }

        if (_assets.Count == 0 && !Directory.EnumerateDirectories(_assetDatabase.AssetsRoot).Any())
        {
            _allProjectItems.Add(new("Assets", "Import an image, font, or GLB to begin", "Empty Folder"));
        }

        _allProjectItems.Add(new("Runtime", Path.GetFileName(_sceneRuntimeProject.ProjectPath), $"Scene · {_sceneRuntimeProject.ProfileId}"));
        if (!string.Equals(_sceneRuntimeProject.ProfileId, _playRuntimeProject.ProfileId, StringComparison.Ordinal))
        {
            _allProjectItems.Add(new("Runtime", Path.GetFileName(_playRuntimeProject.ProjectPath), $"Play · {_playRuntimeProject.ProfileId}"));
        }

        _allProjectItems.Add(new("Project", Path.GetFileName(_project.DefinitionPath), "Project"));
        ApplyProjectFilter();
    }

    private void ApplyProjectFilter()
    {
        var query = ProjectSearchText.Trim();
        ProjectFiles.Clear();
        foreach (var item in _allProjectItems.Where(item =>
                     query.Length == 0
                     || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (item.Details?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            ProjectFiles.Add(item);
        }
    }

    private IReadOnlyList<string> FindAssetReferences(Guid assetId)
    {
        var direct = SceneAssetReferences.Find(_session.Document, assetId).Select(entity => entity.Name);
        var authored = _assetDatabase.FindDependents(assetId, transitive: true)
            .Select(record => $"asset {record.Metadata.SourcePath}");
        return direct.Concat(authored).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string AssetKindLabel(AssetKind kind) => kind switch
    {
        AssetKind.Model => "GLB Model",
        AssetKind.Texture => "Texture",
        AssetKind.Font => "Font",
        AssetKind.Material => "Material",
        AssetKind.TextStyle => "Text Style",
        _ => kind.ToString(),
    };

    private static Bitmap? TryLoadThumbnail(string path)
    {
        try
        {
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            return null;
        }
    }

    private async Task SetSceneSelectionSafelyAsync()
    {
        if (!_sceneHost.IsRunning)
        {
            return;
        }

        try
        {
            await _sceneHost.SetSelectionsAsync(_session.SelectedEntityId, SelectedEntityIds);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            AddConsole("Warning", $"Scene selection was not delivered: {exception.Message}");
        }
    }

    private async Task PushSceneToolSettingsSafelyAsync()
    {
        if (!_sceneHost.IsRunning)
        {
            return;
        }

        try
        {
            await _sceneHost.SetSceneToolSettingsAsync(_sceneToolSettings);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            AddConsole("Warning", $"Scene tool settings were not delivered: {exception.Message}");
        }
    }

    private async Task PushSceneCameraSafelyAsync()
    {
        if (!_sceneHost.IsRunning)
        {
            return;
        }

        try
        {
            await _sceneHost.SetSceneCameraAsync(_sceneCamera);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            AddConsole("Warning", $"Scene camera settings were not delivered: {exception.Message}");
        }
    }

    private void NotifySceneToolSettings()
    {
        OnPropertyChanged(nameof(GizmoSpaceLabel));
        OnPropertyChanged(nameof(IsMoveTool));
        OnPropertyChanged(nameof(IsRotateTool));
        OnPropertyChanged(nameof(IsScaleTool));
        OnPropertyChanged(nameof(ActiveSnapLabel));
        OnPropertyChanged(nameof(ActiveSnapUnits));
        OnPropertyChanged(nameof(ActiveSnapAmount));
        OnPropertyChanged(nameof(GridLabel));
        OnPropertyChanged(nameof(PivotModeLabel));
        NotifyPhase5ToolSettings();
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            AddConsole("Error", exception.Message);
            StatusMessage = "Operation failed";
        }
        finally
        {
            NotifyCommandStates();
        }
    }

    private void NotifyInspector()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedEnabled));
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(PositionX));
        OnPropertyChanged(nameof(PositionY));
        OnPropertyChanged(nameof(PositionZ));
        OnPropertyChanged(nameof(RotationX));
        OnPropertyChanged(nameof(RotationY));
        OnPropertyChanged(nameof(RotationZ));
        OnPropertyChanged(nameof(RotationW));
        OnPropertyChanged(nameof(ScaleX));
        OnPropertyChanged(nameof(ScaleY));
        OnPropertyChanged(nameof(ScaleZ));
        RefreshComponentInspector();
    }

    private sealed record PendingDuplicateDrag(
        DuplicateEntitiesCommand Command,
        DateTimeOffset StartedAt);

    private void RefreshComponentInspector()
    {
        InspectorComponents.Clear();
        AvailableComponents.Clear();
        RefreshModelMaterialSlots();

        var entity = SelectedEntity;
        if (entity is null)
        {
            SelectedComponentToAdd = null;
            NotifyComponentUi();
            return;
        }

        var descriptors = _sceneCatalog?.Components.ToDictionary(
            descriptor => descriptor.TypeId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, EditorComponentDescriptor>(StringComparer.Ordinal);

        foreach (var component in entity.Components.Records.Where(component =>
                     component.TypeId is not BuiltInComponentTypes.Transform))
        {
            descriptors.TryGetValue(component.TypeId, out var descriptor);
            var schemaMatches = descriptor is not null && component.SchemaVersion == descriptor.SchemaVersion;
            var fields = !schemaMatches
                ? []
                : CreatePropertyInspectors(entity.Id, component, descriptor!).ToArray();
            var unavailableDescription = descriptor is null
                ? $"The adapter did not register '{component.TypeId}'. Its saved data is being preserved."
                : component.SchemaVersion > descriptor.SchemaVersion
                    ? $"Saved schema {component.SchemaVersion} is newer than adapter schema {descriptor.SchemaVersion}. The data is preserved read-only."
                    : $"Saved schema {component.SchemaVersion} requires an upgrade to schema {descriptor.SchemaVersion} before editing.";

            InspectorComponents.Add(new ComponentInspectorViewModel(
                component.Id,
                component.TypeId,
                descriptor?.DisplayName ?? "Missing Component",
                schemaMatches ? descriptor!.Description : unavailableDescription,
                component.Enabled,
                !schemaMatches,
                fields,
                enabled => SetComponentEnabled(entity.Id, component.Id, enabled),
                () => RemoveComponent(entity.Id, component.Id)));
        }

        foreach (var descriptor in descriptors.Values
                     .Where(descriptor => descriptor.Modes.HasFlag(EditorComponentModes.Scene))
                      .Where(descriptor => descriptor.AllowMultiple
                          || entity.Components.FindByType(descriptor.TypeId) is null)
                      .Where(descriptor => CanAddWithDependencies(entity, descriptor, descriptors))
                     .OrderBy(descriptor => descriptor.Category, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            AvailableComponents.Add(new ComponentOptionViewModel(descriptor));
        }

        if (SelectedComponentToAdd is null
            || AvailableComponents.All(option => option.Descriptor.TypeId != SelectedComponentToAdd.Descriptor.TypeId))
        {
            SelectedComponentToAdd = AvailableComponents.FirstOrDefault();
        }

        NotifyComponentUi();
    }

    private IEnumerable<ComponentPropertyViewModel> CreatePropertyInspectors(
        Guid entityId,
        SceneComponentRecord component,
        EditorComponentDescriptor descriptor)
    {
        foreach (var property in descriptor.Properties)
        {
            var effectiveProperty = CreateInteractionProperty(component.TypeId, property);
            var value = TryReadProperty(component.Data, property.Name)
                ?? TryReadProperty(descriptor.DefaultData, property.Name)
                ?? default;

            yield return new ComponentPropertyViewModel(
                effectiveProperty,
                value,
                node => SetComponentProperty(entityId, component.Id, effectiveProperty, node),
                CreateReferenceOptions(effectiveProperty));
        }
    }

    private EditorPropertyDescriptor CreateInteractionProperty(string componentTypeId, EditorPropertyDescriptor property)
    {
        if (_sceneCatalog is null)
        {
            return property;
        }

        if (string.Equals(property.Name, "actionId", StringComparison.Ordinal))
        {
            var options = _sceneCatalog.Actions.Select(action => action.Id).Order(StringComparer.Ordinal).ToArray();
            return options.Length == 0 ? property : property with { Kind = EditorPropertyKind.Enum, Options = options };
        }

        if (!string.Equals(property.Name, "bindingId", StringComparison.Ordinal))
        {
            return property;
        }

        var requiredKind = componentTypeId switch
        {
            BuiltInComponentTypes.UiToggle => EditorBindingValueKind.Boolean,
            BuiltInComponentTypes.UiSlider => EditorBindingValueKind.Number,
            BuiltInComponentTypes.UiTextInput => EditorBindingValueKind.String,
            _ => (EditorBindingValueKind?)null,
        };
        var bindings = _sceneCatalog.Bindings
            .Where(binding => requiredKind is null || binding.Kind == requiredKind)
            .Select(binding => binding.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return bindings.Length == 0 ? property : property with { Kind = EditorPropertyKind.Enum, Options = bindings };
    }

    private IReadOnlyList<ReferenceOptionViewModel> CreateReferenceOptions(EditorPropertyDescriptor property) => property.Kind switch
    {
        EditorPropertyKind.AssetReference => _allProjectItems
            .Where(item => item.AssetId is not null)
            .Where(item => property.AcceptedAssetKinds.Count == 0
                || property.AcceptedAssetKinds.Any(kind => AssetItemMatchesKind(item, kind)))
            .Select(item => new ReferenceOptionViewModel(
                item.AssetId!.Value,
                Path.GetFileName(item.Name),
                $"{item.Name} · {item.Kind}",
                item.Thumbnail))
            .Prepend(property.IsRequired
                ? null!
                : new ReferenceOptionViewModel(Guid.Empty, "None", "Clear this optional asset reference"))
            .Where(option => option is not null)
            .ToArray(),
        EditorPropertyKind.EntityReference => _session.Document.Traverse()
            .Select(entity => new ReferenceOptionViewModel(
                entity.Id,
                entity.Name,
                entity.Id.ToString("D")))
            .ToArray(),
        _ => [],
    };

    private bool AssetItemMatchesKind(ProjectItemViewModel item, string kind)
    {
        var record = item.AssetId is { } id ? _assets.FirstOrDefault(asset => asset.Metadata.AssetId == id) : null;
        return record is not null
            && string.Equals(record.Metadata.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase);
    }

    private void SetComponentEnabled(Guid entityId, Guid componentId, bool enabled)
    {
        var component = _session.Document.FindEntity(entityId)?.Components.Find(componentId);
        if (component is null || component.Enabled == enabled)
        {
            return;
        }

        _session.Execute(new SetComponentEnabledCommand(entityId, componentId, component.Enabled, enabled));
    }

    private void RemoveComponent(Guid entityId, Guid componentId)
    {
        var component = _session.Document.FindEntity(entityId)?.Components.Find(componentId);
        if (component is null)
        {
            return;
        }

        var dependent = _sceneCatalog?.Components.FirstOrDefault(descriptor =>
            descriptor.RequiredComponentTypeIds.Contains(component.TypeId, StringComparer.Ordinal)
            && _session.Document.FindEntity(entityId)?.Components.FindByType(descriptor.TypeId) is not null);
        if (dependent is not null)
        {
            StatusMessage = $"Cannot remove {component.TypeId}; {dependent.DisplayName} requires it.";
            return;
        }

        _session.Execute(new RemoveComponentCommand(entityId, component));
        StatusMessage = $"Removed {component.TypeId}";
    }

    private void SetComponentProperty(
        Guid entityId,
        Guid componentId,
        EditorPropertyDescriptor descriptor,
        JsonNode? value)
    {
        var component = _session.Document.FindEntity(entityId)?.Components.Find(componentId);
        if (component is null || descriptor.IsReadOnly)
        {
            return;
        }

        var data = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())?.AsObject() ?? new JsonObject()
            : new JsonObject();
        data[descriptor.Name] = value?.DeepClone();
        var updated = JsonSerializer.SerializeToElement(data, SceneSerializer.Options);
        if (string.Equals(component.Data.GetRawText(), updated.GetRawText(), StringComparison.Ordinal))
        {
            return;
        }

        _session.Execute(new SetComponentDataCommand(
            entityId,
            componentId,
            component.Data,
            updated,
            descriptor.DisplayName));
    }

    private bool CanApplyMigrationProposal()
    {
        if (_pendingMigrationProposal is not { Upgrades.Count: > 0 } proposal
            || proposal.DocumentRevision != _session.Revision)
        {
            return false;
        }

        return proposal.Upgrades.All(upgrade =>
            _session.Document.FindEntity(upgrade.EntityId)?.Components.Find(upgrade.ComponentId) is { } component
            && component.TypeId == upgrade.ComponentTypeId
            && component.SchemaVersion == upgrade.FromSchemaVersion);
    }

    private void ApplyMigrationProposal()
    {
        if (!CanApplyMigrationProposal() || _pendingMigrationProposal is not { } proposal)
        {
            StatusMessage = "The migration proposal is stale; waiting for the runtime to refresh it.";
            return;
        }

        var upgrades = proposal.Upgrades.Select(upgrade =>
        {
            var component = _session.Document.FindEntity(upgrade.EntityId)!.Components.Find(upgrade.ComponentId)!;
            return new ComponentSchemaUpgrade(
                upgrade.EntityId,
                upgrade.ComponentId,
                component.SchemaVersion,
                component.Data,
                upgrade.ToSchemaVersion,
                upgrade.MigratedData);
        }).ToArray();
        _session.Execute(new UpgradeComponentSchemasCommand(upgrades));
        StatusMessage = $"Applied {upgrades.Length} component schema upgrade{(upgrades.Length == 1 ? string.Empty : "s")}; save when ready.";
        ClearMigrationProposal();
    }

    private void ClearMigrationProposal()
    {
        if (_pendingMigrationProposal is null)
        {
            return;
        }

        _pendingMigrationProposal = null;
        OnPropertyChanged(nameof(HasMigrationProposal));
        OnPropertyChanged(nameof(MigrationProposalSummary));
        ApplyMigrationProposalCommand.RaiseCanExecuteChanged();
    }

    private async Task RunUntestedStereoKitAsync()
    {
        var mode = _blockedCompatibilityMode;
        _allowUntestedStereoKit = true;
        ClearCompatibilityBlock();
        if (mode == RuntimeSessionMode.Scene)
        {
            await StartSceneHostAsync();
        }
        else
        {
            await StartPlayAsync();
        }
    }

    private void ClearCompatibilityBlock()
    {
        if (_blockedStereoKitVersion is null)
        {
            return;
        }

        _blockedStereoKitVersion = null;
        OnPropertyChanged(nameof(HasCompatibilityBlock));
        OnPropertyChanged(nameof(CompatibilityBlockSummary));
        RunUntestedStereoKitCommand.RaiseCanExecuteChanged();
    }

    private void WaitForRuntime()
    {
        var runtime = _unresponsiveRuntime;
        if (runtime is null)
        {
            return;
        }

        runtime.WaitForResponse();
        ClearUnresponsiveRuntime(runtime);
        StatusMessage = $"Waiting for {(runtime.Mode == RuntimeSessionMode.Scene ? "Scene" : "Game")}…";
    }

    private async Task RestartUnresponsiveRuntimeAsync()
    {
        var runtime = _unresponsiveRuntime;
        if (runtime is null)
        {
            return;
        }

        var mode = runtime.Mode;
        ClearUnresponsiveRuntime(runtime);
        await runtime.StopAsync("Restarting unresponsive runtime");
        if (mode == RuntimeSessionMode.Scene && _sceneBuild is { } sceneBuild)
        {
            await RecoverSceneHostAsync(sceneBuild.BuildId);
        }
        else if (mode == RuntimeSessionMode.Play)
        {
            await StartPlayAsync();
        }
    }

    private async Task StopUnresponsiveRuntimeAsync()
    {
        var runtime = _unresponsiveRuntime;
        if (runtime is null)
        {
            return;
        }

        ClearUnresponsiveRuntime(runtime);
        await runtime.StopAsync("Stopped after becoming unresponsive");
        if (runtime.Mode == RuntimeSessionMode.Play)
        {
            ActiveViewport = RuntimeSessionMode.Scene;
        }
    }

    private void ClearUnresponsiveRuntime(RuntimeSession runtime)
    {
        if (!ReferenceEquals(_unresponsiveRuntime, runtime))
        {
            return;
        }

        _unresponsiveRuntime = null;
        NotifyUnresponsiveRuntime();
    }

    private void NotifyUnresponsiveRuntime()
    {
        OnPropertyChanged(nameof(HasUnresponsiveRuntime));
        OnPropertyChanged(nameof(UnresponsiveRuntimeSummary));
        WaitForRuntimeCommand.RaiseCanExecuteChanged();
        RestartUnresponsiveRuntimeCommand.RaiseCanExecuteChanged();
        StopUnresponsiveRuntimeCommand.RaiseCanExecuteChanged();
    }

    private void PersistDiagnosticBundle(RuntimeSession runtime, string reason, RuntimeEventArgs args)
    {
        var profile = runtime.Mode == RuntimeSessionMode.Scene ? _sceneRuntimeProject : _playRuntimeProject;
        var input = new DiagnosticBundleInput(
            _project.ProjectId,
            _project.Name,
            _project.DefinitionPath,
            runtime.Mode.ToString(),
            reason,
            profile.ProfileId,
            args.BuildId ?? runtime.BuildId,
            args.ExitCode,
            SceneSerializer.Serialize(_session.Document),
            ConsoleEntries.Select(entry => $"{entry.Timestamp:O} [{entry.Level}] {entry.Message}").ToArray(),
            profile.Environment.Keys.ToArray(),
            DateTimeOffset.UtcNow);
        _ = Task.Run(async () =>
        {
            try
            {
                var path = await _diagnosticBundleWriter.WriteAsync(input);
                Dispatcher.UIThread.Post(() => AddConsole("Info", $"[{runtime.Mode}] Diagnostic bundle: {path}"));
            }
            catch (Exception exception)
            {
                Dispatcher.UIThread.Post(() => AddConsole("Warning", $"[{runtime.Mode}] Could not write diagnostic bundle: {exception.Message}"));
            }
        });
    }

    private void HandleSourceFilesChanged(object? sender, IReadOnlyList<string> paths)
    {
        if (!AutoRebuildEnabled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = RunSafelyAsync(async () =>
        {
            if (_sourceRefreshInProgress)
            {
                return;
            }

            _sourceRefreshInProgress = true;
            try
            {
                AddConsole("Info", $"[Watcher] {paths.Count} source file change{(paths.Count == 1 ? string.Empty : "s")} detected; rebuilding Scene.");
                await StartSceneHostAsync();
            }
            finally
            {
                _sourceRefreshInProgress = false;
            }
        }));
    }

    private void HandleAssetFilesChanged(object? sender, IReadOnlyList<string> paths)
    {
        if (!AutoRefreshAssetsEnabled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = RunSafelyAsync(async () =>
        {
            if (_assetRefreshInProgress)
            {
                return;
            }

            _assetRefreshInProgress = true;
            try
            {
                AddConsole("Info", $"[Watcher] {paths.Count} asset file change{(paths.Count == 1 ? string.Empty : "s")} detected; refreshing assets.");
                await RefreshAssetsAsync();
            }
            finally
            {
                _assetRefreshInProgress = false;
            }
        }));
    }

    private void HandleWatcherError(object? sender, Exception exception) =>
        Dispatcher.UIThread.Post(() => AddConsole("Warning", $"[Watcher] {exception.Message}"));

    private static string FindCommonDirectory(IReadOnlyList<string> paths)
    {
        var fullPaths = paths.Select(Path.GetFullPath).ToArray();
        var candidate = new DirectoryInfo(fullPaths[0]);
        while (candidate is not null)
        {
            var prefix = candidate.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPaths.All(path => path.Equals(candidate.FullName, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException("The project and runtime projects do not share a watchable directory.");
    }

    private static JsonElement? TryReadProperty(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;

    private static bool CanAddWithDependencies(
        SceneEntity entity,
        EditorComponentDescriptor descriptor,
        IReadOnlyDictionary<string, EditorComponentDescriptor> descriptors)
    {
        var planned = new HashSet<string>(StringComparer.Ordinal);
        return Visit(descriptor);

        bool Visit(EditorComponentDescriptor candidate)
        {
            if (entity.Components.FindByType(candidate.TypeId) is not null || !planned.Add(candidate.TypeId))
            {
                return true;
            }

            var existingTypes = entity.Components.Records.Select(component => component.TypeId).ToHashSet(StringComparer.Ordinal);
            var conflicts = candidate.ConflictingComponentTypeIds.Any(existingTypes.Contains)
                || descriptors.Values
                    .Where(other => existingTypes.Contains(other.TypeId))
                    .Any(other => other.ConflictingComponentTypeIds.Contains(candidate.TypeId, StringComparer.Ordinal));
            if (conflicts)
            {
                return false;
            }

            return candidate.RequiredComponentTypeIds.All(requiredTypeId =>
                descriptors.TryGetValue(requiredTypeId, out var required) && Visit(required));
        }
    }

    private void NotifyComponentUi()
    {
        OnPropertyChanged(nameof(AddComponentHint));
        AddSelectedComponentCommand.RaiseCanExecuteChanged();
    }

    private void NotifyCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        RestartSceneHostCommand.RaiseCanExecuteChanged();
        StartPlayCommand.RaiseCanExecuteChanged();
        StopPlayCommand.RaiseCanExecuteChanged();
        TogglePauseCommand.RaiseCanExecuteChanged();
        StepPlayCommand.RaiseCanExecuteChanged();
        ShowGameCommand.RaiseCanExecuteChanged();
        FrameSelectionCommand.RaiseCanExecuteChanged();
        AddSelectedComponentCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsSceneHostRunning));
        OnPropertyChanged(nameof(IsPlayRunning));
        NotifyHierarchyCommands();
    }

    private void NotifyHierarchyCommands()
    {
        DuplicateEntitiesCommand.RaiseCanExecuteChanged();
        DeleteEntitiesCommand.RaiseCanExecuteChanged();
        BeginHierarchyRenameCommand.RaiseCanExecuteChanged();
    }

    private void AddConsole(string level, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddConsole(level, message));
            return;
        }

        ConsoleEntries.Add(new(DateTimeOffset.Now, level, RedactConsolePaths(message)));
        while (ConsoleEntries.Count > 500)
        {
            ConsoleEntries.RemoveAt(0);
        }
    }

    private string RedactConsolePaths(string message)
    {
        var result = message.Replace(
            _workspaceDisplayRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            "<workspace>",
            StringComparison.OrdinalIgnoreCase);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            result = result.Replace(localAppData, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            result = result.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static SceneDocument CreateFallbackScene() => new()
    {
        Name = "Main",
        Roots =
        [
            new SceneEntity
            {
                Name = "Welcome Cube",
                Components =
                {
                    Transform = new(
                        new Vector3Value(0, 0, -0.65),
                        QuaternionValue.Identity,
                        Vector3Value.One),
                    PrimitiveMeshRenderer = new(),
                },
            },
        ],
    };

    private RuntimeTelemetryMessage? DisplayTelemetry => ActiveViewport == RuntimeSessionMode.Scene
        ? _sceneTelemetry
        : _playTelemetry;

    private void NotifyRuntimeTelemetry()
    {
        RuntimeComponentStates.Clear();
        foreach (var component in DisplayTelemetry?.InspectedEntity?.Components ?? [])
        {
            RuntimeComponentStates.Add(new(
                component.DisplayName,
                component.TypeId,
                component.State,
                component.IsLive));
        }

        OnPropertyChanged(nameof(HasRuntimeTelemetry));
        OnPropertyChanged(nameof(HasRuntimeInspection));
        OnPropertyChanged(nameof(RuntimePerformanceSummary));
        OnPropertyChanged(nameof(RuntimeCountsSummary));
        OnPropertyChanged(nameof(RuntimeMemorySummary));
        OnPropertyChanged(nameof(RuntimeInspectionTitle));
    }

    private static string FormatBytes(long bytes)
    {
        var megabytes = bytes / (1024.0 * 1024.0);
        return megabytes >= 1024
            ? $"{megabytes / 1024:0.00} GB"
            : $"{megabytes:0.0} MB";
    }

    private void SavePreferences() => _preferencesService.Save(new(
        AutoRebuildEnabled,
        AutoRefreshAssetsEnabled,
        ShowRuntimeInspection));

    private IReadOnlyList<RuntimeProfileOptionViewModel> GetProfileOptions(RuntimeProfileMode mode)
    {
        if (_project.IsLegacyFormat)
        {
            var spec = _project.CreateRuntimeProjectSpec(mode);
            return [new(spec.ProfileId, spec.DisplayName)];
        }

        return _project.RuntimeProfiles
            .Where(profile => profile.Modes.Contains(mode))
            .Select(profile => new RuntimeProfileOptionViewModel(
                profile.Id,
                string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName))
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _session.Changed -= HandleSessionChanged;
        _sceneHost.EventReceived -= HandleRuntimeEvent;
        _playHost.EventReceived -= HandleRuntimeEvent;
        _sourceWatcher.FilesChanged -= HandleSourceFilesChanged;
        _assetWatcher.FilesChanged -= HandleAssetFilesChanged;
        _sourceWatcher.Error -= HandleWatcherError;
        _assetWatcher.Error -= HandleWatcherError;
        _recoveryStore.WriteFailed -= HandleRecoveryWriteFailed;
        _sourceWatcher.Dispose();
        _assetWatcher.Dispose();
        _recoveryStore.Dispose();
        await _playHost.DisposeAsync();
        await _sceneHost.DisposeAsync();
        foreach (var item in _allProjectItems)
        {
            item.Thumbnail?.Dispose();
        }
    }
}

public sealed record RuntimeWindowChangedEventArgs(RuntimeSessionMode Mode, nint WindowHandle);

public sealed record RuntimeProfileOptionViewModel(string Id, string DisplayName);

public sealed record DeploymentProfileOptionViewModel(
    EditorProjectDefinition.DeploymentProfileDefinition Profile,
    string DisplayName);

public sealed record RuntimeComponentStatusViewModel(
    string DisplayName,
    string TypeId,
    string State,
    bool IsLive)
{
    public string Indicator => IsLive ? "●" : "○";
}

public sealed class HierarchyItemViewModel(
    Guid id,
    string name,
    bool isEnabled,
    int depth,
    bool isSelected = false) : ObservableObject
{
    private bool _isRenaming;
    private string _editName = name;
    private bool _isSelected = isSelected;

    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsEnabled { get; } = isEnabled;
    public int Depth { get; } = depth;
    public string DisplayName => $"{new string(' ', Depth * 4)}{Name}";
    public string Icon => IsEnabled ? "◆" : "◇";

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(IsDisplayVisible));
            }
        }
    }

    public bool IsDisplayVisible => !IsRenaming;
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ProjectItemViewModel(
    string group,
    string name,
    string kind,
    Guid? assetId = null,
    Bitmap? thumbnail = null,
    string? details = null,
    bool hasError = false,
    string? relativePath = null,
    bool isFolder = false,
    string? templatePath = null) : ObservableObject
{
    private bool _isRenaming;
    private string _editName = name;

    public string Group { get; } = group;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public Guid? AssetId { get; } = assetId;
    public Bitmap? Thumbnail { get; } = thumbnail;
    public string? Details { get; } = details;
    public bool HasError { get; } = hasError;
    public string? RelativePath { get; } = relativePath;
    public bool IsFolder { get; } = isFolder;
    public string? TemplatePath { get; } = templatePath;
    public string Icon => IsFolder ? "▰" : TemplatePath is not null ? "◇" : AssetId is null ? "" : "◆";
    public bool IsDisplayVisible => !IsRenaming;
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(IsDisplayVisible));
            }
        }
    }
}

public sealed record ConsoleEntryViewModel(DateTimeOffset Timestamp, string Level, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss");
}
