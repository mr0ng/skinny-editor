using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace StereoKitEditor.Assets;

internal sealed record AssetImportContext(
    string SourcePath,
    string ThumbnailPath,
    Guid AssetId,
    AssetImporterSettings Settings,
    AssetMetadata? Prior,
    bool ReuseInspection);

internal sealed record AssetInspectionResult(
    AssetBounds? Bounds = null,
    IReadOnlyList<string>? SourceDependencies = null,
    IReadOnlyList<Guid>? AssetDependencies = null,
    IReadOnlyList<AssetDiagnostic>? Diagnostics = null,
    ModelAssetMetadata? Model = null,
    TextureAssetMetadata? Texture = null,
    FontAssetMetadata? Font = null,
    MaterialAssetDocument? Material = null,
    TextStyleAssetDocument? TextStyle = null)
{
    public IReadOnlyList<string> EffectiveSourceDependencies => SourceDependencies ?? [];
    public IReadOnlyList<Guid> EffectiveAssetDependencies => AssetDependencies ?? [];
    public IReadOnlyList<AssetDiagnostic> EffectiveDiagnostics => Diagnostics ?? [];
}

internal interface IAssetImporter
{
    string Id { get; }
    int Version { get; }
    AssetKind Kind { get; }
    IReadOnlySet<string> Extensions { get; }
    Task<AssetInspectionResult> InspectAsync(AssetImportContext context, CancellationToken cancellationToken);
}

internal sealed class AssetImporterRegistry
{
    private readonly IReadOnlyList<IAssetImporter> _importers;

    private AssetImporterRegistry(IReadOnlyList<IAssetImporter> importers) => _importers = importers;

    public static AssetImporterRegistry CreateDefault() => new(
    [
        new GlbAssetImporter(),
        new TextureAssetImporter(),
        new FontAssetImporter(),
        new MaterialAssetImporter(),
        new TextStyleAssetImporter(),
    ]);

    public IAssetImporter? Find(string path)
    {
        var fileName = Path.GetFileName(path);
        return _importers.FirstOrDefault(importer => importer.Extensions.Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<string> SupportedExtensions => _importers
        .SelectMany(importer => importer.Extensions)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

internal sealed class GlbAssetImporter : IAssetImporter
{
    public string Id => AssetMetadata.GlbImporterId;
    public int Version => AssetMetadata.GlbImporterVersion;
    public AssetKind Kind => AssetKind.Model;
    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".glb" };

    public async Task<AssetInspectionResult> InspectAsync(
        AssetImportContext context,
        CancellationToken cancellationToken)
    {
        if (context.ReuseInspection && context.Prior is { } prior)
        {
            return new(
                prior.Bounds,
                prior.Dependencies,
                prior.AssetDependencies,
                prior.Diagnostics.Where(item => item.Code != "SKINNY-ASSET-MISSING-DEPENDENCY").ToArray(),
                prior.Model);
        }

        var diagnostics = new List<AssetDiagnostic>();
        GlbInspection inspection;
        try
        {
            await using var stream = File.Open(context.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            inspection = GlbMetadataReader.Inspect(stream);
            diagnostics.AddRange(inspection.Diagnostics);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or EndOfStreamException)
        {
            inspection = new(null, [], []);
            diagnostics.Add(new(
                AssetDiagnosticSeverity.Error,
                "SKINNY-ASSET-GLB-INVALID",
                exception.Message,
                "Export the model as a valid glTF 2.0 binary (.glb) and refresh the Project panel."));
        }

        foreach (var dependency in inspection.Dependencies)
        {
            var dependencyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(context.SourcePath)!, dependency));
            if (!File.Exists(dependencyPath))
            {
                diagnostics.Add(new(
                    AssetDiagnosticSeverity.Error,
                    "SKINNY-ASSET-MISSING-DEPENDENCY",
                    $"Model dependency '{dependency}' was not found.",
                    "Restore the referenced file beside the model or re-export as a self-contained GLB."));
            }
        }

        PngThumbnailWriter.Write(
            context.ThumbnailPath,
            inspection.Bounds,
            diagnostics.Any(item => item.Severity == AssetDiagnosticSeverity.Error));
        return new(inspection.Bounds, inspection.Dependencies, [], diagnostics, inspection.Model);
    }
}

internal sealed class TextureAssetImporter : IAssetImporter
{
    private static readonly HashSet<string> MandatoryDecodedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    public string Id => "skinny.texture";
    public int Version => 1;
    public AssetKind Kind => AssetKind.Texture;
    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".gif", ".hdr", ".pic", ".ktx2",
    };

    public Task<AssetInspectionResult> InspectAsync(
        AssetImportContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ReuseInspection && context.Prior?.Texture is { } priorTexture)
        {
            return Task.FromResult(new AssetInspectionResult(
                AssetDependencies: context.Prior.AssetDependencies,
                Diagnostics: context.Prior.Diagnostics,
                Texture: priorTexture));
        }

