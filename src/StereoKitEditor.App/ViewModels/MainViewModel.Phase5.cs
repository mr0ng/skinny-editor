using System.Text.Json;
using StereoKitEditor.Assets;
using StereoKitEditor.App.Infrastructure;
using StereoKitEditor.Core;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.App.ViewModels;

public sealed partial class MainViewModel
{
    public RelayCommand AddQuadCommand { get; private set; } = null!;
    public RelayCommand AddTextCommand { get; private set; } = null!;
    public RelayCommand AddUiPanelCommand { get; private set; } = null!;
    public RelayCommand AddUiTextCommand { get; private set; } = null!;
    public RelayCommand AddUiImageCommand { get; private set; } = null!;
    public RelayCommand AddUiSpacerCommand { get; private set; } = null!;
    public RelayCommand AddUiSeparatorCommand { get; private set; } = null!;
    public RelayCommand AddUiButtonCommand { get; private set; } = null!;
    public RelayCommand AddUiToggleCommand { get; private set; } = null!;
    public RelayCommand AddUiSliderCommand { get; private set; } = null!;
    public RelayCommand AddUiTextInputCommand { get; private set; } = null!;
    public RelayCommand ToggleUiInteractionModeCommand { get; private set; } = null!;

    public string UiInteractionModeLabel => _sceneToolSettings.UiInteractionMode == SceneUiInteractionMode.Edit
        ? "Edit UI"
        : "Preview UI";

    public bool IsUiPreviewMode => _sceneToolSettings.UiInteractionMode == SceneUiInteractionMode.Preview;

