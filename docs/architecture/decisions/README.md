# Architecture decision index

Architecture decisions are versioned in the repository. This index intentionally records no unapproved technology choice.

## Accepted ADRs

| ADR | Topic |
|---|---|
| [ADR-001](./ADR-001-stack-and-harness.md) | Runtime stack (.NET 10 LTS, WPF, branch-node SQLite, ASP.NET Core, React/TypeScript, PostgreSQL) and the `dotnet test Commerce.sln` harness. |
| [ADR-002](./ADR-002-offline-authority-and-identity.md) | Tenancy/RLS, identity continuity, offline administrative freshness policy, shared-master conflict handling. |
| [ADR-003](./ADR-003-pending-offline-orders.md) | Pending offline orders and idempotent synchronization. |
| [ADR-004](./ADR-004-release-signing-and-upgrades.md) | Release signing, provenance, and safe upgrade/recovery sequencing. |
| [ADR-005](./ADR-005-signing-and-windows-fleet.md) | Windows administrative floor and conditional MSIX/MSI packaging. |
| [ADR-006](./ADR-006-domain-and-subdomain-topology.md) | Single apex domain (`vacaverde.com.ar`) with reserved `admin.` / `pedidos.` / `api.` subdomains; applies on acquisition, not a current configuration. |
| [ADR-007](./ADR-007-monorepo-and-independent-deployability.md) | Monorepo retained under `src/Commerce.*`; independent per-unit deployability as a goal, not a present property. |
| [ADR-008](./ADR-008-customer-separate-from-identity.md) | Customer modelled as a commercial party independent of Identity; identity optionally links via `CustomerId`. |
| [ADR-009](./ADR-009-guest-and-registered-ordering.md) | Guest and registered ordering coexist on one screen; accounts are admin-provisioned only; guest orders are classified non-priority. |
| [ADR-010](./ADR-010-centralized-server-side-pricing.md) | Centralized server-side price resolution; guest sees list price, registered sees entity commercial conditions. |
| [ADR-011](./ADR-011-payment-lifecycle-separate-from-order.md) | Payment modelled as a lifecycle separate from order fulfilment; no provider selected. |

## Planned ADR topics

| Topic | Why it needs a decision |
|---|---|
| Hardware integrations | Scale, printer, scanner, and cash-drawer models/protocols require field evidence. |
| Legacy `CustomerOrderingAccess` disposition | ADR-009 leaves open whether the existing link credential binds to the same Customer, is a superseded predecessor, or becomes an account-activation step. A future design must resolve it. |
| Payment provider and settlement integration | ADR-011 models the lifecycle but selects no provider; PCI scope, reconciliation, and fiscal integration need field and commercial evidence. |

## ADR rule

An ADR records a decision only after it names the problem, alternatives, operational implications, and evidence. Until then, this index and the architecture pages must label the topic as pending or provisional.
