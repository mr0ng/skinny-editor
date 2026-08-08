using System.Text.Json;
using System.Diagnostics;
using StereoKitEditor.Adapter;
using StereoKitEditor.Assets;
using StereoKitEditor.Core;
using StereoKitEditor.Protocol;
using StereoKitEditor.Runtime;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Tests;

public sealed class Phase5VisualContentTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Scene_RoundTripsQuadImageTextAndSpatialUiWithTypedSchemas()
    {
        var textureId = Guid.NewGuid();
        var panel = new SceneEntity
        {
            Name = "Panel",
            Components = { UiPanel = new() { Size = new(0.6, 0.4) } },
            Children =
            [
                new SceneEntity
                {
                    Name = "Label",
                    Components =
                    {
                        UiRect = new() { PreferredSize = new(0.3, 0.06) },
                        UiText = new() { Text = "Status" },
                    },
                },
                new SceneEntity
                {
                    Name = "Toggle",
                    Components =
                    {
                        UiRect = new(),
                        UiToggle = new() { BindingId = "sample.enabled", DesignValue = true },
                    },
                },
            ],
        };
        var scene = new SceneDocument
        {
            Roots =
            [
                new SceneEntity { Name = "Quad", Components = { PrimitiveMeshRenderer = new() { Primitive = PrimitiveKind.Quad, BaseColorTextureOverrideId = textureId } } },
                new SceneEntity { Name = "Image", Components = { ImageRenderer = new() { TextureAssetId = textureId } } },
                new SceneEntity { Name = "Text", Components = { TextRenderer = new() { Text = "Hello\nworld" } } },
                panel,
            ],
        };

        var json = SceneSerializer.Serialize(scene);
        var roundTrip = SceneSerializer.Deserialize(json);

        Assert.Equal(PrimitiveKind.Quad, roundTrip.Roots[0].Components.PrimitiveMeshRenderer?.Primitive);
        Assert.Equal(2, roundTrip.Roots[0].Components.FindByType(BuiltInComponentTypes.PrimitiveMeshRenderer)?.SchemaVersion);
        Assert.Equal(textureId, roundTrip.Roots[1].Components.ImageRenderer?.TextureAssetId);
        Assert.Equal("Hello\nworld", roundTrip.Roots[2].Components.TextRenderer?.Text);
        Assert.Equal("sample.enabled", roundTrip.Roots[3].Children[1].Components.UiToggle?.BindingId);
        Assert.Equal(json, SceneSerializer.Serialize(roundTrip));
    }

    [Fact]
    public async Task TextureImport_IsStableProducesMetadataAndReimportsSettings()
    {
        var directory = CreateWorkspace();
        try
        {
            var source = Path.Combine(directory, "Assets", "Pixel.png");
            await File.WriteAllBytesAsync(source, OnePixelPng, TestContext.Current.CancellationToken);
            var database = CreateDatabase(directory);

            var first = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));
            var updated = await database.UpdateImporterSettingsAsync(
                first.Metadata.AssetId,
                first.Metadata.ImporterSettings with
                {
                    ColorSpace = TextureColorSpace.Linear,
                    TextureUsage = TextureUsage.Data,
                    SampleMode = TextureSampleMode.Point,
                    AddressMode = TextureAddressMode.Clamp,
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(AssetKind.Texture, first.Metadata.Kind);
            Assert.Equal(1, first.Metadata.Texture?.Width);
            Assert.Equal(1, first.Metadata.Texture?.Height);
            Assert.True(File.Exists(first.ThumbnailFullPath));
            Assert.Equal(first.Metadata.AssetId, updated.Metadata.AssetId);
            Assert.Equal(TextureColorSpace.Linear, updated.Metadata.ImporterSettings.ColorSpace);
            Assert.Equal(TextureSampleMode.Point, updated.Metadata.ImporterSettings.SampleMode);
            Assert.DoesNotContain(updated.Metadata.Diagnostics, diagnostic => diagnostic.Code == "SKINNY-ASSET-TEXTURE-COLORSPACE");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthoredAssets_TrackTransitiveTextureMaterialTextStyleDependencies()
    {
        var directory = CreateWorkspace();
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "Assets", "Pixel.png"),
                OnePixelPng,
                TestContext.Current.CancellationToken);
            var database = CreateDatabase(directory);
            var texture = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));
            var material = await database.CreateMaterialAsync(
                "Ui.skmaterial.json",
                new MaterialAssetDocument { BaseColorTextureId = texture.Metadata.AssetId },
                TestContext.Current.CancellationToken);
            var style = await database.CreateTextStyleAsync(
                "Heading.sktextstyle.json",
                new TextStyleAssetDocument { MaterialAssetId = material.Metadata.AssetId },
                TestContext.Current.CancellationToken);

            Assert.Equal([texture.Metadata.AssetId], material.Metadata.AssetDependencies);
            Assert.Equal([material.Metadata.AssetId], style.Metadata.AssetDependencies);
            Assert.Equal(
                [material.Metadata.AssetId, style.Metadata.AssetId],
                database.FindDependents(texture.Metadata.AssetId, transitive: true)
                    .Select(asset => asset.Metadata.AssetId).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FontImport_CatalogsWindowsTrueTypeFontWithStableIdentity()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var systemFont = new[] { "segoeui.ttf", "arial.ttf" }
            .Select(name => Path.Combine(fonts, name))
            .FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(systemFont));
        var directory = CreateWorkspace();
        try
        {
            File.Copy(systemFont!, Path.Combine(directory, "Assets", "Sample.ttf"));
            var database = CreateDatabase(directory);
            var first = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));
            var second = Assert.Single(await database.RefreshAsync(TestContext.Current.CancellationToken));

            Assert.Equal(AssetKind.Font, first.Metadata.Kind);
            Assert.False(first.HasErrors);
            Assert.Equal("Sample", first.Metadata.Font?.FamilyName);
            Assert.Equal(first.Metadata.AssetId, second.Metadata.AssetId);
            Assert.True(File.Exists(first.ThumbnailFullPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InteractionResolver_IsolatesSceneDesignStateAndInvokesOnlyInPlay()
    {
        var value = 2.0;
        var invocations = 0;
        var builder = new EditorAdapterBuilder();
        builder.RegisterBinding(
            new()
            {
                Id = "sample.amount",
                DisplayName = "Amount",
                Kind = EditorBindingValueKind.Number,
                Modes = EditorInteractionModes.ScenePreviewAndPlay,
                DesignValue = JsonSerializer.SerializeToElement(0.5),
            },
            () => JsonSerializer.SerializeToElement(value),
            updated => value = updated.GetDouble());
        builder.RegisterAction(
            new() { Id = "sample.apply", DisplayName = "Apply", Modes = EditorInteractionModes.ScenePreviewAndPlay },
            _ => invocations++);

        var sceneMode = SceneUiInteractionMode.Preview;
        var scene = new RuntimeInteractionResolver(builder, RuntimeSessionMode.Scene, () => sceneMode);
        Assert.True(scene.TryRead("sample.amount", out var design));
        Assert.Equal(0.5, design.GetDouble());
        Assert.True(scene.TryWrite("sample.amount", JsonSerializer.SerializeToElement(0.75), out _));
        Assert.Equal(2.0, value);
        Assert.False(scene.TryInvoke(new("sample.apply", Guid.NewGuid(), Guid.NewGuid(), EditorRuntimeMode.Scene), out _));
        Assert.Equal(0, invocations);

        var play = new RuntimeInteractionResolver(builder, RuntimeSessionMode.Play, () => SceneUiInteractionMode.Edit);
        Assert.True(play.TryWrite("sample.amount", JsonSerializer.SerializeToElement(3.0), out _));
        Assert.True(play.TryInvoke(new("sample.apply", Guid.NewGuid(), Guid.NewGuid(), EditorRuntimeMode.Play), out _));
        Assert.Equal(3.0, value);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void SpatialLayout_UsesHierarchyOrderFlowAndNestedRegions()
    {
        var nested = new SceneEntity
        {
            Name = "Nested",
            Components = { UiRect = new() { PreferredSize = new(0.1, 0.03) }, UiText = new() },
        };
        var first = new SceneEntity
        {
            Name = "First",
            Components = { UiRect = new() { PreferredSize = new(0.2, 0.05), Padding = ThicknessValue.Uniform(0.01) }, UiText = new() },
            Children = [nested],
        };
        var second = new SceneEntity
        {
            Name = "Second",
            Components = { UiRect = new() { PreferredSize = new(0.25, 0.04) }, UiButton = new() },
        };
        var panel = new SceneEntity { Name = "Panel", Components = { UiPanel = new() }, Children = [first, second] };

        var layouts = SpatialUiLayoutEngine.Calculate(panel, new(0.5, 0.35));

        Assert.Equal([first.Id, nested.Id, second.Id], layouts.Select(layout => layout.Entity.Id).ToArray());
        Assert.True(layouts[0].Center.y > layouts[2].Center.y);
        Assert.True(layouts[1].Size.x <= layouts[0].Size.x);
    }

    [Fact]
    public void SpatialLayout_AbsoluteChildRetainsParentRegionForAnchorEditing()
    {
        var absolute = new SceneEntity
        {
            Name = "Absolute",
            Components =
            {
                UiRect = new()
                {
                    LayoutMode = UiLayoutMode.Absolute,
                    Anchor = UiAnchor.BottomRight,
                    Pivot = new(1, 1),
                    Position = new(-0.02, -0.03),
                    Size = new(0.12, 0.08),
                },
                UiImage = new() { TextureAssetId = Guid.NewGuid() },
            },
        };
        var panel = new SceneEntity { Name = "Panel", Components = { UiPanel = new() }, Children = [absolute] };

        var layout = Assert.Single(SpatialUiLayoutEngine.Calculate(panel, new(0.5, 0.35)));

        Assert.Equal(0.5f, layout.ParentRegion.Width, 3);
        Assert.Equal(0.35f, layout.ParentRegion.Height, 3);
        var anchor = SpatialUiLayoutEngine.AnchorPoint(UiAnchor.BottomRight, layout.ParentRegion);
        Assert.Equal(0.25f, anchor.x, 3);
        Assert.Equal(-0.175f, anchor.y, 3);
    }

    [Fact]
    public void FrameUiElement_UsesCalculatedLayoutCenterAndKeepsOwningPanelVisible()
    {
        var slider = new SceneEntity
        {
            Name = "Amount",
            Components =
            {
                UiRect = new() { PreferredSize = new(0.4, 0.06) },
                UiSlider = new(),
            },
        };
        var panel = new SceneEntity
        {
            Name = "Controls",
            Components =
            {
                Transform = new(
                    new Vector3Value(0.43, 0.07, -0.76),
                    QuaternionValue.Identity,
                    Vector3Value.One),
                UiPanel = new() { Size = new(0.48, 0.52) },
            },
            Children = [slider],
        };
        var scene = new SceneDocument { Roots = [panel] };

        var resolved = SceneViewportController.TryResolveUiFrameTarget(
            scene,
            slider.Id,
            out var panelWorld,
            out var bounds);

        Assert.True(resolved);
        Assert.Equal(0.43f, panelWorld.Translation.x, 3);
        Assert.Equal(0.07f, panelWorld.Translation.y, 3);
        Assert.Equal(-0.76f, panelWorld.Translation.z, 3);
        Assert.Equal(0.48, bounds.SizeX, 3);
        Assert.Equal(0.52, bounds.SizeY, 3);
        Assert.NotEqual(0, bounds.CenterY);
    }

    [Fact]
    public void SpatialLayout_LargeVisualPanelRemainsDeterministicAndBounded()
    {
        var children = Enumerable.Range(0, 500).Select(index => new SceneEntity
        {
            Name = $"Element {index}",
            Components =
            {
                UiRect = new()
                {
                    PreferredSize = new(0.18, 0.025),
                    SameLine = index % 2 == 1,
                    LineBreak = index % 2 == 1,
                },
                UiText = new() { Text = $"Status {index}" },
            },
        }).ToArray();
        var panel = new SceneEntity { Name = "Large Panel", Components = { UiPanel = new() }, Children = [.. children] };

        var stopwatch = Stopwatch.StartNew();
        var first = SpatialUiLayoutEngine.Calculate(panel, new(0.5, 0.35));
        var second = SpatialUiLayoutEngine.Calculate(panel, new(0.5, 0.35));
        stopwatch.Stop();

        Assert.Equal(500, first.Count);
        Assert.Equal(first.Select(layout => layout.Center).ToArray(), second.Select(layout => layout.Center).ToArray());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Layout took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void RuntimeAssetDescriptor_RoundTripsTypedMetadataAndDependencies()
    {
        var dependency = Guid.NewGuid();
        var descriptor = new RuntimeAssetDescriptor(
            Guid.NewGuid(),
            "Texture",
            "Assets/image.png",
            "hash",
            null,
            [],
            JsonSerializer.SerializeToElement(new { texture = new { width = 128, height = 64 } }),
            [dependency]);

        var json = JsonSerializer.Serialize(descriptor);
        var roundTrip = JsonSerializer.Deserialize<RuntimeAssetDescriptor>(json)!;

        Assert.Equal(128, roundTrip.Metadata?.GetProperty("texture").GetProperty("width").GetInt32());
        Assert.Equal([dependency], roundTrip.EffectiveDependencies);
    }

    [Fact]
    public void ComponentDataCommit_RoundTripsUndoableLayoutPayload()
    {
        var message = new ComponentDataCommittedMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new UiPanelComponent { Size = new(0.8, 0.6) }, SceneSerializer.Options),
            "UI Bounds");

        var roundTrip = JsonSerializer.Deserialize<ComponentDataCommittedMessage>(JsonSerializer.Serialize(message))!;

        Assert.Equal(message.EntityId, roundTrip.EntityId);
        Assert.Equal("UI Bounds", roundTrip.Description);
        Assert.Equal(0.8, roundTrip.Data.GetProperty("size")[0].GetDouble());
    }

    [Fact]
    public async Task PackagedSample_ContainsCompleteVisualAndInteractivePanelVerticalSlice()
    {
        var workspace = FindWorkspace();
        var sourceAssets = Path.Combine(workspace, "samples", "HelloEditor", "Assets");
        var sourceScene = Path.Combine(workspace, "samples", "HelloEditor", "Scenes", "Main.skscene.json");
        var sourceTemplate = Path.Combine(workspace, "samples", "HelloEditor", "Templates", "Spatial Status Panel.sktemplate.json");
        var directory = CreateWorkspace();
        try
        {
            foreach (var source in Directory.EnumerateFiles(sourceAssets))
            {
                File.Copy(source, Path.Combine(directory, "Assets", Path.GetFileName(source)));
            }

            var database = CreateDatabase(directory);
            var assets = await database.RefreshAsync(TestContext.Current.CancellationToken);
            var scene = SceneSerializer.Deserialize(await File.ReadAllTextAsync(sourceScene, TestContext.Current.CancellationToken));
            var template = SceneTemplateSerializer.Deserialize(await File.ReadAllTextAsync(sourceTemplate, TestContext.Current.CancellationToken));

            Assert.Contains(assets, asset => asset.Metadata.Kind == AssetKind.Texture && asset.Metadata.Texture is { Width: > 0, Height: > 0 });
            Assert.Contains(assets, asset => asset.Metadata.Kind == AssetKind.Material && asset.Metadata.AssetDependencies.Count == 1);
            Assert.Contains(assets, asset => asset.Metadata.Kind == AssetKind.TextStyle);
            Assert.Contains(scene.Roots, entity => entity.Components.PrimitiveMeshRenderer is { Primitive: PrimitiveKind.Quad, MaterialAssetId: not null });
            var cube = Assert.Single(
                scene.Roots,
                entity => entity.Components.PrimitiveMeshRenderer is { Primitive: PrimitiveKind.Cube });
            Assert.True(cube.Components.PrimitiveMeshRenderer!.Color.B > cube.Components.PrimitiveMeshRenderer.Color.R);
            Assert.Contains(scene.Roots, entity => entity.Components.ImageRenderer is not null);
            Assert.Contains(scene.Roots, entity => entity.Components.TextRenderer?.TextStyleAssetId is not null);
            var panel = Assert.Single(scene.Roots, entity => entity.Components.UiPanel is not null);
            Assert.Contains(panel.Children, entity => entity.Components.UiImage is not null);
            Assert.Contains(panel.Children, entity => entity.Components.UiToggle?.BindingId == "hello.enabled");
            Assert.Contains(panel.Children, entity => entity.Components.UiSlider?.BindingId == "hello.amount");
            Assert.Contains(panel.Children, entity => entity.Components.UiTextInput?.BindingId == "hello.message");
            Assert.Contains(panel.Children, entity => entity.Components.UiButton?.ActionId == "hello.reset");
            Assert.NotNull(template.Root.Components.UiPanel);
            Assert.Contains(template.Root.Children, entity => entity.Components.UiImage is not null);
            Assert.Contains(template.Root.Children, entity => entity.Components.UiButton?.ActionId == "hello.reset");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AssetDatabase CreateDatabase(string directory) => new(
        Guid.Parse("FC72CC3A-7106-4FA7-8364-D07667381C6E"),
        directory,
        "Assets",
        Path.Combine(directory, "Cache"));

    private static string CreateWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"skinny-phase5-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        return directory;
    }

    private static string FindWorkspace()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "StereoKitEditor.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the SKinny Editor workspace.");
    }
}