        var diagnostics = new List<AssetDiagnostic>();
        TextureAssetMetadata metadata;
        using var bitmap = SKBitmap.Decode(context.SourcePath);
        if (bitmap is not null)
        {
            metadata = new()
            {
                Width = bitmap.Width,
                Height = bitmap.Height,
                HasAlpha = bitmap.AlphaType != SKAlphaType.Opaque,
                SourceFormat = Path.GetExtension(context.SourcePath).TrimStart('.').ToUpperInvariant(),
            };
            WriteTextureThumbnail(bitmap, context.ThumbnailPath);
        }
        else if (TryReadKtx2Header(context.SourcePath, out var width, out var height))
        {
            metadata = new()
            {
                Width = width,
                Height = height,
                SourceFormat = "KTX2",
            };
            diagnostics.Add(new(
                AssetDiagnosticSeverity.Info,
                "SKINNY-ASSET-TEXTURE-PREVIEW-UNAVAILABLE",
                "The KTX2 texture is valid enough to catalog, but the desktop thumbnail decoder cannot preview it.",
                "Validate the texture in Scene or use PNG/JPEG while authoring if a thumbnail is important."));
            PngThumbnailWriter.Write(context.ThumbnailPath, null, hasError: false);
        }
        else
        {
            metadata = new()
            {
                Width = 1,
                Height = 1,
                SourceFormat = Path.GetExtension(context.SourcePath).TrimStart('.').ToUpperInvariant(),
            };
            var mandatory = MandatoryDecodedExtensions.Contains(Path.GetExtension(context.SourcePath));
            diagnostics.Add(new(
                mandatory ? AssetDiagnosticSeverity.Error : AssetDiagnosticSeverity.Warning,
                mandatory ? "SKINNY-ASSET-TEXTURE-INVALID" : "SKINNY-ASSET-TEXTURE-PREVIEW-UNAVAILABLE",
                mandatory
                    ? "The image could not be decoded as a supported PNG or JPEG."
                    : "StereoKit may load this texture format, but the desktop importer could not inspect or preview it.",
                mandatory
                    ? "Replace the file with a valid image and refresh assets."
                    : "Validate it in Scene, or convert it to PNG/JPEG for complete editor metadata."));
            PngThumbnailWriter.Write(context.ThumbnailPath, null, mandatory);
        }

        if (context.Settings.TextureUsage is TextureUsage.Normal or TextureUsage.MetalRough or TextureUsage.Occlusion or TextureUsage.Data
            && context.Settings.ColorSpace == TextureColorSpace.Srgb)
        {
            diagnostics.Add(new(
                AssetDiagnosticSeverity.Warning,
                "SKINNY-ASSET-TEXTURE-COLORSPACE",
                $"{context.Settings.TextureUsage} textures normally use Linear color space.",
                "Change Color Space to Linear unless the source was intentionally authored as display color."));
        }

        return Task.FromResult(new AssetInspectionResult(Diagnostics: diagnostics, Texture: metadata));
    }

    private static void WriteTextureThumbnail(SKBitmap source, string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        const int maximum = 192;
        var scale = Math.Min(1, maximum / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = source.Resize(new SKImageInfo(width, height), SKFilterQuality.Medium) ?? source.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static bool TryReadKtx2Header(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!string.Equals(Path.GetExtension(path), ".ktx2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[28];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length
            || !header[..12].SequenceEqual(new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return false;
        }

        width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]));
        height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]));
        return width > 0 && height > 0;
    }
}

internal sealed class FontAssetImporter : IAssetImporter
{
    public string Id => "skinny.font";
    public int Version => 1;
    public AssetKind Kind => AssetKind.Font;
    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ttf" };

    public Task<AssetInspectionResult> InspectAsync(AssetImportContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ReuseInspection && context.Prior?.Font is { } priorFont)
        {
            return Task.FromResult(new AssetInspectionResult(
                AssetDependencies: context.Prior.AssetDependencies,
                Diagnostics: context.Prior.Diagnostics,
                Font: priorFont));
        }

        Span<byte> signature = stackalloc byte[4];
        using var stream = File.OpenRead(context.SourcePath);
        var valid = stream.Read(signature) == signature.Length
            && (signature.SequenceEqual(new byte[] { 0, 1, 0, 0 })
                || signature.SequenceEqual("true"u8)
                || signature.SequenceEqual("typ1"u8));
        var diagnostics = valid
            ? Array.Empty<AssetDiagnostic>()
            :
            [
                new AssetDiagnostic(
                    AssetDiagnosticSeverity.Error,
                    "SKINNY-ASSET-FONT-INVALID",
                    "The file does not have a recognized TrueType font signature.",
                    "Import a valid .ttf font file."),
            ];
        PngThumbnailWriter.Write(context.ThumbnailPath, null, !valid);
        return Task.FromResult(new AssetInspectionResult(
            Diagnostics: diagnostics,
            Font: new FontAssetMetadata { FamilyName = Path.GetFileNameWithoutExtension(context.SourcePath) }));
    }
}

