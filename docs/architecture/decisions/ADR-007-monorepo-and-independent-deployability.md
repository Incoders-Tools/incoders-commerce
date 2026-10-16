# ADR-007: Monorepo retained; independent deployability as a goal

## Status

Accepted

## Context

An external architecture document proposes an `apps/` + `packages/` monorepo layout. This repository does not use it: first-party projects live under `src/Commerce.*` with a single `Commerce.sln` and one `dotnet test Commerce.sln` harness (ADR-001). Separately, the deployment shape is not what "monorepo of independent apps" implies: `Commerce.Web` is built into `Commerce.Cloud.Api`'s `wwwroot` and shipped in the same container image (`deploy/staging-runbook.md`, `deploy/README.md`), and `openspec/specs/safe-release-upgrades/spec.md` treats them as one cloud deploy. Only `Commerce.Pos.Windows` is an independent artifact (signed MSIX/MSI, ADR-004/ADR-005).

## Decision

- **The monorepo is retained.** One repository, one solution, one test harness. Shared domain code (`Commerce.Domain`, `Commerce.Application`) is consumed by project reference, not by publishing internal packages.
- **The `apps/` + `packages/` reorganization is rejected for now.** `src/Commerce.*` is the layout; a rename would churn every project reference, CI path, and document for no behavioural gain. It remains a candidate future ADR if and when a concrete need (e.g. multiple non-.NET apps) appears.
- **Independent deployability is a goal, not a current property.** Target state: each deployable unit can be built, versioned, and released without forcing a release of the others. Present state: POS is independent; Cloud.Api and Web are one artifact. This ADR does not mandate splitting them.
- **Constraint that follows from the goal**: no deployable unit may depend on another's in-process internals. Cross-unit communication is over the API contract (ADR-001's branch-node rule, generalized). Co-locating Web inside Cloud.Api's image is a packaging choice and must remain reversible — the SPA must keep talking to the API over HTTP with a configurable base origin, never via server-rendered coupling.
- **Per-app path-filtered CI/CD is deferred** to its own change.

## Consequences

- A change that makes the SPA structurally inseparable from Cloud.Api (shared server-side rendering state, non-configurable origin) contradicts this ADR.
- Release tooling may keep shipping Cloud.Api + Web as one image; that is explicitly allowed and is not a violation.
- `Commerce.sln` remains the single build/test entry point; nothing here changes ADR-001's harness decision.
