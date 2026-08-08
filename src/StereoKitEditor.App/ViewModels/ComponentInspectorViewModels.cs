using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StereoKitEditor.Adapter;
using StereoKitEditor.App.Infrastructure;

namespace StereoKitEditor.App.ViewModels;

public sealed class ComponentOptionViewModel(EditorComponentDescriptor descriptor)
{
    public EditorComponentDescriptor Descriptor { get; } = descriptor;
    public string DisplayName => Descriptor.DisplayName;
    public string Category => Descriptor.Category;
    public string Label => $"{Descriptor.Category} / {Descriptor.DisplayName}";
}

public sealed class ComponentInspectorViewModel : ObservableObject
{
    private readonly Action<bool> _setEnabled;
    private bool _enabled;

    public ComponentInspectorViewModel(
        Guid componentId,
        string typeId,
        string displayName,
        string? description,
        bool enabled,
        bool isMissing,
        IEnumerable<ComponentPropertyViewModel> properties,
        Action<bool> setEnabled,
        Action remove)
    {
        ComponentId = componentId;
        TypeId = typeId;
        DisplayName = displayName;
        Description = description ?? string.Empty;
        _enabled = enabled;
        IsMissing = isMissing;
        Properties = [.. properties];
        _setEnabled = setEnabled;
        RemoveCommand = new RelayCommand(remove);
    }

    public Guid ComponentId { get; }
    public string TypeId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;
    public bool IsMissing { get; }
    public bool HasProperties => Properties.Count > 0;
    public IReadOnlyList<ComponentPropertyViewModel> Properties { get; }
    public RelayCommand RemoveCommand { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _setEnabled(value);
            }
        }
    }
}

public sealed class ComponentPropertyViewModel : ObservableObject
{
    private readonly EditorPropertyDescriptor _descriptor;
    private readonly Action<JsonNode?> _commit;
    private bool _booleanValue;
    private string? _selectedOption;
    private string _valueText;
    private string _validationMessage = string.Empty;
    private Color _colorValue;
    private string _referenceSearch = string.Empty;
    private ReferenceOptionViewModel? _selectedReference;
    private readonly IReadOnlyList<ReferenceOptionViewModel> _allReferenceOptions;

    public ComponentPropertyViewModel(
        EditorPropertyDescriptor descriptor,
        JsonElement value,
        Action<JsonNode?> commit,
        IEnumerable<ReferenceOptionViewModel>? referenceOptions = null)
    {
        _descriptor = descriptor;
        _commit = commit;
        _booleanValue = value.ValueKind == JsonValueKind.True;
        _selectedOption = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        _valueText = FormatValue(value, descriptor.Kind);
        _colorValue = ParseColor(value);
        _allReferenceOptions = referenceOptions?.ToArray() ?? [];
        FilteredReferenceOptions = new ObservableCollection<ReferenceOptionViewModel>(_allReferenceOptions);
        _selectedReference = _allReferenceOptions.FirstOrDefault(option =>
            string.Equals(option.Id.ToString("D"), _valueText, StringComparison.OrdinalIgnoreCase));
    }

