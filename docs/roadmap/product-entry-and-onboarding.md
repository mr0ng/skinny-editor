# Windows installation, Project Hub, and project onboarding

Status: **in progress — existing-project transactional foundation delivered**

## Purpose

Phase 4 turns the current portable, descriptor-driven Windows preview into a product that can be launched, installed, and used without starting in a terminal or manually scaffolding editor integration.

The phase does not weaken the existing project boundary: StereoKit projects remain normal `.sln`/`.csproj` projects, project code continues to run outside the editor process, and onboarding must not silently convert or take ownership of an existing application.

## Current baseline versus Phase 4

| Capability | Current baseline | Phase 4 result |
|---|---|---|
| Application identity | Branded multi-resolution icon is already applied to the executable, windows, taskbar, and dialogs | The same identity flows through installer, uninstall entry, shortcuts, file launch, and update UI |
| Distribution | Self-contained portable Windows folder and verified ZIP/checksum | Signed installer, uninstall support, Start-menu entry, and optional desktop shortcut |
| Starting the editor | Run the executable or pass `--project`; the no-project build shows a basic launcher | A dedicated Project Hub is the normal start surface |
| Existing SKinny project | Browse to or pass an existing `.skproject.json`; recent projects are available in the editor | Open and manage existing projects from the Hub, Explorer/file launch, and validated recents |
| New project | A launcher workflow generates the bundled, pinned starter template and opens it through first-run trust/build validation | Selectable templates, target/version choices, staged restore/build, and automatic first Scene/Play verification |
| Existing StereoKit project | Safe analysis, compatibility report, reviewed direct/dedicated scaffolding, transaction report, safe validation, and rollback | Trust-gated restore/build/handshake/Scene/Play validation and broader guided remediation |
| Updates | Replace the portable folder manually | Defined signing, release-channel, update-check, rollback, and compatibility strategy |

## Product principles

1. **Non-destructive by default.** Analysis does not write. No onboarding files are changed until the user reviews and confirms an explicit proposal.
2. **Normal StereoKit use remains valid.** An onboarded project must retain its ordinary command-line/IDE build and launch path. Editor-specific code is isolated behind editor launch arguments or a dedicated head.
3. **Honest compatibility.** The Hub reports what can be authored, what can only be run, and what remains opaque. It never promises to reconstruct arbitrary code-first draw calls into a hierarchy.
4. **Transactional changes.** Planned writes have a manifest, preflight validation, recoverable backups where existing files change, and a tested rollback path.
5. **Pinned and reproducible dependencies.** Generated projects record explicit .NET, StereoKit, runtime SDK, adapter, protocol, and scene-format versions.
6. **Trust remains a boundary.** Opening a descriptor or double-clicking a project file may inspect safe metadata, but it does not build or execute project code until workspace trust is granted.

## Workstream A — Windows installation and desktop integration

### Scope

- Retain and verify the delivered branded application icon at all Windows shell sizes.
- Produce an installer with a normal uninstall entry.
- Create a Start-menu application entry.
- Offer a desktop shortcut as an explicit installer option.
- Register the selected project-file launch experience.
- Preserve user projects during uninstall. Preferences, caches, recovery data, and SDK packages need individually documented retain/remove behavior.
- Define per-user versus per-machine installation, elevation, repair, upgrade, and downgrade behavior.
- Sign installer, executable, updater, and release metadata when a production certificate is available.
- Define stable/preview channels, update discovery, download verification, compatibility checks, rollback, and release notes.

### Project-file association decision gate

Windows normally associates the final filename extension, so `.skproject.json` is treated as `.json`. Claiming `.json` would be unsafe because it would affect unrelated JSON files.

Before shell registration, choose and test one of these approaches:

1. Introduce a dedicated project extension such as `.skinnyproject` whose contents retain the versioned JSON descriptor shape; this is the recommended low-complexity option.
2. Keep `.skproject.json` and add a narrowly scoped Explorer shell verb/handler that recognizes the complete suffix; this carries more implementation and maintenance cost.
3. Keep `.skproject.json` browse/drag-and-drop only and defer direct Explorer launch.

The installer must not register SKinny Editor as the default handler for all `.json` files.

### Acceptance criteria

- A clean Windows user can install, launch from Start, optionally launch from a desktop shortcut, repair/upgrade, and uninstall.
- Explorer project launch opens the Hub/project safely without running code before trust.
- Upgrade preserves projects, recents, preferences, and compatible recovery data.
- Uninstall behavior clearly states which per-user data is retained and offers an explicit cleanup path.
- Every downloaded update is authenticated before execution, and a failed update leaves a runnable prior version.

