using StereoKit;
using StereoKitEditor.Adapter;
using StereoKitEditor.Runtime;
using StereoKitEditor.Scene;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HelloStereoKitProject;

internal static class Program
{
    private static int Main(string[] args) =>
        EditorRuntimeHost.IsEditorLaunch(args)
            ? EditorRuntimeHost.Run(args, new HelloProjectAdapter())
            : RunStandalone();

    private static int RunStandalone()
    {
        var settings = new SKSettings
        {
            appName = "Hello StereoKit Project",
            mode = AppMode.Simulator,
            flatscreenWidth = 900,
            flatscreenHeight = 600,
            standbyMode = StandbyMode.None,
        };
        if (!SK.Initialize(settings))
        {
            return 1;
        }

        var material = Material.Default.Copy();
        SK.Run(() =>
        {
            var rotation = Quat.FromAngles(0, Time.Totalf * 35, 0);
            Mesh.Cube.Draw(
                material,
                Matrix.TRS(new Vec3(0, 0, -0.65f), rotation, new Vec3(0.20f, 0.20f, 0.20f)),
                new Color(0.12f, 0.78f, 0.66f, 1),
                RenderLayer.Layer0);
        });
        return 0;
    }
}

internal sealed class HelloProjectAdapter : IEditorProjectAdapter
{
    private bool _panelEnabled = true;
    private double _panelAmount = 0.5;
    private string _panelMessage = "Hello from the project adapter";
    public string Id => "com.example.hello-project";
    public string DisplayName => "Hello External Project";
    public string Version => "0.1.0";

    public void Configure(EditorAdapterBuilder builder)
    {
        builder.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "com.example.marker",
                SchemaVersion = 2,
                DisplayName = "Project Marker",
                Category = "Hello Project",
                Description = "A project-owned marker rendered by the external StereoKit project.",
                DefaultData = JsonSerializer.SerializeToElement(new
                {
                    color = new[] { 0.1, 0.72, 0.66, 1.0 },
                    size = 0.12,
                    verticalOffset = 0.18,
                    visible = true,
                    shape = "Cube",
                    label = "Marker",
                }),
                Properties =
                [
                    new()
                    {
                        Name = "color",
                        DisplayName = "Color",
                        Kind = EditorPropertyKind.Color,
                    },
                    new()
                    {
                        Name = "size",
                        DisplayName = "Size",
                        Kind = EditorPropertyKind.Number,
                        Minimum = 0.02,
                        Maximum = 0.5,
                        Increment = 0.01,
                        Units = "m",
                        Presentation = EditorPropertyPresentation.Slider,
                    },
                    new()
                    {
                        Name = "verticalOffset",
                        DisplayName = "Vertical Offset",
                        Kind = EditorPropertyKind.Number,
                        Minimum = -1,
                        Maximum = 1,
                        Increment = 0.01,
                        Units = "m",
                    },
                    new()
                    {
                        Name = "visible",
                        DisplayName = "Visible",
                        Kind = EditorPropertyKind.Boolean,
                    },
                    new()
                    {
                        Name = "shape",
                        DisplayName = "Shape",
                        Kind = EditorPropertyKind.Enum,
                        Options = ["Cube", "Sphere"],
                    },
                    new()
                    {
                        Name = "label",
                        DisplayName = "Label",
                        Kind = EditorPropertyKind.String,
                        Description = "An authoring label introduced by the schema-2 migration sample.",
                    },
                ],
            },
            () => new MarkerRuntime(),
            [
                new EditorComponentMigration
                {
                    FromVersion = 1,
                    ToVersion = 2,
                    Upgrade = data =>
                    {
                        var migrated = JsonNode.Parse(data.GetRawText())?.AsObject() ?? new JsonObject();
                        migrated["label"] ??= "Marker";
                        return JsonSerializer.SerializeToElement(migrated, SceneSerializer.Options);
                    },
                },
            ]);

        builder.RegisterBinding(
            new EditorBindingDescriptor
            {
                Id = "hello.enabled",
                DisplayName = "Panel Enabled",
                Kind = EditorBindingValueKind.Boolean,
                Modes = EditorInteractionModes.ScenePreviewAndPlay,
                DesignValue = JsonSerializer.SerializeToElement(true),
            },
            () => JsonSerializer.SerializeToElement(_panelEnabled),
            value => _panelEnabled = value.GetBoolean());
        builder.RegisterBinding(
            new EditorBindingDescriptor
            {
                Id = "hello.amount",
                DisplayName = "Amount",
                Kind = EditorBindingValueKind.Number,
                Modes = EditorInteractionModes.ScenePreviewAndPlay,
                DesignValue = JsonSerializer.SerializeToElement(0.5),
            },
            () => JsonSerializer.SerializeToElement(_panelAmount),
            value => _panelAmount = value.GetDouble());
        builder.RegisterBinding(
            new EditorBindingDescriptor
            {
                Id = "hello.message",
                DisplayName = "Message",
                Kind = EditorBindingValueKind.String,
                Modes = EditorInteractionModes.ScenePreviewAndPlay,
                DesignValue = JsonSerializer.SerializeToElement("Preview text"),
            },
            () => JsonSerializer.SerializeToElement(_panelMessage),
            value => _panelMessage = value.GetString() ?? string.Empty);
        builder.RegisterAction(
            new EditorActionDescriptor
            {
                Id = "hello.reset",
                DisplayName = "Reset Panel",
                Modes = EditorInteractionModes.Play,
            },
            _ =>
            {
                _panelEnabled = true;
                _panelAmount = 0.5;
                _panelMessage = "Reset from the spatial UI";
            });
    }

    public void Initialize(EditorProjectRuntimeContext context)
    {
        Log.Info($"[Adapter Sample] Initialized {context.Mode} from HelloStereoKitProject.csproj");
    }

    public void Step(EditorProjectRuntimeContext context)
    {
    }

    public void Shutdown(EditorProjectRuntimeContext context)
    {
        Log.Info($"[Adapter Sample] Shut down {context.Mode}");
    }
}

