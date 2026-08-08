using Avalonia.Controls;
using Avalonia.Interactivity;

namespace StereoKitEditor.App;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    public ConfirmationDialog(
        string title,
        string heading,
        string message,
        string confirmLabel = "Move to Trash",
        string cancelLabel = "Cancel")
        : this()
    {
        Title = title;
        DataContext = new
        {
            Heading = heading,
            Message = message,
            ConfirmLabel = confirmLabel,
            CancelLabel = cancelLabel,
        };
    }

    private void HandleConfirm(object? sender, RoutedEventArgs args) => Close(true);
    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(false);
}
