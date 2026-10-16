# Proposal: POS Installation Identity via Operator Sign-In

## Intent

`LocalInstallationStore.LoadOrCreate` self-mints `Guid.NewGuid()` for both `organizationId` and `branchId` on first run and trusts it forever. `DeviceBearerAuthenticationHandler` accepts `Bearer {organizationId}.{installationId}` with no signature, no lookup, no server-issued material — the caller asserts its own tenant. A POS can therefore sync into a non-existent organization indefinitely. This closes the open question deferred by `commerce-deployment-orchestration`: installation-bound device credential issuance for `Pos.Windows`.

## Scope

### In Scope
- WPF sign-in screen in `Commerce.Pos.Windows`, shown when no valid device credential exists locally, reusing the exact credential-verification path behind `/account/sign-in` (ADR-002 constraint).
- Branch-picker step listing only branches in the signed-in `UserAccount.BranchScope`; new branch-listing query on `PostgresOrganizationStore`.
- Server-side issuance of an installation-bound device credential bound to organization + branch + installation, replacing the self-signed bearer shape.
- `LocalInstallationStore` persists the server-confirmed identity and credential material instead of fabricated GUIDs.
- `InstallationIdentityService` reworked or subsumed so installation registration is durable and server-verifiable (design's call).
- `DeviceBearerAuthenticationHandler` verifies the issued credential server-side; `CloudTenantScope` carries branch identity where the sync path needs it.

### Out of Scope
- Pairing-code UX generated in the SPA (exploration Approach 3).
- Changes to `TenantAuthorizationService`, `PostgresUserAccountStore` credential logic, or SPA `/account/sign-in` behavior.
- UI polish beyond a minimal functional sign-in + picker, consistent with the existing walking-skeleton WPF surface.
- Device-credential rotation/expiry policy beyond initial issuance — follow-up ADR unless design finds it trivially cheap.

## Capabilities

### New Capabilities
- `pos-installation-identity`: operator sign-in, branch selection, and server-issued installation-bound device credential for `Pos.Windows`.

### Modified Capabilities
- `tenant-access-foundation`: device bearer authentication must verify a server-issued credential and resolve branch-level scope instead of trusting caller-submitted GUIDs.
- `branch-offline-sync`: sync requests authenticate with the issued credential; unregistered installations are rejected.
- `organization-persistence`: branch listing constrained to a user's `BranchScope`.

## Approach

Exploration Approach 2, chosen by the user over the manual-GUID stopgap. Sign-in reuses Identity's existing credential store; a credential-exchange endpoint returns an installation-bound token persisted server-side. Validation is application-layer lookup, honoring the established "no FK retrofit onto existing tables" decision.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Pos.Windows/LocalInstallationStore.cs` | Modified | Stop self-minting; persist server-confirmed identity |
| `src/Commerce.Pos.Windows/MainWindow.xaml.cs`, `App.xaml.cs`, `PosHostBuilder.cs` | Modified | First-run sign-in/picker wiring |
| `src/Commerce.Pos.Windows/CloudSyncClient` | Modified | New credential header shape |
| `src/Commerce.Cloud.Api/Authentication/DeviceBearerAuthenticationHandler.cs` | Modified | Real verification |
| `src/Commerce.Cloud.Api/Tenancy/CloudTenantScope.cs` | Modified | Branch identity |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modified | Device credential issuance |
| `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` | Modified | Branch-by-user query |
| `src/Commerce.Application/Access/InstallationIdentityService.cs` | Modified/Removed | Durable or subsumed |
| `deploy/db/migrations/` | New | Device credential storage |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Bearer shape change breaks sync mid-rollout | High | POS + Cloud.Api ship together; no independently-mergeable halves |
| `InstallationIdentityService` durability spills scope | Med | Design bounds it to this flow or replaces it |
| WPF UI over-investment | Med | Minimal functional screens only |
| Credential lifecycle left undefined | Med | Issuance-only now; rotation flagged as follow-up ADR |

## Rollback Plan

Revert the coordinated POS + Cloud.Api commits together and roll back the credential-storage migration. Existing `installation.json` files become stale; operators re-run first-run sign-in (or the reverted self-mint path) to restore sync.

## Dependencies

- `commerce-organization-persistence` (`organizations`/`branches` tables, migration `0003`).
- ADR-002: ASP.NET Core Identity remains the sole identity provider.

## Success Criteria

- [ ] A fresh POS install cannot sync until an operator signs in and selects a branch.
- [ ] `Bearer` credentials are rejected unless server-issued and bound to that installation.
- [ ] Branch picker lists only branches in the operator's `BranchScope`.
- [ ] Sync rows are tagged with a real, existing organization + branch.
- [ ] No parallel identity system is introduced; sign-in reuses the existing credential store.
