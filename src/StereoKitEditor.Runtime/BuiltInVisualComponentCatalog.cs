using System.Text.Json;
using System.Text.Json.Nodes;
using StereoKitEditor.Adapter;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

public static partial class EditorRuntimeHost
{
    private static void RegisterPhase5BuiltInComponents(EditorAdapterBuilder builder)
    {
        builder.RegisterComponent(
            Descriptor(
                BuiltInComponentTypes.PrimitiveMeshRenderer,
                2,
                "Primitive Mesh Renderer",
                "Rendering",
                "Renders Cube, Sphere, or Quad geometry with a reusable Material or direct Texture override.",
                new PrimitiveMeshRendererComponent(),
                RendererConflicts(BuiltInComponentTypes.PrimitiveMeshRenderer),
                [
                    EnumProperty("primitive", "Primitive", ["Cube", "Sphere", "Quad"]),
                    AssetProperty("materialAssetId", "Material", "Material"),
                    AssetProperty("baseColorTextureOverrideId", "Base Texture Override", "Texture"),
                    ColorProperty("color", "Tint"),
                    Vector2Property("uvScale", "UV Scale"),
                    Vector2Property("uvOffset", "UV Offset"),
                    BoolProperty("visible", "Visible"),
                ]),
            static () => new NoOpBuiltInRuntime(),
            [new EditorComponentMigration
            {
                FromVersion = 1,
                ToVersion = 2,
                Upgrade = data =>
                {
                    var value = JsonNode.Parse(data.GetRawText())?.AsObject() ?? new JsonObject();
                    value.TryAdd("materialAssetId", null);
                    value.TryAdd("baseColorTextureOverrideId", null);
                    value.TryAdd("uvScale", new JsonArray(1, 1));
                    value.TryAdd("uvOffset", new JsonArray(0, 0));
                    return JsonSerializer.SerializeToElement(value, SceneSerializer.Options);
                },
            }]);

        builder.RegisterComponent(
            Descriptor(
                BuiltInComponentTypes.ImageRenderer,
                1,
                "Image Renderer",
                "Rendering",
                "Places a Texture as an aspect-aware world-space image.",
                new ImageRendererComponent(),
                RendererConflicts(BuiltInComponentTypes.ImageRenderer),
                [
                    RequiredAssetProperty("textureAssetId", "Texture", "Texture"),
                    Vector2Property("size", "Size", "m"),
                    EnumProperty("sizingMode", "Sizing", ["PreserveAspect", "Stretch", "Fit", "Fill", "NativePixels"]),
                    NumberProperty("pixelsPerMeter", "Pixels per Meter", 1, 100000, 10),
                    Vector2Property("pivot", "Pivot"),
                    ColorProperty("tint", "Tint"),
                    BoolProperty("doubleSided", "Double Sided"),
                    EnumProperty("billboard", "Billboard", ["None", "FaceCamera", "YAxisOnly"]),
                    EnumProperty("surfacePreset", "Surface", ["WorldOpaque", "WorldTransparent", "Overlay"]),
                    BoolProperty("visible", "Visible"),
                ]),
            static () => new NoOpBuiltInRuntime());

        builder.RegisterComponent(
            Descriptor(
                BuiltInComponentTypes.TextRenderer,
                1,
                "Text Renderer",
                "Rendering",
                "Renders measured, selectable, styleable world-space text.",
                new TextRendererComponent(),
                RendererConflicts(BuiltInComponentTypes.TextRenderer),
                [
                    new EditorPropertyDescriptor { Name = "text", DisplayName = "Text", Kind = EditorPropertyKind.String, Presentation = EditorPropertyPresentation.MultilineText },
                    AssetProperty("textStyleAssetId", "Text Style", "TextStyle"),
                    AssetProperty("fontAssetId", "Font Override", "Font"),
                    NumberProperty("characterHeight", "Character Height", 0.001, 10, 0.001, "m"),
                    ColorProperty("color", "Color"),
                    Vector2Property("bounds", "Layout Bounds", "m"),
                    EnumProperty("fit", "Fit", ["Overflow", "Wrap", "Clip", "Squeeze", "Exact"]),
                    EnumProperty("horizontalAlignment", "Horizontal", ["Left", "Center", "Right"]),
                    EnumProperty("verticalAlignment", "Vertical", ["Top", "Center", "Bottom"]),
                    Vector2Property("pivot", "Pivot"),
                    EnumProperty("billboard", "Billboard", ["None", "FaceCamera", "YAxisOnly"]),
                    EnumProperty("surfacePreset", "Surface", ["WorldOpaque", "WorldTransparent", "Overlay"]),
                    BoolProperty("visible", "Visible"),
                ]),
            static () => new NoOpBuiltInRuntime());

        builder.RegisterComponent(
            Descriptor(
                BuiltInComponentTypes.UiPanel,
                1,
                "UI Panel",
                "Spatial UI",
                "Compiles retained child entities into a StereoKit spatial UI surface.",
                new UiPanelComponent(),
                RendererConflicts(BuiltInComponentTypes.UiPanel),
                [
                    StringProperty("title", "Title"),
                    EnumProperty("kind", "Panel Kind", ["Window", "BodyOnly", "HeaderOnly", "Surface"]),
                    Vector2Property("size", "Size", "m"),
                    BoolProperty("autoWidth", "Auto Width"),
                    BoolProperty("autoHeight", "Auto Height"),
                    BoolProperty("movableInGame", "Movable in Game"),
                    BoolProperty("farInteraction", "Far Interaction"),
                    BoolProperty("visible", "Visible"),
                ]),
            static () => new NoOpBuiltInRuntime());

        builder.RegisterComponent(
            Descriptor(
                BuiltInComponentTypes.UiRect,
                1,
                "UI Rect",
                "Spatial UI/Layout",
                "Controls an element's retained Flow or Absolute panel layout.",
                new UiRectComponent(),
                [],
                [
                    EnumProperty("layoutMode", "Layout", ["Flow", "Absolute"]),
                    Vector2Property("preferredSize", "Preferred Size", "m"),
                    Vector2Property("minimumSize", "Minimum Size", "m"),
                    Vector4Property("margin", "Margin", "m"),
                    Vector4Property("padding", "Padding", "m"),
                    BoolProperty("sameLine", "Same Line"),
                    BoolProperty("lineBreak", "Line Break"),
                    EnumProperty("anchor", "Anchor", ["TopLeft", "TopCenter", "TopRight", "CenterLeft", "Center", "CenterRight", "BottomLeft", "BottomCenter", "BottomRight"]),
                    Vector2Property("pivot", "Pivot"),
                    Vector2Property("position", "Position", "m"),
                    Vector2Property("size", "Absolute Size", "m"),
                    BoolProperty("stretchWidth", "Stretch Width"),
                    BoolProperty("stretchHeight", "Stretch Height"),
                    BoolProperty("clip", "Clip"),
                ]),
            static () => new NoOpBuiltInRuntime());

        RegisterUiElement(builder, BuiltInComponentTypes.UiText, "UI Text", new UiTextComponent(),
        [
            new EditorPropertyDescriptor { Name = "text", DisplayName = "Text", Kind = EditorPropertyKind.String, Presentation = EditorPropertyPresentation.MultilineText },
            AssetProperty("textStyleAssetId", "Text Style", "TextStyle"),
            EnumProperty("alignment", "Alignment", ["Left", "Center", "Right"]),
            BoolProperty("wrap", "Wrap"),
            ColorProperty("color", "Color"),
        ]);
        RegisterUiElement(builder, BuiltInComponentTypes.UiImage, "UI Image", new UiImageComponent(),
        [
            RequiredAssetProperty("textureAssetId", "Texture", "Texture"),
            EnumProperty("sizingMode", "Sizing", ["PreserveAspect", "Stretch", "Fit", "Fill", "NativePixels"]),
            ColorProperty("tint", "Tint"),
        ]);
        RegisterUiElement(builder, BuiltInComponentTypes.UiSpacer, "UI Spacer", new UiSpacerComponent(), []);
        RegisterUiElement(builder, BuiltInComponentTypes.UiSeparator, "UI Separator", new UiSeparatorComponent(),
        [EnumProperty("orientation", "Orientation", ["Horizontal", "Vertical"])]);
        RegisterUiElement(builder, BuiltInComponentTypes.UiButton, "UI Button", new UiButtonComponent(),
        [
            StringProperty("label", "Label"),
            AssetProperty("imageTextureAssetId", "Image", "Texture"),
            StringProperty("actionId", "Action ID"),
            BoolProperty("enabled", "Enabled"),
        ]);
        RegisterUiElement(builder, BuiltInComponentTypes.UiToggle, "UI Toggle", new UiToggleComponent(),
        [
            StringProperty("label", "Label"),
            StringProperty("bindingId", "Binding ID"),
            BoolProperty("designValue", "Design Value"),
            BoolProperty("enabled", "Enabled"),
        ]);
        RegisterUiElement(builder, BuiltInComponentTypes.UiSlider, "UI Slider", new UiSliderComponent(),
        [
            StringProperty("label", "Label"),
            StringProperty("bindingId", "Binding ID"),
            NumberProperty("designValue", "Design Value", -1000000, 1000000, 0.01),
            NumberProperty("minimum", "Minimum", -1000000, 1000000, 0.01),
            NumberProperty("maximum", "Maximum", -1000000, 1000000, 0.01),
            NumberProperty("increment", "Increment", 0.000001, 1000000, 0.01),
            StringProperty("units", "Units"),
            BoolProperty("enabled", "Enabled"),
        ]);
        RegisterUiElement(builder, BuiltInComponentTypes.UiTextInput, "UI Text Input", new UiTextInputComponent(),
        [
            StringProperty("label", "Label"),
            StringProperty("bindingId", "Binding ID"),
            StringProperty("designValue", "Design Value"),
            StringProperty("placeholder", "Placeholder"),
            IntegerProperty("maximumLength", "Maximum Length", 1, 32768),
            BoolProperty("enabled", "Enabled"),
        ]);
    }

