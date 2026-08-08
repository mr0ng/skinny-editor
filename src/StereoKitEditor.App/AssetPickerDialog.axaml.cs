using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using StereoKitEditor.App.Infrastructure;
using StereoKitEditor.App.ViewModels;

namespace StereoKitEditor.App;

public partial class AssetPickerDialog : Window
{
    private readonly Model _model;

    public AssetPickerDialog()
        : this("Choose an asset", [])
    {
    }

    public AssetPickerDialog(string heading, IEnumerable<ProjectItemViewModel> items)
    {
        InitializeComponent();
        _model = new Model(heading, items.Where(item => item.AssetId is not null));
        DataContext = _model;
        Opened += (_, _) => AssetList.SelectedIndex = _model.Filtered.Count > 0 ? 0 : -1;
    }

    private void HandleAccept(object? sender, RoutedEventArgs args)
    {
        if (AssetList.SelectedItem is Choice choice)
        {
            Close(choice.Id);
        }
    }

    private void HandleCancel(object? sender, RoutedEventArgs args) => Close(null);

    private void HandleDoubleTapped(object? sender, TappedEventArgs args)
    {
        HandleAccept(sender, args);
        args.Handled = true;
    }

    private sealed class Model : ObservableObject
    {
        private readonly IReadOnlyList<Choice> _all;
        private string _search = string.Empty;

        public Model(string heading, IEnumerable<ProjectItemViewModel> items)
        {
            Heading = heading;
            _all = items.Select(item => new Choice(
                item.AssetId!.Value,
                Path.GetFileName(item.Name),
                item.RelativePath ?? item.Name,
                item.Kind,
                item.Thumbnail)).ToArray();
            Apply();
        }

        public string Heading { get; }
        public ObservableCollection<Choice> Filtered { get; } = [];
        public string Search
        {
            get => _search;
            set
            {
                if (SetProperty(ref _search, value)) Apply();
            }
        }

        private void Apply()
        {
            Filtered.Clear();
            foreach (var item in _all.Where(item => string.IsNullOrWhiteSpace(Search)
                         || item.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                         || item.Path.Contains(Search, StringComparison.OrdinalIgnoreCase)))
            {
                Filtered.Add(item);
            }
        }
    }

    private sealed record Choice(Guid Id, string Name, string Path, string Kind, Bitmap? Thumbnail);
}
