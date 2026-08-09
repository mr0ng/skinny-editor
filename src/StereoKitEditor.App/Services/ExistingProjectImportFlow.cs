using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.App.Services;

public static class ExistingProjectImportFlow
{
    public static async Task<ExistingProjectOnboardingResult?> RunAsync(
        Window owner,
        Action<string>? reportStatus = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import an existing StereoKit project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("StereoKit solution or project")
                {
                    Patterns = ["*.sln", "*.csproj"],
                },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        reportStatus?.Invoke("Inspecting safe project metadata; no project code is running…");
        ExistingProjectAnalysis analysis;
        try
        {
            analysis = await Task.Run(() => new ExistingStereoKitProjectAnalyzer().Analyze(path));
        }
        finally
        {
            reportStatus?.Invoke(string.Empty);
        }

        return await new ExistingProjectOnboardingWindow(analysis)
            .ShowDialog<ExistingProjectOnboardingResult?>(owner);
    }
}
