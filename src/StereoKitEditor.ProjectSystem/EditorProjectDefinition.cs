using System.Text.Json;
using System.Text.Json.Serialization;

namespace StereoKitEditor.ProjectSystem;

public sealed class EditorProjectDefinition
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public int FormatVersion { get; init; }
    public Guid ProjectId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Solution { get; init; } = string.Empty;
    public string AssetsRoot { get; init; } = "Assets";
    public string ScenesRoot { get; init; } = "Scenes";
    public string StartupScene { get; init; } = string.Empty;

    // Format 1 compatibility fields.
    public string DesktopProject { get; init; } = string.Empty;
    public string BuildConfiguration { get; init; } = "Debug";
    public string StereoKitVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> BuildProfiles { get; init; } = [];

    // Format 2 runtime profiles.
    public string DefaultSceneProfile { get; init; } = string.Empty;
    public string DefaultPlayProfile { get; init; } = string.Empty;
    public IReadOnlyList<RuntimeProfileDefinition> RuntimeProfiles { get; init; } = [];
    public IReadOnlyList<DeploymentProfileDefinition> DeploymentProfiles { get; init; } = [];

    [JsonIgnore]
    public string DefinitionPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string ProjectDirectory => Path.GetDirectoryName(DefinitionPath)
        ?? throw new InvalidOperationException("The project definition has no parent directory.");

    [JsonIgnore]
    public bool IsLegacyFormat => FormatVersion == 1;

    public static EditorProjectDefinition Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var definition = JsonSerializer.Deserialize<EditorProjectDefinition>(File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException($"Project definition '{fullPath}' is empty.");
        definition.DefinitionPath = fullPath;
        definition.Validate();
        return definition;
    }

    public string ResolveSolutionPath() => ResolveRequiredPath(Solution, nameof(Solution));

    public string ResolveStartupScenePath() => ResolveRequiredPath(StartupScene, nameof(StartupScene));

    public RuntimeProjectSpec CreateRuntimeProjectSpec(
        RuntimeProfileMode mode = RuntimeProfileMode.Scene,
        string? profileId = null)
    {
        if (IsLegacyFormat)
        {
            return RuntimeProjectSpec.Legacy(
                ProjectId,
                ResolveRequiredPath(DesktopProject, nameof(DesktopProject)),
                string.IsNullOrWhiteSpace(BuildConfiguration) ? "Debug" : BuildConfiguration);
        }

        var requestedId = profileId;
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            requestedId = mode == RuntimeProfileMode.Scene ? DefaultSceneProfile : DefaultPlayProfile;
        }

        var profile = RuntimeProfiles.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, requestedId, StringComparison.Ordinal));
        if (profile is null)
        {
            throw new InvalidDataException($"Runtime profile '{requestedId}' was not found.");
        }

        if (!profile.Modes.Contains(mode))
        {
            throw new InvalidDataException($"Runtime profile '{profile.Id}' does not support {mode}.");
        }

        return profile.CreateSpec(ProjectDirectory, ProjectId);
    }

    private string ResolveRequiredPath(string relativePath, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException($"Project property '{propertyName}' is required.");
        }

        return Path.GetFullPath(Path.Combine(ProjectDirectory, relativePath));
    }

    private void Validate()
    {
        if (FormatVersion is not (1 or 2))
        {
            throw new InvalidDataException($"Unsupported project format version {FormatVersion}; expected 1 or 2.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new InvalidDataException("ProjectId must be a non-empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("Project name is required.");
        }

        _ = ResolveSolutionPath();
        _ = ResolveStartupScenePath();

        if (IsLegacyFormat)
        {
            ValidateProjectPath(ResolveRequiredPath(DesktopProject, nameof(DesktopProject)));
            return;
        }

        if (RuntimeProfiles.Count == 0)
        {
            throw new InvalidDataException("Project format 2 requires at least one runtime profile.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in RuntimeProfiles)
        {
            profile.Validate(ProjectDirectory);
            if (!ids.Add(profile.Id))
            {
                throw new InvalidDataException($"Runtime profile ID '{profile.Id}' is duplicated.");
            }
        }

        ValidateDefaultProfile(DefaultSceneProfile, RuntimeProfileMode.Scene, ids);
        ValidateDefaultProfile(DefaultPlayProfile, RuntimeProfileMode.Play, ids);

        var deploymentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in DeploymentProfiles)
        {
            profile.Validate(ProjectDirectory);
            if (!deploymentIds.Add(profile.Id))
            {
                throw new InvalidDataException($"Deployment profile ID '{profile.Id}' is duplicated.");
            }
        }
    }

    private void ValidateDefaultProfile(
        string profileId,
        RuntimeProfileMode mode,
        IReadOnlySet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !ids.Contains(profileId))
        {
            throw new InvalidDataException($"Default {mode} profile '{profileId}' was not found.");
        }

        var profile = RuntimeProfiles.Single(candidate => candidate.Id == profileId);
        if (!profile.Modes.Contains(mode))
        {
            throw new InvalidDataException($"Default {mode} profile '{profileId}' does not support {mode}.");
        }
    }

    private static void ValidateProjectPath(string projectPath)
    {
        if (!string.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Runtime projects must reference a .csproj file.");
        }
    }

    public sealed record RuntimeProfileDefinition
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Project { get; init; } = string.Empty;
        public string Configuration { get; init; } = "Debug";
        public string? TargetFramework { get; init; }
        public string? RuntimeIdentifier { get; init; }
        public string? WorkingDirectory { get; init; }
        public IReadOnlyList<string> Arguments { get; init; } = [];
        public IReadOnlyDictionary<string, string> Environment { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyList<RuntimeProfileMode> Modes { get; init; } = [];

        internal RuntimeProjectSpec CreateSpec(string descriptorDirectory, Guid projectId)
        {
            var projectPath = ResolvePath(descriptorDirectory, Project);
            var workingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
                ? Path.GetDirectoryName(projectPath)!
                : ResolvePath(descriptorDirectory, WorkingDirectory);
            return new(
                projectId,
                Id,
                string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName,
                projectPath,
                string.IsNullOrWhiteSpace(Configuration) ? "Debug" : Configuration,
                TargetFramework,
                RuntimeIdentifier,
                workingDirectory,
                Arguments,
                Environment,
                Modes);
        }

        internal void Validate(string descriptorDirectory)
        {
            if (string.IsNullOrWhiteSpace(Id))
            {
                throw new InvalidDataException("Every runtime profile requires an ID.");
            }

            var projectPath = ResolvePath(descriptorDirectory, Project);
            ValidateProjectPath(projectPath);
            if (Modes.Count == 0)
            {
                throw new InvalidDataException($"Runtime profile '{Id}' must support at least one mode.");
            }

            if (Modes.Distinct().Count() != Modes.Count)
            {
                throw new InvalidDataException($"Runtime profile '{Id}' contains duplicate modes.");
            }
        }

        private static string ResolvePath(string descriptorDirectory, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException("Runtime profile project path is required.");
            }

            return Path.GetFullPath(Path.Combine(descriptorDirectory, relativePath));
        }
    }

    public sealed record DeploymentProfileDefinition
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Provider { get; init; } = "android-adb";
        public string Project { get; init; } = string.Empty;
        public string Configuration { get; init; } = "Release";
        public string TargetFramework { get; init; } = "net8.0-android";
        public string ApkPath { get; init; } = string.Empty;
        public string PackageName { get; init; } = string.Empty;
        public string? MainActivity { get; init; }
        public string? DeviceSerial { get; init; }

        internal void Validate(string descriptorDirectory)
        {
            if (string.IsNullOrWhiteSpace(Id)
                || !string.Equals(Provider, "android-adb", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(Project)
                || string.IsNullOrWhiteSpace(TargetFramework)
                || string.IsNullOrWhiteSpace(ApkPath)
                || string.IsNullOrWhiteSpace(PackageName))
            {
                throw new InvalidDataException(
                    "Android deployment profiles require an ID, provider 'android-adb', project, target framework, APK path, and package name.");
            }

            ValidateProjectPath(ResolvePath(descriptorDirectory, Project));
            _ = ResolvePath(descriptorDirectory, ApkPath);
        }

        public string ResolveProjectPath(string descriptorDirectory) => ResolvePath(descriptorDirectory, Project);
        public string ResolveApkPath(string descriptorDirectory) => ResolvePath(descriptorDirectory, ApkPath);

        private static string ResolvePath(string descriptorDirectory, string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException("Deployment paths must be project-relative.");
            }

            var root = Path.GetFullPath(descriptorDirectory);
            var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A deployment path escapes the project directory.");
            }

            return resolved;
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeProfileMode>))]
public enum RuntimeProfileMode
{
    Scene,
    Play,
}

public sealed record RuntimeProjectSpec(
    Guid ProjectId,
    string ProfileId,
    string DisplayName,
    string ProjectPath,
    string Configuration,
    string? TargetFramework,
    string? RuntimeIdentifier,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<RuntimeProfileMode> Modes)
{
    public RuntimeProjectSpec(string projectPath, string configuration)
        : this(
            CreateStableProjectId(projectPath),
            "adhoc",
            "Ad hoc",
            projectPath,
            configuration,
            null,
            null,
            Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException("The runtime project has no parent directory."),
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            [RuntimeProfileMode.Scene, RuntimeProfileMode.Play])
    {
    }

    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)
        ?? throw new InvalidOperationException("The runtime project has no parent directory.");

    public static RuntimeProjectSpec Legacy(Guid projectId, string projectPath, string configuration) => new(
        projectId,
        "legacy-desktop",
        "Legacy Desktop",
        projectPath,
        configuration,
        null,
        null,
        Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("The runtime project has no parent directory."),
        [],
        new Dictionary<string, string>(StringComparer.Ordinal),
        [RuntimeProfileMode.Scene, RuntimeProfileMode.Play]);

    private static Guid CreateStableProjectId(string projectPath)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath).ToUpperInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
