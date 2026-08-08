using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StereoKitEditor.Assets;

public enum AssetKind
{
    Model,
    Texture,
    Font,
    Material,
    TextStyle,
}

public enum AssetDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record AssetDiagnostic(
    AssetDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? SuggestedAction = null);

public sealed record AssetBounds(
    double CenterX,
    double CenterY,
    double CenterZ,
    double SizeX,
    double SizeY,
    double SizeZ)
{
    public double LargestDimension => Math.Max(SizeX, Math.Max(SizeY, SizeZ));
}

public sealed record AssetImporterSettings
{
    public double Scale { get; init; } = 1;
    public bool GenerateThumbnail { get; init; } = true;
    public TextureColorSpace ColorSpace { get; init; } = TextureColorSpace.Srgb;
    public TextureUsage TextureUsage { get; init; } = TextureUsage.Color;
    public TextureSampleMode SampleMode { get; init; } = TextureSampleMode.Linear;
    public TextureAddressMode AddressMode { get; init; } = TextureAddressMode.Wrap;
    public TextureMipmapMode GenerateMipmaps { get; init; } = TextureMipmapMode.Auto;
    public TextureAlphaHint AlphaHint { get; init; } = TextureAlphaHint.Auto;
}

public sealed record AssetMetadata
{
    public const int CurrentFormatVersion = 2;
    public const string GlbImporterId = "skinny.glb";
    public const int GlbImporterVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Guid AssetId { get; init; } = Guid.NewGuid();
    public AssetKind Kind { get; init; } = AssetKind.Model;
    public string SourcePath { get; init; } = string.Empty;
    public string NormalizedPathKey { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public long SourceLength { get; init; }
    public long SourceLastWriteUtcTicks { get; init; }
    public string ImporterId { get; init; } = GlbImporterId;
    public int ImporterVersion { get; init; } = GlbImporterVersion;
    public AssetImporterSettings ImporterSettings { get; init; } = new();
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<Guid> AssetDependencies { get; init; } = [];
    public IReadOnlyList<string> GeneratedArtifactKeys { get; init; } = [];
    public IReadOnlyList<AssetDiagnostic> Diagnostics { get; init; } = [];
    public string ThumbnailCacheKey { get; init; } = string.Empty;
    public AssetBounds? Bounds { get; init; }
    public ModelAssetMetadata? Model { get; init; }
    public TextureAssetMetadata? Texture { get; init; }
    public FontAssetMetadata? Font { get; init; }
    public MaterialAssetDocument? Material { get; init; }
    public TextStyleAssetDocument? TextStyle { get; init; }
}

public sealed record AssetRecord(
    AssetMetadata Metadata,
    string SourceFullPath,
    string MetadataFullPath,
    string ThumbnailFullPath)
{
    public bool HasErrors => Metadata.Diagnostics.Any(item => item.Severity == AssetDiagnosticSeverity.Error);
}

public sealed record TrashedAssetRecord(
    Guid AssetId,
    string OriginalRelativePath,
    string TrashDirectory);

public sealed class AssetDatabase
{
    private static readonly AssetImporterRegistry Importers = AssetImporterRegistry.CreateDefault();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Guid _projectId;
    private readonly string _projectDirectory;
    private readonly string _assetsRoot;
    private readonly string _cacheRoot;
    private IReadOnlyList<AssetRecord> _records = [];

    public AssetDatabase(
        Guid projectId,
        string projectDirectory,
        string assetsRoot,
        string? cacheRoot = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project ID must not be empty.", nameof(projectId));
        }

