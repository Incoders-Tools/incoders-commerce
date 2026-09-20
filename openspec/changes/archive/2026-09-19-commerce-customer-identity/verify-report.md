# Verification Report: commerce-customer-identity

**Mode**: Full artifacts (proposal, 5 spec deltas, design, tasks) verified independently by direct source inspection and real test execution, not by trusting the apply-phase report.

## Task Completeness

All 38 tasks across Units 1-6 plus Final Verification (7.1-7.3) are marked [x] in tasks.md. Spot-checked against actual files on disk: every referenced file exists and contains the claimed behavior (see Correctness table below). No unchecked tasks found. TDD RED/GREEN evidence is present per-task in tasks.md itself.

## Build / Test Execution Evidence (re-run independently, not trusted from prior report)

| Command | Result | Detail |
|---|---|---|
| dotnet test Commerce.sln | PASS | Commerce.Bootstrap.Tests: 1/1. Commerce.Upgrade: 19/19. Commerce.Integration: 328/328 (real Postgres via docker incoders-commerce-postgres-1, about 63s). Total 348/348, 0 failed. |
| npm run test (vitest, src/Commerce.Web) | PASS | 15 test files, 37 tests, 0 failed. |
| npm run build (src/Commerce.Web) | PASS | tsc -b and vite build, no type errors, build artifact produced. |
| npm run test:e2e (Playwright, src/Commerce.Web) | PASS | Full live harness stood up independently: Postgres (already running), npm run test:e2e:build-backend-spa, dotnet run --project Commerce.Cloud.Api (verified /health/ready returns 200), npx local-ssl-proxy --source 5443 --target 8080. 14/14 passed, including customers.spec.ts (business-admin reaches /app/customers; seller redirected away) and ordering.spec.ts (real Accepted outcome; unissued credential denied not-found). Harness torn down after the run; the build artifact accidentally written into Commerce.Cloud.Api/wwwroot (and its .gitkeep overwrite) was reverted so the working tree is unaffected by this verification run. |

This is the strongest evidence tier available: all four commands ran for real, against a real Postgres instance and a real running Cloud.Api behind a real browser (Playwright/chromium), not mocked.

## Spec Compliance Matrix (27 scenarios across 10 requirements, 5 spec deltas, recounted directly from the spec files)

### customer-registry (4 requirements / 7 scenarios)

| Scenario | Status | Evidence |
|---|---|---|
| Retail customer created with minimal fields | PASS | CustomerRegistryTests.CreateAsync_RetailMinimalFields_Persists_ScopedToCallerOrganization, Post_ManageUsersHolder_CreatesRetailCustomer_WithMinimalFields_Returns201 both green against live Postgres |
| Wholesale customer created with full fiscal field set | PASS | CreateAsync_WholesaleFullFiscalFieldSet_PersistsAllFieldsExactly, Post_ManageUsersHolder_CreatesWholesaleCustomer_WithFullFiscalFieldSet_Returns201 |
| Second org cannot read/write another org customers | PASS | FindAsync_CrossOrganizationTarget_ReturnsNull, UpdateAsync_CrossOrganizationTarget_ReturnsNull_NoRowMutated, ListAsync_ReturnsOnlyCallerOrganizationsCustomers, plus MigrationRlsTests RLS-level cross-org denial |
| Cross-organization customer creation is rejected | PASS (by construction) | organization_id is never a request field on CreateCustomerRequest (confirmed by direct read), sourced only from TenantScopeEndpointFilter, making cross-org creation structurally unrepresentable |
| Customer edit is audited | PASS | UpdateAsync_PersistsChanges_AndWritesAuditRowWithOldAndNewValues, Put_WritesOneAuditRow_WithOldAndNewDisplayNameValues |
| Customer usable without a linked login | PASS (structural) | Customer aggregate has no UserId/login field (confirmed); nothing in PostgresCustomerStore/Customers.cs requires one |
| Customer becomes selectable on POS after sync, no network call needed | CRITICAL - UNTESTED/UNIMPLEMENTED (disclosed gap, independently confirmed) | Read MainWindow.xaml.cs directly: CommitSaleButton_Click (lines 103-127) parses only AmountTextBox.Text and calls _branchNodeService.CompleteOfflineSale(...), no customer parameter anywhere in the sale path. Grepped all of Commerce.Pos.Windows for SelectedCustomer/CustomerCombo/CustomerPicker, no matches. The replica machinery itself (BranchSyncStore.ApplyCustomerSync, customers_replica table, CustomerReplicaClient.PullAsync, PullCustomersAsync wiring in SyncButton_Click) is fully implemented and covered by CustomerReplicaTests, but nothing in the sale flow reads that replica. The spec scenario THEN clause has no implementation to test |