## Workstream B — Project Hub

The Hub becomes the default no-project window and the return point after closing a project.

### Primary actions

- **New Project** — choose a template, project name, location, .NET target, StereoKit version, and supported editor SDK version.
- **Import Existing Project** — analyze a normal StereoKit solution/project that does not yet have SKinny metadata.
- **Open Existing SKinny Project** — browse to an existing descriptor or supported dedicated project file.
- **Recent Projects** — show name, canonical path, last opened time, compatibility/status, pin/remove controls, and a clear missing/moved state.

### Supporting experience

- Choose default project locations and remember the last browsed location.
- Explain template contents before creation.
- Detect installed .NET SDKs, required workloads, NuGet availability, write permissions, path/name validity, and supported Windows architecture.
- Check selected .NET, StereoKit, SKinny runtime, adapter, protocol, and scene-format compatibility before creating or launching.
- Offer actionable remediation without silently installing system-wide prerequisites.
- Keep recent-project metadata local; never treat it as project source data.
- Support keyboard navigation, progress/cancellation, clear failure states, and diagnostic export.

### Acceptance criteria

- A user can reach New, Import, Open, and Recent without a terminal.
- Invalid or unavailable recent entries are explained and can be repaired or removed.
- Prerequisite failures identify the exact missing SDK/workload/package/source and provide a retry path.
- No project code runs merely because its card appears in the Hub.

## Workstream C — New-project creation

### Generated project set

The exact layout is template-versioned, but a complete project contains:

- solution (`.sln`);
- normal application project (`.csproj`);
- initial source/entry point;
- editor runtime adapter and initial registered component catalog;
- project descriptor;
- initial scene;
- `Assets`, `Scenes`, and other required authoring folders;
- NuGet/version configuration required for reproducible restore;
- appropriate ignore rules for generated caches, recovery, and build output;
- a small README describing normal and editor launch paths.

### Creation flow

1. Validate name, destination, path length, write access, selected targets, and prerequisite availability.
2. Show a summary of files, versions, and package sources that will be created.
3. Generate into a staging directory so partial projects are not left behind.
4. Move the completed template into place and restore NuGet dependencies.
5. Build the selected runtime profile.
6. Launch and verify the first Scene session, then the first isolated Play session.
7. Open the project only after the verification result is visible; allow opening a failed project with diagnostics when useful.
8. On cancellation or failure, remove only files created by this transaction or retain them through an explicit **Keep for troubleshooting** choice.

### Version selection

- Present a tested compatibility matrix rather than an unbounded package-version text box.
- Default to the recommended pinned StereoKit and .NET pair for the selected template.
- Allow advanced choices only when the editor/runtime adapter combination is known to support them.
- Store selected versions in project-controlled files; never silently follow `latest`.

### Acceptance criteria

- From a clean Hub, a supported user can create a project and reach a rendered Scene and Play session without manually editing files.
- The generated project also restores, builds, and runs normally outside SKinny Editor.
- Repeating creation never overwrites a non-empty destination.
- Offline/missing-package failures are recoverable and identify which feed or package is unavailable.

## Workstream D — Existing-project onboarding

Current implementation: the portable launcher exposes safe `.sln`/`.csproj`
inspection, the compatibility/opaque-content report, selectable direct or
dedicated-head proposals, per-file diff review, manifest-backed apply,
persistent reports, safe descriptor validation, and hash-guarded rollback.
Restore/build/adapter/Scene/Play validation remains behind the existing
workspace-trust prompt and is the next integration step for this workstream.

### Analysis is read-only

The importer first inventories:

- candidate solutions and startup projects;
- target frameworks, runtime identifiers, output types, and installed SDK compatibility;
- StereoKit package/version and initialization/entry-point shape;
- project references, package sources, central package management, and build customizations;
- existing SKinny runtime/adapter/descriptor/scene files;
- desktop-head suitability and whether build output can host Scene and Play;
- source and asset locations;
- recognized authorable components and runtime content that remains opaque;
- conflicts, unsupported shapes, and changes that require manual integration.

Analysis must not evaluate arbitrary project targets or execute the application. If deeper MSBuild evaluation is required, it occurs only after trust and is visibly distinguished from safe inspection.

### Compatibility report

The report classifies the project as:

- **Ready to open** — an existing valid SKinny project;
- **Direct opt-in supported** — the main project can add the runtime adapter without changing its ordinary launch behavior;
- **Dedicated editor head recommended** — the safest integration is a small editor-specific project referencing selected production code/assets;
- **Manual integration required** — some scaffolding can be generated, but the user must connect project-specific startup or components;
- **Run-only or unsupported** — the editor cannot safely offer normal authoring for the detected shape.

