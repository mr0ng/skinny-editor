namespace StereoKitEditor.ProjectSystem;

public static class EditorProjectLocator
{
    public static string ResolveStartupProject(
        string workspaceRoot,
        IReadOnlyList<string> arguments,
        string? environmentOverride = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var requested = ReadProjectArgument(arguments)
            ?? environmentOverride
            ?? Path.Combine("samples", "HelloEditor", "HelloEditor.skproject.json");
        var path = Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(workspaceRoot, requested));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The requested SKinny Editor project descriptor was not found: {path}",
                path);
        }

        return path;
    }

    private static string? ReadProjectArgument(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument[(argument.IndexOf('=') + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }

            if (string.Equals(argument, "--project", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count)
            {
                var value = arguments[index + 1].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }
}