    public string DisplayName => _descriptor.DisplayName;
    public string Description => _descriptor.Description ?? string.Empty;
    public bool HasDescription => Description.Length > 0;
    public string Units => _descriptor.Units ?? string.Empty;
    public bool HasUnits => Units.Length > 0;
    public bool IsReadOnly => _descriptor.IsReadOnly;
    public bool IsEditable => !_descriptor.IsReadOnly;
    public bool IsBoolean => _descriptor.Kind == EditorPropertyKind.Boolean;
    public bool IsEnum => _descriptor.Kind is EditorPropertyKind.Enum or EditorPropertyKind.Flags;
    public bool IsColor => _descriptor.Kind == EditorPropertyKind.Color;
    public bool IsAssetReference => _descriptor.Kind == EditorPropertyKind.AssetReference;
    public bool IsEntityReference => _descriptor.Kind == EditorPropertyKind.EntityReference;
    public bool IsReference => IsAssetReference || IsEntityReference;
    public bool IsText => !IsBoolean && !IsEnum && !IsColor && !IsReference;
    public bool IsSlider => _descriptor.Presentation == EditorPropertyPresentation.Slider;
    public bool IsMultilineText => _descriptor.Presentation == EditorPropertyPresentation.MultilineText;
    public bool IsPlainText => IsText && !IsSlider && !IsMultilineText;
    public double SliderMinimum => _descriptor.Minimum ?? 0;
    public double SliderMaximum => _descriptor.Maximum ?? 1;
    public double SliderIncrement => _descriptor.Increment ?? Math.Max((SliderMaximum - SliderMinimum) / 100, 0.001);
    public double SliderValue => double.TryParse(_valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value
        : SliderMinimum;
    public ObservableCollection<ReferenceOptionViewModel> FilteredReferenceOptions { get; }
    public IReadOnlyList<string> Options => _descriptor.Options;
    public string Watermark => _descriptor.Kind switch
    {
        EditorPropertyKind.Color => "R, G, B, A",
        EditorPropertyKind.Vector2 => "X, Y",
        EditorPropertyKind.Vector3 => "X, Y, Z",
        EditorPropertyKind.Vector4 or EditorPropertyKind.Quaternion => "X, Y, Z, W",
        _ => string.Empty,
    };

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => ValidationMessage.Length > 0;

    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (SetProperty(ref _booleanValue, value))
            {
                ValidationMessage = string.Empty;
                _commit(JsonValue.Create(value));
            }
        }
    }

    public string? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (value is not null && SetProperty(ref _selectedOption, value))
            {
                ValidationMessage = string.Empty;
                _commit(JsonValue.Create(value));
            }
        }
    }

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (!SetProperty(ref _valueText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SliderValue));

            if (TryParse(value, _descriptor, out var node, out var error))
            {
                ValidationMessage = string.Empty;
                _commit(node);
            }
            else
            {
                ValidationMessage = error;
            }
        }
    }

    public Color ColorValue
    {
        get => _colorValue;
        set
        {
            if (SetProperty(ref _colorValue, value))
            {
                ValidationMessage = string.Empty;
                _commit(new JsonArray(
                    value.R / 255d,
                    value.G / 255d,
                    value.B / 255d,
                    value.A / 255d));
            }
        }
    }

    public void CommitSliderValue(double value)
    {
        value = Math.Clamp(value, SliderMinimum, SliderMaximum);
        if (_descriptor.Kind == EditorPropertyKind.Integer)
        {
            value = Math.Round(value);
        }

        var formatted = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (string.Equals(_valueText, formatted, StringComparison.Ordinal))
        {
            return;
        }

        _valueText = formatted;
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(SliderValue));
        ValidationMessage = string.Empty;
        _commit(_descriptor.Kind == EditorPropertyKind.Integer
            ? JsonValue.Create((long)value)
            : JsonValue.Create(value));
    }

    public string ReferenceSearch
    {
        get => _referenceSearch;
        set
        {
            if (!SetProperty(ref _referenceSearch, value))
            {
                return;
            }

            var selectedId = _selectedReference?.Id;
            FilteredReferenceOptions.Clear();
            foreach (var option in _allReferenceOptions.Where(option =>
                         string.IsNullOrWhiteSpace(value)
                         || option.Label.Contains(value, StringComparison.OrdinalIgnoreCase)
                         || option.Details.Contains(value, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredReferenceOptions.Add(option);
            }

            _selectedReference = selectedId is { } id
                ? FilteredReferenceOptions.FirstOrDefault(option => option.Id == id)
                : null;
            OnPropertyChanged(nameof(SelectedReference));
        }
    }

    public ReferenceOptionViewModel? SelectedReference
    {
        get => _selectedReference;
        set
        {
            if (value is not null && SetProperty(ref _selectedReference, value))
            {
                _valueText = value.Id == Guid.Empty ? string.Empty : value.Id.ToString("D");
                OnPropertyChanged(nameof(ValueText));
                ValidationMessage = string.Empty;
                _commit(value.Id == Guid.Empty ? null : JsonValue.Create(_valueText));
            }
        }
    }

    private static string FormatValue(JsonElement value, EditorPropertyKind kind)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (kind is EditorPropertyKind.String or EditorPropertyKind.AssetReference or EditorPropertyKind.EntityReference)
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", value.EnumerateArray().Select(item => item.GetRawText()));
        }

        return value.GetRawText();
    }

    private static Color ParseColor(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            return Colors.White;
        }

        var channels = value.EnumerateArray()
            .Select(channel => (byte)Math.Round(Math.Clamp(channel.GetDouble(), 0, 1) * 255))
            .ToArray();
        return Color.FromArgb(channels[3], channels[0], channels[1], channels[2]);
    }

    private static bool TryParse(
        string text,
        EditorPropertyDescriptor descriptor,
        out JsonNode? node,
        out string error)
    {
        node = null;
        error = string.Empty;

        switch (descriptor.Kind)
        {
            case EditorPropertyKind.String:
            case EditorPropertyKind.AssetReference:
            case EditorPropertyKind.EntityReference:
                node = JsonValue.Create(text);
                return true;
            case EditorPropertyKind.Integer:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    var clamped = Math.Clamp(
                        (double)integer,
                        descriptor.Minimum ?? long.MinValue,
                        descriptor.Maximum ?? long.MaxValue);
                    node = JsonValue.Create((long)clamped);
                    return true;
                }

                error = "Enter a whole number.";
                return false;
            case EditorPropertyKind.Number:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    node = JsonValue.Create(Math.Clamp(
                        number,
                        descriptor.Minimum ?? double.MinValue,
                        descriptor.Maximum ?? double.MaxValue));
                    return true;
                }

                error = "Enter a number.";
                return false;
            case EditorPropertyKind.Vector2:
                return TryParseArray(text, 2, false, out node, out error);
            case EditorPropertyKind.Vector3:
                return TryParseArray(text, 3, false, out node, out error);
            case EditorPropertyKind.Vector4:
            case EditorPropertyKind.Quaternion:
                return TryParseArray(text, 4, false, out node, out error);
            case EditorPropertyKind.Color:
                return TryParseArray(text, 4, true, out node, out error);
            default:
                error = $"{descriptor.Kind} editing is not available yet.";
                return false;
        }
    }

    private static bool TryParseArray(
        string text,
        int expectedCount,
        bool clampColor,
        out JsonNode? node,
        out string error)
    {
        var tokens = text.Split([',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != expectedCount
            || tokens.Any(token => !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            node = null;
            error = $"Enter {expectedCount} comma-separated numbers.";
            return false;
        }

        var array = new JsonArray();
        foreach (var token in tokens)
        {
            var value = double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            array.Add(clampColor ? Math.Clamp(value, 0, 1) : value);
        }

        node = array;
        error = string.Empty;
        return true;
    }
}

public sealed record ReferenceOptionViewModel(
    Guid Id,
    string Label,
    string Details,
    Bitmap? Thumbnail = null)
{
    public bool HasThumbnail => Thumbnail is not null;
}

public sealed class ModelMaterialSlotViewModel : ObservableObject
{
    private readonly Action<Guid> _commit;
    private ReferenceOptionViewModel? _selectedMaterial;

    public ModelMaterialSlotViewModel(
        int index,
        string name,
        IReadOnlyList<ReferenceOptionViewModel> materials,
        Guid selectedMaterialId,
        Action<Guid> commit)
    {
        Index = index;
        Name = name;
        Materials = materials;
        _selectedMaterial = materials.FirstOrDefault(option => option.Id == selectedMaterialId)
            ?? materials.FirstOrDefault();
        _commit = commit;
    }

    public int Index { get; }
    public string Name { get; }
    public string Label => $"{Index + 1}. {Name}";
    public IReadOnlyList<ReferenceOptionViewModel> Materials { get; }

    public ReferenceOptionViewModel? SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            if (value is not null && SetProperty(ref _selectedMaterial, value))
            {
                _commit(value.Id);
            }
        }
    }
}
