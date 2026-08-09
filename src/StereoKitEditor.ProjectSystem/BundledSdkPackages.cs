namespace StereoKitEditor.ProjectSystem;

internal static class BundledSdkPackages
{
    internal static readonly string[] RequiredPackageIds =
    [
        "SKinny.Editor.Adapter",
        "SKinny.Editor.Scene",
        "SKinny.Editor.Protocol",
        "SKinny.Editor.Runtime",
    ];

    public static IReadOnlyList<string> FindRequired(
        string version,
        string? preferredDirectory = null,
        bool allowGlobalPackageCache = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            directories.Add(Path.GetFullPath(preferredDirectory));
        }
        else
        {
            directories.Add(Path.Combine(AppContext.BaseDirectory, "sdk"));
        }

        var resolved = new List<string>();
        var missing = new List<string>();
        foreach (var packageId in RequiredPackageIds)
        {
            var fileName = $"{packageId}.{version}.nupkg";
            var package = directories
                .Where(Directory.Exists)
                .Select(directory => Path.Combine(directory, fileName))
                .FirstOrDefault(File.Exists);
            if (package is null && allowGlobalPackageCache)
            {
                package = FindInGlobalPackageCache(packageId, version);
            }

            if (package is null)
            {
                missing.Add(fileName);
            }
            else
            {
                resolved.Add(package);
            }
        }

        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"The matching bundled SKinny SDK feed is missing: {string.Join(", ", missing)}. " +
                "Use a packaged SKinny Editor build or create one with scripts/package-windows.ps1.");
        }

        return resolved;
    }

    private static string? FindInGlobalPackageCache(string packageId, string version)
    {
        var globalPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(globalPackages))
        {
            globalPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        var normalizedId = packageId.ToLowerInvariant();
        var path = Path.Combine(
            Path.GetFullPath(globalPackages),
            normalizedId,
            version.ToLowerInvariant(),
            $"{normalizedId}.{version.ToLowerInvariant()}.nupkg");
        return File.Exists(path) ? path : null;
    }
}
