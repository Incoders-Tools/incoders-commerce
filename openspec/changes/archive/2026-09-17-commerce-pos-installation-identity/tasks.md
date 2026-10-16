# Tasks: POS Installation Identity via Operator Sign-In

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1,300 (design estimate) |
| 400-line budget risk | High |
| Chained PRs recommended | No |
| Suggested split | Single PR (`size:exception`) |
| Delivery strategy | exception-ok |
| Chain strategy | size-exception |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: size-exception
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Full bearer-contract cutover: schema, store, endpoint, handler, POS rewrite, deletions, tests, manual verify | PR 1 | `dotnet test tests/Commerce.Integration` | `deploy/pos-manual-verify.md` Part 2, executed live | Revert one commit; `DROP TABLE device_credentials` |

## Phase 1: Schema and Readiness

- [x] 1.1 Create `deploy/db/migrations/0004_device_credentials.sql`: `device_credentials` table, FORCE RLS, three policies (`SELECT USING (true)`, `INSERT WITH CHECK` org-scoped, `UPDATE USING (true) WITH CHECK (is_revoked)`), grants. Never touch 0001-0003.
- [x] 1.2 Hand-sync same DDL into `deploy/dev/db/init-rls.sql`.
- [x] 1.3 RED: extend `MigrationRlsTests.cs` — 0004 idempotent, unscoped SELECT returns row, cross-org INSERT fails, unscoped UPDATE to revoked succeeds, unscoped UPDATE to un-revoke/rewrite fails.
- [x] 1.4 Extend `PostgresReadinessHealthCheck.cs` to verify `device_credentials` FORCE RLS + all three policies; fail closed, remediation names `0004`.

## Phase 2: Delete Superseded Identity System

- [x] 2.1 Delete `src/Commerce.Application/Access/InstallationIdentityService.cs` and `src/Commerce.Domain/Tenancy/Installation.cs`.
- [x] 2.2 Relocate hardware-replacement scenario from `TenantAccessTests.cs` into new `DeviceCredentialStoreTests.cs` (Phase 3), asserted against real Postgres.
- [x] 2.3 Update `PosCompositionRootTests.cs`: assert `InstallationIdentityService` no longer registered.

## Phase 3: Credential Store and Endpoint (Cloud.Api)

- [x] 3.1 RED: `DeviceCredentialStoreTests.cs` — issue, `FindByTokenHashAsync` hit/miss, revoked-row still returned, re-issue revokes prior (same and cross-org), hardware-replacement lineage.
- [x] 3.2 Create `Persistence/DeviceCredentialRecords.cs`: `DeviceCredentialRecord`, `IssuedDeviceCredential`, `DeviceTokenHasher` (32-byte secret gen + SHA-256 hash).
- [x] 3.3 Create `Persistence/PostgresDeviceCredentialStore.cs`: `IssueAsync` (one txn: unscoped revoke-prior + scoped insert), `FindByTokenHashAsync` (unscoped), `RevokeAsync`.
- [x] 3.4 Add `ListBranchesAsync(scope, branchIds, ct)` to `PostgresOrganizationStore.cs` (org-scoped, ordered).
- [x] 3.5 RED: `DeviceEndpointTests.cs` — identical-401 matrix, `no-branches-assigned` 403, single-branch auto-pair, multi-branch list, `branch-not-in-scope` 403, token returned once.
- [x] 3.6 Create `Endpoints/Device.cs`: `POST /device/pair` reusing `PostgresUserAccountStore`'s exact verification path; extract shared dummy-hash constant to internal holder (no duplication).
- [x] 3.7 Register store + `MapDeviceEndpoints()` in `Program.cs`.

## Phase 4: Bearer Verification Rewrite

- [x] 4.1 RED: `DeviceBearerAuthenticationHandlerTests.cs` (or extend existing) — valid token accepted, revoked rejected, unknown/malformed rejected, old `{org}.{installation}` shape rejected.
- [x] 4.2 Rewrite `DeviceBearerAuthenticationHandler.cs`: hash → lookup → revocation check → claims from row only; delete stale self-signed XML doc.
- [x] 4.3 Create `Tenancy/DeviceIdentity.cs`: `TryResolve(principal)` reads `branch_id`/`installation_id` claims; `CloudTenantScope` unchanged.
- [x] 4.4 RED+GREEN: assert persisted `sync_inbox` row carries the credential row's org/branch, not client-claimed values.

## Phase 5: POS Windows Rewrite

- [x] 5.1 Add `System.Security.Cryptography.ProtectedData` PackageReference to `Commerce.Pos.Windows.csproj`.
- [x] 5.2 RED: `LocalInstallationStoreTests.cs` — round-trip, first-run unpaired, re-pair preserves `InstallationId`, decrypt failure treated as no valid credential (never throws).
- [x] 5.3 Rewrite `LocalInstallationStore.cs`: drop self-minted GUIDs; DPAPI `Protect`/`Unprotect` (`CurrentUser`) on save/load.
- [x] 5.4 Create `DevicePairingClient.cs`: typed anonymous `HttpClient` for `POST /device/pair`, discriminated `PairingOutcome`.
- [x] 5.5 Create `PairingWindow.xaml`/`.xaml.cs`: sign-in fields, collapsed branch picker revealed on `branch-selection-required`, status text; handles first-run and re-pair.
- [x] 5.6 Modify `MainWindow.xaml`/`.xaml.cs`: identity block, always-visible "Re-pair terminal" button, `SyncPushResult.CredentialRejected` hint appended (distinct from network failure).
- [x] 5.7 Modify `CloudSyncClient.cs`: `PushAsync(envelope, deviceToken, ct)`, add `CredentialRejected` result.
- [x] 5.8 Modify `PosHostBuilder.cs`/`App.xaml.cs`: drop `InstallationIdentityService`; show `PairingWindow` modally when unpaired; shutdown on cancel.

## Phase 6: Structural Proof and Confirmation

- [x] 6.1 Add architecture test in `PosCompositionRootTests.cs`: `Commerce.BranchNode` assembly does not reference `Commerce.Pos.Windows`.
- [x] 6.2 Confirm no Playwright/SPA test depends on the POS device-bearer shape (cookie-auth only) — record confirmation, do not assume.
- [x] 6.3 Run full xUnit suite against `deploy/dev/compose.yaml`; confirm count reconciles (127 minus moved/deleted, plus new).

## Phase 7: Manual Verification and Docs

- [x] 7.1 Extend `deploy/pos-manual-verify.md` Part 2 with the 7-step script from design.md.
- [x] 7.2 Execute the script live on this Windows machine; record actual results (not assumed).