    private static void RegisterUiElement<T>(
        EditorAdapterBuilder builder,
        string typeId,
        string displayName,
        T defaultData,
        IReadOnlyList<EditorPropertyDescriptor> properties)
    {
        builder.RegisterComponent(
            Descriptor(
                typeId,
                1,
                displayName,
                "Spatial UI/Elements",
                $"Retained {displayName} element compiled by its parent UI Panel.",
                defaultData!,
                UiElementConflicts(typeId),
                properties,
                required: [BuiltInComponentTypes.UiRect]),
            static () => new NoOpBuiltInRuntime());
    }

    private static EditorComponentDescriptor Descriptor<T>(
        string typeId,
        int version,
        string displayName,
        string category,
        string description,
        T defaultData,
        IReadOnlyList<string> conflicts,
        IReadOnlyList<EditorPropertyDescriptor> properties,
        IReadOnlyList<string>? required = null) => new()
        {
            TypeId = typeId,
            SchemaVersion = version,
            DisplayName = displayName,
            Category = category,
            Description = description,
            ConflictingComponentTypeIds = conflicts,
            RequiredComponentTypeIds = required ?? [],
            DefaultData = JsonSerializer.SerializeToElement(defaultData, SceneSerializer.Options),
            Properties = properties,
        };

    private static IReadOnlyList<string> RendererConflicts(string self) =>
    new[]
    {
        BuiltInComponentTypes.PrimitiveMeshRenderer,
        BuiltInComponentTypes.ModelRenderer,
        BuiltInComponentTypes.ImageRenderer,
        BuiltInComponentTypes.TextRenderer,
        BuiltInComponentTypes.UiPanel,
    }.Where(typeId => !string.Equals(typeId, self, StringComparison.Ordinal)).ToArray();

