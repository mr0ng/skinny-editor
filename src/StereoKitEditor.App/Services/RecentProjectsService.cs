using System.Text.Json;

namespace StereoKitEditor.App.Services;

public sealed record RecentProjectEntry(string Path, string Name, DateTimeOffset LastOpenedUtc)
{
    public bool Exists => File.Exists(Path);
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
}

public sealed class RecentProjectsService(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SKinnyEditor",
        "recent-projects.json"));

    public IReadOnlyList<RecentProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            return (JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(Path), Options) ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .OrderByDescending(entry => entry.LastOpenedUtc)
                .Take(10)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void RecordOpened(string projectPath, string projectName)
    {
        try
        {
            projectPath = System.IO.Path.GetFullPath(projectPath);
            var entries = Load()
                .Where(entry => !string.Equals(entry.Path, projectPath, StringComparison.OrdinalIgnoreCase))
                .Prepend(new RecentProjectEntry(projectPath, projectName, DateTimeOffset.UtcNow))
                .Take(10)
                .ToArray();
            Write(entries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recent-project history is optional and must never prevent startup.
        }
    }

    private void Write(IReadOnlyList<RecentProjectEntry> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, Options));
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