    public IReadOnlyList<AssetRecord> TextureAssets => _assets
        .Where(asset => asset.Metadata.Kind == AssetKind.Texture && !asset.HasErrors)
        .OrderBy(asset => asset.Metadata.SourcePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<AssetRecord> ProjectAssetCatalog => _assets;

    public bool HasModelMaterialSlots => ModelMaterialSlots.Count > 0;

    public AssetRecord? FindAsset(Guid assetId) => _assets.FirstOrDefault(asset => asset.Metadata.AssetId == assetId);

    private void RefreshModelMaterialSlots()
    {
        ModelMaterialSlots.Clear();
        var entity = SelectedEntity;
        var renderer = entity?.Components.ModelRenderer;
        var modelAsset = renderer is null ? null : FindAsset(renderer.AssetId);
        if (entity is null || renderer is null || modelAsset?.Metadata.Model?.MaterialSlots is not { Count: > 0 } slots)
        {
            OnPropertyChanged(nameof(HasModelMaterialSlots));
            return;
        }

        var materialOptions = new[]
        {
            new ReferenceOptionViewModel(Guid.Empty, "Model / global default", "Use the model material, or the global Material Override above."),
        }.Concat(_assets
            .Where(asset => asset.Metadata.Kind == AssetKind.Material && !asset.HasErrors)
            .OrderBy(asset => asset.Metadata.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(asset => new ReferenceOptionViewModel(
                asset.Metadata.AssetId,
                Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(asset.Metadata.SourcePath)),
                asset.Metadata.SourcePath,
                TryLoadThumbnail(asset.ThumbnailFullPath))))
            .ToArray();

        foreach (var slot in slots)
        {
            var key = slot.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var selectedId = renderer.MaterialOverrides.TryGetValue(key, out var materialId)
                ? materialId
                : Guid.Empty;
            ModelMaterialSlots.Add(new ModelMaterialSlotViewModel(
                slot.Index,
                string.IsNullOrWhiteSpace(slot.Name) ? $"Visual {slot.Index + 1}" : slot.Name,
                materialOptions,
                selectedId,
                id => SetModelMaterialSlot(entity.Id, slot.Index, id)));
        }

        OnPropertyChanged(nameof(HasModelMaterialSlots));
    }

    private void SetModelMaterialSlot(Guid entityId, int slotIndex, Guid materialId)
    {
        var entity = _session.Document.FindEntity(entityId);
        var component = entity?.Components.FindByType(BuiltInComponentTypes.ModelRenderer);
        var renderer = entity?.Components.ModelRenderer;
        if (entity is null || component is null || renderer is null)
        {
            return;
        }

        var overrides = new Dictionary<string, Guid>(renderer.MaterialOverrides, StringComparer.Ordinal);
        var key = slotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (materialId == Guid.Empty)
        {
            overrides.Remove(key);
        }
        else
        {
            overrides[key] = materialId;
        }

        var updated = JsonSerializer.SerializeToElement(
            renderer with { MaterialOverrides = overrides },
            SceneSerializer.Options);
        if (string.Equals(component.Data.GetRawText(), updated.GetRawText(), StringComparison.Ordinal))
        {
            return;
        }

        _session.Execute(new SetComponentDataCommand(
            entity.Id,
            component.Id,
            component.Data,
            updated,
            $"Material Slot {slotIndex + 1}"));
        StatusMessage = materialId == Guid.Empty
            ? $"Cleared material override for slot {slotIndex + 1}"
            : $"Changed material override for slot {slotIndex + 1}";
    }

    private void InitializePhase5Commands()
    {
        AddQuadCommand = new RelayCommand(() => AddPrimitive(PrimitiveKind.Quad));
        AddTextCommand = new RelayCommand(() => AddTextEntity());
        AddUiPanelCommand = new RelayCommand(AddUiPanelEntity);
        AddUiTextCommand = new RelayCommand(() => AddUiElement("UI Text", components => components.UiText = new()));
        AddUiImageCommand = new RelayCommand(() =>
        {
            var texture = TextureAssets.FirstOrDefault();
            if (texture is null)
            {
                StatusMessage = "Import an image before creating a UI Image";
                return;
            }

            AddUiElement("UI Image", components => components.UiImage = new() { TextureAssetId = texture.Metadata.AssetId });
        });
        AddUiSpacerCommand = new RelayCommand(() => AddUiElement("UI Spacer", components => components.UiSpacer = new()));
        AddUiSeparatorCommand = new RelayCommand(() => AddUiElement("UI Separator", components => components.UiSeparator = new()));
        AddUiButtonCommand = new RelayCommand(() => AddUiElement("UI Button", components => components.UiButton = new()));
        AddUiToggleCommand = new RelayCommand(() => AddUiElement("UI Toggle", components => components.UiToggle = new()));
        AddUiSliderCommand = new RelayCommand(() => AddUiElement("UI Slider", components => components.UiSlider = new()));
        AddUiTextInputCommand = new RelayCommand(() => AddUiElement("UI Text Input", components => components.UiTextInput = new()));
        ToggleUiInteractionModeCommand = new RelayCommand(ToggleUiInteractionMode);
    }

    public void CreateEntityForAsset(Guid assetId)
    {
        var asset = FindAsset(assetId);
        if (asset is null)
        {
            StatusMessage = "The selected asset is no longer available";
            return;
        }

        switch (asset.Metadata.Kind)
        {
            case AssetKind.Model:
                CreateModelEntity(assetId);
                break;
            case AssetKind.Texture:
                AddImageEntity(assetId);
                break;
            case AssetKind.Font:
                AddTextEntity(fontAssetId: assetId);
                break;
            case AssetKind.Material:
                AddPrimitive(PrimitiveKind.Quad);
                ApplyAssetToSelection(assetId);
                break;
            case AssetKind.TextStyle:
                AddTextEntity(textStyleAssetId: assetId);
                break;
        }
    }

    public void DropAssetIntoScene(Guid assetId)
    {
        if (!ApplyAssetToSelection(assetId))
        {
            CreateEntityForAsset(assetId);
        }
    }

    public bool ApplyAssetToSelection(Guid assetId)
    {
        var asset = FindAsset(assetId);
        if (asset is null || SelectedEntityIds.Count == 0)
        {
            return false;
        }

        var commands = new List<ISceneCommand>();
        foreach (var entityId in SelectedEntityIds)
        {
            var entity = _session.Document.FindEntity(entityId);
            if (entity is null)
            {
                continue;
            }

            SceneComponentRecord? record = null;
            object? updated = null;
            switch (asset.Metadata.Kind)
            {
                case AssetKind.Texture when entity.Components.PrimitiveMeshRenderer is { } primitive:
                    record = entity.Components.FindByType(BuiltInComponentTypes.PrimitiveMeshRenderer);
                    updated = primitive with { BaseColorTextureOverrideId = assetId };
                    break;
                case AssetKind.Texture when entity.Components.ImageRenderer is { } image:
                    record = entity.Components.FindByType(BuiltInComponentTypes.ImageRenderer);
                    updated = image with { TextureAssetId = assetId };
                    break;
                case AssetKind.Texture when entity.Components.UiImage is { } uiImage:
                    record = entity.Components.FindByType(BuiltInComponentTypes.UiImage);
                    updated = uiImage with { TextureAssetId = assetId };
                    break;
                case AssetKind.Material when entity.Components.PrimitiveMeshRenderer is { } primitive:
                    record = entity.Components.FindByType(BuiltInComponentTypes.PrimitiveMeshRenderer);
                    updated = primitive with { MaterialAssetId = assetId };
                    break;
                case AssetKind.Material when entity.Components.ModelRenderer is { } model:
                    record = entity.Components.FindByType(BuiltInComponentTypes.ModelRenderer);
                    updated = model with { MaterialAssetId = assetId };
                    break;
                case AssetKind.Font when entity.Components.TextRenderer is { } text:
                    record = entity.Components.FindByType(BuiltInComponentTypes.TextRenderer);
                    updated = text with { FontAssetId = assetId };
                    break;
                case AssetKind.TextStyle when entity.Components.TextRenderer is { } text:
                    record = entity.Components.FindByType(BuiltInComponentTypes.TextRenderer);
                    updated = text with { TextStyleAssetId = assetId };
                    break;
                case AssetKind.TextStyle when entity.Components.UiText is { } uiText:
                    record = entity.Components.FindByType(BuiltInComponentTypes.UiText);
                    updated = uiText with { TextStyleAssetId = assetId };
                    break;
            }

            if (record is not null && updated is not null)
            {
                commands.Add(new SetComponentDataCommand(
                    entity.Id,
                    record.Id,
                    record.Data,
                    JsonSerializer.SerializeToElement(updated, SceneSerializer.Options),
                    $"Apply {asset.Metadata.Kind}"));
            }
        }

        if (commands.Count == 0)
        {
            return false;
        }

        _session.Execute(commands.Count == 1
            ? commands[0]
            : new CompositeSceneCommand($"Apply {asset.Metadata.Kind} to {commands.Count} objects", commands));
        StatusMessage = $"Applied {Path.GetFileName(asset.Metadata.SourcePath)} to {commands.Count} object{(commands.Count == 1 ? string.Empty : "s")}";
        return true;
    }

    public async Task CreateMaterialAssetAsync(string relativePath)
    {
        try
        {
            var record = await _assetDatabase.CreateMaterialAsync(relativePath);
            await RefreshAssetsAsync();
            StatusMessage = $"Created Material {Path.GetFileName(record.Metadata.SourcePath)}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            StatusMessage = "Material creation failed";
            AddConsole("Error", exception.Message);
        }
    }

    public async Task CreateTextStyleAssetAsync(string relativePath)
    {
        try
        {
            var record = await _assetDatabase.CreateTextStyleAsync(relativePath);
            await RefreshAssetsAsync();
            StatusMessage = $"Created Text Style {Path.GetFileName(record.Metadata.SourcePath)}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            StatusMessage = "Text Style creation failed";
            AddConsole("Error", exception.Message);
        }
    }

    public async Task UpdateTextureSettingsAsync(Guid assetId, AssetImporterSettings settings)
    {
        await _assetDatabase.UpdateImporterSettingsAsync(assetId, settings);
        await RefreshAssetsAsync();
        StatusMessage = "Texture import settings updated";
    }

    public async Task UpdateMaterialAssetAsync(Guid assetId, MaterialAssetDocument document)
    {
        await _assetDatabase.UpdateMaterialAsync(assetId, document);
        await RefreshAssetsAsync();
        StatusMessage = "Material updated";
    }

    public async Task UpdateTextStyleAssetAsync(Guid assetId, TextStyleAssetDocument document)
    {
        await _assetDatabase.UpdateTextStyleAsync(assetId, document);
        await RefreshAssetsAsync();
        StatusMessage = "Text Style updated";
    }

    private void AddImageEntity(Guid textureAssetId)
    {
        var asset = FindAsset(textureAssetId);
        if (asset?.Metadata.Kind != AssetKind.Texture)
        {
            StatusMessage = "An Image entity requires a Texture asset";
            return;
        }

        var entity = new SceneEntity
        {
            Name = Path.GetFileNameWithoutExtension(asset.Metadata.SourcePath),
            Components =
            {
                Transform = PlacementTransform(),
                ImageRenderer = new() { TextureAssetId = textureAssetId },
            },
        };
        _session.Execute(new AddRootEntityCommand(entity));
        SelectOnly(entity.Id);
        StatusMessage = $"Created Image {entity.Name}";
    }

    private void AddTextEntity(Guid? fontAssetId = null, Guid? textStyleAssetId = null)
    {
        var entity = new SceneEntity
        {
            Name = "Text",
            Components =
            {
                Transform = PlacementTransform(),
                TextRenderer = new()
                {
                    Text = "Text",
                    FontAssetId = fontAssetId,
                    TextStyleAssetId = textStyleAssetId,
                },
            },
        };
        _session.Execute(new AddRootEntityCommand(entity));
        SelectOnly(entity.Id);
        StatusMessage = "Created Text";
    }

    private void AddUiPanelEntity()
    {
        var entity = new SceneEntity
        {
            Name = "UI Panel",
            Components =
            {
                Transform = PlacementTransform(),
                UiPanel = new(),
            },
        };
        _session.Execute(new AddRootEntityCommand(entity));
        SelectOnly(entity.Id);
        StatusMessage = "Created spatial UI Panel";
    }

    private void AddUiElement(string name, Action<EntityComponents> configure)
    {
        var parent = FindUiParent(SelectedEntity);
        if (parent is null)
        {
            AddUiPanelEntity();
            parent = SelectedEntity;
        }

        if (parent is null)
        {
            return;
        }

        var entity = new SceneEntity { Name = name };
        entity.Components.UiRect = new();
        configure(entity.Components);
        _session.Execute(new AddEntityCommand(parent.Id, entity));
        SelectOnly(entity.Id);
        StatusMessage = $"Created {name}";
    }

    private SceneEntity? FindUiParent(SceneEntity? selected)
    {
        if (selected is null)
        {
            return null;
        }

        if (selected.Components.UiPanel is not null || _session.Document.Roots.Any(root => ContainsUiPanelAncestor(root, selected.Id, false)))
        {
            return selected;
        }

        return null;
    }

    private static bool ContainsUiPanelAncestor(SceneEntity entity, Guid targetId, bool insidePanel)
    {
        insidePanel |= entity.Components.UiPanel is not null;
        if (entity.Id == targetId)
        {
            return insidePanel;
        }

        return entity.Children.Any(child => ContainsUiPanelAncestor(child, targetId, insidePanel));
    }

    private TransformComponent PlacementTransform() => new(
        new Vector3Value(_sceneCamera.Pivot.X, _sceneCamera.Pivot.Y, _sceneCamera.Pivot.Z),
        QuaternionValue.Identity,
        Vector3Value.One);

    private void ToggleUiInteractionMode()
    {
        _sceneToolSettings = _sceneToolSettings with
        {
            UiInteractionMode = _sceneToolSettings.UiInteractionMode == SceneUiInteractionMode.Edit
                ? SceneUiInteractionMode.Preview
                : SceneUiInteractionMode.Edit,
        };
        NotifyPhase5ToolSettings();
        _ = PushSceneToolSettingsSafelyAsync();
        StatusMessage = _sceneToolSettings.UiInteractionMode == SceneUiInteractionMode.Edit
            ? "UI Edit mode: clicks select and transform authored elements"
            : "UI Preview mode: controls receive input; project actions remain disabled";
    }

    private void NotifyPhase5ToolSettings()
    {
        OnPropertyChanged(nameof(UiInteractionModeLabel));
        OnPropertyChanged(nameof(IsUiPreviewMode));
    }
}