internal abstract class JsonAuthoredAssetImporter<TDocument> : IAssetImporter where TDocument : class
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public abstract string Id { get; }
    public abstract int Version { get; }
    public abstract AssetKind Kind { get; }
    public abstract IReadOnlySet<string> Extensions { get; }

    public async Task<AssetInspectionResult> InspectAsync(AssetImportContext context, CancellationToken cancellationToken)
    {
        if (context.ReuseInspection && Reuse(context.Prior) is { } reused)
        {
            return reused;
        }

        try
        {
            await using var stream = File.OpenRead(context.SourcePath);
            var document = await JsonSerializer.DeserializeAsync<TDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("The authored asset file was empty.");
            var result = Validate(document, context.AssetId);
            PngThumbnailWriter.Write(context.ThumbnailPath, null, result.EffectiveDiagnostics.Any(item =>
                item.Severity == AssetDiagnosticSeverity.Error));
            return result;
        }
        catch (JsonException exception)
        {
            PngThumbnailWriter.Write(context.ThumbnailPath, null, hasError: true);
            return new AssetInspectionResult(Diagnostics:
            [
                new AssetDiagnostic(
                    AssetDiagnosticSeverity.Error,
                    "SKINNY-ASSET-AUTHORED-INVALID",
                    exception.Message,
                    "Repair the JSON asset in the Inspector or restore it from source control."),
            ]);
        }
    }

    protected abstract AssetInspectionResult? Reuse(AssetMetadata? prior);
    protected abstract AssetInspectionResult Validate(TDocument document, Guid sidecarAssetId);
}

internal sealed class MaterialAssetImporter : JsonAuthoredAssetImporter<MaterialAssetDocument>
{
    public override string Id => "skinny.material";
    public override int Version => 1;
    public override AssetKind Kind => AssetKind.Material;
    public override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".skmaterial.json" };

    protected override AssetInspectionResult? Reuse(AssetMetadata? prior) => prior?.Material is { } material
        ? new(AssetDependencies: prior.AssetDependencies, Diagnostics: prior.Diagnostics, Material: material)
        : null;

    protected override AssetInspectionResult Validate(MaterialAssetDocument document, Guid sidecarAssetId)
    {
        var diagnostics = new List<AssetDiagnostic>();
        if (document.FormatVersion != MaterialAssetDocument.CurrentFormatVersion)
        {
            diagnostics.Add(new(AssetDiagnosticSeverity.Error, "SKINNY-ASSET-MATERIAL-VERSION",
                $"Material format {document.FormatVersion} is not supported.", "Open it with a compatible editor version."));
        }

        if (document.AssetId != sidecarAssetId)
        {
            diagnostics.Add(new(AssetDiagnosticSeverity.Error, "SKINNY-ASSET-ID-MISMATCH",
                "The Material asset ID does not match its metadata sidecar.", "Use the Material Inspector repair action."));
        }

        if (document.Metallic is < 0 or > 1 || document.Roughness is < 0 or > 1
            || document.AlphaCutoff is < 0 or > 1 || document.QueueOffset is < -100 or > 100)
        {
            diagnostics.Add(new(AssetDiagnosticSeverity.Error, "SKINNY-ASSET-MATERIAL-RANGE",
                "One or more Material values are outside their safe range.", "Clamp metallic, roughness, alpha cutoff, and queue offset in the Inspector."));
        }

        return new(AssetDependencies: document.TextureDependencies(), Diagnostics: diagnostics, Material: document);
    }
}

internal sealed class TextStyleAssetImporter : JsonAuthoredAssetImporter<TextStyleAssetDocument>
{
    public override string Id => "skinny.text-style";
    public override int Version => 1;
    public override AssetKind Kind => AssetKind.TextStyle;
    public override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".sktextstyle.json" };

    protected override AssetInspectionResult? Reuse(AssetMetadata? prior) => prior?.TextStyle is { } style
        ? new(AssetDependencies: prior.AssetDependencies, Diagnostics: prior.Diagnostics, TextStyle: style)
        : null;

    protected override AssetInspectionResult Validate(TextStyleAssetDocument document, Guid sidecarAssetId)
    {
        var diagnostics = new List<AssetDiagnostic>();
        if (document.FormatVersion != TextStyleAssetDocument.CurrentFormatVersion)
        {
            diagnostics.Add(new(AssetDiagnosticSeverity.Error, "SKINNY-ASSET-TEXTSTYLE-VERSION",
                $"Text Style format {document.FormatVersion} is not supported.", "Open it with a compatible editor version."));
        }

        if (document.AssetId != sidecarAssetId)
        {
            diagnostics.Add(new(AssetDiagnosticSeverity.Error, "SKINNY-ASSET-ID-MISMATCH",
                "The Text Style asset ID does not match its metadata sidecar.", "Use the Text Style Inspector repair action."));
        }

        if (document.CharacterHeight is <= 0 or > 10)
        {
            diagnostics.Add(new(AssetDiagnosticSeverity.Error, "SKINNY-ASSET-TEXTSTYLE-SIZE",
                "Character height must be greater than zero and at most 10 meters.", "Correct Character Height in the Inspector."));
        }

        return new(AssetDependencies: document.Dependencies(), Diagnostics: diagnostics, TextStyle: document);
    }
}
