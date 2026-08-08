# SKinny Editor project SDK

These preview packages let a normal StereoKit project expose an explicit component catalog and run in SKinny Editor's isolated Scene and Play processes.

Start with `SKinny.Editor.Runtime`; it references the matching adapter, protocol, and scene packages. Preview versions are exactly paired. See `docs/15-installation-and-onboarding.md` in the source distribution for the startup hook, adapter example, descriptor format, compatibility policy, and workspace-trust boundary.

The portable Windows bundle also contains a runnable Hello Editor project, extension guidance, and the completion/acceptance checklist. Run `scripts/verify-release.ps1` from the source workspace to rebuild and validate the complete release candidate.
