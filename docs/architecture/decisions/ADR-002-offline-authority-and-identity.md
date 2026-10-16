# ADR-002: Offline authority, tenancy, and identity continuity

## Status

Accepted

## Context

`openspec/changes/commerce-foundation/design.md` requires local sales to remain offline-first while cloud identity remains authoritative for revocation. The decision index (`docs/architecture/decisions/README.md`) lists "Synchronization authority and conflicts" and "Identity continuity and recovery" as pending. Without an explicit policy, offline administrative actions and shared-master edits have no defined authority, and installation identity behavior across upgrades and hardware replacement is undefined.

## Decision

- **Tenancy**: Organization, branch, user, role, and permission boundaries are enforced in PostgreSQL via tenant keys, claim-derived filters, and RLS with a non-owner runtime role. Cloud requests derive scope from authenticated credentials, not from submitted tenant IDs. A second-organization fixture is required in tests to prove non-disclosure across tenants. Ports must preserve the option of a future database-per-tenant isolation model without requiring it now.
- **Identity**: ASP.NET Core Identity is the identity provider. Customer credentials are opaque and hashed, bound to a specific tenant/customer, and revoked at the cloud origin regardless of branch connectivity — a branch that is offline at the moment of revocation must still honor it once it reconnects, or per the freshness policy below while still offline.
- **Offline administrative actions**: Branches cache encrypted admin verifier/permission snapshots locally. Offline administrative actions are permitted only against this cached snapshot, subject to a freshness policy: a snapshot older than the freshness window is treated as stale and offline administrative actions requiring elevated permissions are denied until the branch reconnects and refreshes the snapshot. This is a deliberate, explicit freshness policy — not an inferred or unbounded "offline lease".
- **Local sale authority**: The branch owns sales, cash, and stock. Local sales continue regardless of connectivity and are never blocked waiting on cloud authority.
- **Cloud authority**: The cloud owns online-order origin and customer revocation. These are never delegated to the branch.
- **Shared-master conflicts**: Local/web management adapters invoke the same shared use cases. Versioned shared-master commands may originate on either channel. Non-commuting concurrent edits retain both histories for manual review — last-write-wins is explicitly rejected as a conflict policy.
- **Installation identity continuity**: Installation identity survives application upgrades. Replacing the notebook (hardware) creates a new installation identity; upgrades never do.

## Consequences

- Tests must include a second-organization fixture proving cross-tenant denial (see `tests/Commerce.Integration/TenantAccessTests.cs` in Unit 2).
- The freshness policy for cached admin snapshots must be implemented and tested explicitly; "offline forever" for administrative actions is not acceptable, and neither is requiring connectivity for every administrative action.
- Shared-master conflict handling must retain both histories on non-commuting edits; this rules out simple upsert/last-write-wins persistence for those masters.
- Hardware replacement flows (see `SINGLE_DEVICE_BRANCH_PROFILE.md`) must explicitly mint a new installation identity, distinct from in-place upgrades handled under ADR-004.
