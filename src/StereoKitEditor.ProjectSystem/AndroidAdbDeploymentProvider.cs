using System.Diagnostics;

namespace StereoKitEditor.ProjectSystem;

public sealed record DeploymentOutput(bool IsError, string Text);
public sealed record DeploymentResult(string ProfileId, string ApkPath, TimeSpan Duration);

public sealed class AndroidAdbDeploymentProvider
{
    public async Task<DeploymentResult> DeployAsync(
        EditorProjectDefinition.DeploymentProfileDefinition profile,
        string projectDirectory,
        Action<DeploymentOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var stopwatch = Stopwatch.StartNew();
        var projectPath = profile.ResolveProjectPath(projectDirectory);
        var apkPath = profile.ResolveApkPath(projectDirectory);
        var publishArguments = new[]
        {
            "publish",
            projectPath,
            "--configuration",
            profile.Configuration,
            "--framework",
            profile.TargetFramework,
            "--nologo",
        };
        await RunAsync("dotnet", publishArguments, projectDirectory, output, cancellationToken);
        if (!File.Exists(apkPath))
        {
            throw new FileNotFoundException(
                "Android publish succeeded, but the configured APK was not found. Update deploymentProfiles[].apkPath.",
                apkPath);
        }

        var devicePrefix = string.IsNullOrWhiteSpace(profile.DeviceSerial)
            ? Array.Empty<string>()
            : ["-s", profile.DeviceSerial];
        await RunAsync(
            "adb",
            devicePrefix.Concat(["install", "-r", apkPath]).ToArray(),
            projectDirectory,
            output,
            cancellationToken);
        var launchArguments = string.IsNullOrWhiteSpace(profile.MainActivity)
            ? devicePrefix.Concat([
                "shell", "monkey", "-p", profile.PackageName, "-c", "android.intent.category.LAUNCHER", "1",
            ]).ToArray()
            : devicePrefix.Concat([
                "shell", "am", "start", "-n", $"{profile.PackageName}/{profile.MainActivity}",
            ]).ToArray();
        await RunAsync("adb", launchArguments, projectDirectory, output, cancellationToken);
        return new(profile.Id, apkPath, stopwatch.Elapsed);
    }

    public static IReadOnlyList<string> CreateInstallArguments(
        EditorProjectDefinition.DeploymentProfileDefinition profile,
        string projectDirectory)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.DeviceSerial))
        {
            result.Add("-s");
            result.Add(profile.DeviceSerial);
        }

        result.Add("install");
        result.Add("-r");
        result.Add(profile.ResolveApkPath(projectDirectory));
        return result;
    }

    private static async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<DeploymentOutput>? output,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {executable}.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"'{executable}' was not found. Install the required Android tooling and ensure it is on PATH.",
                exception);
        }

        var standardOutput = PumpAsync(process.StandardOutput, false, output, cancellationToken);
        var standardError = PumpAsync(process.StandardError, true, output, cancellationToken);
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

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}.");
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        bool isError,
        Action<DeploymentOutput>? output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                output?.Invoke(new(isError, line));
            }
        }
    }
}
