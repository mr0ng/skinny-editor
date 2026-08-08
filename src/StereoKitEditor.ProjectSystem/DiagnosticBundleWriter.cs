using System.Text.Json;

namespace StereoKitEditor.ProjectSystem;

public sealed record DiagnosticBundleInput(
    Guid ProjectId,
    string ProjectName,
    string ProjectDefinitionPath,
    string RuntimeMode,
    string Reason,
    string ProfileId,
    string? BuildId,
    int? ExitCode,
    string SceneJson,
    IReadOnlyList<string> LogLines,
    IReadOnlyList<string> EnvironmentVariableNames,
    DateTimeOffset OccurredAt);

public sealed class DiagnosticBundleWriter(string? root = null, int retainedBundleCount = 10)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Root { get; } = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SKinnyEditor",
        "diagnostics"));
    public int RetainedBundleCount { get; } = Math.Max(1, retainedBundleCount);

    public async Task<string> WriteAsync(
        DiagnosticBundleInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var projectDirectory = Path.Combine(Root, input.ProjectId.ToString("N"));
        Directory.CreateDirectory(projectDirectory);
        var bundleName = $"{input.OccurredAt:yyyyMMdd-HHmmss-fff}-{Sanitize(input.RuntimeMode)}-{Guid.NewGuid():N}";
        var staging = Path.Combine(projectDirectory, $".staging-{Guid.NewGuid():N}");
        var destination = Path.Combine(projectDirectory, bundleName);
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = new
            {
                formatVersion = 1,
                input.ProjectId,
                input.ProjectName,
                projectDefinitionPath = Path.GetFullPath(input.ProjectDefinitionPath),
                input.RuntimeMode,
                input.Reason,
                input.ProfileId,
                input.BuildId,
                input.ExitCode,
                input.OccurredAt,
                environmentVariableNames = input.EnvironmentVariableNames
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                note = "Environment variable values are intentionally excluded.",
            };
            await File.WriteAllTextAsync(
                Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(staging, "scene.skscene.json"),
                input.SceneJson,
                cancellationToken);
            await File.WriteAllLinesAsync(
                Path.Combine(staging, "runtime.log"),
                input.LogLines.TakeLast(500),
                cancellationToken);
            Directory.Move(staging, destination);
            Prune(projectDirectory);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private void Prune(string projectDirectory)
    {
        var projectRoot = Path.GetFullPath(projectDirectory) + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(projectDirectory)
                     .Where(path => !Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal))
                     .OrderByDescending(Directory.GetCreationTimeUtc)
                     .Skip(RetainedBundleCount))
        {
            var fullPath = Path.GetFullPath(directory);
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Diagnostic bundle retention resolved outside its project directory.");
            }

            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "runtime" : result.ToLowerInvariant();
    }
}