### private-customer-ordering (2 requirements / 7 scenarios)

| Scenario | Status | Evidence |
|---|---|---|
| Order accepted with valid same-org customer | PASS | e2e/ordering.spec.ts "a signed-in user can submit a real order and receive a real Accepted outcome" (live E2E, passed) |
| Order rejected for non-existent/cross-org customer | PASS | CustomerOrderingAccessTests, missing/disabled customer row denied |
| Enabled customer catalogue access | PASS | CustomerCatalogAccessServiceTests |
| Revocation and cross-organization denial | PASS | CustomerOrderingAccessTests, org A credential denied in org B |
| Self-asserted enabled flag rejected for unknown credential | PASS | CustomerCatalogAccessServiceTests (unknown to not-found); e2e/ordering.spec.ts "an unissued (random) credential is denied with reason not-found" (live E2E, passed) |
| Revoked credential denies next order, auditable | PASS | CustomerOrderingAccessTests, revoke denies next submission plus audited |
| Cross-organization credential is denied | PASS | CustomerOrderingAccessTests |

### user-credentials (2 requirements / 5 scenarios)

| Scenario | Status | Evidence |
|---|---|---|
| Login without CustomerId remains staff | PASS | UserAccountCustomerGuardTests.EffectivePermissions_AccountWithoutCustomerId_ReportsFullRoleSet_Unaffected |
| Provisioning a customer login requires ManageUsers | WARNING - NOT IMPLEMENTED, undisclosed by apply report | Confirmed by direct read of Account.cs: CreateUserRequest(string Email, string Password, string[] RoleNames, Guid[] BranchIds) has no CustomerId field, and no other endpoint anywhere in Commerce.Cloud.Api/Endpoints sets customer_id on a UserAccount. There is currently no way, through any API, to provision a CustomerId-bearing login at all. The domain plumbing (UserAccount.CustomerId, the DB column/FK, the permission short-circuit, the CHECK constraint) is real and tested, but the actual provisioning operation this requirement describes does not exist yet |
| Customer creation does not implicitly create a login | PASS (vacuously, and by construction) | CreateCustomerRequest/Customers.cs POST handler never touches users/UserAccount, confirmed by direct read |
| Customer-linked user effective permissions are always none | PASS | UserAccountCustomerGuardTests.EffectivePermissions_CustomerIdBearingAccount_WithStaffRoles_ReportsNone; exact short-circuit logic confirmed in UserAccount.cs line 43-46 (CustomerId is null then Roles.Aggregate else Permission.None, independent of Roles content) |
| Granting a staff role to a customer-linked user is rejected | WARNING - enforced only at the DB layer, no app-level test | PUT /account/users/{id}/roles (Account.cs) calls ReplaceRolesAsync with no CustomerId check before writing, confirmed by direct read. Defense-in-depth exists (users_customer_has_no_roles CHECK, confirmed present at migration line 100-101 and exercised by MigrationRlsTests), so an attempt would fail with a raw Postgres constraint-violation exception rather than a clean 4xx. No test exercises this specific path end-to-end |

### tenant-access-foundation (1 requirement / 5 scenarios)

