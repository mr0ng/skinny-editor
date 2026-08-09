using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StereoKitEditor.App.Infrastructure;
using StereoKitEditor.App.Services;
using StereoKitEditor.ProjectSystem;

namespace StereoKitEditor.App;

public partial class ProjectLauncherWindow : Window
{
    private readonly ProjectLauncherViewModel _viewModel;

    public ProjectLauncherWindow()
        : this(string.Empty)
    {
    }

    public ProjectLauncherWindow(string startupError)
    {
        InitializeComponent();
        var recent = new RecentProjectsService().Load();
        _viewModel = new(startupError, recent);
        DataContext = _viewModel;
        ProjectsList.SelectedItem = recent.FirstOrDefault(entry => entry.Exists);
    }

    private void HandleOpen(object? sender, RoutedEventArgs args)
    {
        if (ProjectsList.SelectedItem is RecentProjectEntry { Exists: true } entry)
        {
            OpenProject(entry.Path);
        }
    }

    private void HandleDoubleTapped(object? sender, TappedEventArgs args)
    {
        HandleOpen(sender, args);
        args.Handled = true;
    }

    private async void HandleNew(object? sender, RoutedEventArgs args)
    {
        var request = await new NewProjectDialog().ShowDialog<NewProjectRequest?>(this);
        if (request is null)
        {
            return;
        }

        try
        {
            var applicationDirectory = AppContext.BaseDirectory;
            var generator = new NewProjectGenerator(
                Path.Combine(applicationDirectory, "Templates", "StereoKitApp", "1"),
                Path.Combine(applicationDirectory, "sdk"));
            var result = generator.Create(request);
            OpenProject(result.DescriptorPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or DirectoryNotFoundException
                                           or FileNotFoundException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            _viewModel.StartupError = $"Could not create the project. {exception.Message}";
        }
    }

    private async void HandleBrowseDescriptor(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SKinny Editor project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SKinny Editor project")
                {
                    Patterns = ["*.skproject.json"],
                    MimeTypes = ["application/json"],
                },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            OpenProject(path);
        }
    }

    private async void HandleImport(object? sender, RoutedEventArgs args)
    {
        try
        {
            var result = await ExistingProjectImportFlow.RunAsync(
                this,
                message => _viewModel.StartupError = message);
            if (result is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.DescriptorPath))
            {
                OpenProject(result.DescriptorPath);
                return;
            }

            _viewModel.StartupError = result.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or System.Text.Json.JsonException)
        {
            _viewModel.StartupError = exception.Message;
        }
    }

    private void OpenProject(string path)
    {
        try
        {
            var mainWindow = new MainWindow(path);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = mainWindow;
            }

            mainWindow.Show();
            Close();
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException)
        {
            _viewModel.StartupError = exception.Message;
        }
    }

    private sealed class ProjectLauncherViewModel(
        string startupError,
        IEnumerable<RecentProjectEntry> projects) : ObservableObject
    {
        private string _startupError = startupError;

        public ObservableCollection<RecentProjectEntry> Projects { get; } = new(projects);
        public bool HasStartupError => !string.IsNullOrWhiteSpace(StartupError);
        public string StartupError
        {
            get => _startupError;
            set
            {
                if (SetProperty(ref _startupError, value))
                {
                    OnPropertyChanged(nameof(HasStartupError));
                }
            }
        }
    }
}
