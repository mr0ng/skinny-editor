using Avalonia.Controls;
using Avalonia.Interactivity;

namespace StereoKitEditor.App;

public enum UnsavedChangesChoice
{
    Cancel,
    Discard,
    Save,
}

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(UnsavedChangesChoice.Cancel);
    private void HandleDiscard(object? sender, RoutedEventArgs args) => Close(UnsavedChangesChoice.Discard);
    private void HandleSave(object? sender, RoutedEventArgs args) => Close(UnsavedChangesChoice.Save);
}
