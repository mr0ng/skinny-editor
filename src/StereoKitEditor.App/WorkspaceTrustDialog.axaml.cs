using Avalonia.Controls;
using Avalonia.Interactivity;
using StereoKitEditor.App.Services;

namespace StereoKitEditor.App;

public partial class WorkspaceTrustDialog : Window
{
    public WorkspaceTrustDialog()
    {
        InitializeComponent();
    }

    public WorkspaceTrustDialog(WorkspaceTrustSummary summary)
        : this()
    {
        DataContext = summary;
    }

    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(false);
    private void HandleTrust(object? sender, RoutedEventArgs args) => Close(true);
}
