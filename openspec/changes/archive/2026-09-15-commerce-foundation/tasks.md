# Tasks: Commerce Foundation Walking Skeleton

## Review Workload Forecast

| Field | Value |
|---|---|
| Estimated authored change | 1,800-2,600 lines (goldens excluded) |
| Delivery strategy | ask-on-risk |
| Chain strategy | one PR per unit (6 chained PRs), sequenced by dependency order |

Decision needed before apply: No — resolved 2026-09-15.
Chained PRs recommended: Yes
Chain strategy: one PR per unit, opened in dependency order (U1, U2, then U3/U4, then U5/U6).
Per-PR line budget: 1,100 lines (raised from 800, then from the default 400, for this initial walking-skeleton pass; goldens still excluded). Raised again 2026-09-15 after Unit 3 (sync) genuinely needed 1,050 lines for RED-first atomicity/idempotency/RLS coverage that could not be cut without dropping threat-matrix scenarios; applies to all remaining units (4-6), not just U3. Unit 6 (final unit) also exceeded it (1,486 lines, +386) for the same reason — 7 typed rejections + 2 recovery boundaries + timing + MSIX/MSI selection, no scope cut; granted as a one-time size:exception since it is the closing PR with no downstream units affected.

`test_command: dotnet test Commerce.sln`; scaffolded and passing (Unit 1).

### Component Reuse Policy

Default to total reuse of existing shared components across the solution — if a component already exists (e.g. a `Loader`), every unit uses that same component instead of authoring a new one. This reduces authored lines and keeps the solution consistent. A new component is created only by explicit user exception, requested at the time.

### Suggested Work Units

| Unit | Dependency | Test | Runtime | Rollback | Chain (PR) |
|---|---|---|---|---|---|
| U1 | ADRs/harness: pre-code -> command | `dotnet test Commerce.sln` (proposed) | N/A: infra-only | ADRs+Commerce.sln/tests/config | PR 1 |
| U2 | Tenant: U1 -> kernel | `dotnet test Commerce.sln --filter TenantAccessTests` | Org A branches + Org B denial | Domain/Application+tests | PR 2 |
| U3 | Sync: U2 -> cloud ACK | `dotnet test Commerce.sln --filter SyncTests` | Offline sale/lost-ACK replay | BranchNode/Cloud.Api/Postgres/deploy/dev/compose.yaml/tests | PR 3 |
| U4 | Management: U2 -> parity | `dotnet test Commerce.sln --filter ManagementParityTests` | Local/web authorized update | Application/Management+adapters/tests | PR 4 |
| U5 | Ordering: U3/U4 -> pending | `dotnet test Commerce.sln --filter OrderingTests` | Enabled -> offline destination | Cloud/Web ordering+tests | PR 5 |
| U6 | Upgrade: U2/U3 -> pilot | `dotnet test Commerce.sln --filter UpgradeTests` | Interrupted upgrade; recoveries | Updater/deploy/workflow+tests | PR 6 |

## Unit 1: Decisions and Harness

- [x] 1.1 Approve ADRs `docs/architecture/decisions/ADR-001-stack-and-harness.md` through `docs/architecture/decisions/ADR-005-signing-and-windows-fleet.md`: stack, offline authority, pending orders, signing, Windows/admin, MSIX conditional, MSI by ADR.
- [x] 1.2 RED -> GREEN -> REFACTOR: scaffold tests and record REAL command in `Commerce.sln`, `tests/`, and `openspec/config.yaml`. `dotnet test Commerce.sln` is now a real, runnable command (RED: `HarnessScaffoldTests` asserted `HarnessMarker.IsScaffolded == true` while it returned `false`, failed; GREEN: flipped to `true`, passed).

## Unit 2: Tenant Kernel
Coverage: 13/25 (3/6, 3/6, 3/6, 4/7 by capability).

