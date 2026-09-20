# Tasks: Commerce Customer Identity

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2950 (design estimate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes (design), user accepted single-PR exception |
| Suggested split | Single PR, 6 internally-ordered units, size:exception |
| Delivery strategy | single-pr |
| Chain strategy | size-exception |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: size-exception
400-line budget risk: High

**Exception note**: user explicitly accepted single-PR delivery for ~2950 lines (~7.4x budget) after design recommended 6 chained PRs. Unit ordering below MUST be preserved as commit-range structure inside the one PR so each unit remains separately reviewable and revertible.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | `0008` migration + RLS/grants | PR1 (commit range) | `dotnet test --filter MigrationRlsTests` | `/health/ready` before/after apply | Revert range; run inverse SQL block |
| 2 | Customer aggregate + store + guard | PR1 | `dotnet test --filter Customer\|UserAccount` | N/A (no endpoint yet) | Revert range; tables unused |
| 3 | Security fix (access resolver rewrite) | PR1 | `dotnet test --filter CustomerCatalogAccessService\|CloudOrderSubmission` | Manual: submit order w/ self-asserted `accessEnabled` denied | Revert range; defect reopens (documented) |
| 4 | Customers.cs endpoints | PR1 | `dotnet test --filter CustomerRegistryTests` | `WebApplicationFactory` + Postgres | Revert range; aggregate stays, unreachable |
| 5 | Web CustomersScreen + routing | PR1 | `npm run test -- CustomersScreen` | `npm run build` | Revert range; API untouched |
| 6 | POS CustomersWindow + BranchNode sync | PR1 | `dotnet test --filter CustomerReplicaTests` | Manual desktop walkthrough (no WPF harness) | Revert range; cloud/web unaffected |

## Unit 1: Migration 0008 + RLS (customer-registry, tenant-access-foundation)

- [x] 1.1 RED: `tests/Commerce.Integration/MigrationRlsTests.cs` — assert `customers`/`customer_ordering_access` FORCE RLS, no `DELETE` grant, `users_customer_has_no_roles` CHECK rejects non-empty roles, cross-org SELECT/INSERT/UPDATE denied, unscoped un-revoke denied
- [x] 1.2 GREEN: `deploy/db/migrations/0008_customer_registry.sql` — tables, indexes, `users.customer_id` + FK + CHECK, RLS/policies per design's Interfaces/Contracts SQL block
- [x] 1.3 Mirror DDL verbatim into `deploy/dev/db/init-rls.sql`; update `deploy/README.md` apply/inverse sections
- [x] 1.4 GREEN: `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` — add both tables + policy checks
- [x] 1.5 Verify: apply `0008` twice cleanly; `/health/ready` fails before, passes after

## Unit 2: Customer aggregate + guard (customer-registry, user-credentials)

- [x] 2.1 RED: `tests/Commerce.Integration/UserAccountCustomerGuardTests.cs` — `CustomerId`-bearing account with roles reports `Permission.None`; without `CustomerId` reports full set (deviation: placed in `tests/Commerce.Integration`, the repo's ONLY test project — `tests/Commerce.Cloud` does not exist)
- [x] 2.2 GREEN: `src/Commerce.Domain/Identity/UserAccount.cs` — add `Guid? CustomerId`, short-circuit `EffectivePermissions`
- [x] 2.3 RED: `tests/Commerce.Integration/CustomerTests.cs` — `tax_id` invariant, `Enable`/`Disable`, empty `customerId` ctor guard on `Order` (same path deviation as 2.1)
- [x] 2.4 GREEN: `src/Commerce.Domain/Customers/Customer.cs`, `CustomerKind.cs`, `TaxIdType.cs`, `TaxCondition.cs`; `src/Commerce.Domain/Ordering/Order.cs` guard
- [x] 2.5 RED+GREEN: `src/Commerce.Cloud.Api/Persistence/CustomerRecords.cs` DTOs; `PostgresCustomerStore.cs` (Create/Update/Find/List/ListChangedSince) mirroring `PostgresOrganizationStore`; integration tests in `tests/Commerce.Integration/CustomerRegistryTests.cs`

## Unit 3: Security fix — MUST be green before Unit 4 (private-customer-ordering, tenant-access-foundation)

- [x] 3.1 RED: `CustomerCatalogAccessServiceTests.cs` — unknown credential → `not-found`; disabled → `credential-revoked`; cross-org row → `not-found` (string-equal to unknown); allow+deny both audited
- [x] 3.2 GREEN: `src/Commerce.Application/Ordering/ICustomerOrderingAccessResolver.cs`; rewrite `CustomerCatalogAccessService.cs` to `AuthorizeAsync(orgId, credential, corrId)` / `GetPermittedCatalogueAsync(...)`, delete `CustomerOrderingAccess`-taking overloads
- [x] 3.3 GREEN: `src/Commerce.Cloud.Api/Persistence/PostgresCustomerOrderingAccessStore.cs` implementing the resolver + Issue/Revoke
- [x] 3.4 RED: `CustomerOrderingAccessTests.cs` (integration) — credential caller doesn't own denied; revoke denies next submission + audited; org A credential denied in org B; missing/disabled customer row denied; `SubmitOrderRequest` has no `AccessEnabled` member
- [x] 3.5 GREEN: `src/Commerce.Cloud.Api/Endpoints/Ordering.cs` — remove `AccessEnabled`; `CloudOrderSubmissionService.SubmitAsync` — resolve access, assert binding+existence+enabled before `CloudOrderStore.Submit`
- [x] 3.6 GREEN: `src/Commerce.Cloud.Api/Program.cs` — register resolver/store; update `src/Commerce.Web/src/screens/OrderScreen.tsx` and `e2e/ordering.spec.ts` to stop sending `accessEnabled`
- [x] 3.7 Verify: all Unit 3 tests green before starting Unit 4

## Unit 4: Customers.cs endpoints (customer-registry)

- [x] 4.1 RED: `CustomerRegistryTests.cs` — `ManageUsers` creates Retail(2 fields)/Wholesale(full fiscal); `seller` → 403; one audit row per create/edit with old/new values; rolled-back create writes none; cross-org target on `PUT` → 404 identical to nonexistent
- [x] 4.2 GREEN: `src/Commerce.Cloud.Api/Endpoints/Customers.cs` — six routes (`GET`/`GET {id}`/`POST`/`PUT {id}`/`POST {id}/ordering-access`/`DELETE {id}/ordering-access`) using `adminGroup` shape verbatim
- [x] 4.3 GREEN: `Account.cs`/`Device.cs` — `Permissions` on `/account/me`, sign-in response, `OperatorVerifyResponse`
- [x] 4.4 GREEN: `Program.cs` — `MapCustomerEndpoints`
- [x] 4.5 RED+GREEN: `GET /device/customers/sync` — device-bearer required, org from stored credential row, `since` filtering, `disabledIds` propagation

## Unit 5: Web CustomersScreen (customer-registry, user-credentials)

- [x] 5.1 RED: `AuthContext.test.tsx`/`RequireAdmin.test.tsx` — redirects `seller`, allows `ManageUsers` holder
- [x] 5.2 GREEN: `src/Commerce.Web/src/api/customers.ts`, `api/types.ts`, `auth/AuthContext.tsx` (carry `permissions`), `routes/RequireAdmin.tsx`
- [x] 5.3 GREEN: `App.tsx`/`routes/AppLayout.tsx` — `/app/customers` route, nav tab hidden without bit
- [x] 5.4 RED: `CustomersScreen.test.tsx`/`CustomerForm.test.tsx` — list/create/edit against mocked client; `CustomerKind` read-only at edit
- [x] 5.5 GREEN: `src/Commerce.Web/src/screens/CustomersScreen.tsx`, `CustomerForm.tsx`
- [x] 5.6 Verify: `npm run test` and `npm run build` pass; `ordering.spec.ts` passes

## Unit 6: POS CustomersWindow + BranchNode sync (pos-operator-session, customer-registry)

- [x] 6.1 RED: `tests/Commerce.Integration/CustomerReplicaTests.cs` — `customers_replica` upsert idempotent, `RemoveCustomers` deletes, cursor advances only on success, failed pull leaves both untouched (deviation: placed in `tests/Commerce.Integration`, the repo's ONLY test project — `tests/Commerce.BranchNode` does not exist, same deviation precedent as 2.1/2.3)
- [x] 6.2 GREEN: `src/Commerce.BranchNode/BranchSyncStore.cs` — add `customers_replica`/`sync_cursors` tables + Upsert/Remove/List/cursor methods
- [x] 6.3 GREEN: `CachedOperator.cs`, `LocalOperatorStore.cs`, `OperatorProvisioningClient.cs` — carry `Permissions` int
- [x] 6.4 GREEN: `src/Commerce.Pos.Windows/CustomerAdminClient.cs` (cookie client), `CustomerReplicaClient.cs` (device-bearer client); `PosHostBuilder.cs` registration (RED+GREEN via `PosCompositionRootTests` for the composition-root registration; the clients themselves have no direct unit test, same precedent as `OperatorProvisioningClient`)
- [x] 6.5 GREEN: `MainWindow.xaml(.cs)` — permission-gated "Manage customers" button; `PullCustomersAsync()` called after `ReconcileOperatorsAsync()`, before `pending.Count == 0` early return
- [x] 6.6 GREEN: `CustomersWindow.xaml(.cs)` — modal admin sign-in + list/create/edit; connectivity-required messaging
- [x] 6.7 Manual (no WPF harness): structural/wiring verification done (build green, all handlers referenced by the XAML exist and match design's flow); the actual desktop walkthrough (admin creates customer from `CustomersWindow`, appears in web list; `seller` sees no button; offline shows connectivity message; after Sync press, customer selectable offline) remains a runbook step for a human with a running desktop session, consistent with `PairingWindow`/`OperatorLoginWindow` precedent — no automated coverage fabricated

## Final Verification

- [x] 7.1 `dotnet test Commerce.sln` full suite green
- [x] 7.2 `npm run test` and `npm run build` (src/Commerce.Web) green
- [x] 7.3 Confirm all proposal Success Criteria checked

## Follow-up fixes (post sdd-verify: 1 CRITICAL + 2 WARNINGs)

- [x] F.1 Customer-login provisioning: `POST /account/users` (`Account.cs`) accepts optional `CustomerId` on `CreateUserRequest`; validates the target `Customer` exists in the caller's own org (404 if not, RLS-scoped so cross-org is indistinguishable from nonexistent); rejects `CustomerId` + non-empty `RoleNames` together with a clean 400 ("a customer-linked account cannot hold staff roles"). `NewUserAccount`/`PostgresUserAccountStore.InsertAsync`/`LoadActorAsync` carry `CustomerId` end to end; `deploy/db/migrations/0008_customer_registry.sql` adds the nullable FK + `users_customer_has_no_roles` CHECK. Covered by `UserAccountCustomerGuardTests` (domain guard) and `MigrationRlsTests` (DB constraint); `dotnet build` of `Commerce.Cloud.Api` verified clean.
- [x] F.2 Clean 400 instead of raw DB exception: `PUT /account/users/{id}/roles` (Account.cs) checks `target.CustomerId is not null` BEFORE calling `ReplaceRolesAsync`, returning `ValidationProblem`/400 instead of letting the `users_customer_has_no_roles` Postgres CHECK throw raw.
- [x] F.3 Optional sale-time customer picker (narrowed scope: anonymous walk-in stays the default, zero-friction path; this is for the optional wholesale/delivery case only):
  - [x] F.3.1 RED: `tests/Commerce.Integration/SaleCustomerPickerTests.cs` (pre-existing) — empty replica returns only the walk-in sentinel; non-empty replica sorts by display name with walk-in always first
  - [x] F.3.2 GREEN: `src/Commerce.Pos.Windows/SaleCustomerPicker.cs` — pure `BuildItems(IReadOnlyList<CustomerReplica>)` mapping, no I/O
  - [x] F.3.3 Wire into `MainWindow.xaml`/`.xaml.cs`: optional `CustomerPickerComboBox` next to the amount field, defaulting to walk-in (index 0); refreshed after `PullCustomersAsync()` (preserving the current selection by `CustomerId` when still present) and reset to walk-in after each committed sale. `CommitSaleButton_Click`'s existing offline-sale path is unchanged — the picker is UI-only in this iteration; `SaleEffect`/`CompleteOfflineSale` do not yet carry customer attribution (no domain change was in the narrowed scope). WPF wiring itself has no automated harness (established precedent); manual verification only.
- [x] F.4 Full verification: `dotnet test Commerce.sln` (354 tests, 0 failed), `npm run test` (37 tests, 0 failed) and `npm run build` (src/Commerce.Web) all green.
