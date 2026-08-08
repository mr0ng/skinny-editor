using System.Text.Json;

namespace StereoKitEditor.App.Services;

public sealed record EditorLayoutSettings(
    double HierarchyWidth = 220,
    double InspectorWidth = 340,
    double BottomHeight = 210,
    double ProjectWidth = 520)
{
    public EditorLayoutSettings Clamp() => this with
    {
        HierarchyWidth = Math.Clamp(HierarchyWidth, 140, 600),
        InspectorWidth = Math.Clamp(InspectorWidth, 240, 700),
        BottomHeight = Math.Clamp(BottomHeight, 100, 500),
        ProjectWidth = Math.Clamp(ProjectWidth, 220, 1_200),
    };
}

public sealed class EditorLayoutSettingsService(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SKinnyEditor",
        "layout.json"));

    public EditorLayoutSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? (JsonSerializer.Deserialize<EditorLayoutSettings>(File.ReadAllText(Path), Options)
                    ?? new EditorLayoutSettings()).Clamp()
                : new EditorLayoutSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new EditorLayoutSettings();
        }
    }

    public async Task SaveAsync(
        EditorLayoutSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The layout settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(settings.Clamp(), Options),
                cancellationToken);
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