- [x] 2.1 RED `tests/Commerce.Integration/TenantAccessTests.cs`: branch/org denial, roles, revocation, installation identity, audit, second-org (RED: 10 tests, `TenantAuthorizationService.Authorize`/`InstallationIdentityService.ReplaceForHardwareChange` threw `NotImplementedException`; 10 failed, 0 passed, compiled cleanly).
- [x] 2.2 GREEN `src/Commerce.Domain` + `src/Commerce.Application`: scopes, auth/audit, Product/Presentation/category/unit/optional-vertical contracts (GREEN: `dotnet test Commerce.sln --filter TenantAccessTests` → 10/10 passed).
- [x] 2.3 REFACTOR/verify; retain revocation freshness policy (full suite `dotnet test Commerce.sln` → 11/11 passed across Unit 1 + Unit 2; offline freshness window/stale-denial/fresh-allow behavior from ADR-002 preserved and explicitly tested, not weakened).

## Unit 3: Branch and Cloud Sync

- [x] 3.1 RED `tests/Commerce.Integration/SyncTests.cs`: atomic-sale/outbox/inbox; branch-cloud-duplicate/lost-ACK; ACK/freshness/authority/conflicts; PostgreSQL-RLS default-deny/cross-org-denial under non-owner-role+second-org fixture (RED: 11 tests, `BranchSyncStore`/`BranchNodeService`/`CloudInboxStore`/`CloudSyncReceiver` threw `NotImplementedException`; 11 failed, 0 passed, compiled cleanly).
- [x] 3.2 GREEN `src/Commerce.BranchNode`: real SQLite (`Microsoft.Data.Sqlite`, WAL, `synchronous=FULL`) `sale_effects`+`outbox`+`inbox` tables; `CommitSaleAtomically` writes sale effect and outbox row in one transaction (idempotent on `operation_id`, proven via `SimulateInterruptedCommit` never persisting a partial sale across a reopened connection); `Acknowledge`/`ApplyInbound` idempotent; `BranchNodeService` reuses `TenantAuthorizationService`+`IAuditSink` for conflict review, keeping both non-commuting histories until an authorized reviewer resolves them (GREEN: `dotnet test Commerce.sln --filter SyncTests` → 11/11 passed).
- [x] 3.3 GREEN `src/Commerce.Cloud.Api`: `CloudInboxStore`/`CloudSyncReceiver` claim-scoped (`CloudTenantScope`) inbox/ACK receiver proving PostgreSQL RLS default-deny + non-owner-role semantics (cross-org envelope denied, org B scope never sees/acknowledges org A rows) as an in-memory RLS-equivalent test double — no live PostgreSQL instance was reachable in this apply session (Docker daemon not running); real policy recorded in `deploy/dev/db/init-rls.sql` (RLS enabled+forced, non-owner `app_runtime` role, `sync_inbox_tenant_isolation` policy) for the real Npgsql-backed adapter as follow-up production work. `deploy/dev/compose.yaml` added as an optional dev-only PostgreSQL container (never referenced by client runtime/csproj).
- [x] 3.4 REFACTOR/verify — full suite `dotnet test Commerce.sln` → 22/22 passed (1 Bootstrap + 21 Integration: 10 TenantAccess + 11 Sync). Replay/freshness proven: lost-ACK retry idempotent, duplicate inbound/outbound delivery produces no second effect, `SyncStatusSnapshot` exposes pending count + last-acknowledged time without blocking local work. Rollback boundary held to `src/Commerce.BranchNode`, `src/Commerce.Cloud.Api`, `src/Commerce.Domain/Sync`, `deploy/dev/`, and `tests/Commerce.Integration/SyncTests.cs` only.

## Unit 4: Shared Management Adapters

