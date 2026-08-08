using System.Text.Json;
using StereoKitEditor.Adapter;
using System.Text.Json.Nodes;

namespace StereoKitEditor.Tests;

public sealed class AdapterContractTests
{
    [Fact]
    public void OptionalAssetReference_AllowsNullDefaultButRequiredReferenceDoesNot()
    {
        var optional = new EditorAdapterBuilder();
        optional.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "sample.optional-reference",
                SchemaVersion = 1,
                DisplayName = "Optional Reference",
                DefaultData = JsonSerializer.SerializeToElement(new { materialAssetId = (string?)null }),
                Properties =
                [
                    new()
                    {
                        Name = "materialAssetId",
                        DisplayName = "Material",
                        Kind = EditorPropertyKind.AssetReference,
                        AcceptedAssetKinds = ["Material"],
                    },
                ],
            },
            static () => new NoOpRuntime());

        var required = new EditorAdapterBuilder();
        Assert.Throws<ArgumentException>(() => required.RegisterComponent(
            new EditorComponentDescriptor
            {
                TypeId = "sample.required-reference",
                SchemaVersion = 1,
                DisplayName = "Required Reference",
                DefaultData = JsonSerializer.SerializeToElement(new { textureAssetId = (string?)null }),
                Properties =
                [
                    new()
                    {
                        Name = "textureAssetId",
                        DisplayName = "Texture",
                        Kind = EditorPropertyKind.AssetReference,
                        AcceptedAssetKinds = ["Texture"],
                        IsRequired = true,
                    },
                ],
            },
            static () => new NoOpRuntime()));
    }

    [Fact]
    public void Builder_RejectsDuplicateComponentTypeIds()
    {
        var builder = new EditorAdapterBuilder();
        var descriptor = CreateDescriptor("com.example.marker");
        builder.RegisterComponent(descriptor, () => new NoOpRuntime());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterComponent(descriptor, () => new NoOpRuntime()));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_RejectsDuplicatePropertyNames()
    {
        var builder = new EditorAdapterBuilder();
        var descriptor = CreateDescriptor("com.example.invalid") with
        {
            Properties =
            [
                new() { Name = "size", DisplayName = "Size", Kind = EditorPropertyKind.Number },
                new() { Name = "size", DisplayName = "Size again", Kind = EditorPropertyKind.Number },
            ],
        };

        Assert.Throws<ArgumentException>(() =>
            builder.RegisterComponent(descriptor, () => new NoOpRuntime()));
    }

    [Fact]
    public void Builder_RejectsPropertyDefaultWithWrongJsonShape()
    {
        var builder = new EditorAdapterBuilder();
        var descriptor = CreateDescriptor("com.example.invalid-default") with
        {
            DefaultData = JsonSerializer.SerializeToElement(new { visible = "yes" }),
            Properties =
            [
                new() { Name = "visible", DisplayName = "Visible", Kind = EditorPropertyKind.Boolean },
            ],
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.RegisterComponent(descriptor, () => new NoOpRuntime()));

        Assert.Contains("incompatible default", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_AcceptsSafeDeclarativeSliderAndRejectsIncompatiblePresentation()
    {
        var valid = new EditorAdapterBuilder();
        valid.RegisterComponent(
            CreateDescriptor("com.example.slider") with
            {
                DefaultData = JsonSerializer.SerializeToElement(new { size = 0.5 }),
                Properties =
                [
                    new()
                    {
                        Name = "size",
                        DisplayName = "Size",
                        Kind = EditorPropertyKind.Number,
                        Minimum = 0,
                        Maximum = 1,
                        Presentation = EditorPropertyPresentation.Slider,
                    },
                ],
            },
            () => new NoOpRuntime());

        var invalid = new EditorAdapterBuilder();
        var exception = Assert.Throws<ArgumentException>(() => invalid.RegisterComponent(
            CreateDescriptor("com.example.bad-slider") with
            {
                DefaultData = JsonSerializer.SerializeToElement(new { label = "hello" }),
                Properties =
                [
                    new()
                    {
                        Name = "label",
                        DisplayName = "Label",
                        Kind = EditorPropertyKind.String,
                        Presentation = EditorPropertyPresentation.Slider,
                    },
                ],
            },
            () => new NoOpRuntime()));

        Assert.Contains("Slider presentation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_MigratesThroughCompleteDeterministicChain()
    {
        var builder = new EditorAdapterBuilder();
        var descriptor = CreateDescriptor("com.example.migrated") with { SchemaVersion = 3 };
        builder.RegisterComponent(
            descriptor,
            () => new NoOpRuntime(),
            [Migration(1, "second", 2), Migration(2, "third", 3)]);

        var original = JsonSerializer.SerializeToElement(new { first = 1 });
        Assert.True(builder.TryMigrate(
            descriptor.TypeId,
            1,
            original,
            out var version,
            out var migrated,
            out var error), error);
        Assert.Equal(3, version);
        Assert.Equal(1, migrated.GetProperty("first").GetInt32());
        Assert.Equal(2, migrated.GetProperty("second").GetInt32());
        Assert.Equal(3, migrated.GetProperty("third").GetInt32());
        Assert.False(original.TryGetProperty("second", out _));
    }

    [Fact]
    public void Builder_RejectsIncompleteMigrationChain()
    {
        var builder = new EditorAdapterBuilder();
        var descriptor = CreateDescriptor("com.example.incomplete") with { SchemaVersion = 3 };

        var exception = Assert.Throws<ArgumentException>(() => builder.RegisterComponent(
            descriptor,
            () => new NoOpRuntime(),
            [Migration(2, "third", 3)]));

        Assert.Contains("complete migration chain", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_ValidatesDependenciesAndRejectsCycles()
    {
        var missing = new EditorAdapterBuilder();
        missing.RegisterComponent(
            CreateDescriptor("com.example.consumer") with
            {
                RequiredComponentTypeIds = ["com.example.service"],
            },
            () => new NoOpRuntime());
        Assert.Contains(
            "requires unregistered",
            Assert.Throws<InvalidOperationException>(missing.ValidateRegistrations).Message,
            StringComparison.Ordinal);

        var cyclic = new EditorAdapterBuilder();
        cyclic.RegisterComponent(
            CreateDescriptor("com.example.first") with
            {
                RequiredComponentTypeIds = ["com.example.second"],
            },
            () => new NoOpRuntime());
        cyclic.RegisterComponent(
            CreateDescriptor("com.example.second") with
            {
                RequiredComponentTypeIds = ["com.example.first"],
            },
            () => new NoOpRuntime());
        Assert.Contains(
            "dependency cycle",
            Assert.Throws<InvalidOperationException>(cyclic.ValidateRegistrations).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static EditorComponentMigration Migration(int from, string propertyName, int value) => new()
    {
        FromVersion = from,
        ToVersion = from + 1,
        Upgrade = data =>
        {
            var migrated = JsonNode.Parse(data.GetRawText())!.AsObject();
            migrated[propertyName] = value;
            return JsonSerializer.SerializeToElement(migrated);
        },
    };

    private static EditorComponentDescriptor CreateDescriptor(string typeId) => new()
    {
        TypeId = typeId,
        SchemaVersion = 1,
        DisplayName = "Marker",
        DefaultData = JsonSerializer.SerializeToElement(new { }),
    };

    private sealed class NoOpRuntime : IEditorComponentRuntime
    {
        public void Create(EditorComponentContext context, JsonElement data) { }
        public void Apply(EditorComponentContext context, JsonElement data) { }
        public void Step(EditorComponentContext context) { }
        public void Destroy(EditorComponentContext context) { }
    }
}
