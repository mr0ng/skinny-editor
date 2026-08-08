# Extension authoring

Status: **preview API implemented and packaged**

SKinny Editor extends a normal StereoKit project through an explicit runtime adapter. The desktop editor never loads the project assembly. Instead, the isolated Scene or Play process publishes a component catalog containing stable type IDs, property schemas, defaults, dependencies, conflicts, and migration metadata.

This boundary is intentional: project code can render and simulate inside the child process, while a broken or untrusted custom desktop control cannot execute inside the editor UI process.

## Register a component

Reference `SKinny.Editor.Runtime` at the same preview version shipped with the editor, route editor launches to `EditorRuntimeHost`, and register the component from `IEditorProjectAdapter.Configure`:

```csharp
builder.RegisterComponent(
    new EditorComponentDescriptor
    {
        TypeId = "com.example.marker",
        SchemaVersion = 1,
        DisplayName = "Marker",
        Category = "Example",
        Description = "A project-owned Scene and Play component.",
        DefaultData = JsonSerializer.SerializeToElement(new
        {
            size = 0.12,
            note = "Authoring note",
            color = new[] { 0.1, 0.72, 0.66, 1.0 },
        }),
        Properties =
        [
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
                Name = "note",
                DisplayName = "Note",
                Kind = EditorPropertyKind.String,
                Presentation = EditorPropertyPresentation.MultilineText,
            },
            new()
            {
                Name = "color",
                DisplayName = "Color",
                Kind = EditorPropertyKind.Color,
            },
        ],
    },
    () => new MarkerRuntime());
```

The complete executable example is [the Hello StereoKit project](../../samples/HelloStereoKitProject/Program.cs). Its Size field uses the declarative slider presentation, its Color uses the generated color editor, and its runtime geometry advertises local pick bounds.

## Supported Inspector data

The catalog supports Boolean, integer, number, string, enum, flags, Vector2/3/4, quaternion, color, asset reference, and entity reference fields. Metadata supplies descriptions, groups, units, numeric ranges, increments, defaults, read-only state, and options.

`EditorPropertyPresentation` can request:

- `Auto`: the standard editor selected from the property kind;
- `Slider`: a bounded integer/number slider plus exact numeric entry;
- `MultilineText`: a source-controlled multi-line string editor.

Asset and entity references use searchable, identity-backed pickers. Color uses the native color editor. Every edit becomes an editor command and remains undoable; no Inspector control mutates a live runtime object directly.

Project-supplied Avalonia controls are deliberately unsupported in the preview. A component-specific desktop panel would require a separately trusted extension-host design; arbitrary controls are not loaded into the main editor process.

## Runtime lifecycle

Implement `IEditorComponentRuntime` for one instance per component GUID:

```csharp
public sealed class MarkerRuntime : IEditorComponentRuntime
{
    public void Create(EditorComponentContext context, JsonElement data) { }
    public void Apply(EditorComponentContext context, JsonElement data) { }
    public void Step(EditorComponentContext context) { }
    public void Destroy(EditorComponentContext context) { }
}
```

`Create` allocates instance state, `Apply` receives authored data changes, `Step` runs on the StereoKit frame thread, and `Destroy` releases resources. Respect `context.Mode`, `context.PlayState`, and `context.SessionCancellation`. Resolve durable asset GUIDs through `context.Assets`; do not serialize absolute machine paths into component data.

Implement `IEditorComponentPickBoundsProvider` when custom Scene geometry should select its owning object. Return conservative entity-local bounds. Imported node names and draw-call order are never object identity.

## Dependencies, conflicts, and migrations

Use `RequiredComponentTypeIds` and `ConflictingComponentTypeIds` for static composition rules. The adapter rejects missing requirements, cycles, invalid defaults, duplicate type/property IDs, and incompatible declarative presentations before publishing the catalog.

Schema changes require a deterministic one-version-at-a-time migration chain. The runtime proposes upgrades; the editor shows Apply/Later, records Apply as one undo step, preserves unknown/newer data, and creates a backup before the first legacy scene save. Migrations must not depend on time, files, network state, or a live runtime instance.

## Extension verification checklist

1. Build the normal application and run its non-editor entry point.
2. Open the descriptor and confirm the component appears under Add Component.
3. Edit every property type, undo it, save, close, and reopen.
4. Verify Scene and Play create independent runtime instances.
5. Disable/remove the component and confirm `Destroy` runs without leaking state.
6. Change an asset catalog entry without a scene revision and confirm asset-backed state reapplies.
7. Test missing/older/newer schemas and the migration proposal.
8. Force a Scene crash and confirm the editor reconstructs unsaved state once without a restart loop.
9. Run `scripts\verify-package-consumer.ps1` to restore, build, and execute against only the packaged SDK dependency graph.

The adapter contract remains pre-1.0. Pair all four SDK packages at the release version and rebuild the project when the contract version changes.
