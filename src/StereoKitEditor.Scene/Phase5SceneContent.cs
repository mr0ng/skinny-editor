using System.Text.Json;
using System.Text.Json.Serialization;

namespace StereoKitEditor.Scene;

public static partial class BuiltInComponentTypes
{
    public const string ImageRenderer = "stereokit.image-renderer";
    public const string TextRenderer = "stereokit.text-renderer";
    public const string UiPanel = "stereokit.ui-panel";
    public const string UiRect = "stereokit.ui-rect";
    public const string UiText = "stereokit.ui-text";
    public const string UiImage = "stereokit.ui-image";
    public const string UiSpacer = "stereokit.ui-spacer";
    public const string UiSeparator = "stereokit.ui-separator";
    public const string UiButton = "stereokit.ui-button";
    public const string UiToggle = "stereokit.ui-toggle";
    public const string UiSlider = "stereokit.ui-slider";
    public const string UiTextInput = "stereokit.ui-text-input";
}

public sealed partial class EntityComponents
{
    public ImageRendererComponent? ImageRenderer
    {
        get => GetData<ImageRendererComponent>(BuiltInComponentTypes.ImageRenderer);
        set => SetOptional(BuiltInComponentTypes.ImageRenderer, value);
    }

    public TextRendererComponent? TextRenderer
    {
        get => GetData<TextRendererComponent>(BuiltInComponentTypes.TextRenderer);
        set => SetOptional(BuiltInComponentTypes.TextRenderer, value);
    }

    public UiPanelComponent? UiPanel
    {
        get => GetData<UiPanelComponent>(BuiltInComponentTypes.UiPanel);
        set => SetOptional(BuiltInComponentTypes.UiPanel, value);
    }

    public UiRectComponent? UiRect
    {
        get => GetData<UiRectComponent>(BuiltInComponentTypes.UiRect);
        set => SetOptional(BuiltInComponentTypes.UiRect, value);
    }

    public UiTextComponent? UiText
    {
        get => GetData<UiTextComponent>(BuiltInComponentTypes.UiText);
        set => SetOptional(BuiltInComponentTypes.UiText, value);
    }

    public UiImageComponent? UiImage
    {
        get => GetData<UiImageComponent>(BuiltInComponentTypes.UiImage);
        set => SetOptional(BuiltInComponentTypes.UiImage, value);
    }

    public UiSpacerComponent? UiSpacer
    {
        get => GetData<UiSpacerComponent>(BuiltInComponentTypes.UiSpacer);
        set => SetOptional(BuiltInComponentTypes.UiSpacer, value);
    }

    public UiSeparatorComponent? UiSeparator
    {
        get => GetData<UiSeparatorComponent>(BuiltInComponentTypes.UiSeparator);
        set => SetOptional(BuiltInComponentTypes.UiSeparator, value);
    }

    public UiButtonComponent? UiButton
    {
        get => GetData<UiButtonComponent>(BuiltInComponentTypes.UiButton);
        set => SetOptional(BuiltInComponentTypes.UiButton, value);
    }

    public UiToggleComponent? UiToggle
    {
        get => GetData<UiToggleComponent>(BuiltInComponentTypes.UiToggle);
        set => SetOptional(BuiltInComponentTypes.UiToggle, value);
    }

    public UiSliderComponent? UiSlider
    {
        get => GetData<UiSliderComponent>(BuiltInComponentTypes.UiSlider);
        set => SetOptional(BuiltInComponentTypes.UiSlider, value);
    }

    public UiTextInputComponent? UiTextInput
    {
        get => GetData<UiTextInputComponent>(BuiltInComponentTypes.UiTextInput);
        set => SetOptional(BuiltInComponentTypes.UiTextInput, value);
    }

    private void SetOptional<T>(string typeId, T? value) where T : class
    {
        if (value is null)
        {
            RemoveByType(typeId);
        }
        else
        {
            SetData(typeId, value);
        }
    }
}

