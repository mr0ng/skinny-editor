using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using System.Diagnostics;
using StereoKitEditor.App.Controls;
using StereoKitEditor.App.Services;
using StereoKitEditor.App.ViewModels;
using StereoKitEditor.Protocol;

namespace StereoKitEditor.App;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> AssetIdDataFormat =
        DataFormat.CreateStringApplicationFormat("skinny.asset-id");
    private static readonly DataFormat<string> EntityIdDataFormat =
        DataFormat.CreateStringApplicationFormat("skinny.entity-id");
    private readonly MainViewModel _viewModel;
    private readonly EditorLayoutSettingsService _layoutSettings = new();
    private readonly RecentProjectsService _recentProjects = new();
    private ProjectItemViewModel? _dragProjectItem;
    private Point _dragStart;
    private HierarchyItemViewModel? _dragHierarchyItem;
    private Point _hierarchyDragStart;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private string? _pendingProjectPath;
    private EditorLayoutSettings _preferredLayout = new();

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? projectPath)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(projectPath);
        _recentProjects.RecordOpened(_viewModel.ProjectDefinitionPath, _viewModel.ProjectName);
        DataContext = _viewModel;
        _viewModel.RuntimeWindowChanged += HandleRuntimeWindowChanged;
        _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        Activated += HandleActivated;
        Opened += HandleOpened;
        Closing += HandleClosing;
        SizeChanged += HandleWindowSizeChanged;
    }

    private async void HandleOpened(object? sender, EventArgs args)
    {
        Opened -= HandleOpened;
        ApplyLayout(_layoutSettings.Load());
        if (_viewModel.HasPendingRecovery)
        {
            var restore = await new ConfirmationDialog(
                "Recover scene — SKinny Editor",
                "Unsaved scene changes were found",
                _viewModel.PendingRecoveryDescription,
                "Restore",
                "Discard").ShowDialog<bool>(this);
            if (restore)
            {
                _viewModel.RestorePendingRecovery();
            }
            else
            {
                _viewModel.DiscardPendingRecovery();
            }
        }

        if (!_viewModel.IsWorkspaceTrusted)
        {
            var trustDialog = new WorkspaceTrustDialog(_viewModel.WorkspaceTrust);
            var trusted = await trustDialog.ShowDialog<bool>(this);
            if (!trusted)
            {
                _viewModel.DeclineWorkspaceTrust();
                return;
            }

            await _viewModel.TrustWorkspaceAsync();
        }

        await _viewModel.InitializeAsync();
    }

    private void HandleRuntimeWindowChanged(object? sender, RuntimeWindowChangedEventArgs args)
    {
        var viewport = args.Mode == RuntimeSessionMode.Scene ? SceneViewport : GameViewport;
        if (args.WindowHandle == 0)
        {
            viewport.DetachWindow();
        }
        else
        {
            viewport.AttachWindow(args.WindowHandle);
        }
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.ActiveViewport))
        {
            var viewport = _viewModel.ActiveViewport == RuntimeSessionMode.Scene
                ? SceneViewport
                : GameViewport;
            viewport.FocusWindow();
        }
    }

    private void HandleActivated(object? sender, EventArgs args)
    {
        var viewport = _viewModel.ActiveViewport == RuntimeSessionMode.Scene
            ? SceneViewport
            : GameViewport;
        viewport.FocusWindow();
    }

    private async void ImportGlb_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import GLB model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GLB model")
                {
                    Patterns = ["*.glb"],
                    MimeTypes = ["model/gltf-binary"],
                },
            ],
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _viewModel.ImportGlbAsync(path);
        }
    }

    private async void ImportVisualAsset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import visual assets",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images, fonts, and models")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.psd", "*.gif", "*.hdr", "*.pic", "*.ktx2", "*.ttf", "*.glb"],
                },
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.psd", "*.gif", "*.hdr", "*.pic", "*.ktx2"],
                },
                new FilePickerFileType("TrueType fonts") { Patterns = ["*.ttf"] },
                new FilePickerFileType("GLB models") { Patterns = ["*.glb"] },
            ],
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { Length: > 0 } path)
            {
                await _viewModel.ImportAssetAsync(path);
            }
        }
    }

    private async void OpenProject_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SKinny Editor project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SKinny Editor project")
                {
                    Patterns = ["*.skproject.json"],
                    MimeTypes = ["application/json"],
                },
            ],
        });
        QueueProjectSwitch(files.FirstOrDefault()?.TryGetLocalPath());
    }

    private async void ImportProject_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        try
        {
            var result = await ExistingProjectImportFlow.RunAsync(this, _viewModel.ReportStatus);
            if (result is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.DescriptorPath))
            {
                QueueProjectSwitch(result.DescriptorPath);
                return;
            }

            _viewModel.ReportStatus(result.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or System.Text.Json.JsonException)
        {
            _viewModel.ReportStatus($"Could not import the StereoKit project. {exception.Message}");
        }
    }

    private async void RecentProjects_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var entries = _recentProjects.Load();
        if (entries.Count == 0)
        {
            OpenProject_Click(sender, args);
            return;
        }

        var path = await new RecentProjectsDialog(entries).ShowDialog<string?>(this);
        QueueProjectSwitch(path);
    }

    private void QueueProjectSwitch(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.GetFullPath(path);
        if (string.Equals(path, _viewModel.ProjectDefinitionPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingProjectPath = path;
        Close();
    }

    private void ProjectItem_DoubleTapped(object? sender, TappedEventArgs args)
    {
        if ((sender as Control)?.DataContext is ProjectItemViewModel { AssetId: { } assetId })
        {
            _viewModel.CreateEntityForAsset(assetId);
            args.Handled = true;
        }
        else if ((sender as Control)?.DataContext is ProjectItemViewModel { TemplatePath: { } templatePath })
        {
            _viewModel.InstantiateTemplate(templatePath);
            args.Handled = true;
        }
    }

    private async void CreateAssetFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var path = await new TextPromptDialog(
            "Create asset folder",
            "Folder path relative to Assets (for example Models/Vehicles):")
            .ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _viewModel.CreateAssetFolderAsync(path);
        }
    }

    private void ProjectList_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.F2 && ProjectList.SelectedItem is ProjectItemViewModel item)
        {
            BeginProjectRenameAndFocus(item);
            args.Handled = true;
        }
    }

    private void ProjectCreateEntity_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var item = GetProjectItem(sender);
        if (item?.AssetId is { } assetId)
        {
            _viewModel.DropAssetIntoScene(assetId);
        }
        else if (item?.TemplatePath is { } templatePath)
        {
            _viewModel.InstantiateTemplate(templatePath);
        }
    }

    private async void CreateImageEntity_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var textureItems = _viewModel.ProjectFiles.Where(item =>
            item.AssetId is { } id && _viewModel.FindAsset(id)?.Metadata.Kind == StereoKitEditor.Assets.AssetKind.Texture);
        var assetId = await new AssetPickerDialog("Choose a Texture for the Image", textureItems)
            .ShowDialog<Guid?>(this);
        if (assetId is { } id)
        {
            _viewModel.CreateEntityForAsset(id);
        }
    }

    private async void CreateMaterialAsset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var name = await new TextPromptDialog(
            "Create Material",
            "Material path relative to Assets:",
            "New Material.skmaterial.json").ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _viewModel.CreateMaterialAssetAsync(name);
        }
    }

    private async void CreateTextStyleAsset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var name = await new TextPromptDialog(
            "Create Text Style",
            "Text Style path relative to Assets:",
            "New Text Style.sktextstyle.json").ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _viewModel.CreateTextStyleAssetAsync(name);
        }
    }

    private async void ProjectEditAsset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (GetProjectItem(sender)?.AssetId is not { } assetId
            || _viewModel.FindAsset(assetId) is not { } asset)
        {
            return;
        }

        if (asset.Metadata.Kind is not (StereoKitEditor.Assets.AssetKind.Texture
            or StereoKitEditor.Assets.AssetKind.Material
            or StereoKitEditor.Assets.AssetKind.TextStyle))
        {
            return;
        }

        var result = await new VisualAssetSettingsDialog(asset, _viewModel.ProjectAssetCatalog)
            .ShowDialog<VisualAssetEditResult?>(this);
        if (result?.TextureSettings is { } textureSettings)
        {
            await _viewModel.UpdateTextureSettingsAsync(assetId, textureSettings);
        }
        else if (result?.Material is { } material)
        {
            await _viewModel.UpdateMaterialAssetAsync(assetId, material);
        }
        else if (result?.TextStyle is { } textStyle)
        {
            await _viewModel.UpdateTextStyleAssetAsync(assetId, textStyle);
        }
    }

    private void ProjectRename_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (GetProjectItem(sender) is { AssetId: not null } item)
        {
            BeginProjectRenameAndFocus(item);
        }
    }

    private async void ProjectMove_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (GetProjectItem(sender) is not { AssetId: { } assetId, RelativePath: { } currentPath })
        {
            return;
        }

        var path = await new TextPromptDialog(
            "Move asset",
            "New path relative to Assets. Keep the .glb extension:",
            currentPath).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _viewModel.MoveAssetAsync(assetId, path);
        }
    }

    private async void ProjectDelete_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (GetProjectItem(sender) is not { AssetId: { } assetId } item)
        {
            return;
        }

        var confirmed = await new ConfirmationDialog(
            "Delete asset",
            $"Move {item.Name} to project trash?",
            "Referenced assets are protected. Unreferenced source and metadata files are moved to .skinny/Trash and can be recovered manually.")
            .ShowDialog<bool>(this);
        if (confirmed)
        {
            await _viewModel.DeleteAssetAsync(assetId);
        }
    }

    private void BeginProjectRenameAndFocus(ProjectItemViewModel item)
    {
        _viewModel.BeginProjectRename(item);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = ProjectList.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(textBox => ReferenceEquals(textBox.DataContext, item) && textBox.IsVisible);
            editor?.Focus();
            editor?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private async void ProjectRenameTextBox_KeyDown(object? sender, KeyEventArgs args)
    {
        if ((sender as Control)?.DataContext is not ProjectItemViewModel item)
        {
            return;
        }

        if (args.Key == Key.Enter)
        {
            await _viewModel.CommitProjectRenameAsync(item);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            _viewModel.CancelProjectRename(item);
            args.Handled = true;
        }
    }

    private async void ProjectRenameTextBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((sender as Control)?.DataContext is ProjectItemViewModel { IsRenaming: true } item)
        {
            await _viewModel.CommitProjectRenameAsync(item);
        }
    }

    private ProjectItemViewModel? GetProjectItem(object? sender) =>
        (sender as Control)?.DataContext as ProjectItemViewModel
        ?? ProjectList.SelectedItem as ProjectItemViewModel;

    private void ProjectItem_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if ((sender as Control)?.DataContext is not ProjectItemViewModel { AssetId: not null } item)
        {
            return;
        }

        _dragProjectItem = item;
        _dragStart = args.GetPosition(this);
    }

    private async void ProjectItem_PointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragProjectItem?.AssetId is not { } assetId)
        {
            return;
        }

        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragProjectItem = null;
            return;
        }

        var current = args.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < 5 && Math.Abs(current.Y - _dragStart.Y) < 5)
        {
            return;
        }

        _dragProjectItem = null;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(AssetIdDataFormat, assetId.ToString("D")));
        await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Copy);
    }

    private void SceneViewport_DragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = args.DataTransfer.Contains(AssetIdDataFormat)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        args.Handled = true;
    }

    private void SceneViewport_Drop(object? sender, DragEventArgs args)
    {
        var value = args.DataTransfer.TryGetValue(AssetIdDataFormat);
        if (Guid.TryParse(value, out var assetId))
        {
            _viewModel.DropAssetIntoScene(assetId);
        }

        args.Handled = true;
    }

    private void HierarchyList_SelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (HierarchyList.ItemCount == 0)
        {
            return;
        }

        _viewModel.SelectHierarchyItems(
            HierarchyList.SelectedItems?.OfType<HierarchyItemViewModel>() ?? []);
    }

    private void HierarchyList_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Source is TextBox)
        {
            return;
        }

        if (args.Key == Key.F2 && _viewModel.SelectedItem is { } item)
        {
            BeginRenameAndFocus(item);
            args.Handled = true;
        }
        else if (args.Key == Key.Delete)
        {
            ExecuteHierarchyCommand(_viewModel.DeleteEntitiesCommand);
            args.Handled = true;
        }
        else if (args.Key == Key.D && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ExecuteHierarchyCommand(_viewModel.DuplicateEntitiesCommand);
            args.Handled = true;
        }
        else if (args.Key == Key.N
                 && args.KeyModifiers.HasFlag(KeyModifiers.Control)
                 && args.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ExecuteHierarchyCommand(_viewModel.CreateChildCommand);
            args.Handled = true;
        }
    }

    private void HierarchyItem_DoubleTapped(object? sender, TappedEventArgs args)
    {
        if ((sender as Control)?.DataContext is { } dataContext
            && dataContext is HierarchyItemViewModel item)
        {
            BeginRenameAndFocus(item);
            args.Handled = true;
        }
    }

    private void ComponentSlider_PointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (sender is Slider { DataContext: ComponentPropertyViewModel property } slider)
        {
            property.CommitSliderValue(slider.Value);
            args.Handled = true;
        }
    }

    private void HierarchyRename_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((sender as Control)?.DataContext is HierarchyItemViewModel item)
        {
            BeginRenameAndFocus(item);
            args.Handled = true;
        }
    }

    private void HierarchyCreateChild_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        ExecuteHierarchyContextCommand(sender, _viewModel.CreateChildCommand);
        args.Handled = true;
    }

    private void HierarchyDuplicate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        ExecuteHierarchyContextCommand(sender, _viewModel.DuplicateEntitiesCommand);
        args.Handled = true;
    }

    private void HierarchyDelete_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        ExecuteHierarchyContextCommand(sender, _viewModel.DeleteEntitiesCommand);
        args.Handled = true;
    }

    private void ExecuteHierarchyContextCommand(object? sender, System.Windows.Input.ICommand command)
    {
        if ((sender as Control)?.DataContext is HierarchyItemViewModel item
            && !HierarchyList.SelectedItems!.Contains(item))
        {
            _viewModel.SelectHierarchyItems([item]);
        }

        ExecuteHierarchyCommand(command);
    }

    private static void ExecuteHierarchyCommand(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private async void HierarchySaveTemplate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((sender as Control)?.DataContext is not HierarchyItemViewModel item)
        {
            return;
        }

        _viewModel.SelectHierarchyItems([item]);
        var name = await new TextPromptDialog(
            "Save scene template",
            "Template name:",
            item.Name).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _viewModel.SaveSelectedAsTemplateAsync(name);
        }
    }

    private void BeginRenameAndFocus(HierarchyItemViewModel item)
    {
        _viewModel.BeginHierarchyRename(item);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = HierarchyList.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(textBox => ReferenceEquals(textBox.DataContext, item) && textBox.IsVisible);
            editor?.Focus();
            editor?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void HierarchyRename_KeyDown(object? sender, KeyEventArgs args)
    {
        if ((sender as Control)?.DataContext is not HierarchyItemViewModel item)
        {
            return;
        }

        if (args.Key == Key.Enter)
        {
            _viewModel.CommitHierarchyRename(item);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            _viewModel.CancelHierarchyRename(item);
            args.Handled = true;
        }
    }

    private void HierarchyRename_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((sender as Control)?.DataContext is HierarchyItemViewModel { IsRenaming: true } item)
        {
            _viewModel.CommitHierarchyRename(item);
        }
    }

    private void HierarchyItem_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if ((sender as Control)?.DataContext is not HierarchyItemViewModel item)
        {
            return;
        }

        _dragHierarchyItem = item;
        _hierarchyDragStart = args.GetPosition(this);
    }

    private async void HierarchyItem_PointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragHierarchyItem is not { } item)
        {
            return;
        }

        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragHierarchyItem = null;
            return;
        }

        var current = args.GetPosition(this);
        if (Math.Abs(current.X - _hierarchyDragStart.X) < 5
            && Math.Abs(current.Y - _hierarchyDragStart.Y) < 5)
        {
            return;
        }

        _dragHierarchyItem = null;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(EntityIdDataFormat, item.Id.ToString("D")));
        await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
    }

    private void Hierarchy_DragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = args.DataTransfer.Contains(EntityIdDataFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        args.Handled = true;
    }

    private void HierarchyItem_Drop(object? sender, DragEventArgs args)
    {
        if (args.DataTransfer.Contains(EntityIdDataFormat)
            && (sender as Control)?.DataContext is HierarchyItemViewModel target)
        {
            _viewModel.ReparentSelectedEntities(target.Id);
        }

        args.Handled = true;
    }

    private void HierarchyRoot_Drop(object? sender, DragEventArgs args)
    {
        if (args.DataTransfer.Contains(EntityIdDataFormat))
        {
            _viewModel.ReparentSelectedEntities(null);
        }

        args.Handled = true;
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_shutdownComplete)
        {
            return;
        }

        args.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        if (_viewModel.HasUnsavedChanges)
        {
            var choice = await new UnsavedChangesDialog().ShowDialog<UnsavedChangesChoice>(this);
            if (choice == UnsavedChangesChoice.Cancel)
            {
                _pendingProjectPath = null;
                _shutdownStarted = false;
                return;
            }

            if (choice == UnsavedChangesChoice.Save && !await _viewModel.TrySaveNowAsync())
            {
                _pendingProjectPath = null;
                _shutdownStarted = false;
                return;
            }

            if (choice == UnsavedChangesChoice.Discard)
            {
                _viewModel.DiscardUnsavedRecovery();
            }
        }

        await _layoutSettings.SaveAsync(_preferredLayout);
        _viewModel.RuntimeWindowChanged -= HandleRuntimeWindowChanged;
        _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        Activated -= HandleActivated;
        SceneViewport.DetachWindow();
        GameViewport.DetachWindow();
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _shutdownComplete = true;
            if (_pendingProjectPath is { } projectPath)
            {
                LaunchProject(projectPath);
            }

            Close();
        }
    }

    private static void LaunchProject(string projectPath)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The editor executable path is unavailable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        Process.Start(startInfo);
    }

    private void ApplyLayout(EditorLayoutSettings settings)
    {
        _preferredLayout = settings.Clamp();
        ApplyResponsiveLayout();
    }

    private void HandleWindowSizeChanged(object? sender, SizeChangedEventArgs args) =>
        ApplyResponsiveLayout();

    private void LayoutSplitter_PointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _preferredLayout = CaptureLayout().Clamp();
            ApplyResponsiveLayout();
        }, DispatcherPriority.Background);
    }

    private void ApplyResponsiveLayout()
    {
        var availableWidth = Math.Max(MinWidth, Bounds.Width > 0 ? Bounds.Width : Width);
        var availableHeight = Math.Max(MinHeight, Bounds.Height > 0 ? Bounds.Height : Height);

        const double splitterWidth = 10;
        const double minimumSceneWidth = 300;
        const double minimumHierarchyWidth = 140;
        // Preserve enough room for three readable transform fields side by side.
        const double minimumInspectorWidth = 340;
        var sideBudget = Math.Max(
            minimumHierarchyWidth + minimumInspectorWidth,
            availableWidth - splitterWidth - minimumSceneWidth);
        var hierarchyWidth = Math.Clamp(_preferredLayout.HierarchyWidth, minimumHierarchyWidth, 600);
        var inspectorWidth = Math.Clamp(_preferredLayout.InspectorWidth, minimumInspectorWidth, 700);
        var excess = Math.Max(0, hierarchyWidth + inspectorWidth - sideBudget);
        var hierarchyRoom = hierarchyWidth - minimumHierarchyWidth;
        var inspectorRoom = inspectorWidth - minimumInspectorWidth;
        var shrinkable = hierarchyRoom + inspectorRoom;
        if (excess > 0 && shrinkable > 0)
        {
            hierarchyWidth -= Math.Min(hierarchyRoom, excess * hierarchyRoom / shrinkable);
            inspectorWidth -= Math.Min(inspectorRoom, excess * inspectorRoom / shrinkable);
        }

        const double fixedVerticalChrome = 111;
        const double minimumWorkspaceHeight = 260;
        var maximumBottomHeight = Math.Max(100, availableHeight - fixedVerticalChrome - minimumWorkspaceHeight);
        var bottomHeight = Math.Clamp(_preferredLayout.BottomHeight, 100, Math.Min(500, maximumBottomHeight));
        var maximumProjectWidth = Math.Max(220, availableWidth - 5 - 320);
        var projectWidth = Math.Clamp(_preferredLayout.ProjectWidth, 220, Math.Min(1_200, maximumProjectWidth));

        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(hierarchyWidth);
        WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(inspectorWidth);
        RootGrid.RowDefinitions[4].Height = new GridLength(bottomHeight);
        BottomGrid.ColumnDefinitions[0].Width = new GridLength(projectWidth);
        BottomGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
    }

    private EditorLayoutSettings CaptureLayout() => new(
        WorkspaceGrid.ColumnDefinitions[0].ActualWidth,
        WorkspaceGrid.ColumnDefinitions[4].ActualWidth,
        RootGrid.RowDefinitions[4].ActualHeight,
        BottomGrid.ColumnDefinitions[0].ActualWidth);
}