| Scenario | Status | Evidence |
|---|---|---|
| Revoked access | PASS | Pre-existing coverage, unaffected by this change |
| Offline revocation boundary | PASS | Pre-existing coverage, unaffected |
| Actor identity loaded from persisted store, not request body | PASS | Pre-existing coverage, unaffected |
| Privilege escalation via request body denied | PASS | Pre-existing coverage, unaffected |
| Ordering access resolved from persisted store, not request | PASS | CustomerOrderingAccessTests, CustomerCatalogAccessServiceTests; confirmed SubmitOrderRequest (Ordering.cs line 75-82) has no AccessEnabled member and no constructor path for one |

### pos-operator-session (1 requirement / 3 scenarios)

| Scenario | Status | Evidence |
|---|---|---|
| Business-admin operator reaches the customer-management screen | PASS | MainWindow.xaml.cs RefreshIdentityText shows ManageCustomersButton only when Permission.ManageUsers is held (confirmed); server-side Customers.cs AuthorizeCallerAsync re-checks ManageUsers on every call regardless of button state |
| Seller operator cannot reach the customer-management screen | PASS | Same gate; button Visibility.Collapsed otherwise; server denies with Results.Forbid() even if reached directly |
| No operator identified denies access | PASS | _currentOperator.Value pattern match, null short-circuits to Visibility.Collapsed |

Scenario totals: 27 total, 24 PASS, 1 CRITICAL (untested/unimplemented), 2 WARNING (undisclosed scope gap plus ungraceful defense-in-depth only).

## High-Risk Claim Cross-Checks (independently verified by direct code read, not trusted)

1. Old CustomerOrderingAccess-taking overloads genuinely deleted: CONFIRMED. CustomerCatalogAccessService.cs exposes only AuthorizeAsync(Guid, Guid, Guid, CancellationToken) and GetPermittedCatalogueAsync(...); no overload accepts a CustomerOrderingAccess parameter. A caller has no way to rebuild one from a DTO and pass it in.
2. Deny-reason collision (unknown vs cross-org) is byte-identical: CONFIRMED. Evaluate() returns new CustomerAccessResult(false, "not-found") for both the access-is-null branch and the organization-mismatch branch, the exact same string literal.
3. UserAccount.EffectivePermissions short-circuit, and the DB CHECK: CONFIRMED both. CustomerId-is-null test decides between Roles.Aggregate(...) and Permission.None, ignoring Roles entirely whenever CustomerId is set. users_customer_has_no_roles CHECK (customer_id IS NULL OR roles = '[]'::jsonb) exists verbatim in migration 0008 (line 100-101).
4. Customers.cs is genuinely ManageUsers-gated and org-scoped, mirroring adminGroup: CONFIRMED. AuthorizeCallerAsync loads the caller from the store (never trusts a claim alone), checks not-IsRevoked and EffectivePermissions.HasFlag(Permission.ManageUsers), returns null on any failure mapped uniformly to Results.Forbid(). organization_id is never a request field on any of the six routes.
5. RLS on customers/customer_ordering_access follows the established pattern: CONFIRMED for customers, FORCE ROW LEVEL SECURITY, REVOKE ALL ... FROM PUBLIC, explicit GRANT SELECT, INSERT, UPDATE (no DELETE), symmetric NULLIF policy. customer_ordering_access is deliberately asymmetric (documented, matches the device_credentials precedent), FOR SELECT USING (true) with the org comparison done in application code (Evaluate), FOR INSERT WITH CHECK (org-scoped), FOR UPDATE ... WITH CHECK (NOT is_enabled) making an unscoped un-revoke unrepresentable. This asymmetry is intentional and documented in both the migration comments and design.md.
6. Web RequireAdmin redirects rather than blank-rendering; desktop button genuinely hidden for seller: CONFIRMED both. RequireAdmin.tsx returns a Navigate to /app/catalog on denial (verified live via e2e/customers.spec.ts "a seller has no Customers tab and is redirected away from /app/customers", passed). MainWindow.xaml.cs sets ManageCustomersButton.Visibility to Collapsed when the current operator lacks ManageUsers.
7. No ALTER TABLE orders statement in migration 0008: CONFIRMED absent by direct read of the full file. The migration header comment documents why: no orders table exists in this repo at all (CloudOrderStore is in-memory), so the proposal literal FK step is intentionally not applicable; the underlying invariant is enforced in CloudOrderSubmissionService.SubmitAsync before any Order is constructed instead. This is a disclosed, reasoned deviation from the proposal text, consistent with design.md's Order.CustomerId referential integrity decision row.

