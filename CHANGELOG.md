# Changelog

All notable public changes to SKinny Editor are recorded here.

## [Unreleased]

### Added

- A launcher-based New Project workflow that safely generates a complete StereoKit application from a bundled, versioned starter template.
- Generated projects include fresh document IDs, a normal standalone entry point, editor adapter, initial scene, pinned local SDK packages, and first-open trust/build validation.
- Read-only analysis and compatibility reporting for existing StereoKit solutions and projects.
- Previewed direct opt-in and dedicated editor-head scaffolding with transactional apply,
  persistent reports, backups, and hash-guarded rollback that preserves later user edits.
- A shared **Open Project** menu for opening SKinny descriptors, importing existing
  StereoKit projects, and accessing recent projects from the launcher or editor.

### Known limitations

- Direct opt-in still requires explicit review of the generated startup hook, and deeper
  restore/build/Scene/Play validation requires workspace trust.

## [0.3.0-preview.1] - 2026-08-08

First public preview.

### Added

- Resizable desktop editor with Hierarchy, Scene, Inspector, Project, and Console panels.
- Separate Scene and Game runtimes with project trust and isolated process management.
- Structured scene authoring with child objects, multi-selection, rename, duplicate, delete, undo, redo, and recovery.
- Move, Rotate, and Scale tools, camera navigation, snapping, framing, and orientation gizmos.
- GLB, texture, image, material, font, text, Quad, and spatial UI authoring workflows.
- Project-owned component catalogs and generated Inspector presentations.
- Four matching editor SDK packages and a bundled example project.
- Self-contained, portable Windows x64 distribution with a SHA-256 checksum.

### Known limitations

- The preview is distributed as an unsigned portable ZIP, not an installer.
- Existing projects use the descriptor and runtime-adapter onboarding workflow.
- Arbitrary procedural content that is only created by project code remains runtime-owned and may be opaque to the editor.
- Scene, protocol, and adapter contracts remain pre-1.0 and may change.
- Hardware-specific DPI, GPU, device-loss, and headset coverage is still expanding.

[0.3.0-preview.1]: https://github.com/mr0ng/skinny-editor/releases/tag/v0.3.0-preview.1