        _projectId = projectId;
        _projectDirectory = Path.GetFullPath(projectDirectory);
        _assetsRoot = ResolveUnder(_projectDirectory, assetsRoot, "Assets root");
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SKinnyEditor",
            "asset-cache"));
    }

    public string AssetsRoot => _assetsRoot;
    public IReadOnlyList<AssetRecord> Records => _records;
    public IReadOnlyList<string> SupportedExtensions => Importers.SupportedExtensions;

    public AssetRecord? Find(Guid assetId) =>
        _records.FirstOrDefault(record => record.Metadata.AssetId == assetId);

    public async Task<IReadOnlyList<AssetRecord>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_assetsRoot);
        var records = new List<AssetRecord>();
        var ids = new HashSet<Guid>();
        foreach (var sourcePath in Directory.EnumerateFiles(_assetsRoot, "*", SearchOption.AllDirectories)
                     .Where(path => Importers.Find(path) is not null)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ImportRecordAsync(sourcePath, cancellationToken);
            if (!ids.Add(record.Metadata.AssetId))
            {
                record = await ReassignDuplicateIdAsync(record, cancellationToken);
                ids.Add(record.Metadata.AssetId);
            }

            records.Add(record);
        }

        records = [.. await ValidateAssetDependenciesAsync(records, cancellationToken)];
        _records = records;
        return _records;
    }

    public IReadOnlyList<AssetRecord> FindDependents(Guid assetId, bool transitive = false)
    {
        var result = new List<AssetRecord>();
        var visited = new HashSet<Guid> { assetId };
        var frontier = new Queue<Guid>();
        frontier.Enqueue(assetId);
        while (frontier.Count > 0)
        {
            var target = frontier.Dequeue();
            foreach (var dependent in _records.Where(record => record.Metadata.AssetDependencies.Contains(target)))
            {
                if (!visited.Add(dependent.Metadata.AssetId))
                {
                    continue;
                }

                result.Add(dependent);
                if (transitive)
                {
                    frontier.Enqueue(dependent.Metadata.AssetId);
                }
            }

            if (!transitive)
            {
                break;
            }
        }

        return result;
    }

    public async Task<AssetRecord> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The asset file to import does not exist.", sourcePath);
        }

        if (Importers.Find(sourcePath) is null)
        {
            throw new InvalidDataException(
                $"Unsupported asset type. Supported extensions: {string.Join(", ", SupportedExtensions)}");
        }

        Directory.CreateDirectory(_assetsRoot);
        string destination;
        if (IsUnder(_assetsRoot, sourcePath))
        {
            destination = sourcePath;
        }
        else
        {
            destination = UniqueDestination(Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destination, overwrite: false);
        }

        await RefreshAsync(cancellationToken);
        return _records.Single(record =>
            string.Equals(record.SourceFullPath, destination, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AssetRecord> MoveAsync(
        Guid assetId,
        string newRelativePath,
        CancellationToken cancellationToken = default)
    {
        var record = Find(assetId) ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
        var sourceImporter = Importers.Find(record.SourceFullPath)
            ?? throw new InvalidOperationException("The asset no longer has a registered importer.");
        var destinationImporter = Importers.Find(newRelativePath);
        if (destinationImporter is null || destinationImporter.Id != sourceImporter.Id)
        {
            throw new InvalidDataException(
                $"The asset must retain a compatible {record.Metadata.Kind} extension.");
        }

        var destination = ResolveUnder(_assetsRoot, newRelativePath, "Asset destination");
        if (string.Equals(destination, record.SourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return record;
        }

        if (File.Exists(destination))
        {
            throw new IOException($"The destination asset already exists: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var destinationMetadata = MetadataPath(destination);
        File.Move(record.SourceFullPath, destination);
        if (File.Exists(record.MetadataFullPath))
        {
            File.Move(record.MetadataFullPath, destinationMetadata);
        }

        await RefreshAsync(cancellationToken);
        return Find(assetId) ?? throw new InvalidOperationException("The moved asset lost its stable ID.");
    }

    public string CreateFolder(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("The asset folder name cannot be empty.");
        }

        var folder = ResolveUnder(_assetsRoot, relativePath.Trim(), "Asset folder");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public async Task<TrashedAssetRecord> DeleteAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var record = Find(assetId) ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
        cancellationToken.ThrowIfCancellationRequested();

        var trashDirectory = Path.Combine(
            _projectDirectory,
            ".skinny",
            "Trash",
            "Assets",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{assetId:N}");
        var trashedSource = Path.Combine(trashDirectory, record.Metadata.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var trashedMetadata = trashedSource + ".skmeta";
        Directory.CreateDirectory(Path.GetDirectoryName(trashedSource)!);

        var sourceMoved = false;
        try
        {
            File.Move(record.SourceFullPath, trashedSource);
            sourceMoved = true;
            if (File.Exists(record.MetadataFullPath))
            {
                File.Move(record.MetadataFullPath, trashedMetadata);
            }
        }
        catch
        {
            if (sourceMoved && File.Exists(trashedSource) && !File.Exists(record.SourceFullPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(record.SourceFullPath)!);
                File.Move(trashedSource, record.SourceFullPath);
            }

            throw;
        }

        await RefreshAsync(cancellationToken);
        return new(assetId, record.Metadata.SourcePath, trashDirectory);
    }

    public async Task<AssetRecord> CreateMaterialAsync(
        string relativePath,
        MaterialAssetDocument? document = null,
        CancellationToken cancellationToken = default)
    {
        relativePath = EnsureAuthoredExtension(relativePath, ".skmaterial.json");
        var destination = ResolveUnder(_assetsRoot, relativePath, "Material destination");
        if (File.Exists(destination))
        {
            throw new IOException($"The Material asset already exists: {destination}");
        }

        var assetId = document?.AssetId is { } requested && requested != Guid.Empty ? requested : Guid.NewGuid();
        await WriteAuthoredAssetAsync(
            destination,
            (document ?? new MaterialAssetDocument()) with { AssetId = assetId },
            cancellationToken);
        await WriteInitialMetadataAsync(destination, assetId, AssetKind.Material, cancellationToken);
        await RefreshAsync(cancellationToken);
        return Find(assetId) ?? throw new InvalidOperationException("The new Material was not cataloged.");
    }

    public async Task<AssetRecord> UpdateMaterialAsync(
        Guid assetId,
        MaterialAssetDocument document,
        CancellationToken cancellationToken = default)
    {
        var record = RequireKind(assetId, AssetKind.Material);
        await WriteAuthoredAssetAsync(record.SourceFullPath, document with { AssetId = assetId }, cancellationToken);
        await RefreshAsync(cancellationToken);
        return Find(assetId) ?? throw new InvalidOperationException("The updated Material was not cataloged.");
    }

    public async Task<AssetRecord> CreateTextStyleAsync(
        string relativePath,
        TextStyleAssetDocument? document = null,
        CancellationToken cancellationToken = default)
    {
        relativePath = EnsureAuthoredExtension(relativePath, ".sktextstyle.json");
        var destination = ResolveUnder(_assetsRoot, relativePath, "Text Style destination");
        if (File.Exists(destination))
        {
            throw new IOException($"The Text Style asset already exists: {destination}");
        }

        var assetId = document?.AssetId is { } requested && requested != Guid.Empty ? requested : Guid.NewGuid();
        await WriteAuthoredAssetAsync(
            destination,
            (document ?? new TextStyleAssetDocument()) with { AssetId = assetId },
            cancellationToken);
        await WriteInitialMetadataAsync(destination, assetId, AssetKind.TextStyle, cancellationToken);
        await RefreshAsync(cancellationToken);
        return Find(assetId) ?? throw new InvalidOperationException("The new Text Style was not cataloged.");
    }

    public async Task<AssetRecord> UpdateTextStyleAsync(
        Guid assetId,
        TextStyleAssetDocument document,
        CancellationToken cancellationToken = default)
    {
        var record = RequireKind(assetId, AssetKind.TextStyle);
        await WriteAuthoredAssetAsync(record.SourceFullPath, document with { AssetId = assetId }, cancellationToken);
        await RefreshAsync(cancellationToken);
        return Find(assetId) ?? throw new InvalidOperationException("The updated Text Style was not cataloged.");
    }

    public async Task<AssetRecord> UpdateImporterSettingsAsync(
        Guid assetId,
        AssetImporterSettings settings,
        CancellationToken cancellationToken = default)
    {
        var record = Find(assetId) ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
        await WriteMetadataAsync(record.MetadataFullPath, record.Metadata with
        {
            ImporterSettings = settings,
            ImporterVersion = 0,
        }, cancellationToken);
        await RefreshAsync(cancellationToken);
        return Find(assetId) ?? throw new InvalidOperationException("The updated asset was not cataloged.");
    }

    private async Task<AssetRecord> ImportRecordAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var importer = Importers.Find(sourcePath)
            ?? throw new InvalidDataException($"No importer is registered for '{sourcePath}'.");
        var metadataPath = MetadataPath(sourcePath);
        var prior = await TryReadMetadataAsync(metadataPath, cancellationToken);
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(_assetsRoot, sourcePath));
        var sourceInfo = new FileInfo(sourcePath);
        var canReuseImport = prior is
        {
            ContentHash.Length: > 0,
            ImporterId: var priorImporterId,
            ImporterVersion: var priorImporterVersion,
        }
            && string.Equals(priorImporterId, importer.Id, StringComparison.Ordinal)
            && priorImporterVersion == importer.Version
            && prior.SourceLength == sourceInfo.Length
            && prior.SourceLastWriteUtcTicks == sourceInfo.LastWriteTimeUtc.Ticks;
        var contentHash = canReuseImport
            ? prior!.ContentHash
            : await ComputeHashAsync(sourcePath, cancellationToken);
        var cacheDirectory = Path.Combine(
            _cacheRoot,
            _projectId.ToString("N"),
            contentHash,
            importer.Id.Replace('.', '-'),
            importer.Version.ToString());
        var thumbnailPath = Path.Combine(cacheDirectory, "thumbnail.png");
        var assetId = prior?.AssetId is { } priorId && priorId != Guid.Empty ? priorId : Guid.NewGuid();
        var inspection = await importer.InspectAsync(
            new(
                sourcePath,
                thumbnailPath,
                assetId,
                prior?.ImporterSettings ?? DefaultSettings(importer.Kind),
                prior,
                canReuseImport && File.Exists(thumbnailPath)),
            cancellationToken);
        var diagnostics = inspection.EffectiveDiagnostics.ToList();
        foreach (var dependencyId in inspection.EffectiveAssetDependencies)
        {
            if (dependencyId == assetId)
            {
                diagnostics.Add(new(
                    AssetDiagnosticSeverity.Error,
                    "SKINNY-ASSET-SELF-REFERENCE",
                    "An authored asset cannot reference itself.",
                    "Remove the recursive asset reference."));
            }
        }

        var thumbnailKey = $"{importer.Id}-v{importer.Version}-{contentHash}";
        Directory.CreateDirectory(cacheDirectory);
        if (!File.Exists(thumbnailPath))
        {
            PngThumbnailWriter.Write(thumbnailPath, inspection.Bounds, diagnostics.Any(item =>
                item.Severity == AssetDiagnosticSeverity.Error));
        }

        var metadata = new AssetMetadata
        {
            AssetId = assetId,
            Kind = importer.Kind,
            SourcePath = relativePath,
            NormalizedPathKey = relativePath.ToLowerInvariant(),
            ContentHash = contentHash,
            SourceLength = sourceInfo.Length,
            SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
            ImporterId = importer.Id,
            ImporterVersion = importer.Version,
            ImporterSettings = prior?.ImporterSettings ?? DefaultSettings(importer.Kind),
            Dependencies = inspection.EffectiveSourceDependencies,
            AssetDependencies = inspection.EffectiveAssetDependencies,
            GeneratedArtifactKeys = [thumbnailKey],
            Diagnostics = diagnostics,
            ThumbnailCacheKey = thumbnailKey,
            Bounds = inspection.Bounds,
            Model = inspection.Model,
            Texture = inspection.Texture,
            Font = inspection.Font,
            Material = inspection.Material,
            TextStyle = inspection.TextStyle,
        };
        if (prior is null || !MetadataEquivalent(prior, metadata))
        {
            await WriteMetadataAsync(metadataPath, metadata, cancellationToken);
        }

        return new(metadata, Path.GetFullPath(sourcePath), metadataPath, thumbnailPath);
    }

    private async Task<AssetRecord> ReassignDuplicateIdAsync(
        AssetRecord record,
        CancellationToken cancellationToken)
    {
        var metadata = record.Metadata with
        {
            AssetId = Guid.NewGuid(),
            Diagnostics = record.Metadata.Diagnostics.Concat(
            [
                new AssetDiagnostic(
                    AssetDiagnosticSeverity.Warning,
                    "SKINNY-ASSET-DUPLICATE-ID",
                    "A copied metadata sidecar duplicated another asset ID, so this copy received a new ID."),
            ]).ToArray(),
        };
        await WriteMetadataAsync(record.MetadataFullPath, metadata, cancellationToken);
        return record with { Metadata = metadata };
    }

    private async Task<AssetMetadata?> TryReadMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var metadata = await JsonSerializer.DeserializeAsync<AssetMetadata>(stream, JsonOptions, cancellationToken);
            return metadata?.FormatVersion switch
            {
                AssetMetadata.CurrentFormatVersion => metadata,
                1 => metadata with { FormatVersion = AssetMetadata.CurrentFormatVersion },
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(
        string path,
        AssetMetadata metadata,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool MetadataEquivalent(AssetMetadata first, AssetMetadata second) =>
        string.Equals(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(second, JsonOptions),
            StringComparison.Ordinal);

    private string UniqueDestination(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(_assetsRoot, fileName);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(_assetsRoot, $"{baseName} {suffix}{extension}");
        }

        return candidate;
    }

    private static string MetadataPath(string sourcePath) => sourcePath + ".skmeta";

    private static AssetImporterSettings DefaultSettings(AssetKind kind) => kind == AssetKind.Texture
        ? new AssetImporterSettings
        {
            TextureUsage = TextureUsage.Color,
            ColorSpace = TextureColorSpace.Srgb,
            AddressMode = TextureAddressMode.Wrap,
        }
        : new AssetImporterSettings();

    private AssetRecord RequireKind(Guid assetId, AssetKind kind)
    {
        var record = Find(assetId) ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
        if (record.Metadata.Kind != kind)
        {
            throw new InvalidOperationException($"Asset '{assetId}' is {record.Metadata.Kind}, not {kind}.");
        }

        return record;
    }

    private async Task WriteInitialMetadataAsync(
        string sourcePath,
        Guid assetId,
        AssetKind kind,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(_assetsRoot, sourcePath));
        await WriteMetadataAsync(MetadataPath(sourcePath), new AssetMetadata
        {
            AssetId = assetId,
            Kind = kind,
            SourcePath = relativePath,
            NormalizedPathKey = relativePath.ToLowerInvariant(),
            ImporterSettings = DefaultSettings(kind),
        }, cancellationToken);
    }

    private static async Task WriteAuthoredAssetAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string EnsureAuthoredExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;

    private static async Task<IReadOnlyList<AssetRecord>> ValidateAssetDependenciesAsync(
        IReadOnlyList<AssetRecord> records,
        CancellationToken cancellationToken)
    {
        var byId = records.ToDictionary(record => record.Metadata.AssetId);
        var cycleMembers = FindCycleMembers(records, byId);
        var result = new List<AssetRecord>(records.Count);
        foreach (var record in records)
        {
            var diagnostics = record.Metadata.Diagnostics
                .Where(item => item.Code is not "SKINNY-ASSET-MISSING-ASSET-DEPENDENCY" and not "SKINNY-ASSET-DEPENDENCY-CYCLE")
                .ToList();
            foreach (var dependency in record.Metadata.AssetDependencies.Distinct())
            {
                if (!byId.ContainsKey(dependency))
                {
                    diagnostics.Add(new(
                        AssetDiagnosticSeverity.Error,
                        "SKINNY-ASSET-MISSING-ASSET-DEPENDENCY",
                        $"Referenced asset '{dependency}' was not found.",
                        "Restore the missing asset or choose a replacement in the Inspector."));
                }
            }

            if (cycleMembers.Contains(record.Metadata.AssetId))
            {
                diagnostics.Add(new(
                    AssetDiagnosticSeverity.Error,
                    "SKINNY-ASSET-DEPENDENCY-CYCLE",
                    "This authored asset participates in a dependency cycle.",
                    "Remove one of the circular Material or Text Style references."));
            }

            var updatedMetadata = record.Metadata with { Diagnostics = diagnostics };
            if (!MetadataEquivalent(record.Metadata, updatedMetadata))
            {
                await WriteMetadataAsync(record.MetadataFullPath, updatedMetadata, cancellationToken);
            }

            result.Add(record with { Metadata = updatedMetadata });
        }

        return result;
    }

    private static HashSet<Guid> FindCycleMembers(
        IReadOnlyList<AssetRecord> records,
        IReadOnlyDictionary<Guid, AssetRecord> byId)
    {
        var cycleMembers = new HashSet<Guid>();
        var state = new Dictionary<Guid, int>();
        var stack = new List<Guid>();
        foreach (var record in records)
        {
            Visit(record.Metadata.AssetId);
        }

        return cycleMembers;

        void Visit(Guid id)
        {
            if (state.TryGetValue(id, out var currentState))
            {
                if (currentState == 1)
                {
                    var start = stack.LastIndexOf(id);
                    if (start >= 0)
                    {
                        foreach (var member in stack.Skip(start))
                        {
                            cycleMembers.Add(member);
                        }
                    }
                }

                return;
            }

            state[id] = 1;
            stack.Add(id);
            if (byId.TryGetValue(id, out var record))
            {
                foreach (var dependency in record.Metadata.AssetDependencies.Where(byId.ContainsKey))
                {
                    Visit(dependency);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string ResolveUnder(string root, string relativePath, string label)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{label} must be relative.");
        }

        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsUnder(root, resolved))
        {
            throw new InvalidDataException($"{label} escapes its configured root.");
        }

        return resolved;
    }

    private static bool IsUnder(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
    }
}
