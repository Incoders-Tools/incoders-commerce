# ADR-001: Runtime stack and test harness

## Status

Accepted

## Context

The commerce foundation walking skeleton needs a concrete, buildable stack before any implementation task can start. `docs/architecture/decisions/README.md` previously left "Local persistence", "Cloud persistence and tenancy", and "Local-node topology" as pending decisions, and `openspec/config.yaml` recorded `test_command: null` with no runnable harness. `openspec/changes/commerce-foundation/design.md` proposes a concrete baseline evaluated against `.NET` support policy, WPF desktop guidance, and Docker Compose being a development-only convenience, not a client runtime dependency.

Strict TDD is enabled for this project (`openspec/config.yaml` → `strict_tdd: true`). Without a real, runnable test command, no implementation task can produce genuine RED → GREEN evidence.

## Decision

Adopt the following stack for the commerce foundation:

- **Runtime**: .NET 10 LTS for all first-party services and desktop clients.
- **POS / local management client**: WPF, running on the branch notebook alongside a same-machine Windows Service branch node. WinUI 3 remains a viable future alternative; it is not selected now.
- **Branch node**: a Windows Service co-located with the POS notebook. Only the branch node opens the local SQLite database directly; other local processes (including the WPF shell) reach it exclusively through its API, never through the SQLite file, to keep the door open for future multi-station topologies.
- **Local persistence**: SQLite, serialized writer, WAL journal mode, `synchronous=FULL`. This resolves the "Local persistence" item in the decision index as SQLite for the walking skeleton; a local server database remains an option for a future scalable profile and is not foreclosed by this ADR.
- **Cloud API**: ASP.NET Core.
- **Cloud web clients**: React with TypeScript.
- **Cloud persistence**: PostgreSQL, with tenant keys, claim-derived filters, and row-level security (RLS) enforced by default-deny policy.
- **Development-only orchestration**: Docker Compose (`deploy/dev/compose.yaml`) is for local development convenience only. No client or production runtime depends on Docker.
- **Test harness**: a real, runnable `dotnet test Commerce.sln` command backed by a `Commerce.sln` solution file and a `tests/` project, replacing the previously null `test_command`. `openspec/config.yaml` is updated to record this command once it is proven runnable (RED scaffold, then GREEN).

## Consequences

- Implementation tasks can now run genuine RED → GREEN → REFACTOR cycles against `dotnet test Commerce.sln`.
- Local persistence is resolved as SQLite for this walking skeleton; revisiting it for a scalable, multi-station profile remains a distinct, future ADR concern.
- The branch-node-owns-SQLite boundary constrains later units (`src/Commerce.BranchNode`) to expose an API rather than shared file access, avoiding a costly rework when multi-station support is added.
- Docker Compose usage must never leak into client packaging or production deployment; CI/build tooling must not assume Docker is present on end-user machines.
- `docs/architecture/decisions/README.md` should be updated to remove "Local-node topology" and "Local persistence" from the pending list once this ADR lands, and to link to this ADR.
