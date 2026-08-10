namespace StereoKitEditor.ProjectSystem;

public sealed class StereoKitProjectCompatibilityEvaluator
{
    private readonly IReadOnlyList<string> _testedVersions;

    public StereoKitProjectCompatibilityEvaluator(string testedVersion)
        : this([testedVersion])
    {
    }

    public StereoKitProjectCompatibilityEvaluator(IEnumerable<string> testedVersions)
    {
        ArgumentNullException.ThrowIfNull(testedVersions);
        _testedVersions = testedVersions
            .Select(RequireVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_testedVersions.Count == 0)
        {
            throw new ArgumentException(
                "At least one tested StereoKit version is required.",
                nameof(testedVersions));
        }

        TestedVersion = _testedVersions[^1];
    }

    public string TestedVersion { get; }

    public StereoKitProjectCompatibilityAssessment Evaluate(InspectedDotnetProject? project)
    {
        if (project is null || !project.ReferencesStereoKit)
        {
            return new(
                StereoKitProjectCompatibility.Unresolved,
                null,
                TestedVersion,
                false,
                "The StereoKit startup project could not be identified safely.");
        }

        var projectVersion = project.StereoKitVersion;
        if (!string.IsNullOrWhiteSpace(projectVersion)
            && _testedVersions.Contains(projectVersion, StringComparer.OrdinalIgnoreCase))
        {
            return new(
                StereoKitProjectCompatibility.Tested,
                projectVersion,
                TestedVersion,
                false,
                $"StereoKit {projectVersion} is tested with this SKinny Editor runtime bridge.");
        }

        if (string.IsNullOrWhiteSpace(projectVersion)
            || projectVersion.Contains("$(", StringComparison.Ordinal)
            || !SemanticVersion.TryParse(projectVersion, out var parsedProject)
            || !SemanticVersion.TryParse(TestedVersion, out var parsedTested))
        {
            return new(
                StereoKitProjectCompatibility.Unresolved,
                projectVersion,
                TestedVersion,
                false,
                string.IsNullOrWhiteSpace(projectVersion)
                    ? $"SKinny could not resolve the project's effective StereoKit version. Automatic import requires a concrete version compatible with {TestedVersion}."
                    : $"StereoKit version '{projectVersion}' could not be compared safely with the tested editor version {TestedVersion}.");
        }

        var comparison = parsedProject.CompareTo(parsedTested);
        if (comparison == 0)
        {
            return new(
                StereoKitProjectCompatibility.Unresolved,
                projectVersion,
                TestedVersion,
                false,
                $"StereoKit version '{projectVersion}' is semantically equivalent to {TestedVersion}, but it is not an exact runtime-tested package version. Choose an exact version before importing.");
        }

        if (comparison < 0)
        {
            var canUpgrade = project.StereoKitVersionSource is not null;
            return new(
                StereoKitProjectCompatibility.UpgradeRequired,
                projectVersion,
                TestedVersion,
                canUpgrade,
                canUpgrade
                    ? $"This project uses StereoKit {projectVersion}, but the bundled SKinny runtime requires {TestedVersion} or newer. SKinny can apply an explicit, reversible upgrade to {TestedVersion}; NuGet will download it during restore."
                    : $"This project uses StereoKit {projectVersion}, but the bundled SKinny runtime requires {TestedVersion} or newer. The version declaration could not be changed automatically.");
        }

        return new(
            StereoKitProjectCompatibility.UntestedNewer,
            projectVersion,
            TestedVersion,
            false,
            $"This project uses StereoKit {projectVersion}, which is newer than the tested editor version {TestedVersion}. Import can continue, but running it requires the explicit experimental override.");
    }

    private static string RequireVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private sealed record SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<PrereleasePart> Prerelease) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = null!;
            var normalized = value.Split('+', 2)[0];
            var pieces = normalized.Split('-', 2);
            var core = pieces[0].Split('.');
            var patch = 0;
            if (core.Length is < 2 or > 3
                || !int.TryParse(core[0], out var major)
                || !int.TryParse(core[1], out var minor)
                || core.Length > 2 && !int.TryParse(core[2], out patch))
            {
                return false;
            }

            var prerelease = pieces.Length == 1
                ? []
                : pieces[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .Select(PrereleasePart.Parse)
                    .ToArray();
            if (pieces.Length == 2 && prerelease.Length == 0)
            {
                return false;
            }

            version = new(major, minor, patch, prerelease);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var core = Major.CompareTo(other.Major);
            if (core == 0) core = Minor.CompareTo(other.Minor);
            if (core == 0) core = Patch.CompareTo(other.Patch);
            if (core != 0) return core;
            if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
            {
                return Prerelease.Count == other.Prerelease.Count
                    ? 0
                    : Prerelease.Count == 0 ? 1 : -1;
            }

            for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
            {
                var comparison = Prerelease[index].CompareTo(other.Prerelease[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return Prerelease.Count.CompareTo(other.Prerelease.Count);
        }
    }

    private sealed record PrereleasePart(string Text, int? Number) : IComparable<PrereleasePart>
    {
        public static PrereleasePart Parse(string value) =>
            int.TryParse(value, out var number) ? new(value, number) : new(value, null);

        public int CompareTo(PrereleasePart? other)
        {
            if (other is null) return 1;
            if (Number is { } left && other.Number is { } right) return left.CompareTo(right);
            if (Number is not null || other.Number is not null) return Number is not null ? -1 : 1;
            return string.Compare(Text, other.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
