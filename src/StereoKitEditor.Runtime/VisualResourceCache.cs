using System.Text.Json;
using System.Text.Json.Serialization;
using StereoKit;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

internal sealed class VisualResourceCache(Func<Guid, RuntimeAssetDescriptor?> resolve)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, Tex> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Material> _materials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Font> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextStyle> _textStyles = new(StringComparer.Ordinal);

    public void InvalidateAll()
    {
        _textures.Clear();
        _sprites.Clear();
        _materials.Clear();
        _fonts.Clear();
        _textStyles.Clear();
    }

    public void Invalidate(IReadOnlySet<Guid> assetIds, IReadOnlyDictionary<Guid, RuntimeAssetDescriptor> catalog)
    {
        if (assetIds.Count == 0)
        {
            return;
        }

        RemoveKeys(_textures, assetIds);
        RemoveKeys(_sprites, assetIds);
        RemoveKeys(_fonts, assetIds);
        var kinds = assetIds
            .Select(id => catalog.TryGetValue(id, out var asset) ? asset.Kind : string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (kinds.Contains("Texture") || kinds.Contains("Material"))
        {
            _materials.Clear();
        }

        if (kinds.Contains("Font") || kinds.Contains("TextStyle") || kinds.Contains("Material"))
        {
            _textStyles.Clear();
        }

        static void RemoveKeys<T>(Dictionary<string, T> cache, IReadOnlySet<Guid> ids)
        {
            foreach (var key in cache.Keys.Where(key => ids.Any(id => key.StartsWith(id.ToString("N"), StringComparison.Ordinal))).ToArray())
            {
                cache.Remove(key);
            }
        }
    }

    public bool TryGetTexture(Guid assetId, out Tex texture, out string? error)
    {
        error = null;
        var asset = resolve(assetId);
        if (asset is null || !string.Equals(asset.Kind, "Texture", StringComparison.OrdinalIgnoreCase))
        {
            texture = null!;
            error = $"Texture asset '{assetId}' was not found.";
            return false;
        }

        var metadata = ReadMetadata(asset);
        var key = $"{asset.AssetId:N}:{asset.ContentHash}:{metadata?.ImporterSettings?.ColorSpace}:{metadata?.ImporterSettings?.SampleMode}:{metadata?.ImporterSettings?.AddressMode}";
        if (_textures.TryGetValue(key, out texture!))
        {
            return true;
        }

        try
        {
            texture = Tex.FromFile(
                asset.SourcePath,
                metadata?.ImporterSettings?.ColorSpace != RuntimeTextureColorSpace.Linear,
                10);
            if (texture is null)
            {
                error = $"StereoKit could not load texture '{asset.SourcePath}'.";
                return false;
            }

            texture.AddressMode = metadata?.ImporterSettings?.AddressMode switch
            {
                RuntimeTextureAddressMode.Clamp => TexAddress.Clamp,
                RuntimeTextureAddressMode.Mirror => TexAddress.Mirror,
                _ => TexAddress.Wrap,
            };
            texture.SampleMode = metadata?.ImporterSettings?.SampleMode switch
            {
                RuntimeTextureSampleMode.Point => TexSample.Point,
                RuntimeTextureSampleMode.Anisotropic => TexSample.Anisotropic,
                _ => TexSample.Linear,
            };
            if (texture.SampleMode == TexSample.Anisotropic)
            {
                texture.Anisoptropy = 8;
            }

            _textures[key] = texture;
            return true;
        }
        catch (Exception exception)
        {
            texture = null!;
            error = exception.Message;
            return false;
        }
    }

    public bool TryGetSprite(Guid assetId, out Sprite sprite, out string? error)
    {
        var asset = resolve(assetId);
        if (asset is null)
        {
            sprite = null!;
            error = $"Texture asset '{assetId}' was not found.";
            return false;
        }

        var key = $"{asset.AssetId:N}:{asset.ContentHash}";
        if (_sprites.TryGetValue(key, out sprite!))
        {
            error = null;
            return true;
        }

        if (!TryGetTexture(assetId, out var texture, out error))
        {
            sprite = null!;
            return false;
        }

        sprite = Sprite.FromTex(texture, SpriteType.Single, $"skinny-sprite-{key}");
        _sprites[key] = sprite;
        return true;
    }

    public Material GetMaterial(
        Guid? materialAssetId,
        Guid? baseColorOverrideId,
        double uvScaleX,
        double uvScaleY,
        double uvOffsetX,
        double uvOffsetY,
        bool imageDefaults,
        bool doubleSided,
        RenderSurfacePreset surfacePreset,
        out string? error)
    {
        error = null;
        RuntimeMaterialDocument? document = null;
        RuntimeAssetDescriptor? materialAsset = null;
        if (materialAssetId is { } materialId && materialId != Guid.Empty)
        {
            materialAsset = resolve(materialId);
            document = materialAsset is { Kind: var kind }
                && string.Equals(kind, "Material", StringComparison.OrdinalIgnoreCase)
                ? ReadMetadata(materialAsset)?.Material
                : null;
            if (document is null)
            {
                error = $"Material asset '{materialId}' was not found or is invalid.";
            }
        }

        var textureId = baseColorOverrideId ?? document?.BaseColorTextureId;
        var key = string.Join(':',
            materialAsset?.ContentHash ?? (imageDefaults ? "image" : "default"),
            textureId?.ToString("N") ?? "none",
            uvScaleX, uvScaleY, uvOffsetX, uvOffsetY, doubleSided, surfacePreset);
        if (_materials.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var transparency = document?.Transparency
            ?? (imageDefaults && surfacePreset != RenderSurfacePreset.WorldOpaque
                ? RuntimeMaterialTransparency.Blend
                : RuntimeMaterialTransparency.Opaque);
        var shaderFamily = document?.ShaderFamily ?? (imageDefaults ? RuntimeMaterialShaderFamily.Unlit : RuntimeMaterialShaderFamily.Standard);
        Material material = (shaderFamily, transparency) switch
        {
            (RuntimeMaterialShaderFamily.Unlit, RuntimeMaterialTransparency.Cutout) => Material.UnlitClip.Copy(),
            (RuntimeMaterialShaderFamily.Unlit, _) => Material.Unlit.Copy(),
            (_, RuntimeMaterialTransparency.Cutout) => Material.PBRClip.Copy(),
            _ => Material.Default.Copy(),
        };

        SetTexture(material, MatParamName.DiffuseTex, textureId, ref error);
        SetTexture(material, MatParamName.NormalTex, document?.NormalTextureId, ref error);
        SetTexture(material, MatParamName.MetalTex, document?.MetalRoughTextureId, ref error);
        SetTexture(material, MatParamName.OcclusionTex, document?.OcclusionTextureId, ref error);
        SetTexture(material, MatParamName.EmissionTex, document?.EmissionTextureId, ref error);

        if (document is not null)
        {
            material[MatParamName.ColorTint] = ToColor(document.ColorTint);
            material[MatParamName.MetallicAmount] = (float)Math.Clamp(document.Metallic, 0, 1);
            material[MatParamName.RoughnessAmount] = (float)Math.Clamp(document.Roughness, 0, 1);
            material[MatParamName.EmissionFactor] = (float)Math.Max(0, document.EmissionFactor);
            material[MatParamName.ClipCutoff] = (float)Math.Clamp(document.AlphaCutoff, 0, 1);
            material.FaceCull = document.Cull switch
            {
                RuntimeMaterialCullMode.Front => Cull.Front,
                RuntimeMaterialCullMode.None => Cull.None,
                _ => Cull.Back,
            };
            material.DepthTest = document.DepthTest switch
            {
                RuntimeMaterialDepthTest.LessOrEqual => DepthTest.LessOrEq,
                RuntimeMaterialDepthTest.Always => DepthTest.Always,
                _ => DepthTest.Less,
            };
            material.DepthWrite = document.DepthWrite;
            material.QueueOffset = Math.Clamp(document.QueueOffset, -100, 100);
        }
        else if (imageDefaults)
        {
            material.FaceCull = doubleSided ? Cull.None : Cull.Back;
            material.DepthWrite = false;
        }

        if (surfacePreset == RenderSurfacePreset.Overlay)
        {
            material.DepthTest = DepthTest.Always;
            material.DepthWrite = false;
            material.QueueOffset = Math.Max(material.QueueOffset, 50);
        }
        else if (surfacePreset == RenderSurfacePreset.WorldOpaque && document is null)
        {
            material.Transparency = Transparency.None;
            material.DepthWrite = true;
        }

        material.Transparency = transparency switch
        {
            RuntimeMaterialTransparency.Blend => Transparency.Blend,
            RuntimeMaterialTransparency.Additive => Transparency.Add,
            RuntimeMaterialTransparency.Cutout => Transparency.MSAA,
            _ => Transparency.None,
        };
        material[MatParamName.TexTransform] = Matrix.TRS(
            new Vec3(
                (float)(uvOffsetX + (document?.UvOffset.X ?? 0)),
                (float)(uvOffsetY + (document?.UvOffset.Y ?? 0)),
                0),
            Quat.Identity,
            new Vec3(
                (float)(uvScaleX * (document?.UvScale.X ?? 1)),
                (float)(uvScaleY * (document?.UvScale.Y ?? 1)),
                1));
        _materials[key] = material;
        return material;
    }

    public TextStyle GetTextStyle(
        Guid? textStyleAssetId,
        Guid? fontOverrideId,
        double characterHeight,
        StereoKitEditor.Scene.ColorValue color,
        out string? error)
    {
        error = null;
        RuntimeTextStyleDocument? document = null;
        RuntimeAssetDescriptor? styleAsset = null;
        if (textStyleAssetId is { } styleId && styleId != Guid.Empty)
        {
            styleAsset = resolve(styleId);
            document = styleAsset is { Kind: var kind }
                && string.Equals(kind, "TextStyle", StringComparison.OrdinalIgnoreCase)
                ? ReadMetadata(styleAsset)?.TextStyle
                : null;
            if (document is null)
            {
                error = $"Text Style asset '{styleId}' was not found or is invalid.";
            }
        }

        var fontId = fontOverrideId ?? document?.FontAssetId;
        var height = document is not null && Math.Abs(characterHeight - 0.035) < 0.000001
            ? document.CharacterHeight
            : characterHeight > 0 ? characterHeight : document?.CharacterHeight ?? 0.035;
        var styleColor = document?.Color ?? new RuntimeColor(1, 1, 1, 1);
        var effectiveColor = new RuntimeColor(
            color.R * styleColor.R,
            color.G * styleColor.G,
            color.B * styleColor.B,
            color.A * styleColor.A);
        var key = string.Join(':', styleAsset?.ContentHash ?? "default", fontId?.ToString("N") ?? "default",
            height, effectiveColor.R, effectiveColor.G, effectiveColor.B, effectiveColor.A);
        if (_textStyles.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var font = Font.Default;
        if (fontId is { } requestedFont && requestedFont != Guid.Empty
            && !TryGetFont(requestedFont, out font, out var fontError))
        {
            error = fontError;
            font = Font.Default;
        }

        TextStyle style;
        if (document?.MaterialAssetId is { } materialId && materialId != Guid.Empty)
        {
            var material = GetMaterial(
                materialId,
                null,
                1, 1, 0, 0,
                imageDefaults: false,
                doubleSided: true,
                RenderSurfacePreset.WorldTransparent,
                out var materialError);
            error ??= materialError;
            style = Text.MakeStyle(font, (float)Math.Clamp(height, 0.001, 10), material, ToColor(effectiveColor));
        }
        else
        {
            style = Text.MakeStyle(font, (float)Math.Clamp(height, 0.001, 10), ToColor(effectiveColor));
        }
        _textStyles[key] = style;
        return style;
    }

    private bool TryGetFont(Guid assetId, out Font font, out string? error)
    {
        var asset = resolve(assetId);
        if (asset is null || !string.Equals(asset.Kind, "Font", StringComparison.OrdinalIgnoreCase))
        {
            font = Font.Default;
            error = $"Font asset '{assetId}' was not found.";
            return false;
        }

        var key = $"{asset.AssetId:N}:{asset.ContentHash}";
        if (!_fonts.TryGetValue(key, out font!))
        {
            try
            {
                font = Font.FromFile([asset.SourcePath]);
                _fonts[key] = font;
            }
            catch (Exception exception)
            {
                font = Font.Default;
                error = exception.Message;
                return false;
            }
        }

        error = null;
        return true;
    }

    private void SetTexture(Material material, MatParamName parameter, Guid? assetId, ref string? error)
    {
        if (assetId is not { } id || id == Guid.Empty)
        {
            return;
        }

        if (TryGetTexture(id, out var texture, out var textureError))
        {
            material[parameter] = texture;
        }
        else
        {
            error ??= textureError;
        }
    }

    private static RuntimeVisualMetadata? ReadMetadata(RuntimeAssetDescriptor asset)
    {
        if (asset.Metadata is not { } metadata || metadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return metadata.Deserialize<RuntimeVisualMetadata>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Color ToColor(RuntimeColor color) => new(
        (float)color.R,
        (float)color.G,
        (float)color.B,
        (float)color.A);

    private sealed record RuntimeVisualMetadata
    {
        public RuntimeTextureImporterSettings? ImporterSettings { get; init; }
        public RuntimeMaterialDocument? Material { get; init; }
        public RuntimeTextStyleDocument? TextStyle { get; init; }
    }

    private sealed record RuntimeTextureImporterSettings
    {
        public RuntimeTextureColorSpace ColorSpace { get; init; }
        public RuntimeTextureSampleMode SampleMode { get; init; }
        public RuntimeTextureAddressMode AddressMode { get; init; }
    }

    private sealed record RuntimeMaterialDocument
    {
        public RuntimeMaterialShaderFamily ShaderFamily { get; init; }
        public Guid? BaseColorTextureId { get; init; }
        public Guid? NormalTextureId { get; init; }
        public Guid? MetalRoughTextureId { get; init; }
        public Guid? OcclusionTextureId { get; init; }
        public Guid? EmissionTextureId { get; init; }
        public RuntimeColor ColorTint { get; init; } = new(1, 1, 1, 1);
        public double Metallic { get; init; }
        public double Roughness { get; init; } = 1;
        public double EmissionFactor { get; init; }
        public RuntimeVector2 UvScale { get; init; } = new(1, 1);
        public RuntimeVector2 UvOffset { get; init; }
        public RuntimeMaterialTransparency Transparency { get; init; }
        public double AlphaCutoff { get; init; } = 0.5;
        public RuntimeMaterialCullMode Cull { get; init; }
        public bool DepthWrite { get; init; } = true;
        public RuntimeMaterialDepthTest DepthTest { get; init; }
        public int QueueOffset { get; init; }
    }

    private sealed record RuntimeTextStyleDocument
    {
        public Guid? FontAssetId { get; init; }
        public double CharacterHeight { get; init; } = 0.035;
        public RuntimeColor Color { get; init; } = new(1, 1, 1, 1);
        public Guid? MaterialAssetId { get; init; }
    }

    private readonly record struct RuntimeVector2(double X, double Y);
    private readonly record struct RuntimeColor(double R, double G, double B, double A);
    private enum RuntimeTextureColorSpace { Srgb, Linear }
    private enum RuntimeTextureSampleMode { Linear, Point, Anisotropic }
    private enum RuntimeTextureAddressMode { Wrap, Clamp, Mirror }
    private enum RuntimeMaterialShaderFamily { Standard, Unlit }
    private enum RuntimeMaterialTransparency { Opaque, Cutout, Blend, Additive }
    private enum RuntimeMaterialCullMode { Back, Front, None }
    private enum RuntimeMaterialDepthTest { Less, LessOrEqual, Always }
}