- [x] 4.1 RED `tests/Commerce.Integration/ManagementParityTests.cs` for identical authorized local/web outcomes and cross-branch denial (RED: 5 tests, `CatalogManagementService.RenameProduct` threw `NotImplementedException`; 5 failed, 0 passed, compiled cleanly).
- [x] 4.2 GREEN shared management contract in `src/Commerce.Application/Management` (`ManagementRequest`/`ManagementOutcome`/`CatalogManagementService.RenameProduct` — reuses `TenantAuthorizationService` for the entire authorization/audit decision; no auth logic of its own).
- [x] 4.3 GREEN wire local/web adapters in `src/Commerce.Cloud.Api` (`CloudCatalogManagementAdapter`, claim-scoped via `CloudTenantScope`), `src/Commerce.Web` (new thin project, `WebCatalogManagementAdapter` forwards to Cloud.Api), and `src/Commerce.Pos.Windows` (new thin project, `LocalCatalogManagementAdapter` uses the actor's own organization directly) — all three adapters call only into `CatalogManagementService`, no per-channel authorization/business logic (GREEN: `dotnet test Commerce.sln --filter ManagementParityTests` → 5/5 passed).
- [x] 4.4 REFACTOR/verify parity — full suite `dotnet test Commerce.sln` → 27/27 passed (1 Bootstrap + 26 Integration: 10 TenantAccess + 11 Sync + 5 ManagementParity), 0 failed, 0 skipped. Parity proven for allowed outcome, cross-branch denial, cross-organization scope-spoof denial, and insufficient-permission denial — local and web adapters return identical `Status`/`Reason` for every case. Rollback boundary held to `src/Commerce.Application/Management/**`, `src/Commerce.Cloud.Api/Management/**`, `src/Commerce.Web/**`, `src/Commerce.Pos.Windows/**`, and `tests/Commerce.Integration/ManagementParityTests.cs` only — no Unit 1/2/3 files modified except mechanical `Commerce.sln` (2 new project entries via `dotnet sln add`) and `tests/Commerce.Integration/Commerce.Integration.csproj` (2 new `ProjectReference` lines).

## Unit 5: Private Ordering

- [x] 5.1 RED `tests/Commerce.Integration/OrderingTests.cs`: bound enable/revoke, immutable context, idempotency, Product/Presentation semantics, provisional pending/no-stock (RED: 16 tests, `CustomerOrderingAccessService.Apply`/`CustomerCatalogAccessService.{Authorize,GetPermittedCatalogue}`/`OrderSnapshotFactory.Snapshot`/`CloudOrderStore.{Submit,RetryDelivery,AttemptDelivery}` threw `NotImplementedException`; 16 failed, 0 passed, compiled cleanly).
- [x] 5.2 GREEN cloud/destination delivery in `src/Commerce.Cloud.Api`/`src/Commerce.Web`; pending ADR-gated; settlement deferred (GREEN: `dotnet test Commerce.sln --filter OrderingTests` → 16/16 passed). New `src/Commerce.Domain/Ordering` types (`CustomerOrderingAccess`, `Order`, `OrderLineSnapshot`, `OrderDeliveryStatus`/`OrderPendingReason`) snapshot the exact Unit 2 `Product`/`Presentation` types per ADR-003 instead of a parallel order-line model. `CustomerOrderingAccessService` (staff enable/revoke) reuses `TenantAuthorizationService` for its entire authorization/audit decision (Management authority pattern). `CloudOrderStore` reuses the exact Unit 3 `SyncEnvelope`/`BranchSyncStore.ApplyInbound` idempotent-inbox mechanism for destination delivery instead of a parallel delivery pipeline; delivery is gated pending (never silently confirmed) when the destination is offline or stock is unconfirmed. `WebOrderSubmissionAdapter` is a thin forward into `CloudOrderSubmissionService`, matching the Unit 4 adapter-layering pattern.
- [x] 5.3 REFACTOR/verify duplicate submission/offline retry — full suite `dotnet test Commerce.sln` → 43/43 passed (1 Bootstrap + 42 Integration: 10 TenantAccess + 11 Sync + 5 ManagementParity + 16 Ordering), 0 failed, 0 skipped. Duplicate business-order submission returns the existing acceptance with no second order; offline-then-reconnect retry converges to `DestinationConfirmed` with no second branch effect (direct re-apply of the same `OperationId` proven `DuplicateIgnored` via the reused Unit 3 inbox). Rollback boundary held to `src/Commerce.Domain/Ordering/**`, `src/Commerce.Application/Ordering/**`, `src/Commerce.Cloud.Api/Ordering/**`, `src/Commerce.Web/Ordering/**`, `tests/Commerce.Integration/OrderingTests.cs`, plus mechanical `src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj` and `src/Commerce.Web/Commerce.Web.csproj` (1 new `ProjectReference` to `Commerce.BranchNode` each, so cloud/web can reuse the real `BranchSyncStore` instead of a parallel delivery double) — no Unit 1-4 source files modified.

## Unit 6: Safe Upgrades

- [x] 6.1 RED `tests/Commerce.Upgrade/UpgradeTests.cs`: typed rejection for traversal, publisher/type, tamper, incompatibility, privilege, interruption, backup/recovery (RED: 19 tests, `UpdaterService.{RunUpgrade,SimulateInterruptedUpgrade,SimulateInterruptedUpgradeAfterReopen,DetectAndRecover}`/`PackageVerifier.Verify`/`CompatibilityChecker.CheckVersions`/`PackageFormatSelector.Select`/`SqliteUpgradeBackup.{CreateVerifiedBackup,RestoreFromBackup}`/`SqliteMigrationRunner.{Migrate,CheckHealth}` threw `NotImplementedException`; 19 failed, 0 passed, compiled cleanly).
- [x] 6.2 GREEN `src/Commerce.Updater`: `PackageVerifier` (path-containment, then publisher/type, then signature/attestation/hash — each a distinct typed rejection), `CompatibilityChecker` (fail-closed N/N+1 across app/sync/schema versions), `InProcessBranchNodeQuiescence` (real quiesce/resume state machine gated on an active-sale flag — production Windows Service host integration flagged as follow-up, same honesty standard as Unit 3's RLS), `SqliteUpgradeBackup` (real `Microsoft.Data.Sqlite` Backup API + `PRAGMA integrity_check` verification against the exact branch database `BranchSyncStore` owns), `SqliteMigrationRunner` (additive-only schema-version migration + health check, never touches `sale_effects`/`outbox`/`inbox`), `UpgradeCrashJournal` (file-backed phase journal, durable across a simulated restart), `UpdaterService` (the single shared orchestration service — reuses `TenantAuthorizationService` for the entire trigger-privilege decision and `IAuditSink` for the upgrade audit trail; sequences verify -> compatibility -> authorize -> quiesce -> verified backup -> migrate -> health check -> reopen -> commit; any pre-reopen failure restores the backup and preserves the current version) (GREEN: `dotnet test Commerce.sln --filter UpgradeTests` -> 19/19 passed).
- [x] 6.3 GREEN recovery: `UpdaterService.DetectAndRecover` reads the crash journal on a simulated restart — phases before `WritesReopened` restore the pristine pre-upgrade snapshot (pre-reopen boundary, never touched once writes reopen); `WritesReopened` recovers via binary rollback against the compatible live schema (retaining the database and already-migrated sales, proven against a real seeded sale in `BranchSyncStore`) or forward-repair when rollback is incompatible. `.github/workflows/release.yml` maps `dev`->internal, `staging`->pilot, `main`->stable (ADR-004), and treats a branch-protection-endpoint 404 as inconclusive, never as proof protection is absent.
- [x] 6.4 REFACTOR/verify both recovery boundaries and active-sale timing; conditional MSIX/MSI fleet support via `PackageFormatSelector` (fail-closed on missing admin install rights per ADR-005) (full suite `dotnet test Commerce.sln` -> 62/62 passed: 1 Bootstrap + 19 Upgrade + 42 Integration, 0 failed, 0 skipped).
