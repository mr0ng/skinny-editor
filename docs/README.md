# SKinny Editor documentation

This directory contains the documentation intended to ship with the public
repository. It explains the product, supported workflows, architecture, and
direction without exposing private reference projects or machine-specific
planning notes.

## Releases

- [0.3.0-preview.1](releases/0.3.0-preview.1.md) — first public preview
- [Changelog](../CHANGELOG.md) — release history and known limitations

## Architecture

- [Architecture overview](architecture/overview.md) — process boundaries,
  project integration, safety boundaries, and the structured-authoring model.

## Guides

- [Installation and onboarding](guides/installation-and-onboarding.md) — run a
  packaged build, open a project, integrate the SDK, and recover local state.
- [Extension authoring](guides/extension-authoring.md) — register components,
  expose Inspector properties, implement lifecycle callbacks, and verify an
  extension.
- [Visual authoring](guides/visual-authoring.md) — work with textures, images,
  Quads, materials, text, and spatial UI.

## Roadmap

- [Public roadmap](roadmap/README.md) — delivered capabilities, current product
  focus, later work, and deliberately deferred scope.
- [Windows project entry and onboarding](roadmap/product-entry-and-onboarding.md)
  — launcher workflows, new-project creation, and existing-project import.
- [Visual content and spatial UI](roadmap/visual-content-and-spatial-ui.md) —
  delivered visual-authoring architecture and remaining usability work.

The roadmap describes direction, not a release guarantee. Priorities can change
as the preview is tested against more StereoKit projects.