    private static IReadOnlyList<string> UiElementConflicts(string self) =>
    new[]
    {
        BuiltInComponentTypes.UiText,
        BuiltInComponentTypes.UiImage,
        BuiltInComponentTypes.UiSpacer,
        BuiltInComponentTypes.UiSeparator,
        BuiltInComponentTypes.UiButton,
        BuiltInComponentTypes.UiToggle,
        BuiltInComponentTypes.UiSlider,
        BuiltInComponentTypes.UiTextInput,
    }.Where(typeId => !string.Equals(typeId, self, StringComparison.Ordinal)).ToArray();

    private static EditorPropertyDescriptor AssetProperty(string name, string displayName, params string[] kinds) => new()
    {
        Name = name,
        DisplayName = displayName,
        Kind = EditorPropertyKind.AssetReference,
        AcceptedAssetKinds = kinds,
    };

    private static EditorPropertyDescriptor RequiredAssetProperty(string name, string displayName, params string[] kinds) => new()
    {
        Name = name,
        DisplayName = displayName,
        Kind = EditorPropertyKind.AssetReference,
        AcceptedAssetKinds = kinds,
        IsRequired = true,
    };

    private static EditorPropertyDescriptor BoolProperty(string name, string displayName) => new()
    { Name = name, DisplayName = displayName, Kind = EditorPropertyKind.Boolean };

    private static EditorPropertyDescriptor StringProperty(string name, string displayName) => new()
    { Name = name, DisplayName = displayName, Kind = EditorPropertyKind.String };

    private static EditorPropertyDescriptor ColorProperty(string name, string displayName) => new()
    { Name = name, DisplayName = displayName, Kind = EditorPropertyKind.Color };

    private static EditorPropertyDescriptor Vector2Property(string name, string displayName, string? units = null) => new()
    { Name = name, DisplayName = displayName, Kind = EditorPropertyKind.Vector2, Units = units };

    private static EditorPropertyDescriptor Vector4Property(string name, string displayName, string? units = null) => new()
    { Name = name, DisplayName = displayName, Kind = EditorPropertyKind.Vector4, Units = units };

    private static EditorPropertyDescriptor EnumProperty(string name, string displayName, IReadOnlyList<string> options) => new()
    { Name = name, DisplayName = displayName, Kind = EditorPropertyKind.Enum, Options = options };

    private static EditorPropertyDescriptor NumberProperty(
        string name,
        string displayName,
        double minimum,
        double maximum,
        double increment,
        string? units = null) => new()
        {
            Name = name,
            DisplayName = displayName,
            Kind = EditorPropertyKind.Number,
            Minimum = minimum,
            Maximum = maximum,
            Increment = increment,
            Units = units,
        };

    private static EditorPropertyDescriptor IntegerProperty(
        string name,
        string displayName,
        int minimum,
        int maximum) => new()
        {
            Name = name,
            DisplayName = displayName,
            Kind = EditorPropertyKind.Integer,
            Minimum = minimum,
            Maximum = maximum,
            Increment = 1,
        };
}
