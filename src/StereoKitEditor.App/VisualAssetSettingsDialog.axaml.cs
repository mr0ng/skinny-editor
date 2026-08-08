using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using StereoKitEditor.Assets;

namespace StereoKitEditor.App;

public partial class VisualAssetSettingsDialog : Window
{
    private readonly Model _model;

    public VisualAssetSettingsDialog()
        : this(new(new AssetMetadata(), string.Empty, string.Empty, string.Empty), [])
    {
    }

    public VisualAssetSettingsDialog(AssetRecord asset, IReadOnlyList<AssetRecord> catalog)
    {
        InitializeComponent();
        _model = new Model(asset, catalog);
        DataContext = _model;
    }

    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(null);
    private void HandleSave(object? sender, RoutedEventArgs args) => Close(_model.CreateResult());

    private sealed class Model
    {
        private readonly AssetRecord _asset;
        private readonly MaterialAssetDocument _material;
        private readonly TextStyleAssetDocument _textStyle;

        public Model(AssetRecord asset, IReadOnlyList<AssetRecord> catalog)
        {
            _asset = asset;
            _material = asset.Metadata.Material ?? new MaterialAssetDocument { AssetId = asset.Metadata.AssetId };
            _textStyle = asset.Metadata.TextStyle ?? new TextStyleAssetDocument { AssetId = asset.Metadata.AssetId };
            Title = Path.GetFileName(asset.Metadata.SourcePath);
            Subtitle = $"{asset.Metadata.Kind} · {asset.Metadata.SourcePath}";

            var textures = References(catalog, AssetKind.Texture);
            TextureReferences = textures;
            FontReferences = References(catalog, AssetKind.Font);
            MaterialReferences = References(catalog, AssetKind.Material);
            BaseColorTexture = Find(textures, _material.BaseColorTextureId);
            NormalTexture = Find(textures, _material.NormalTextureId);
            MetalRoughTexture = Find(textures, _material.MetalRoughTextureId);
            OcclusionTexture = Find(textures, _material.OcclusionTextureId);
            EmissionTexture = Find(textures, _material.EmissionTextureId);
            FontReference = Find(FontReferences, _textStyle.FontAssetId);
            MaterialReference = Find(MaterialReferences, _textStyle.MaterialAssetId);

            var settings = asset.Metadata.ImporterSettings;
            ColorSpace = settings.ColorSpace;
            TextureUsage = settings.TextureUsage;
            SampleMode = settings.SampleMode;
            AddressMode = settings.AddressMode;
            MipmapMode = settings.GenerateMipmaps;
            AlphaHint = settings.AlphaHint;
            ShaderFamily = _material.ShaderFamily;
            MaterialColor = ToColor(_material.ColorTint);
            Metallic = (decimal)_material.Metallic;
            Roughness = (decimal)_material.Roughness;
            EmissionFactor = (decimal)_material.EmissionFactor;
            UvScaleX = (decimal)_material.UvScale.X;
            UvScaleY = (decimal)_material.UvScale.Y;
            UvOffsetX = (decimal)_material.UvOffset.X;
            UvOffsetY = (decimal)_material.UvOffset.Y;
            MaterialTransparency = _material.Transparency;
            AlphaCutoff = (decimal)_material.AlphaCutoff;
            CullMode = _material.Cull;
            DepthWrite = _material.DepthWrite;
            DepthTest = _material.DepthTest;
            QueueOffset = _material.QueueOffset;
            CharacterHeight = (decimal)_textStyle.CharacterHeight;
            TextColor = ToColor(_textStyle.Color);
        }

