using System.Text.Json;

namespace StereoKitEditor.App.Services;

public sealed record EditorPreferences(
    bool AutoRebuildSource = false,
    bool AutoRefreshAssets = false,
    bool ShowRuntimeInspection = true);

public sealed class EditorPreferencesService(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SKinnyEditor",
        "preferences.json"));

    public EditorPreferences Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(Path), Options) ?? new()
                : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(EditorPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, Options));
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are optional and cannot be allowed to interrupt authoring.
        }
    }
}
