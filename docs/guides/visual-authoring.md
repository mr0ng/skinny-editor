# Visual authoring guide

This guide covers the currently implemented preview workflows.

Status: **implemented in Phase 5; Windows authoring first**  
Updated: **2026-08-08**

This guide covers the structured visual content SKinny Editor can save, reopen, edit, and run in isolated Scene and Game hosts.

## Fast path

1. Open a `.skproject.json` project.
2. Use **Import…** in the Project panel to add an image, TrueType font, or GLB model.
3. Create a **Quad**, **Image**, **Text**, or **Spatial UI Panel** from the create menu.
4. Drag a Texture or Material from Project onto a compatible selected object, or choose it from the component Inspector.
5. Save the scene, then use **Play** to verify the cloned runtime behavior.

The packaged `HelloEditor` example demonstrates every major path and includes a reusable **Spatial Status Panel** template.

## Textures and images

Imported image files become Texture assets with a stable GUID sidecar, dimensions, aspect ratio, alpha information when detectable, a thumbnail, diagnostics, and editable import settings. PNG and JPEG are the mandatory tested path; other still-image formats may be passed through when StereoKit supports them, with a generic preview if the editor cannot decode a thumbnail.

Texture settings include color space, intended usage, sampling, address mode, mipmaps, and alpha intent. UI/color images normally use sRGB; normal/data textures should use Linear.

- Double-clicking or dropping a Texture into an empty Scene area creates an aspect-aware Image object.
- Dropping a Texture onto a Cube, Sphere, or Quad sets its base-texture override.
- Dropping a Texture onto an Image or UI Image replaces its source.
- Image Renderer exposes size, sizing mode, pixels per meter, pivot, tint, double-sided, billboard, and surface/depth behavior.

## Quads and materials

A Quad is geometry; a Material is a reusable authored surface. Keeping them separate lets several objects share one Material without generating hidden files.

Create Material assets from **Project → + Asset → Material**. The Material editor supports:

- Standard and Unlit shader families;
- base color, normal, metal/roughness, occlusion, and emission texture references;
- tint, metallic, roughness, emission, UV scale/offset;
- opaque, cutout, blend, and additive behavior;
- alpha cutoff, culling, depth write/test, and a bounded queue offset.

Primitive renderers support a Material, an optional per-object base Texture override, tint, and UV scale/offset. Model Renderer supports one global Material override plus an individual Material picker for each visual slot discovered in the GLB.

## Text and text styles

Create standalone Text from the Scene create menu. Text Renderer supports multiline text, optional Text Style, optional direct Font override, character height, color, bounds, wrapping/fit, horizontal/vertical alignment, pivot, billboard, and surface behavior.

TrueType `.ttf` files import as Font assets. Create Text Style assets from **Project → + Asset → Text Style** to share a Font, character height, color, and optional Material. Font and Text Style changes invalidate only dependent cached resources.

## Spatial UI

Create a **Panel**, then select it or one of its descendants before adding UI Text, Image, Spacer, Separator, Button, Toggle, Slider, or Text Input children. The Hierarchy is the durable UI tree and its order is the draw/layout order.

Every UI element has a UI Rect:

- **Flow** uses preferred/minimum size, margin, padding, same-line, line-break, and stretch controls.
- **Absolute** uses anchor, pivot, position, size, margin, and stretch controls.

In **Edit UI** mode, controls cannot fire. Clicking selects panel elements; orange corner handles resize the selected panel/element, and an absolute element's cyan anchor handle can be dragged to another anchor while preserving its visible position. These commits are undoable scene edits. In **Preview UI**, controls use design values but project actions remain isolated. In **Game**, the cloned scene can read/write registered bindings and invoke registered actions; that runtime state is discarded on Stop.

The adapter registers stable IDs rather than serializing delegates:

```csharp
builder.RegisterBinding(/* stable ID, type, read, write */);
builder.RegisterAction(/* stable ID, callback */);
```

The Inspector offers registered IDs that match each control's expected value type. Missing or incompatible IDs remain visible as diagnostics rather than silently binding to something else.

## Reuse and dependencies

Save any selected subtree as a Scene Template. Instantiating a template regenerates entity and component IDs while preserving asset GUID references. Deleting a Texture, Font, Material, or Text Style reports its direct and transitive dependents before moving anything to the recoverable project trash.

## Important boundary

SKinny can author only the structured components stored in its scene document. Arbitrary project code such as direct `UI.*`, `Text.Add`, `Mesh.Draw`, or `Sprite.Draw` calls still renders at runtime but cannot be reconstructed into editable hierarchy items. A project can make custom behavior authorable by registering explicit components, bindings, and actions through the adapter.

## Current intentional limits

- still images only; no animated GIF, video surface, or render-to-texture authoring;
- TrueType font import is the first supported font path;
- Standard/Unlit authored Materials, not a shader graph;
- a deliberate StereoKit UI subset, not HTML/CSS or general desktop layout;
- the current editor and simulator distribution targets Windows; a separate macOS edition may follow in a future roadmap.

## Verification

Phase 5 is covered by the automated editor suite, clean adapter `0.3` package consumption, native Scene input, self-contained Windows packaging, checksum/startup validation, and isolated packaged Scene/Play probes with the typed sample asset catalog. Cross-GPU alpha quality, DPI combinations, hand/controller feel, and graphics-device loss remain hands-on release acceptance checks.