        public string Title { get; }
        public string Subtitle { get; }
        public bool IsTexture => _asset.Metadata.Kind == AssetKind.Texture;
        public bool IsMaterial => _asset.Metadata.Kind == AssetKind.Material;
        public bool IsTextStyle => _asset.Metadata.Kind == AssetKind.TextStyle;
        public Array ColorSpaces => Enum.GetValues<TextureColorSpace>();
        public Array TextureUsages => Enum.GetValues<TextureUsage>();
        public Array SampleModes => Enum.GetValues<TextureSampleMode>();
        public Array AddressModes => Enum.GetValues<TextureAddressMode>();
        public Array MipmapModes => Enum.GetValues<TextureMipmapMode>();
        public Array AlphaHints => Enum.GetValues<TextureAlphaHint>();
        public Array ShaderFamilies => Enum.GetValues<MaterialShaderFamily>();
        public Array Transparencies => Enum.GetValues<MaterialTransparency>();
        public Array CullModes => Enum.GetValues<MaterialCullMode>();
        public Array DepthTests => Enum.GetValues<MaterialDepthTest>();
        public IReadOnlyList<Reference> TextureReferences { get; }
        public IReadOnlyList<Reference> FontReferences { get; }
        public IReadOnlyList<Reference> MaterialReferences { get; }
        public TextureColorSpace ColorSpace { get; set; }
        public TextureUsage TextureUsage { get; set; }
        public TextureSampleMode SampleMode { get; set; }
        public TextureAddressMode AddressMode { get; set; }
        public TextureMipmapMode MipmapMode { get; set; }
        public TextureAlphaHint AlphaHint { get; set; }
        public MaterialShaderFamily ShaderFamily { get; set; }
        public Reference BaseColorTexture { get; set; }
        public Reference NormalTexture { get; set; }
        public Reference MetalRoughTexture { get; set; }
        public Reference OcclusionTexture { get; set; }
        public Reference EmissionTexture { get; set; }
        public Color MaterialColor { get; set; }
        public decimal Metallic { get; set; }
        public decimal Roughness { get; set; }
        public decimal EmissionFactor { get; set; }
        public decimal UvScaleX { get; set; }
        public decimal UvScaleY { get; set; }
        public decimal UvOffsetX { get; set; }
        public decimal UvOffsetY { get; set; }
        public MaterialTransparency MaterialTransparency { get; set; }
        public decimal AlphaCutoff { get; set; }
        public MaterialCullMode CullMode { get; set; }
        public bool DepthWrite { get; set; }
        public MaterialDepthTest DepthTest { get; set; }
        public decimal QueueOffset { get; set; }
        public Reference FontReference { get; set; }
        public decimal CharacterHeight { get; set; }
        public Color TextColor { get; set; }
        public Reference MaterialReference { get; set; }

        public VisualAssetEditResult CreateResult() => new(
            IsTexture ? _asset.Metadata.ImporterSettings with
            {
                ColorSpace = ColorSpace,
                TextureUsage = TextureUsage,
                SampleMode = SampleMode,
                AddressMode = AddressMode,
                GenerateMipmaps = MipmapMode,
                AlphaHint = AlphaHint,
            } : null,
            IsMaterial ? _material with
            {
                ShaderFamily = ShaderFamily,
                BaseColorTextureId = BaseColorTexture.Id,
                NormalTextureId = NormalTexture.Id,
                MetalRoughTextureId = MetalRoughTexture.Id,
                OcclusionTextureId = OcclusionTexture.Id,
                EmissionTextureId = EmissionTexture.Id,
                ColorTint = ToAssetColor(MaterialColor),
                Metallic = (double)Metallic,
                Roughness = (double)Roughness,
                EmissionFactor = (double)EmissionFactor,
                UvScale = new((double)UvScaleX, (double)UvScaleY),
                UvOffset = new((double)UvOffsetX, (double)UvOffsetY),
                Transparency = MaterialTransparency,
                AlphaCutoff = (double)AlphaCutoff,
                Cull = CullMode,
                DepthWrite = DepthWrite,
                DepthTest = DepthTest,
                QueueOffset = (int)QueueOffset,
            } : null,
            IsTextStyle ? _textStyle with
            {
                FontAssetId = FontReference.Id,
                CharacterHeight = (double)CharacterHeight,
                Color = ToAssetColor(TextColor),
                MaterialAssetId = MaterialReference.Id,
            } : null);

        private static IReadOnlyList<Reference> References(IReadOnlyList<AssetRecord> catalog, AssetKind kind) =>
            new[] { new Reference(null, "None") }
                .Concat(catalog.Where(asset => asset.Metadata.Kind == kind)
                    .OrderBy(asset => asset.Metadata.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .Select(asset => new Reference(asset.Metadata.AssetId, asset.Metadata.SourcePath)))
                .ToArray();

        private static Reference Find(IReadOnlyList<Reference> values, Guid? id) =>
            values.FirstOrDefault(value => value.Id == id) ?? values[0];

        private static Color ToColor(AssetColor color) => Color.FromArgb(
            (byte)Math.Round(Math.Clamp(color.A, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.R, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.G, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.B, 0, 1) * 255));

        private static AssetColor ToAssetColor(Color color) => new(
            color.R / 255d, color.G / 255d, color.B / 255d, color.A / 255d);
    }

    public sealed record Reference(Guid? Id, string Label);
}

public sealed record VisualAssetEditResult(
    AssetImporterSettings? TextureSettings,
    MaterialAssetDocument? Material,
    TextStyleAssetDocument? TextStyle);
