# Tasks: Commerce Foundation Walking Skeleton

## Review Workload Forecast

| Field | Value |
|---|---|
| Estimated authored change | 1,800-2,600 lines (goldens excluded) |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

`test_command: null`; proposed/unrun.

### Suggested Work Units

| Unit | Dependency | Test | Runtime | Rollback |
|---|---|---|---|---|
| U1 | ADRs/harness: pre-code -> command | `dotnet test Commerce.sln` (proposed) | N/A: infra-only | ADRs+Commerce.sln/tests/config |
| U2 | Tenant: U1 -> kernel | `dotnet test Commerce.sln --filter TenantAccessTests` | Org A branches + Org B denial | Domain/Application+tests |
| U3 | Sync: U2 -> cloud ACK | `dotnet test Commerce.sln --filter SyncTests` | Offline sale/lost-ACK replay | BranchNode/Cloud.Api/Postgres/deploy/dev/compose.yaml/tests |
| U4 | Management: U2 -> parity | `dotnet test Commerce.sln --filter ManagementParityTests` | Local/web authorized update | Application/Management+adapters/tests |
| U5 | Ordering: U3/U4 -> pending | `dotnet test Commerce.sln --filter OrderingTests` | Enabled -> offline destination | Cloud/Web ordering+tests |
| U6 | Upgrade: U2/U3 -> pilot | `dotnet test Commerce.sln --filter UpgradeTests` | Interrupted upgrade; recoveries | Updater/deploy/workflow+tests |

## Unit 1: Decisions and Harness

- [ ] 1.1 Approve ADRs `docs/architecture/decisions/ADR-001-stack-and-harness.md` through `docs/architecture/decisions/ADR-005-signing-and-windows-fleet.md`: stack, offline authority, pending orders, signing, Windows/admin, MSIX conditional, MSI by ADR.
- [ ] 1.2 RED -> GREEN -> REFACTOR: scaffold tests and record REAL command in `Commerce.sln`, `tests/`, and `openspec/config.yaml`; implementation unauthorized.

## Unit 2: Tenant Kernel
Coverage: 13/25 (3/6, 3/6, 3/6, 4/7 by capability).

- [ ] 2.1 RED `tests/Commerce.Integration/TenantAccessTests.cs`: branch/org denial, roles, revocation, installation identity, audit, second-org.
- [ ] 2.2 GREEN `src/Commerce.Domain` + `src/Commerce.Application`: scopes, auth/audit, Product/Presentation/category/unit/optional-vertical contracts.
- [ ] 2.3 REFACTOR/verify; retain revocation freshness policy.

## Unit 3: Branch and Cloud Sync

- [ ] 3.1 RED `tests/Commerce.Integration/SyncTests.cs`: atomic-sale/outbox/inbox; branch-cloud-duplicate/lost-ACK; ACK/freshness/authority/conflicts; PostgreSQL-RLS default-deny/cross-org-denial under non-owner-role+second-org fixture.
- [ ] 3.2 GREEN `src/Commerce.BranchNode`: SQLite ownership, atomic effects/logs, idempotent IDs/ACKs, identity and conflict review.
- [ ] 3.3 GREEN `src/Commerce.Cloud.Api`: PostgreSQL claim filters/RLS/non-owner role; atomic inbox/effect/ACK receiver; `deploy/dev/compose.yaml` optional dev-only, never client runtime.
- [ ] 3.4 REFACTOR/verify replay/freshness; rollback BranchNode/Cloud.Api/Postgres/deploy/dev/compose.yaml/tests.

## Unit 4: Shared Management Adapters

- [ ] 4.1 RED `tests/Commerce.Integration/ManagementParityTests.cs` for identical authorized local/web outcomes and cross-branch denial.
- [ ] 4.2 GREEN shared management contract in `src/Commerce.Application/Management`.
- [ ] 4.3 GREEN wire local/web adapters in `src/Commerce.Cloud.Api`, `src/Commerce.Web`, and `src/Commerce.Pos.Windows`.
- [ ] 4.4 REFACTOR/verify parity; rollback shared contract, adapters, and tests only.

## Unit 5: Private Ordering

- [ ] 5.1 RED `tests/Commerce.Integration/OrderingTests.cs`: bound enable/revoke, immutable context, idempotency, Product/Presentation semantics, provisional pending/no-stock.
- [ ] 5.2 GREEN cloud/destination delivery in `src/Commerce.Cloud.Api`/`src/Commerce.Web`; pending ADR-gated; settlement deferred.
- [ ] 5.3 REFACTOR/verify duplicate submission/offline retry.

## Unit 6: Safe Upgrades

- [ ] 6.1 RED `tests/Commerce.Upgrade/UpgradeTests.cs`: typed rejection for traversal, publisher/type, tamper, incompatibility, privilege, interruption, backup/recovery.
- [ ] 6.2 GREEN `src/Commerce.Updater`: signed hashes/attestation, fail-closed compatibility, quiesce, verified backup, migration/health, crash journal, commit preservation.
- [ ] 6.3 GREEN recovery: pre-reopen snapshot restore; post-reopen live-schema binary rollback/forward repair; `.github/workflows/release.yml` authorized `dev`->internal, `staging`->pilot, `main`->stable; endpoint 404 inconclusive.
- [ ] 6.4 REFACTOR/verify both boundaries/sale timing; conditional MSIX fleet support.