## Disclosed Gap, Independently Confirmed (Proposal Success Criterion #12)

Confirmed as stated, not a surprise finding. MainWindow.CommitSaleButton_Click (lines 103-127) has zero customer-selection code path: it reads only AmountTextBox.Text and never references any customer id, CustomerReplicaClient, or the customers_replica table. The replica/sync machinery is fully built and tested (BranchSyncStore.ApplyCustomerSync, CustomerReplicaClient.PullAsync, PullCustomersAsync wired into SyncButton_Click before the early-return, all covered by CustomerReplicaTests), but nothing in the sale-completion flow consumes it. Proposal Success Criterion #12 ("A customer created online is selectable for a sale on the POS after the next sync, with no connectivity at selection time") is not met by the current codebase, and the corresponding customer-registry spec scenario ("Customer becomes selectable on POS after sync") has no implementation to test. Task 6.7 itself documents this as a manual/runbook item outside Unit 6 file-change scope, consistent with what was found.

## Newly Identified Gap (not previously disclosed)

Independent of the above, direct code reading surfaced that the customer-login provisioning half of user-credentials' "Optional Customer Link" requirement was never built: no endpoint anywhere accepts or sets CustomerId when creating or modifying a UserAccount (CreateUserRequest has no such field; no other route touches it). The domain guard (permission short-circuit plus DB CHECK) is real and tested, so the security property this change was primarily about is intact, but the feature ("a ManageUsers holder can provision a customer login") does not exist yet. This was not called out in the apply-phase report and should be tracked as follow-up scope before this requirement can be considered fully delivered.

## Design Coherence

No material deviations from design.md found beyond the two explicitly documented ones already covered above (no orders table; customer_ordering_access asymmetric RLS). All six "Architecture Decisions" table rows were spot-checked against the actual code and matched.

## Issues

