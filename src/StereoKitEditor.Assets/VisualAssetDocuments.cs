using System.Text.Json.Serialization;

namespace StereoKitEditor.Assets;

[JsonConverter(typeof(JsonStringEnumConverter<TextureColorSpace>))]
public enum TextureColorSpace { Srgb, Linear }

[JsonConverter(typeof(JsonStringEnumConverter<TextureUsage>))]
public enum TextureUsage { Color, Ui, Normal, MetalRough, Occlusion, Emission, Data }

[JsonConverter(typeof(JsonStringEnumConverter<TextureSampleMode>))]
public enum TextureSampleMode { Linear, Point, Anisotropic }

[JsonConverter(typeof(JsonStringEnumConverter<TextureAddressMode>))]
public enum TextureAddressMode { Wrap, Clamp, Mirror }

[JsonConverter(typeof(JsonStringEnumConverter<TextureMipmapMode>))]
public enum TextureMipmapMode { Auto, On, Off }

[JsonConverter(typeof(JsonStringEnumConverter<TextureAlphaHint>))]
public enum TextureAlphaHint { Auto, Opaque, Transparent }

[JsonConverter(typeof(JsonStringEnumConverter<MaterialShaderFamily>))]
public enum MaterialShaderFamily { Standard, Unlit }

[JsonConverter(typeof(JsonStringEnumConverter<MaterialTransparency>))]
public enum MaterialTransparency { Opaque, Cutout, Blend, Additive }

[JsonConverter(typeof(JsonStringEnumConverter<MaterialCullMode>))]
public enum MaterialCullMode { Back, Front, None }

[JsonConverter(typeof(JsonStringEnumConverter<MaterialDepthTest>))]
public enum MaterialDepthTest { Less, LessOrEqual, Always }

public readonly record struct AssetVector2(double X, double Y)
{
    public static AssetVector2 Zero => new(0, 0);
    public static AssetVector2 One => new(1, 1);
}

public readonly record struct AssetColor(double R, double G, double B, double A)
{
    public static AssetColor White => new(1, 1, 1, 1);
}

public sealed record TextureAssetMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double AspectRatio => Height > 0 ? Width / (double)Height : 1;
    public bool? HasAlpha { get; init; }
    public string SourceFormat { get; init; } = string.Empty;
}

public sealed record FontAssetMetadata
{
    public string FamilyName { get; init; } = string.Empty;
    public string SourceFormat { get; init; } = "TrueType";
}

public sealed record ModelMaterialSlot(int Index, string Name);

public sealed record ModelAssetMetadata
{
    public IReadOnlyList<ModelMaterialSlot> MaterialSlots { get; init; } = [];
}

public sealed record MaterialAssetDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Guid AssetId { get; init; }
    public MaterialShaderFamily ShaderFamily { get; init; } = MaterialShaderFamily.Standard;
    public Guid? BaseColorTextureId { get; init; }
    public Guid? NormalTextureId { get; init; }
    public Guid? MetalRoughTextureId { get; init; }
    public Guid? OcclusionTextureId { get; init; }
    public Guid? EmissionTextureId { get; init; }
    public AssetColor ColorTint { get; init; } = AssetColor.White;
    public double Metallic { get; init; }
    public double Roughness { get; init; } = 1;
    public double EmissionFactor { get; init; }
    public AssetVector2 UvScale { get; init; } = AssetVector2.One;
    public AssetVector2 UvOffset { get; init; } = AssetVector2.Zero;
    public MaterialTransparency Transparency { get; init; }
    public double AlphaCutoff { get; init; } = 0.5;
    public MaterialCullMode Cull { get; init; } = MaterialCullMode.Back;
    public bool DepthWrite { get; init; } = true;
    public MaterialDepthTest DepthTest { get; init; } = MaterialDepthTest.Less;
    public int QueueOffset { get; init; }

    public IReadOnlyList<Guid> TextureDependencies() =>
        new[]
        {
            BaseColorTextureId,
            NormalTextureId,
            MetalRoughTextureId,
            OcclusionTextureId,
            EmissionTextureId,
        }
        .Where(id => id is { } value && value != Guid.Empty)
        .Select(id => id!.Value)
        .Distinct()
        .ToArray();
}

public sealed record TextStyleAssetDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Guid AssetId { get; init; }
    public Guid? FontAssetId { get; init; }
    public double CharacterHeight { get; init; } = 0.035;
    public AssetColor Color { get; init; } = AssetColor.White;
    public Guid? MaterialAssetId { get; init; }

    public IReadOnlyList<Guid> Dependencies() => new[] { FontAssetId, MaterialAssetId }
        .Where(id => id is { } value && value != Guid.Empty)
        .Select(id => id!.Value)
        .Distinct()
        .ToArray();
}
