using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace StereoKitEditor.ProjectSystem;

public sealed class DotnetProjectBuilder(string? cacheRoot = null)
{
    public string CacheRoot { get; } = Path.GetFullPath(cacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SKinnyEditor",
        "cache"));

    public async Task<DotnetBuildResult> BuildAsync(
        RuntimeProjectSpec project,
        Action<DotnetBuildOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(project.ProjectPath))
        {
            throw new FileNotFoundException("The configured desktop project does not exist.", project.ProjectPath);
        }

        var stopwatch = Stopwatch.StartNew();
        var buildArguments = new List<string>
        {
            "build",
            project.ProjectPath,
            "--configuration",
            project.Configuration,
            "--nologo",
        };
        AddTargetArguments(buildArguments, project);
        var exitCode = await RunDotnetAsync(
            project,
            buildArguments,
            output,
            cancellationToken);
        if (exitCode != 0)
        {
            throw new DotnetBuildException(project.ProjectPath, exitCode);
        }

        var targetPath = await ResolveTargetPathAsync(project, cancellationToken);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The project built successfully but its TargetPath does not exist.", targetPath);
        }

        var generation = await CreateGenerationAsync(project, targetPath, cancellationToken);
        return generation with { Duration = stopwatch.Elapsed };
    }

    public async Task<string> ResolveTargetPathAsync(
        RuntimeProjectSpec project,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var targetArguments = new List<string>
        {
            "msbuild",
            project.ProjectPath,
            "--nologo",
            "-getProperty:TargetPath",
            $"-p:Configuration={project.Configuration}",
        };
        if (!string.IsNullOrWhiteSpace(project.TargetFramework))
        {
            targetArguments.Add($"-p:TargetFramework={project.TargetFramework}");
        }

        if (!string.IsNullOrWhiteSpace(project.RuntimeIdentifier))
        {
            targetArguments.Add($"-p:RuntimeIdentifier={project.RuntimeIdentifier}");
        }

        var exitCode = await RunDotnetAsync(
            project,
            targetArguments,
            line =>
            {
                if (!line.IsError && !string.IsNullOrWhiteSpace(line.Text))
                {
                    lines.Add(line.Text.Trim());
                }
            },
            cancellationToken);
        if (exitCode != 0)
        {
            throw new DotnetBuildException(project.ProjectPath, exitCode, "Could not resolve the project's TargetPath.");
        }

        var targetPath = lines.LastOrDefault(line => Path.IsPathRooted(line));
        if (targetPath is null)
        {
            throw new InvalidDataException($"MSBuild did not return TargetPath for '{project.ProjectPath}'.");
        }

        return Path.GetFullPath(targetPath);
    }

    public IReadOnlyList<string> PruneGenerations(
        RuntimeProjectSpec project,
        IEnumerable<string> protectedGenerationDirectories,
        int retainedUnusedGenerations = 2)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(protectedGenerationDirectories);
        var profileDirectory = Path.GetFullPath(Path.Combine(
            CacheRoot,
            project.ProjectId.ToString("N"),
            SanitizePathSegment(project.ProfileId)));
        if (!Directory.Exists(profileDirectory))
        {
            return [];
        }

        var protectedPaths = protectedGenerationDirectories
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profileRoot = profileDirectory + Path.DirectorySeparatorChar;
        var removed = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(profileDirectory)
                     .Where(path => !Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal))
                     .Where(path => !protectedPaths.Contains(Path.GetFullPath(path)))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc)
                     .Skip(Math.Max(0, retainedUnusedGenerations)))
        {
            var fullPath = Path.GetFullPath(directory);
            if (!fullPath.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Build generation retention resolved outside its profile directory.");
            }

            Directory.Delete(fullPath, recursive: true);
            removed.Add(fullPath);
        }

        return removed;
    }

    private static async Task<int> RunDotnetAsync(
        RuntimeProjectSpec project,
        IReadOnlyList<string> arguments,
        Action<DotnetBuildOutput>? output,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in project.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The dotnet build process could not be started.");
        }

        var standardOutput = PumpAsync(process.StandardOutput, isError: false, output, cancellationToken);
        var standardError = PumpAsync(process.StandardError, isError: true, output, cancellationToken);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), standardOutput, standardError);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return process.ExitCode;
    }

    private static void AddTargetArguments(List<string> arguments, RuntimeProjectSpec project)
    {
        if (!string.IsNullOrWhiteSpace(project.TargetFramework))
        {
            arguments.Add("--framework");
            arguments.Add(project.TargetFramework);
        }

        if (!string.IsNullOrWhiteSpace(project.RuntimeIdentifier))
        {
            arguments.Add("--runtime");
            arguments.Add(project.RuntimeIdentifier);
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        bool isError,
        Action<DotnetBuildOutput>? output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                var reportsError = isError
                    || line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("error ", StringComparison.OrdinalIgnoreCase);
                output?.Invoke(new(reportsError, line));
            }
        }
    }

    private async Task<DotnetBuildResult> CreateGenerationAsync(
        RuntimeProjectSpec project,
        string sourceTargetPath,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceTargetPath)
            ?? throw new InvalidOperationException("The build target has no parent directory.");
        var profileDirectory = Path.Combine(
            CacheRoot,
            project.ProjectId.ToString("N"),
            SanitizePathSegment(project.ProfileId));
        Directory.CreateDirectory(profileDirectory);

        var temporaryDirectory = Path.Combine(profileDirectory, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await CopyDirectoryAsync(sourceDirectory, temporaryDirectory, cancellationToken);
            var buildId = await ComputeBuildIdAsync(project, temporaryDirectory, cancellationToken);
            var generationDirectory = Path.Combine(profileDirectory, buildId);
            if (Directory.Exists(generationDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            else
            {
                Directory.Move(temporaryDirectory, generationDirectory);
            }

            var relativeTargetPath = Path.GetRelativePath(sourceDirectory, sourceTargetPath);
            var generationTargetPath = Path.Combine(generationDirectory, relativeTargetPath);
            if (!File.Exists(generationTargetPath))
            {
                throw new FileNotFoundException(
                    "The immutable build generation does not contain the runtime target.",
                    generationTargetPath);
            }

            return new(
                generationTargetPath,
                TimeSpan.Zero,
                buildId,
                generationDirectory,
                sourceTargetPath);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFile = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            await using var source = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private static async Task<string> ComputeBuildIdAsync(
        RuntimeProjectSpec project,
        string generationDirectory,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, project.ProfileId);
        AppendText(hash, project.Configuration);
        AppendText(hash, project.TargetFramework ?? string.Empty);
        AppendText(hash, project.RuntimeIdentifier ?? string.Empty);

        foreach (var file in Directory.EnumerateFiles(generationDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(generationDirectory, path), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendText(hash, Path.GetRelativePath(generationDirectory, file).Replace('\\', '/'));
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}

public sealed record DotnetBuildResult(
    string TargetPath,
    TimeSpan Duration,
    string BuildId,
    string GenerationDirectory,
    string SourceTargetPath);

public sealed record DotnetBuildOutput(bool IsError, string Text);

public sealed class DotnetBuildException : Exception
{
    public DotnetBuildException(string projectPath, int exitCode, string? message = null)
        : base(message ?? $"Build failed for '{projectPath}' with exit code {exitCode}.")
    {
        ProjectPath = projectPath;
        ExitCode = exitCode;
    }

    public string ProjectPath { get; }
    public int ExitCode { get; }
}
