using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.App;

public partial class NewProjectDialog : Window
{
    public NewProjectDialog()
    {
        InitializeComponent();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        LocationBox.Text = Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
        Opened += (_, _) => ProjectNameBox.Focus();
    }

    private async void HandleBrowse(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to create the project",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            LocationBox.Text = path;
        }
    }

    private void HandleCreate(object? sender, RoutedEventArgs args) => Accept();
    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(null);

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            Accept();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            Close(null);
            args.Handled = true;
        }
    }

    private void Accept()
    {
        try
        {
            var projectName = NewProjectGenerator.ValidateProjectName(ProjectNameBox.Text ?? string.Empty);
            var parentDirectory = LocationBox.Text?.Trim() ?? string.Empty;
            if (parentDirectory.Length == 0)
            {
                throw new ArgumentException("Choose a parent location for the project.");
            }

            Close(new NewProjectRequest(projectName, parentDirectory));
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
            ValidationText.IsVisible = true;
        }
    }
}