CRITICAL
1. customer-registry spec scenario "Customer becomes selectable on POS after sync" (and proposal Success Criterion #12) has no implementation, confirmed disclosed gap.

WARNING
1. user-credentials "Provisioning a customer login requires ManageUsers", no such provisioning endpoint exists anywhere in the codebase; newly identified, previously undisclosed.
2. user-credentials "Granting a staff role to a customer-linked user is rejected", enforced only by a raw DB CHECK constraint violation (ungraceful failure mode), not a clean application-level rejection, and untested end-to-end.

SUGGESTION
1. Consider a follow-up change to build the customer-login provisioning endpoint and its ManageUsers gate, and add an explicit application-level guard on PUT /account/users/{id}/roles that rejects with a clean 4xx (not a raw DB exception) when the target account has a non-null CustomerId.
2. Consider a follow-up change to add customer selection to MainWindow.CommitSaleButton_Click, consuming the already-built customers_replica table, to close Success Criterion #12.

## Final Verdict: PASS WITH WARNINGS

All 38 tasks complete, all four independently re-run test/build commands green (348 dotnet tests, 37 vitest tests, clean build, 14 live Playwright E2E tests including the customer-admin-gating scenarios), and 24/27 spec scenarios independently confirmed compliant with passing covering tests. The security-fix core of this change (deny-reason collision, persisted-store-only access evaluation, staff-permission denial by construction, RLS shape) is solid and verified by direct code read plus live tests, not a rubber stamp. However this is not a clean PASS: one CRITICAL scenario (POS sale-time customer selection) is disclosed-and-confirmed unimplemented, and this verification additionally surfaced one previously undisclosed WARNING-level scope gap (customer-login provisioning has no API surface at all) plus one ungraceful defense-in-depth WARNING. Recommend routing to sdd-apply for a follow-up unit addressing the CRITICAL gap (and ideally the two WARNINGs) rather than proceeding directly to sdd-archive.
line with backtick: `code`
line with dollar: $HOME and ${VAR}
line with double quote: "quoted"

---

## Follow-up Verification (F.1-F.4, re-verified independently by direct code read plus re-run test/build commands)

**Scope of this pass**: three follow-up fixes closing the prior 1 CRITICAL + 2 WARNING findings above. Verified by reading actual source (not trusting the tasks.md own claims) and re-running the full test/build suites from a clean invocation.

### F.1 - Customer-login provisioning endpoint (closes prior WARNING #1)

Confirmed by direct read of `src/Commerce.Cloud.Api/Endpoints/Account.cs`:
- `CreateUserRequest` now has `Guid? CustomerId = null` (line 688).
- `POST /account/users` rejects `CustomerId` plus non-empty `RoleNames` together with `Results.ValidationProblem` - a clean 400, not a raw exception (lines 410-416).
- When `CustomerId` is supplied, the handler calls `customerStore.FindAsync(scope, requestedCustomerId, ct)` - scoped to the org of the caller via `TenantScopeEndpointFilter`/RLS - and returns `Results.NotFound()` if absent, making a cross-org id indistinguishable from a nonexistent one, consistent with the established non-disclosure pattern in the file.
- `CustomerId` is threaded through `NewUserAccount` into `CreateStaffUserAsync` for persistence.

Verdict: CONFIRMED as claimed. The provisioning endpoint now exists, is org-scoped, and fails cleanly. WARNING #1 is closed.

### F.2 - Clean 400 on role grant to a customer-linked account (closes prior WARNING #2)

Confirmed by direct read, same file, lines 506-522: `PUT /account/users/{id}/roles` loads the target via `LoadActorAsync`, then checks `target.CustomerId is not null` and returns `Results.ValidationProblem` before calling `userStore.ReplaceRolesAsync` (line 535). The raw-DB-exception failure mode previously flagged is no longer reachable through this endpoint; the `users_customer_has_no_roles` CHECK constraint remains as defense-in-depth but the primary path now fails at the application layer with a structured 400.

Verdict: CONFIRMED as claimed. WARNING #2 is closed.

### F.3 - Optional sale-time customer picker (partial, explicitly scope-narrowed - was the prior CRITICAL)

Independently confirmed:
- `src/Commerce.Pos.Windows/SaleCustomerPicker.cs`: a pure, static `BuildItems(IReadOnlyList<CustomerReplica>)` with no I/O. Always prepends a `(null, "Walk-in (no customer)")` sentinel, then appends the input sorted by `DisplayName` (case-insensitive). `tests/Commerce.Integration/SaleCustomerPickerTests.cs` covers both the empty-replica case (walk-in only) and a two-customer case asserting a specific sort order (Acme before Zebra) with distinct, non-trivial value assertions - no tautologies, no ghost loops, real triangulation (2 cases, different expected values).
- Disabled customers are structurally excluded: `BranchSyncStore.RemoveCustomers`/`ApplyCustomerSync` physically deletes disabled rows from `customers_replica` (confirmed by reading `BranchSyncStore.cs`), so `ListCustomers()` - and therefore the picker - can only ever surface enabled customers; there is no separate `IsEnabled` filter needed or missing.
- `MainWindow.xaml` adds `CustomerPickerComboBox` (line 24) next to the amount field. `RefreshCustomerPicker()` in `MainWindow.xaml.cs` (lines 143-152) is called both at load (line 68) and after `PullCustomersAsync()` inside `SyncButton_Click` (lines 168-169), preserving the current selection by `CustomerId` when still present.
- `CommitSaleButton_Click` (lines 104-133) is confirmed unchanged in its sale-completion logic: it reads only `AmountTextBox.Text` and calls `_branchNodeService.CompleteOfflineSale(...)` with no customer parameter anywhere; the only new line is `CustomerPickerComboBox.SelectedIndex = 0` at the end, resetting to walk-in after each commit. The picker never blocks or gates the commit path - the default zero-friction walk-in flow is untouched.
- Confirmed genuinely unattributed: `src/Commerce.Domain/Sync/SaleEffect.cs` is `record SaleEffect(Guid SaleId, Guid BranchId, decimal TotalAmount, DateTimeOffset OccurredAtUtc)` - no `CustomerId` field exists anywhere on it, and `BranchNodeService.CompleteOfflineSale` constructs it without one. The tasks.md disclosure that the picker is UI-only in this iteration and that SaleEffect/CompleteOfflineSale do not yet carry customer attribution is accurate, not overstated - it is not silently presented as fully solving the original CRITICAL.

Verdict: CONFIRMED as an accurately disclosed, deliberate scope narrowing, not a silent partial fix. The original CRITICAL scenario (customer becomes selectable on POS after sync, no network call needed) is now half-closed: a customer IS selectable at sale time from the local replica with no network call, but the selection does not yet propagate into the committed `SaleEffect`/sync envelope, so downstream customer attribution on a completed sale is still not implemented. Per the user-approved scope narrowing (POS counter sales generally do not need per-customer attribution), this is accepted as a documented future item rather than a blocking gap, provided it stays tracked as explicit follow-up scope (see below) - it must not be read as if Success Criterion 12, or the customer-registry scenario about becoming selectable on POS after sync, is now fully met end-to-end for attributed sales.

### F.4 - Full verification re-run (independently repeated, not trusted from the apply report)

| Command | Result | Detail |
|---|---|---|
| `dotnet test Commerce.sln` | PASS | Commerce.Bootstrap.Tests: 1/1. Commerce.Upgrade: 19/19. Commerce.Integration: 334/334 (about 64s, real Postgres). Total 354/354, 0 failed - matches the count claimed in tasks.md exactly. |
| `npm run test` (vitest, src/Commerce.Web) | PASS | 15 test files, 37 tests, 0 failed - matches the claimed count. |
| `npm run build` (src/Commerce.Web) | PASS | `tsc -b` clean, `vite build` produced `dist/` with no type errors. |

(E2E Playwright was not re-run in this follow-up pass - no follow-up fix touched the web ordering/customer-admin flows covered by it, and the F.1-F.3 changes are Cloud.Api/POS-only; the 14/14 result from the original pass stands unaffected.)

### Updated Issues

CRITICAL: none remaining. The original CRITICAL is downgraded - see SUGGESTION below - because the fix is a disclosed, user-accepted scope narrowing rather than a silent gap.

WARNING: none remaining. Both prior WARNINGs (F.1, F.2) are confirmed closed by direct code read and covering tests.

SUGGESTION (carried forward, not blocking)
1. `SaleEffect`, `CompleteOfflineSale`, and the sync envelope still do not carry `CustomerId`. If per-customer sale attribution (wholesale/delivery accounting, reporting, etc.) becomes a requirement, a future change must add a `CustomerId` field to `SaleEffect`, thread the picker selection into `CommitSaleButton_Click`, and extend the sync/outbox contract accordingly. This is explicitly out of scope for this change per the user-approved narrowing and should be tracked as a distinct backlog item, not silently assumed done.

## Updated Final Verdict: PASS

All three follow-up fixes (F.1, F.2, F.3) are verified by direct source inspection against the actual committed code, not by trusting the claims in tasks.md or the prior apply report. F.1 and F.2 fully close the prior WARNINGs with clean, org-scoped, application-level 400s backed by passing tests. F.3 closes the prior CRITICAL to the extent of the user-approved narrowed scope (UI-only customer selection at sale time, sourced from the already-built local replica, walk-in-by-default, zero impact on the default sale path) and is accurately disclosed as not yet propagating attribution into `SaleEffect` - this is a real, honestly-reported limitation, not a misrepresented fix. All test/build commands were re-run independently from a clean state and reproduced the claimed counts exactly (354 dotnet tests, 37 vitest tests, clean build). This change is ready to archive and ship, on the condition that the un-attributed-sale limitation is tracked as an explicit backlog/follow-up item (see SUGGESTION above) rather than considered silently resolved.