internal sealed class MarkerRuntime : IEditorComponentRuntime, IEditorComponentPickBoundsProvider
{
    private Material? _material;
    private Color _color = Color.White;
    private float _size = 0.12f;
    private float _verticalOffset = 0.18f;
    private bool _visible = true;
    private bool _sphere;

    public void Create(EditorComponentContext context, JsonElement data)
    {
        _material = Material.Default.Copy();
        Apply(context, data);
    }

    public void Apply(EditorComponentContext context, JsonElement data)
    {
        _visible = data.TryGetProperty("visible", out var visible) && visible.GetBoolean();
        _size = ReadClampedNumber(data, "size", 0.12f, 0.02f, 0.5f);
        _verticalOffset = ReadClampedNumber(data, "verticalOffset", 0.18f, -1, 1);
        _sphere = data.TryGetProperty("shape", out var shape)
            && string.Equals(shape.GetString(), "Sphere", StringComparison.OrdinalIgnoreCase);
        _color = ReadColor(data);
    }

    public void Step(EditorComponentContext context)
    {
        if (!_visible || _material is null)
        {
            return;
        }

        var transform = context.Transform;
        var pulse = context.Mode == EditorRuntimeMode.Play
            ? 1 + (MathF.Sin(context.SimulationTime * 3) * 0.08f)
            : 1;
        var entityMatrix = Matrix.TRS(
            new Vec3(
                (float)transform.PositionX,
                (float)transform.PositionY,
                (float)transform.PositionZ),
            new Quat(
                (float)transform.RotationX,
                (float)transform.RotationY,
                (float)transform.RotationZ,
                (float)transform.RotationW),
            new Vec3(
                (float)transform.ScaleX,
                (float)transform.ScaleY,
                (float)transform.ScaleZ));
        var markerMatrix = Matrix.TRS(
            new Vec3(0, _verticalOffset, 0),
            Quat.Identity,
            new Vec3(_size * pulse, _size * pulse, _size * pulse));
        (_sphere ? Mesh.Sphere : Mesh.Cube).Draw(
            _material,
            markerMatrix * entityMatrix,
            _color,
            RenderLayer.Layer0);
    }

    public void Destroy(EditorComponentContext context)
    {
        _material = null;
    }

    public EditorPickBounds? GetLocalPickBounds(EditorComponentContext context) =>
        !_visible
            ? null
            : new EditorPickBounds(
                0,
                _verticalOffset,
                0,
                _size,
                _size,
                _size);

    private static float ReadClampedNumber(
        JsonElement data,
        string name,
        float fallback,
        float minimum,
        float maximum) =>
        data.TryGetProperty(name, out var property) && property.TryGetSingle(out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static Color ReadColor(JsonElement data)
    {
        if (!data.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.Array)
        {
            return new Color(0.1f, 0.72f, 0.66f, 1);
        }

        var values = color.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        return values.Length == 4
            ? new Color(values[0], values[1], values[2], values[3])
            : new Color(0.1f, 0.72f, 0.66f, 1);
    }
}
