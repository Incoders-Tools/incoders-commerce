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

## Planned ADR topics

| Topic | Why it needs a decision |
|---|---|
| Hardware integrations | Scale, printer, scanner, and cash-drawer models/protocols require field evidence. |

## ADR rule

An ADR records a decision only after it names the problem, alternatives, operational implications, and evidence. Until then, this index and the architecture pages must label the topic as pending or provisional.
