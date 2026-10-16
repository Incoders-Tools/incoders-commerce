# ADR-005: Windows administrative floor and packaging (MSIX conditional, MSI fallback)

## Status

Accepted

## Context

The commerce foundation runs a WPF POS client plus a same-machine Windows Service branch node on a single notebook (see `SINGLE_DEVICE_BRANCH_PROFILE.md`). Packaging must support administrative installation of a service alongside a desktop app, across a fleet of Windows notebooks with potentially varying OS builds. `docs/architecture/decisions/README.md` lists "Installation and updates" as pending, including the packaging technology choice.

## Decision

- **Windows floor**: the branch notebook and any packaged Windows Service require Windows 10, version 2004, or later, per Microsoft's MSIX packaged-service deployment requirements.
- **Administrative installation**: installing the branch node (Windows Service) requires administrator privileges on the target machine. This is a fixed constraint of running a Windows Service, not a workaround.
- **Packaging is conditional, behind one manifest**: when the target machine meets the Windows 10 2004+ and admin-installation requirements, packaging uses MSIX. When it does not (older Windows build, or an installation context that cannot grant the MSIX packaged-service prerequisites), packaging falls back to a signed MSI. Both paths are driven by the same underlying application manifest, so the choice of MSIX vs. MSI is a packaging-time decision, not a code or feature difference.
- **Signing applies to both paths**: whichever package format is used, the package must be signed and its publisher/hash verified by the updater per ADR-004 before installation or upgrade proceeds.
- **Fleet implication**: because the packaging path is conditional per machine, the release pipeline must be able to produce both artifact types from the same release, and the updater/installer selects the appropriate one based on the detected target machine capabilities rather than assuming fleet-wide uniformity.

## Consequences

- CI/release tooling (`.github/workflows/release.yml`) must be able to build both an MSIX and a signed MSI artifact from the same versioned source, not just one.
- Installation and upgrade documentation must state the admin-privilege requirement explicitly to branch deployment operators.
- Fleet notebooks below the Windows 10 2004 floor are only supported via the MSI path; this ADR does not extend support to older Windows versions for MSIX.
- This ADR is scoped to packaging/installation; it does not change the upgrade-sequencing and recovery decisions in ADR-004, which apply identically regardless of package format.
