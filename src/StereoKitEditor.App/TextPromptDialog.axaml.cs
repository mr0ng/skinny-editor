using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StereoKitEditor.App.Infrastructure;

namespace StereoKitEditor.App;

public partial class TextPromptDialog : Window
{
    private readonly TextPromptDialogModel _model = new();

    public TextPromptDialog()
    {
        InitializeComponent();
        DataContext = _model;
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public TextPromptDialog(string title, string prompt, string value = "")
        : this()
    {
        Title = title;
        _model.Prompt = prompt;
        _model.Value = value;
    }

    private void HandleAccept(object? sender, RoutedEventArgs args) => Accept();
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
        var value = _model.Value.Trim();
        if (value.Length > 0)
        {
            Close(value);
        }
    }

    private sealed class TextPromptDialogModel : ObservableObject
    {
        private string _prompt = string.Empty;
        private string _value = string.Empty;

        public string Prompt { get => _prompt; set => SetProperty(ref _prompt, value); }
        public string Value { get => _value; set => SetProperty(ref _value, value); }
    }
}
