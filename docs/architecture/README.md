# Architecture documentation

This directory is the versioned source of truth for Incoders Commerce architecture. The Wiki is intentionally limited to processes, onboarding, and links.

## Read first

1. [Product requirements](../../PRD.md) define scope and business rules.
2. [Deployment profiles](./deployment-profiles.md) explain the initial notebook and future multi-station topology.
3. [Synchronization](./synchronization.md) records the local/cloud constraints.
4. [Decision index](./decisions/README.md) lists accepted decisions (ADR-001..011) and the topics that still require ADRs.

## Architecture status

| Status | Current position |
|---|---|
| Confirmed | Offline-first local operation; logical branch node; cloud synchronization; Windows local application; reusable commerce core; initial notebook per branch; future multi-station topology; identity continuity; single apex domain with reserved subdomains (pending acquisition); monorepo retained; Customer independent of Identity; centralized server-side pricing; payment lifecycle separate from order — all per ADR-006..011. |
| Provisional | SQLite is a candidate for local operational storage. |
| Pending | Local/cloud database decisions, physical tenancy, shared-data authority, conflict rules, hardware protocols, update/recovery details, and technology stack. |

## Diagram sources

- [Architecture overview](../../diagrams/incoders-commerce-architecture-overview.drawio) — current conceptual overview.
- [Scalable local branch](../../diagrams/incoders-commerce-local-branch-lan.drawio) — future multi-station LAN evolution.
- [System context draft](../../diagrams/incoders-commerce-system-context-draft.drawio) — working context diagram.
- [Technology stack draft](../../diagrams/incoders-commerce-technology-stack-and-integrations-draft.drawio) — proposals only; not an approved stack.

## Existing source documents

- [PRD](../../PRD.md)
- [Initial notebook profile and coordinated replacement](../../SINGLE_DEVICE_BRANCH_PROFILE.md)
- [Product variants and release strategy](../../PRODUCT_VARIANTS_AND_RELEASE_STRATEGY.md)
- [UI product definitions](../../UI_PRODUCT_DEFINITIONS.md)
