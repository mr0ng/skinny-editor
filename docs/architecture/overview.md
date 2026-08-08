# Architecture overview

SKinny Editor is a Windows-first visual editor for StereoKit projects. It uses
a desktop editor shell for authoring and separate StereoKit runtime processes
for the live Scene and isolated Play experiences.

## System shape

```text
SKinny Editor desktop shell
  ├─ project, hierarchy, inspector, assets, and console
  ├─ scene document, commands, undo/redo, and persistence
  ├─ project build and immutable runtime generations
  ├─ embedded Scene runtime process
  └─ isolated Play runtime process
          │
          └─ project adapter and explicit component catalog
```

The processes communicate through a versioned local protocol. Runtime crashes,
hangs, and project-code failures stay outside the desktop editor process so the
authoring session can report the failure and recover.

## Structured authoring boundary

StereoKit applications are code-first. Arbitrary draw calls cannot be reliably
reconstructed into a durable hierarchy after they run. SKinny Editor therefore
authors an explicit scene model containing entities, transforms, components,
and stable asset references.

Projects can opt in through the editor SDK and register a component catalog.
The catalog describes editable properties and connects serialized component
instances to project-owned runtime behavior. Unknown component data is retained
so opening and saving a scene does not silently discard unsupported content.

## Scene and Play

Scene is an always-available authoring runtime driven by the current edit
document. Play starts from a deep-cloned snapshot in a separate runtime process.
Changes made while Play is running do not mutate the edit document unless an
explicit authoring command is submitted.

## Assets

Imported assets receive stable sidecar identities. Scene data refers to those
identities rather than absolute machine paths, allowing assets to move within a
project without breaking every scene reference. Runtime catalogs resolve the
stable identities to local files when a Scene or Play process starts.

## Trust and isolation

Opening a project can invoke its build and runtime code. The editor requires an
explicit trust decision before doing so, reports the command and working
directory, uses current-user local communication, and cleans up child processes
when a runtime or the editor exits.

## Platform direction

The editor shell, native window embedding, input bridge, packaging, installer,
and initial device workflows target Windows. Other desktop platforms are
deferred until the Windows product reaches a stable beta and demand justifies
the additional native integration work.