[JsonConverter(typeof(Vector2ValueJsonConverter))]
public readonly record struct Vector2Value(double X, double Y)
{
    public static Vector2Value Zero => new(0, 0);
    public static Vector2Value One => new(1, 1);
}

[JsonConverter(typeof(ThicknessValueJsonConverter))]
public readonly record struct ThicknessValue(double Left, double Top, double Right, double Bottom)
{
    public static ThicknessValue Zero => new(0, 0, 0, 0);
    public static ThicknessValue Uniform(double value) => new(value, value, value, value);
}

public sealed record ImageRendererComponent
{
    public Guid TextureAssetId { get; init; }
    public Vector2Value Size { get; init; } = new(0.3, 0.3);
    public ImageSizingMode SizingMode { get; init; } = ImageSizingMode.PreserveAspect;
    public double PixelsPerMeter { get; init; } = 1000;
    public Vector2Value Pivot { get; init; } = new(0.5, 0.5);
    public ColorValue Tint { get; init; } = new(1, 1, 1, 1);
    public bool DoubleSided { get; init; } = true;
    public BillboardMode Billboard { get; init; }
    public RenderSurfacePreset SurfacePreset { get; init; } = RenderSurfacePreset.WorldTransparent;
    public bool Visible { get; init; } = true;
}

public sealed record TextRendererComponent
{
    public string Text { get; init; } = "Text";
    public Guid? TextStyleAssetId { get; init; }
    public Guid? FontAssetId { get; init; }
    public double CharacterHeight { get; init; } = 0.035;
    public ColorValue Color { get; init; } = new(1, 1, 1, 1);
    public Vector2Value Bounds { get; init; } = new(0.4, 0.2);
    public TextFitMode Fit { get; init; } = TextFitMode.Wrap;
    public TextHorizontalAlignment HorizontalAlignment { get; init; } = TextHorizontalAlignment.Left;
    public TextVerticalAlignment VerticalAlignment { get; init; } = TextVerticalAlignment.Top;
    public Vector2Value Pivot { get; init; } = new(0, 0);
    public BillboardMode Billboard { get; init; }
    public RenderSurfacePreset SurfacePreset { get; init; } = RenderSurfacePreset.WorldTransparent;
    public bool Visible { get; init; } = true;
}

public sealed record UiPanelComponent
{
    public string Title { get; init; } = "Panel";
    public UiPanelKind Kind { get; init; } = UiPanelKind.Window;
    public Vector2Value Size { get; init; } = new(0.5, 0.35);
    public bool AutoWidth { get; init; }
    public bool AutoHeight { get; init; }
    public bool MovableInGame { get; init; } = true;
    public bool FarInteraction { get; init; } = true;
    public bool Visible { get; init; } = true;
}

public sealed record UiRectComponent
{
    public UiLayoutMode LayoutMode { get; init; } = UiLayoutMode.Flow;
    public Vector2Value PreferredSize { get; init; } = new(0.2, 0.04);
    public Vector2Value MinimumSize { get; init; } = Vector2Value.Zero;
    public ThicknessValue Margin { get; init; } = ThicknessValue.Zero;
    public ThicknessValue Padding { get; init; } = ThicknessValue.Zero;
    public bool SameLine { get; init; }
    public bool LineBreak { get; init; } = true;
    public UiAnchor Anchor { get; init; } = UiAnchor.TopLeft;
    public Vector2Value Pivot { get; init; } = Vector2Value.Zero;
    public Vector2Value Position { get; init; } = Vector2Value.Zero;
    public Vector2Value Size { get; init; } = new(0.2, 0.04);
    public bool StretchWidth { get; init; }
    public bool StretchHeight { get; init; }
    public bool Clip { get; init; }
}

public sealed record UiTextComponent
{
    public string Text { get; init; } = "Text";
    public Guid? TextStyleAssetId { get; init; }
    public TextHorizontalAlignment Alignment { get; init; } = TextHorizontalAlignment.Left;
    public bool Wrap { get; init; } = true;
    public ColorValue Color { get; init; } = new(1, 1, 1, 1);
}

