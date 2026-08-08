using System.Text.Json;

namespace StereoKitEditor.App.Services;

public sealed class WorkspaceTrustService(string? settingsPath = null)
{
    private readonly string _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SKinnyEditor",
        "workspace-trust.json"));

    public bool IsTrusted(Guid projectId, string projectLocation)
    {
        var key = CreateKey(projectId, projectLocation);
        return Load().TrustedWorkspaces.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    public async Task TrustAsync(
        Guid projectId,
        string projectLocation,
        CancellationToken cancellationToken = default)
    {
        var settings = Load();
        var key = CreateKey(projectId, projectLocation);
        if (settings.TrustedWorkspaces.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        settings.TrustedWorkspaces.Add(key);
        settings.TrustedWorkspaces.Sort(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine,
                cancellationToken);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task RevokeAsync(
        Guid projectId,
        string projectLocation,
        CancellationToken cancellationToken = default)
    {
        var settings = Load();
        var removed = settings.TrustedWorkspaces.RemoveAll(key => string.Equals(
            key,
            CreateKey(projectId, projectLocation),
            StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                _settingsPath,
                JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine,
                cancellationToken);
        }
    }

    private TrustSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<TrustSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string CreateKey(Guid projectId, string projectLocation) =>
        $"{projectId:N}|{Path.GetFullPath(projectLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}";

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class TrustSettings
    {
        public List<string> TrustedWorkspaces { get; init; } = [];
    }
}

public sealed record WorkspaceTrustSummary(
    Guid ProjectId,
    string ProjectName,
    string ProjectLocation,
    string RuntimeProject,
    string WorkingDirectory,
    string Command,
    string Arguments,
    string EnvironmentVariableNames);
