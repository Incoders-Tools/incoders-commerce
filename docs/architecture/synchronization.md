# Local-cloud synchronization

The product synchronizes explicit operations and events; it does not blindly replicate database files or tables. The functional source remains [PRD section 11](../../PRD.md#11-operación-local-y-cloud).

## Confirmed constraints

- A local sale is confirmed locally and does not wait for cloud connectivity.
- Operations use global identifiers, support idempotent retry, and remain persisted until confirmation.
- Synchronization resumes automatically after connectivity recovery and runs outside the POS critical path.
- Status remains visible without blocking the cashier: synchronized, syncing, pending, offline, or requires attention.
- Confirmed movements are never silently overwritten; unresolved conflicts require review.
- Cloud views show freshness when a branch is disconnected.

## Preliminary authority

| Information | Primary authority |
|---|---|
| Sales and cash | Local branch |
| Branch hardware and stock | Local branch |
| Preparation and delivery | Local branch operation |
| Online-order origin | Cloud |
| Online payment approval | Payment gateway / cloud |
| Campaigns and consolidated reporting | Cloud |
| Shared masters, users, and permissions | Pending by data type |

## Pending decisions

The authority and conflict policy for shared masters, users, permissions, cross-branch transfers, and offline identity continuity must be documented before implementation. Database, transport, provider, and runtime choices remain open.

For notebook replacement safeguards, see [deployment profiles](./deployment-profiles.md).