public sealed record UiImageComponent
{
    public Guid TextureAssetId { get; init; }
    public ImageSizingMode SizingMode { get; init; } = ImageSizingMode.PreserveAspect;
    public ColorValue Tint { get; init; } = new(1, 1, 1, 1);
}

public sealed record UiSpacerComponent;

public sealed record UiSeparatorComponent
{
    public UiSeparatorOrientation Orientation { get; init; } = UiSeparatorOrientation.Horizontal;
}

public sealed record UiButtonComponent
{
    public string Label { get; init; } = "Button";
    public Guid? ImageTextureAssetId { get; init; }
    public string ActionId { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed record UiToggleComponent
{
    public string Label { get; init; } = "Toggle";
    public string BindingId { get; init; } = string.Empty;
    public bool DesignValue { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record UiSliderComponent
{
    public string Label { get; init; } = "Slider";
    public string BindingId { get; init; } = string.Empty;
    public double DesignValue { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public double Increment { get; init; } = 0.01;
    public string Units { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed record UiTextInputComponent
{
    public string Label { get; init; } = "Input";
    public string BindingId { get; init; } = string.Empty;
    public string DesignValue { get; init; } = string.Empty;
    public string Placeholder { get; init; } = string.Empty;
    public int MaximumLength { get; init; } = 256;
    public bool Enabled { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<ImageSizingMode>))]
public enum ImageSizingMode { PreserveAspect, Stretch, Fit, Fill, NativePixels }

[JsonConverter(typeof(JsonStringEnumConverter<BillboardMode>))]
public enum BillboardMode { None, FaceCamera, YAxisOnly }

[JsonConverter(typeof(JsonStringEnumConverter<RenderSurfacePreset>))]
public enum RenderSurfacePreset { WorldOpaque, WorldTransparent, Overlay }

[JsonConverter(typeof(JsonStringEnumConverter<TextFitMode>))]
public enum TextFitMode { Overflow, Wrap, Clip, Squeeze, Exact }

[JsonConverter(typeof(JsonStringEnumConverter<TextHorizontalAlignment>))]
public enum TextHorizontalAlignment { Left, Center, Right }

[JsonConverter(typeof(JsonStringEnumConverter<TextVerticalAlignment>))]
public enum TextVerticalAlignment { Top, Center, Bottom }

[JsonConverter(typeof(JsonStringEnumConverter<UiPanelKind>))]
public enum UiPanelKind { Window, BodyOnly, HeaderOnly, Surface }

[JsonConverter(typeof(JsonStringEnumConverter<UiLayoutMode>))]
public enum UiLayoutMode { Flow, Absolute }

[JsonConverter(typeof(JsonStringEnumConverter<UiAnchor>))]
public enum UiAnchor { TopLeft, TopCenter, TopRight, CenterLeft, Center, CenterRight, BottomLeft, BottomCenter, BottomRight }

[JsonConverter(typeof(JsonStringEnumConverter<UiSeparatorOrientation>))]
public enum UiSeparatorOrientation { Horizontal, Vertical }

internal sealed class Vector2ValueJsonConverter : JsonConverter<Vector2Value>
{
    public override Vector2Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = Vector3ValueJsonConverter.ReadNumbers(ref reader, 2);
        return new(values[0], values[1]);
    }

    public override void Write(Utf8JsonWriter writer, Vector2Value value, JsonSerializerOptions options) =>
        Vector3ValueJsonConverter.WriteNumbers(writer, value.X, value.Y);
}

internal sealed class ThicknessValueJsonConverter : JsonConverter<ThicknessValue>
{
    public override ThicknessValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = Vector3ValueJsonConverter.ReadNumbers(ref reader, 4);
        return new(values[0], values[1], values[2], values[3]);
    }

    public override void Write(Utf8JsonWriter writer, ThicknessValue value, JsonSerializerOptions options) =>
        Vector3ValueJsonConverter.WriteNumbers(writer, value.Left, value.Top, value.Right, value.Bottom);
}
