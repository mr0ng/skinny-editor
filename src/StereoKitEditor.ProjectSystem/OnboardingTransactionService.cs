using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace StereoKitEditor.ProjectSystem;

public sealed class OnboardingTransactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<OnboardingApplyResult> ApplyAsync(
        OnboardingProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Preflight(proposal);

        var transactionId = Guid.NewGuid();
        var manifestRelativePath = Path.Combine(
            ".skinny",
            "onboarding",
            transactionId.ToString("N"),
            "transaction.json");
        var manifestPath = ResolveTarget(proposal.ProjectRoot, manifestRelativePath);
        var transactionDirectory = Path.GetDirectoryName(manifestPath)!;
        var backupDirectory = Path.Combine(transactionDirectory, "backups");
        var reportPath = Path.Combine(transactionDirectory, "report.json");

        var manifestChanges = new List<OnboardingTransactionChange>();
        for (var index = 0; index < proposal.Changes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = proposal.Changes[index];
            string? backupRelativePath = null;
            if (change.Kind == OnboardingChangeKind.Modify)
            {
                backupRelativePath = Path.Combine("backups", $"{index:D4}-{Path.GetFileName(change.RelativePath)}");
            }

            manifestChanges.Add(new OnboardingTransactionChange(
                change.Kind,
                change.RelativePath,
                change.OriginalSha256,
                change.ProposedSha256,
                backupRelativePath,
                false));
        }

        var manifest = new OnboardingTransactionManifest(
            transactionId,
            proposal.ProposalId,
            proposal.ProjectRoot,
            proposal.IntegrationShape,
            DateTimeOffset.UtcNow,
            OnboardingTransactionStatus.Prepared,
            manifestChanges,
            null);
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);

        OnboardingValidationResult? validation = null;
        try
        {
            Directory.CreateDirectory(backupDirectory);
            foreach (var change in manifestChanges.Where(change =>
                         change.Kind == OnboardingChangeKind.Modify))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResolveTarget(proposal.ProjectRoot, change.RelativePath);
                var backup = Path.Combine(transactionDirectory, change.BackupRelativePath!);
                File.Copy(source, backup, overwrite: false);
            }

            manifest = manifest with { Status = OnboardingTransactionStatus.Applying };
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
            for (var index = 0; index < proposal.Changes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var change = proposal.Changes[index];
                var target = ResolveTarget(proposal.ProjectRoot, change.RelativePath);

                // Recheck immediately before each write so a change made after preflight is preserved.
                VerifyCurrentState(change, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await WriteTargetAtomicAsync(target, change, cancellationToken);
                if (!string.Equals(HashFile(target), change.ProposedSha256, StringComparison.Ordinal))
                {
                    throw new IOException($"Verification failed after writing '{change.RelativePath}'.");
                }

                manifestChanges[index] = manifestChanges[index] with { Applied = true };
                manifest = manifest with { Changes = manifestChanges.ToArray() };
                await WriteManifestAsync(manifestPath, manifest, cancellationToken);
            }

            var descriptorPath = ResolveTarget(proposal.ProjectRoot, proposal.DescriptorRelativePath);
            validation = ValidateAppliedProposal(proposal, descriptorPath);
            if (!validation.Succeeded)
            {
                throw new InvalidDataException(string.Join(
                    Environment.NewLine,
                    validation.Checks.Where(check => !check.Succeeded).Select(check => check.Message)));
            }

            manifest = manifest with { Status = OnboardingTransactionStatus.Applied };
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
            await WriteReportAsync(
                reportPath,
                CreateReport(
                    proposal,
                    transactionId,
                    OnboardingTransactionStatus.Applied,
                    validation.Checks,
                    [],
                    null),
                cancellationToken);
            return new OnboardingApplyResult(manifestPath, reportPath, descriptorPath, validation);
        }
        catch (Exception exception)
        {
            manifest = manifest with
            {
                Status = OnboardingTransactionStatus.Failed,
                Failure = exception.Message,
                Changes = manifestChanges.ToArray(),
            };
            var recoveryFailures = new List<Exception>();
            try
            {
                await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
            }
            catch (Exception manifestException) when (manifestException is IOException
                                                       or UnauthorizedAccessException)
            {
                recoveryFailures.Add(manifestException);
            }

            try
            {
                await WriteReportAsync(
                    reportPath,
                    CreateReport(
                        proposal,
                        transactionId,
                        OnboardingTransactionStatus.Failed,
                        validation?.Checks ?? [],
                        [],
                        exception.Message),
                    CancellationToken.None);
            }
            catch (Exception reportException) when (reportException is IOException
                                                    or UnauthorizedAccessException)
            {
                recoveryFailures.Add(reportException);
            }

            try
            {
                _ = await RollbackAsync(manifestPath, CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                recoveryFailures.Add(rollbackException);
                throw new OnboardingTransactionException(
                    $"Onboarding failed and rollback also failed. Inspect '{manifestPath}'.",
                    manifestPath,
                    new AggregateException([exception, .. recoveryFailures]));
            }

            throw new OnboardingTransactionException(
                $"Onboarding failed and its applied changes were rolled back. Inspect '{manifestPath}'.",
                manifestPath,
                recoveryFailures.Count == 0
                    ? exception
                    : new AggregateException([exception, .. recoveryFailures]));
        }
    }

    public async Task<OnboardingRollbackResult> RollbackAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var canonicalManifest = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<OnboardingTransactionManifest>(
                           await File.ReadAllTextAsync(canonicalManifest, cancellationToken),
                           JsonOptions)
                       ?? throw new InvalidDataException($"Onboarding manifest '{canonicalManifest}' is empty.");
        var expectedManifestRoot = Path.Combine(
            manifest.ProjectRoot,
            ".skinny",
            "onboarding",
            manifest.TransactionId.ToString("N"));
        if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(expectedManifestRoot, canonicalManifest))
        {
            throw new InvalidDataException("The transaction manifest is not in its recorded transaction directory.");
        }

        manifest = manifest with { Status = OnboardingTransactionStatus.RollingBack };
        await WriteManifestAsync(canonicalManifest, manifest, cancellationToken);

        var restored = new List<string>();
        var removed = new List<string>();
        var conflicts = new List<string>();
        foreach (var change in manifest.Changes.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveTarget(manifest.ProjectRoot, change.RelativePath);
            if (change.Kind == OnboardingChangeKind.Create)
            {
                if (!File.Exists(target))
                {
                    continue;
                }

                if (!string.Equals(HashFile(target), change.ProposedSha256, StringComparison.Ordinal))
                {
                    conflicts.Add($"Created file was edited and was preserved: {change.RelativePath}");
                    continue;
                }

                File.Delete(target);
                removed.Add(change.RelativePath);
                DeleteEmptyParents(Path.GetDirectoryName(target)!, manifest.ProjectRoot);
                continue;
            }

            if (!File.Exists(target))
            {
                conflicts.Add($"Modified file is now missing; its backup was preserved: {change.RelativePath}");
                continue;
            }

            var currentHash = HashFile(target);
            if (string.Equals(currentHash, change.OriginalSha256, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(currentHash, change.ProposedSha256, StringComparison.Ordinal))
            {
                conflicts.Add($"Modified file changed after onboarding and was preserved: {change.RelativePath}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(change.BackupRelativePath))
            {
                conflicts.Add($"No backup is recorded for modified file: {change.RelativePath}");
                continue;
            }

            var transactionDirectory = Path.GetDirectoryName(canonicalManifest)!;
            var backup = Path.GetFullPath(Path.Combine(transactionDirectory, change.BackupRelativePath));
            if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(transactionDirectory, backup)
                || !File.Exists(backup)
                || !string.Equals(HashFile(backup), change.OriginalSha256, StringComparison.Ordinal))
            {
                conflicts.Add($"Backup is missing or invalid for modified file: {change.RelativePath}");
                continue;
            }

            ReplaceFromBackupAtomic(backup, target);
            restored.Add(change.RelativePath);
        }

        var status = conflicts.Count == 0
            ? OnboardingTransactionStatus.RolledBack
            : OnboardingTransactionStatus.RollbackConflict;
        manifest = manifest with { Status = status };
        await WriteManifestAsync(canonicalManifest, manifest, cancellationToken);
        var reportPath = Path.Combine(Path.GetDirectoryName(canonicalManifest)!, "report.json");
        if (File.Exists(reportPath))
        {
            var report = JsonSerializer.Deserialize<OnboardingPersistentReport>(
                await File.ReadAllTextAsync(reportPath, cancellationToken),
                JsonOptions);
            if (report is not null)
            {
                await WriteReportAsync(
                    reportPath,
                    report with { Status = status, RollbackConflicts = conflicts },
                    cancellationToken);
            }
        }

        return new OnboardingRollbackResult(canonicalManifest, status, restored, removed, conflicts);
    }

    public static OnboardingTransactionManifest LoadManifest(string manifestPath) =>
        JsonSerializer.Deserialize<OnboardingTransactionManifest>(
            File.ReadAllText(Path.GetFullPath(manifestPath)),
            JsonOptions)
        ?? throw new InvalidDataException($"Onboarding manifest '{manifestPath}' is empty.");

    public static OnboardingPersistentReport LoadReport(string reportPath) =>
        JsonSerializer.Deserialize<OnboardingPersistentReport>(
            File.ReadAllText(Path.GetFullPath(reportPath)),
            JsonOptions)
        ?? throw new InvalidDataException($"Onboarding report '{reportPath}' is empty.");

    private static void Preflight(OnboardingProposal proposal)
    {
        var root = Path.GetFullPath(proposal.ProjectRoot);
        if (!Directory.Exists(root))
        {
            throw new OnboardingPreflightException($"Project root was not found: {root}");
        }

        var duplicates = proposal.Changes.GroupBy(
                change => change.RelativePath,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new OnboardingPreflightException($"Multiple changes target '{duplicates.Key}'.");
        }

        foreach (var change in proposal.Changes)
        {
            var target = ResolveTarget(root, change.RelativePath);
            var proposedHash = OnboardingProposalBuilder.Hash(
                OnboardingProposalBuilder.EncodeUtf8(change.ProposedText, change.WriteUtf8Bom));
            if (!string.Equals(proposedHash, change.ProposedSha256, StringComparison.Ordinal))
            {
                throw new OnboardingPreflightException(
                    $"Proposed content hash does not match for '{change.RelativePath}'.");
            }

            VerifyCurrentState(change, target);
        }
    }

    private static void VerifyCurrentState(OnboardingProposedChange change, string target)
    {
        if (change.Kind == OnboardingChangeKind.Create)
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new OnboardingPreflightException(
                    $"Onboarding will not overwrite existing path '{change.RelativePath}'.");
            }

            return;
        }

        if (!File.Exists(target))
        {
            throw new OnboardingPreflightException(
                $"File selected for modification is missing: '{change.RelativePath}'.");
        }

        if (!string.Equals(HashFile(target), change.OriginalSha256, StringComparison.Ordinal))
        {
            throw new OnboardingPreflightException(
                $"File changed after the proposal was created: '{change.RelativePath}'. Analyze again.");
        }
    }

    private static OnboardingValidationResult ValidateAppliedProposal(
        OnboardingProposal proposal,
        string descriptorPath)
    {
        var checks = new List<OnboardingValidationCheck>();
        try
        {
            var definition = EditorProjectDefinition.Load(descriptorPath);
            checks.Add(new("Descriptor schema", true, $"Descriptor format {definition.FormatVersion} is valid."));
            var runtime = definition.CreateRuntimeProjectSpec();
            checks.Add(new(
                "Runtime project path",
                File.Exists(runtime.ProjectPath),
                File.Exists(runtime.ProjectPath)
                    ? "The configured runtime project exists."
                    : $"Runtime project was not found: {runtime.ProjectPath}"));
            checks.Add(new(
                "Initial scene path",
                File.Exists(definition.ResolveStartupScenePath()),
                File.Exists(definition.ResolveStartupScenePath())
                    ? "The initial scene exists."
                    : $"Initial scene was not found: {definition.ResolveStartupScenePath()}"));
            if (File.Exists(definition.ResolveStartupScenePath()))
            {
                using var scene = JsonDocument.Parse(File.ReadAllText(definition.ResolveStartupScenePath()));
                var validScene = scene.RootElement.TryGetProperty("formatVersion", out var format)
                                 && format.TryGetInt32(out var formatVersion)
                                 && formatVersion == 2
                                 && scene.RootElement.TryGetProperty("roots", out var roots)
                                 && roots.ValueKind == JsonValueKind.Array;
                checks.Add(new(
                    "Initial scene schema",
                    validScene,
                    validScene
                        ? "The initial scene has the expected safe JSON shape."
                        : "The initial scene is missing formatVersion 2 or its roots array."));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                           or System.Text.Json.JsonException)
        {
            checks.Add(new("Descriptor schema", false, exception.Message));
        }

        var changedMsBuildFiles = proposal.Changes
            .Where(change => Path.GetExtension(change.RelativePath) is ".csproj" or ".props" or ".targets")
            .Select(change => ResolveTarget(proposal.ProjectRoot, change.RelativePath))
            .ToArray();
        try
        {
            foreach (var path in changedMsBuildFiles)
            {
                ValidateSafeXml(path);
            }

            checks.Add(new(
                "MSBuild XML shape",
                true,
                changedMsBuildFiles.Length == 0
                    ? "No MSBuild files were changed."
                    : "Changed MSBuild files are well-formed XML; targets were not evaluated."));
        }
        catch (Exception exception) when (exception is IOException or XmlException)
        {
            checks.Add(new("MSBuild XML shape", false, exception.Message));
        }

        var hashesMatch = proposal.Changes.All(change =>
        {
            var target = ResolveTarget(proposal.ProjectRoot, change.RelativePath);
            return File.Exists(target)
                   && string.Equals(HashFile(target), change.ProposedSha256, StringComparison.Ordinal);
        });
        checks.Add(new(
            "Transaction hashes",
            hashesMatch,
            hashesMatch ? "All applied files match the reviewed proposal." : "An applied file does not match the reviewed proposal."));
        checks.Add(new(
            "Execution boundary",
            true,
            "Safe validation did not restore, build, load assemblies, or run application code; those checks require workspace trust."));
        return new(checks);
    }

    private static void ValidateSafeXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        _ = XDocument.Load(reader, LoadOptions.None);
    }

    private static string ResolveTarget(string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new OnboardingPreflightException($"Onboarding path must be project-relative: {relativePath}");
        }

        var target = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        if (!ExistingStereoKitProjectAnalyzer.IsWithinRoot(projectRoot, target))
        {
            throw new OnboardingPreflightException($"Onboarding path escapes the project root: {relativePath}");
        }

        EnsureNoReparsePoint(projectRoot, target, relativePath);

        return target;
    }

    private static void EnsureNoReparsePoint(string projectRoot, string target, string relativePath)
    {
        if (File.Exists(target)
            && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new OnboardingPreflightException(
                $"Onboarding will not write through a symbolic link or reparse point: {relativePath}");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        for (var current = Path.GetDirectoryName(target);
             current is not null && ExistingStereoKitProjectAnalyzer.IsWithinRoot(root, current);
             current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current)
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new OnboardingPreflightException(
                    $"Onboarding will not write through a symbolic link or reparse point: {relativePath}");
            }
        }
    }

    private static async Task WriteTargetAtomicAsync(
        string target,
        OnboardingProposedChange change,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                OnboardingProposalBuilder.EncodeUtf8(change.ProposedText, change.WriteUtf8Bom),
                cancellationToken);
            File.Move(
                temporary,
                target,
                overwrite: change.Kind == OnboardingChangeKind.Modify);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ReplaceFromBackupAtomic(string backup, string target)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.rollback.tmp");
        try
        {
            File.Copy(backup, temporary, overwrite: false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        OnboardingTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static Task WriteReportAsync(
        string path,
        OnboardingPersistentReport report,
        CancellationToken cancellationToken) => WriteJsonAtomicAsync(path, report, cancellationToken);

    private static OnboardingPersistentReport CreateReport(
        OnboardingProposal proposal,
        Guid transactionId,
        OnboardingTransactionStatus status,
        IReadOnlyList<OnboardingValidationCheck> validationChecks,
        IReadOnlyList<string> rollbackConflicts,
        string? failure) => new(
            transactionId,
            proposal.ProjectRoot,
            proposal.Compatibility,
            proposal.IntegrationShape,
            proposal.AnalysisSummary,
            proposal.AnalysisReasons,
            proposal.AnalysisWarnings,
            proposal.AuthorableContent,
            proposal.OpaqueContent,
            proposal.Prerequisites,
            proposal.ExpectedImpact,
            proposal.RemainingManualWork,
            validationChecks,
            proposal.Changes.Select(change => change.RelativePath).ToArray(),
            status,
            rollbackConflicts,
            failure);

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine,
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void DeleteEmptyParents(string directory, string projectRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        for (var current = Path.GetFullPath(directory);
             ExistingStereoKitProjectAnalyzer.IsWithinRoot(root, current) && Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            Directory.Delete(current);
        }
    }
}
