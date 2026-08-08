namespace StereoKitEditor.App.Services;

public static class WorkspaceLocator
{
    public static string FindRoot()
    {
        var candidates = new[]
        {
            new DirectoryInfo(Directory.GetCurrentDirectory()),
            new DirectoryInfo(AppContext.BaseDirectory),
        };

        foreach (var candidate in candidates)
        {
            for (var directory = candidate; directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "StereoKitEditor.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        // Portable distributions do not include the source solution. Relative
        // --project paths are resolved from the launch directory in that case,
        // and a missing default descriptor opens the project launcher.
        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }
}
