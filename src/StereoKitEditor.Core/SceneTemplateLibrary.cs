using StereoKitEditor.Scene;

namespace StereoKitEditor.Core;

public sealed record SceneTemplateRecord(
    Guid TemplateId,
    string Name,
    string Path,
    DateTimeOffset LastWriteTimeUtc);

public sealed class SceneTemplateLibrary(string projectDirectory, string root = "Templates")
{
    public string Root { get; } = ResolveUnder(projectDirectory, root);

    public IReadOnlyList<SceneTemplateRecord> Discover()
    {
        Directory.CreateDirectory(Root);
        var result = new List<SceneTemplateRecord>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.sktemplate.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var template = SceneTemplateSerializer.Deserialize(File.ReadAllText(path));
                result.Add(new(
                    template.TemplateId,
                    template.Name,
                    path,
                    File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                // Invalid templates remain visible through Console diagnostics when explicitly opened.
            }
        }

        return result;
    }

    public async Task<SceneTemplateRecord> SaveAsync(
        SceneEntity source,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("A template requires a valid non-empty file name.");
        }

        Directory.CreateDirectory(Root);
        var path = UniquePath(name);
        var template = new SceneTemplateDocument
        {
            Name = name,
            Root = SceneSerializer.Clone(new SceneDocument { Name = name, Roots = [source] }).Roots[0],
        };
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                SceneTemplateSerializer.Serialize(template),
                cancellationToken);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new(template.TemplateId, template.Name, path, File.GetLastWriteTimeUtc(path));
    }

    public SceneEntity Instantiate(string path)
    {
        path = EnsureWithinRoot(path);
        var template = SceneTemplateSerializer.Deserialize(File.ReadAllText(path));
        return SceneEntityCloner.CloneWithNewIds(template.Root, template.Name);
    }

    private string UniquePath(string name)
    {
        var candidate = Path.Combine(Root, name + ".sktemplate.json");
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(Root, $"{name} {suffix}.sktemplate.json");
        }

        return candidate;
    }

    private string EnsureWithinRoot(string path)
    {
        path = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The template path escapes the project template root.");
        }

        return path;
    }

    private static string ResolveUnder(string projectDirectory, string relativeRoot)
    {
        if (Path.IsPathRooted(relativeRoot))
        {
            throw new InvalidDataException("The template root must be project-relative.");
        }

        var project = Path.GetFullPath(projectDirectory);
        var resolved = Path.GetFullPath(Path.Combine(project, relativeRoot));
        var prefix = Path.TrimEndingDirectorySeparator(project) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The template root escapes the project directory.");
        }

        return resolved;
    }
}
