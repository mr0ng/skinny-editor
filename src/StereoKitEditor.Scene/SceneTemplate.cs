using System.Text.Json;

namespace StereoKitEditor.Scene;

public sealed class SceneTemplateDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Guid TemplateId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Template";
    public required SceneEntity Root { get; init; }
}

public static class SceneTemplateSerializer
{
    public static string Serialize(SceneTemplateDocument template)
    {
        Validate(template);
        return JsonSerializer.Serialize(template, SceneSerializer.Options) + Environment.NewLine;
    }

    public static SceneTemplateDocument Deserialize(string json)
    {
        var template = JsonSerializer.Deserialize<SceneTemplateDocument>(json, SceneSerializer.Options)
            ?? throw new JsonException("The scene template was empty.");
        Validate(template);
        return template;
    }

    private static void Validate(SceneTemplateDocument template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.FormatVersion != SceneTemplateDocument.CurrentFormatVersion)
        {
            throw new JsonException(
                $"Scene template format {template.FormatVersion} is unsupported; expected {SceneTemplateDocument.CurrentFormatVersion}.");
        }

        if (template.TemplateId == Guid.Empty || string.IsNullOrWhiteSpace(template.Name))
        {
            throw new JsonException("A scene template requires a stable ID and name.");
        }

        // Reuse the scene validator for entity/component identity and required Transform rules.
        _ = SceneSerializer.Deserialize(SceneSerializer.Serialize(new SceneDocument
        {
            Name = template.Name,
            Roots = [template.Root],
        }));
    }
}
