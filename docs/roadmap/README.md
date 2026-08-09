# Public roadmap

This roadmap communicates product direction for the Windows preview. It is not
a release guarantee; ordering can change based on project compatibility,
testing, and contributor feedback.

## Delivered preview foundation

- Resizable desktop shell with Hierarchy, Scene, Inspector, Project, and Console
- Structured scene documents with stable entity and component identities
- Separate embedded Scene and isolated Play runtimes
- Move, Rotate, and Scale tools, camera navigation, snapping, and undo/redo
- Child entities, reparenting, rename, duplicate, delete, and multi-selection
- GLB import, stable asset identities, thumbnails, and Model Renderer authoring
- Textures, images, Quads, materials, fonts, text, and structured spatial UI
- Project-owned component catalogs and generated Inspector presentations
- Workspace trust, diagnostics, crash recovery, and child-process cleanup
- Preview SDK packages and a self-contained Windows distribution
- Launcher-based starter-project generation with pinned local SDK packages
- Read-only existing-project analysis, compatibility reports, reviewed scaffolding,
  transaction manifests, persistent onboarding reports, and hash-guarded rollback

## Current product focus

The next major phase makes project entry and onboarding easier while retaining
the portable Windows ZIP as the supported distribution:

1. Complete Project Hub workflows for new, existing, recent, and imported projects
2. Expand new-project template/version selection and automatic Scene/Play verification
3. Expand existing-project onboarding with trust-gated restore/build/Scene/Play validation
4. Broaden compatibility fixtures and guided manual-integration remediation

See [Windows project entry and onboarding](product-entry-and-onboarding.md) for
the detailed implementation sequence.

## Ongoing usability and compatibility

- Continue viewport, gizmo, selection, docking, and Inspector usability testing
- Expand typed asset pickers, material workflows, and spatial UI layout tools
- Validate the adapter against a broader set of ordinary StereoKit projects
- Improve compatibility diagnostics without silently rewriting project code
- Add hardware-dependent DPI, GPU, device-loss, and headset acceptance coverage

## Deferred

- Installer/uninstaller, Start-menu and shell integration, code signing,
  release channels, and automatic updates; revisit if user demand makes the
  portable ZIP insufficient
- macOS and Linux editor shells
- A general-purpose visual scripting system
- Automatic reconstruction of arbitrary runtime draw calls
- Broad marketplace or plugin-distribution infrastructure
- Production-grade collaborative editing
