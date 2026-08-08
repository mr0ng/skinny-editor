using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StereoKitEditor.App.Services;

namespace StereoKitEditor.App;

public partial class RecentProjectsDialog : Window
{
    public RecentProjectsDialog()
    {
        InitializeComponent();
    }

    public RecentProjectsDialog(IReadOnlyList<RecentProjectEntry> projects)
        : this()
    {
        DataContext = projects;
        ProjectsList.SelectedItem = projects.FirstOrDefault(entry => entry.Exists);
    }

    private void HandleOpen(object? sender, RoutedEventArgs args) => OpenSelected();
    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(null);

    private void HandleDoubleTapped(object? sender, TappedEventArgs args)
    {
        OpenSelected();
        args.Handled = true;
    }

    private void OpenSelected()
    {
        if (ProjectsList.SelectedItem is RecentProjectEntry { Exists: true } entry)
        {
            Close(entry.Path);
        }
    }
}
