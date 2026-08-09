using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace StereoKitEditor.ProjectSystem;

public sealed record NewProjectRequest(string ProjectName, string ParentDirectory);

public sealed record NewProjectResult(string ProjectDirectory, string DescriptorPath);

public sealed partial class NewProjectGenerator
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    private readonly string _templateDirectory;
    private readonly string _sdkPackageDirectory;
    private readonly string _sdkVersion;

    public NewProjectGenerator(
        string templateDirectory,
        string sdkPackageDirectory,
        string? sdkVersion = null)
    {
        _templateDirectory = Path.GetFullPath(templateDirectory);
        _sdkPackageDirectory = Path.GetFullPath(sdkPackageDirectory);
        _sdkVersion = string.IsNullOrWhiteSpace(sdkVersion)
            ? CurrentSdkVersion
            : sdkVersion.Trim();
    }

    public static string CurrentSdkVersion
    {
        get
        {
            var informationalVersion = typeof(NewProjectGenerator).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return informationalVersion?.Split('+', 2)[0]
                ?? typeof(NewProjectGenerator).Assembly.GetName().Version?.ToString(3)
                ?? throw new InvalidOperationException("The SKinny Editor SDK version could not be determined.");
        }
    }

    public NewProjectResult Create(NewProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectName = ValidateProjectName(request.ProjectName);
        var parentDirectory = ValidateParentDirectory(request.ParentDirectory);
        var projectDirectory = Path.GetFullPath(Path.Combine(parentDirectory, projectName));
        if (Directory.Exists(projectDirectory) || File.Exists(projectDirectory))
        {
            throw new IOException($"The destination already exists: {projectDirectory}");
        }

        ValidateTemplate();
        var sdkPackages = FindSdkPackages();

        var stagingDirectory = Path.Combine(parentDirectory, $".skinny-new-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var tokens = CreateTokens(projectName);
            CopyTemplate(stagingDirectory, tokens);
            CopySdkPackages(stagingDirectory, sdkPackages);
            EnsureNoTemplateTokensRemain(stagingDirectory);

            MoveDirectoryWithRetry(stagingDirectory, projectDirectory);
            return new NewProjectResult(
                projectDirectory,
                Path.Combine(projectDirectory, $"{projectName}.skproject.json"));
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        const int maximumAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (attempt < maximumAttempts
                                               && exception is IOException or UnauthorizedAccessException
                                               && Directory.Exists(source)
                                               && !Directory.Exists(destination)
                                               && !File.Exists(destination))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    public static string ValidateProjectName(string projectName)
    {
        var name = projectName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new ArgumentException("Enter a project name.", nameof(projectName));
        }

        if (name.Length > 64)
        {
            throw new ArgumentException("Project names must be 64 characters or fewer.", nameof(projectName));
        }

        if (!CSharpIdentifierRegex().IsMatch(name) || CSharpKeywords.Contains(name))
        {
            throw new ArgumentException(
                "Use a C# identifier for the project name: start with a letter or underscore, then use only letters, numbers, or underscores.",
                nameof(projectName));
        }

        return name;
    }

    private static string ValidateParentDirectory(string parentDirectory)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new ArgumentException("Choose a parent location for the project.", nameof(parentDirectory));
        }

        var path = Path.GetFullPath(parentDirectory.Trim());
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The parent location does not exist: {path}");
        }

        return path;
    }

    private void ValidateTemplate()
    {
        if (!Directory.Exists(_templateDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The bundled starter template was not found: {_templateDirectory}");
        }

        if (!Directory.EnumerateFiles(_templateDirectory, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException($"The bundled starter template is empty: {_templateDirectory}");
        }
    }

    private IReadOnlyList<string> FindSdkPackages()
    {
        return BundledSdkPackages.FindRequired(
            _sdkVersion,
            _sdkPackageDirectory,
            allowGlobalPackageCache: false);
    }

    private IReadOnlyDictionary<string, string> CreateTokens(string projectName) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__PROJECT_NAME__"] = projectName,
            ["__PROJECT_SLUG__"] = projectName.Replace('_', '-').ToLowerInvariant(),
            ["__SKINNY_SDK_VERSION__"] = _sdkVersion,
            ["__PROJECT_ID__"] = Guid.NewGuid().ToString("D"),
            ["__SCENE_ID__"] = Guid.NewGuid().ToString("D"),
            ["__ROOT_ENTITY_ID__"] = Guid.NewGuid().ToString("D"),
            ["__TRANSFORM_COMPONENT_ID__"] = Guid.NewGuid().ToString("D"),
            ["__MESH_COMPONENT_ID__"] = Guid.NewGuid().ToString("D"),
            ["__SOLUTION_GUID__"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            ["__SOLUTION_PROJECT_GUID__"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
        };

    private void CopyTemplate(string stagingDirectory, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     _templateDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_templateDirectory, sourceDirectory);
            Directory.CreateDirectory(Path.Combine(stagingDirectory, ReplaceTokens(relativePath, tokens)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     _templateDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = ReplaceTokens(Path.GetRelativePath(_templateDirectory, sourceFile), tokens);
            var destinationFile = Path.Combine(stagingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            var content = File.ReadAllText(sourceFile, Encoding.UTF8);
            File.WriteAllText(destinationFile, ReplaceTokens(content, tokens), new UTF8Encoding(false));
        }
    }

    private static void CopySdkPackages(string stagingDirectory, IReadOnlyList<string> packages)
    {
        var destination = Path.Combine(stagingDirectory, ".skinny", "sdk");
        Directory.CreateDirectory(destination);
        foreach (var package in packages)
        {
            File.Copy(package, Path.Combine(destination, Path.GetFileName(package)), overwrite: false);
        }
    }

    private void EnsureNoTemplateTokensRemain(string stagingDirectory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     stagingDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (TemplateTokenRegex().IsMatch(Path.GetFileName(path)))
            {
                throw new InvalidDataException($"The starter template contains an unknown token in: {path}");
            }

            if (File.Exists(path)
                && !path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                && TemplateTokenRegex().IsMatch(File.ReadAllText(path, Encoding.UTF8)))
            {
                throw new InvalidDataException($"The starter template contains an unknown token in: {path}");
            }
        }
    }

    private static string ReplaceTokens(string value, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (token, replacement) in tokens)
        {
            value = value.Replace(token, replacement, StringComparison.Ordinal);
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpIdentifierRegex();

    [GeneratedRegex("__[A-Z0-9_]+__", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateTokenRegex();
}