For every result, show reasons, authorable content, opaque content, required prerequisites, proposed files, and expected impact on the normal non-editor flow.

### Main project versus dedicated head

The choice is explicit and previewed:

- **Main-project opt-in:** add the matching runtime package and route editor-only launch arguments into an adapter while retaining the current ordinary startup path. Best for small, conventional desktop projects.
- **Dedicated editor head:** create a separate `.csproj` and adapter that reference a bounded part of the existing solution. Recommended for large applications, service-heavy composition roots, multiple targets, or projects where normal startup must remain untouched.

The analyzer may recommend a choice, but it does not silently make one.

### Proposed-change preview and apply

- Show every create/modify action with its target path and purpose.
- Show textual diffs for existing text files.
- Block path escapes, collisions, unsupported encodings, dirty concurrent changes, and changes outside the selected project root.
- Apply all approved changes as one logged transaction.
- Validate descriptor/schema, restore, build, adapter handshake, initial Scene, and isolated Play.
- Report success, warnings, failures, and remaining manual work in one persistent onboarding report.

### Rollback

- Write a transaction manifest before applying changes.
- Back up every file that will be modified; record hashes so rollback does not overwrite later user edits.
- Remove only newly created files whose hashes still match the onboarding output.
- Restore modified files only when they have not changed since onboarding; otherwise present a conflict and preserve both versions.
- Do not depend on Git being installed or the repository being clean, though Git status may be shown as additional context.
- Keep normal project files unchanged when the user cancels before apply or when read-only analysis fails.

### Opaque-content reporting

The final report must distinguish:

- editable scene entities/components exposed through the adapter;
- runtime-only state visible through diagnostics but not editable;
- draw calls, UI windows, services, or dynamic objects that the editor cannot identify structurally;
- assets that can be indexed even when their runtime ownership is opaque;
- concrete adapter/component work needed to expose more content later.

### Acceptance criteria

- Importing an unsupported project yields a useful report and zero source changes.
- A supported small project can choose direct opt-in, preview the diff, apply, validate Scene/Play, roll back, and still use its original normal launch path.
- A complex fixture can choose a dedicated head without modifying its production composition root.
- Cancellation, restore failure, build failure, runtime crash, and interrupted onboarding leave a consistent and explainable state.
- The user can always tell which visible runtime content is editable, inspectable-only, or opaque.

## Recommended implementation sequence

### Phase 4.0 — decision and transaction foundations

- Select installer/update technology and installation scope.
- Resolve the `.skproject.json` shell-association issue.
- Define supported .NET/StereoKit/template version matrix.
- Implement project-analysis result, proposed-change, transaction-manifest, validation-result, and rollback models with destructive-boundary tests.

Exit: a fixture can be analyzed and a deterministic proposal/rollback plan produced without modifying it.

### Phase 4.1 — Project Hub and existing-project open

- Replace the basic no-project launcher with the Hub.
- Implement New/Import/Open/Recent cards and navigation shells.
- Harden existing descriptor opening, recents, missing paths, prerequisites, and workspace trust.

Exit: the portable build supports the complete Hub flow for an already-onboarded project.

### Phase 4.2 — new-project creation

- Add versioned templates, target selection, staged generation, restore/build, and first Scene/Play validation.

Exit: a new user creates and runs a normal StereoKit project without using a terminal.

### Phase 4.3 — existing-project onboarding

- Add safe analysis, compatibility report, direct/dedicated-head proposal, diff preview, transactional apply, validation, opaque-content report, and rollback.

Exit: small and complex test fixtures complete their supported onboarding paths while preserving normal application behavior.

### Phase 4.4 — installer, shell integration, signing, and updates

- Produce installer/uninstaller, Start entry, optional desktop shortcut, selected project-file launch behavior, signing hooks, release channels, authenticated update flow, and rollback.
- Run clean-machine install/upgrade/downgrade/uninstall tests and independent-user onboarding.

Exit: a non-developer can install, create/open/import, update, and uninstall the Windows product without source checkout or terminal setup beyond prerequisites the Hub explicitly identifies.

## Explicit exclusions for this phase

- Silent conversion of arbitrary StereoKit runtime objects into editor entities.
- Automatic rewriting of project-specific application composition without review.
- Owning or replacing the project's normal build system.
- Installing Visual Studio, .NET workloads, Android SDKs, or other system-wide developer prerequisites without explicit user action.
- Associating SKinny Editor with all `.json` files.
- macOS/Linux installers or editor support.
- A background auto-updater that executes unsigned or unauthenticated payloads.
