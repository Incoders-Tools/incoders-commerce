# Design: Commerce Customer Identity

## Technical Approach

Four structurally different pieces land together, plus three codebase facts the
proposal assumed and this design verified to be **false**. Those three are
recorded first, because two of them change what "implement the proposal" means.

**Verified deviation 1 — there is no `orders` table.** `grep -i orders deploy/`
returns nothing. Orders live in `CloudOrderStore`'s in-memory
`Dictionary<Guid, Order>` (`src/Commerce.Cloud.Api/Ordering/CloudOrderStore.cs:30`)
and vanish on restart. The proposal's locked decision "`orders.customer_id`
becomes `NOT NULL` and strictly FK-enforced; delete pre-change order rows" is
therefore **not applicable as written**: there is no column to alter, no
constraint to add, and no row to delete. The *invariant* the decision buys —
"an order cannot be accepted for a `CustomerId` with no matching customer row
in the caller's organization" (success criterion) — is preserved, enforced in
the submission path instead of by a database constraint. Persisting orders is a
separate, larger change and is explicitly **not** pulled in here. See the
decisions table and Open Questions.

**Verified deviation 2 — `Commerce.BranchNode` has no cloud→local sync channel
to reuse.** The project is two files. `BranchSyncStore` owns exactly three
SQLite tables: `sale_effects`, `outbox` (local→cloud), and `inbox`, and `inbox`
stores **only** `(operation_id, applied_at_utc)` — it is a pure idempotency
ledger that discards the envelope payload (`BranchSyncStore.cs:187-196`). There
is no catalog sync, no pricing sync, and no pull direction at all. The
proposal's "reuses the existing cloud→local sync channel (the same one
catalog/pricing already use)" describes a mechanism that does not exist. This
design builds the **minimum viable** pull channel instead, modelled on the one
real precedent for a periodic cloud read on the terminal:
`MainWindow.ReconcileOperatorsAsync` (`MainWindow.xaml.cs:213-232`), which
already runs cloud reconciliation on the explicit Sync button press, before the
`pending.Count == 0` early return.

**Verified deviation 3 — the POS has no operator role data and no cookie
session.** `OperatorVerifyResponse` returns `(status, userId, email,
organizationId)` only (`Device.cs:263`); `CachedOperator` carries no roles
(`CachedOperator.cs:9`); the terminal authenticates with a device bearer token,
never a user cookie. So "gate the desktop screen the same way `MainWindow`'s
existing admin-only affordances are" has no existing mechanism either — neither
`RepairButton_Click` nor `SwitchOperatorButton_Click` is role-gated; both are
unconditionally visible by design ("always visible, explicit action, never an
interrupt"). Gating must be built, and the server must remain the authority.

**The four pieces.**

1. **`Customer` aggregate + `customers` table.** `src/Commerce.Domain/Customers/`,
   `PostgresCustomerStore` mirroring `PostgresOrganizationStore`/`PostgresUserAccountStore`
   byte-for-byte (raw `NpgsqlDataSource`, one transaction per scoped method,
   `set_config('app.current_org_id', $1, true)` always the first statement).
   RLS mirrors `branches_tenant_isolation` exactly.
2. **The security fix, made *unrepresentable* rather than validated-away.**
   `CustomerCatalogAccessService`'s public surface stops accepting a
   `CustomerOrderingAccess` at all. It takes an org id, a credential, and a
   correlation id, and resolves the access record through an injected
   `ICustomerOrderingAccessResolver`. Passing a body-built access object is not
   rejected at runtime — it does not compile. `SubmitOrderRequest` loses
   `AccessEnabled`.
3. **One identity plane.** Nullable `users.customer_id` + FK, with the
   staff-permission denial enforced in `UserAccount.EffectivePermissions` itself
   *and* by a table `CHECK`.
4. **Two admin UIs against one endpoint set.** `Customers.cs` reuses
   `Account.cs`'s `adminGroup` authorization shape verbatim. `Commerce.Web`
   calls it with its existing cookie. `Commerce.Pos.Windows` performs a real
   `POST /account/sign-in` from the customer-management window and calls the
   *same* endpoints with that cookie — no new auth surface, no POS-specific
   customer endpoint, no dual-write.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **`Order.CustomerId` referential integrity, given no `orders` table exists** | **Enforce in the submission path, not in DDL.** `CloudOrderSubmissionService.SubmitAsync` resolves the access record, then requires that (a) the resolved record's `CustomerId` equals the request's `CustomerId` (binding), and (b) `PostgresCustomerStore.FindAsync(scope, customerId)` returns a row with `IsEnabled = true`. Any failure denies **before** `CloudOrderStore.Submit` is reached, so an `Order` object with a dangling `CustomerId` is never constructed. `Order.CustomerId` stays `Guid` and the constructor gains a `Guid.Empty` guard. Migration `0008` contains **no** `orders` statements and **no** row deletion — there is nothing to delete, and shipping a `DELETE FROM orders` against a nonexistent table would abort the migration. The success criterion "an order cannot be accepted for a `CustomerId` with no matching customer row in the caller's organization" is met, and RLS makes a cross-org customer invisible so `FindAsync` returns null identically to "no such customer". | **Create an `orders` table in `0008` and persist orders.** This is the only way to get a literal FK, but it drags in order-line persistence, status/pending-reason transitions, the `OrderId`-as-`OperationId` idempotency contract, and a persisted replacement for `CloudOrderStore` — a change comparable in size to this whole proposal, for an aggregate the proposal explicitly scopes as "carries the data". Recorded as the follow-up that makes the FK real. **Ship a `0008` with `ALTER TABLE orders …` anyway** — fails on the first statement in every environment. |
| **How the persisted access record is read (the security fix's exact shape)** | **A new interface in the Application layer, implemented in Cloud.Api.** `ICustomerOrderingAccessResolver.ResolveAsync(Guid organizationId, Guid credential, CancellationToken)` in `Commerce.Application/Ordering/`, implemented by `PostgresCustomerOrderingAccessStore` in `Commerce.Cloud.Api/Persistence/`. `CustomerCatalogAccessService.AuthorizeAsync(Guid requestedOrganizationId, Guid credential, Guid correlationId)` and `GetPermittedCatalogueAsync(...)` replace the current `CustomerOrderingAccess`-taking overloads; the private `Evaluate` keeps its exact deny reasons (`credential-revoked`, `not-found`, `allowed`) plus a new `not-found` for an unresolved credential — deliberately the **same** reason string as cross-org, so a probe cannot distinguish "wrong org" from "no such credential". Audit still fires on every decision through the existing `IAuditSink`; on an unresolved credential the audited `ActorId` is `Guid.Empty` (there is no customer to name). Keeping `Commerce.Application` free of Npgsql preserves the existing layering — the interface lives with its consumer, the implementation with the database. | **Give `CustomerCatalogAccessService` an `NpgsqlDataSource`** — puts persistence in the Application layer, which nothing else there does, and makes the service untestable without Postgres. **Keep the `CustomerOrderingAccess` parameter and have the endpoint pass a store-read instance** — achieves the same runtime property but leaves the trust boundary as convention: a future caller can rebuild one from a DTO and the compiler is silent. The repo's own idiom (`CreateUserRequest` has no permissions field: "not validated-away, unrepresentable") says make it a type-level property. |
| **`customer_ordering_access` credential storage** | **`credential_hash text PRIMARY KEY` = `sha256(credential.ToString())` hex**, exactly the `device_credentials` precedent (`0004`), including its rationale: the input is a 122-bit uniformly random value, so plain SHA-256 is correct and a slow KDF would add latency to every order submission for zero gain. The wire type stays `Guid` (`SubmitOrderRequest.AccessCredential` is unchanged), so the web client and e2e fixture need no credential-format change — only `accessEnabled` is removed. RLS is **asymmetric**, the `device_credentials_lookup` precedent: `FOR SELECT USING (true)` because the resolver runs inside an already-scoped transaction but matches on the hash, with the org comparison done in `Evaluate` against the row's own `organization_id`; `FOR INSERT WITH CHECK (organization_id = …)` and `FOR UPDATE USING (true) WITH CHECK (NOT is_enabled)` so an unscoped *un*-revoke is unrepresentable. | **Store the raw uuid** — a read of one table hands out live ordering credentials, and `device_credentials` already established that bearer secrets are hashed at rest. **Change the credential to a 32-byte string secret** — strictly better cryptographically, but it changes `CustomerOrderingAccess.Credential`, the DTO, the web client, and the e2e fixture, for 122→256 bits on a value that is already unguessable. The proposal locked this fix as *narrowing*; widening the wire contract fights that. |
| **Staff-permission denial for a `CustomerId`-bearing user** | **Short-circuit `UserAccount.EffectivePermissions` to `Permission.None` whenever `CustomerId` is set, regardless of `Roles`** — one line, one choke point, and *every* authorization site in the repo already reads that property (`Account.cs` ×4, `RoleGrantPolicy`, `TenantAuthorizationService`). A customer login therefore cannot exercise a staff permission even if a future endpoint assigns roles, even if a row is hand-edited in psql, even if `ReplaceRolesAsync` is called on it. Defense-in-depth: `0008` adds `CONSTRAINT users_customer_has_no_roles CHECK (customer_id IS NULL OR roles = '[]'::jsonb)`, so the invalid state is not merely neutralized in C# but rejected by the database. `Roles` stays on the type (removing it would fork the aggregate). | **Enforce it at the provisioning endpoint only (never assign roles to a customer-linked user).** This is a convention on one code path. It is defeated by `PUT /account/users/{id}/roles` (which loads the target and replaces roles with no `customer_id` awareness), by any second provisioning path, and by a direct jsonb write — precisely the "by convention, not by construction" failure the proposal's own risk row names. The `CHECK` alone was also rejected as the *sole* guard: it protects the database, not an in-memory `UserAccount` built by a test or a future non-Postgres store. |
| **Desktop authorization for customer create/edit** | **The customer-management window performs a real `POST /account/sign-in` with the admin's email + password and calls the same `/customers` endpoints with the resulting cookie.** Zero new auth surface (proposal assumption 3 preserved), zero POS-specific customer endpoint, and the server-side gate is literally the same `LoadActorAsync` + `EffectivePermissions.HasFlag(ManageUsers)` check the web hits. Connectivity is required, which the proposal already accepts as the pairing/bootstrap precedent. The cookie is held in a `CookieContainer` on a window-scoped `HttpClient` and discarded when the window closes — never persisted, never written to `operators.json`. Separately, `OperatorVerifyResponse` gains `Permissions` (the `int` from `actor.EffectivePermissions`, server-derived, never body-supplied) and `CachedOperator` gains a `Permissions` field, used **only** to show or hide the button — a UX affordance, explicitly not the security boundary. | **Mint an operator-scoped bearer token at `/device/operators/verify`** — a third auth surface, a new token lifecycle, new revocation semantics, and it contradicts proposal assumption 3. **Add `/device/customers` endpoints authorized by the device bearer token plus a body-supplied `operatorUserId`** — the acting identity would come from the request body, which is exactly the defect this change exists to fix. **Trust the locally cached `Permissions` flag as the gate** — the PIN cache is on a machine the attacker may own; a local flag is not an authorization decision. |
| **Web admin gating** | `GET /account/me` and the sign-in response gain `permissions: number` (server-derived from `LoadActorAsync(scope, userId)`; `/me` currently reads claims only, so it gains one store call). `SignedInResponse` and `AuthContext` carry it; a new `RequireAdmin` route guard mirrors `RequireAuth.tsx` exactly (`Navigate` to `/app/catalog` on denial, default-deny on a null user) and wraps only the `customers` route; `AppLayout`'s nav hides the tab when the bit is clear. The server's `ManageUsers` check remains the authority — the guard prevents a dead-end screen, it does not protect the data. | **Gate on `user.displayName` or a role-name string** — the client would re-derive permission semantics the `RoleCatalog` owns. **Skip client gating and let the screen 403** — a `seller` sees a Customers tab that always fails, and the success criterion "a `seller` cannot reach the customer-management screen on either client" is about reachability. |
| **BranchNode cloud→local customer replication (built, not reused)** | **Minimum viable pull, three parts.** (a) `GET /customers/sync?since={iso8601}` on the existing `/device` device-bearer group (org scope comes from the stored `device_credentials` row via the minted claim, never the request), returning enabled customers with `updated_at_utc > since`, plus the ids of any that became disabled since then so a revocation propagates. (b) A fourth SQLite table in `BranchSyncStore`'s `CREATE TABLE IF NOT EXISTS` block — `customers_replica (customer_id TEXT PRIMARY KEY, organization_id TEXT, display_name TEXT, customer_kind TEXT, tax_id TEXT NULL, phone TEXT NULL, locality TEXT NULL, updated_at_utc TEXT)` plus a one-row `sync_cursors (channel TEXT PRIMARY KEY, last_synced_utc TEXT)` — deliberately a **projection**, not the full aggregate: the sale-time selector needs name, kind, and enough to disambiguate, and replicating `Notes`/`DiscountPercentage`/`PaymentTerms` to every terminal widens the offline blast radius for no current use. (c) The pull runs inside `MainWindow.SyncButton_Click`, immediately after `ReconcileOperatorsAsync()` and **before** the `pending.Count == 0` early return, matching that method's own documented placement rationale. Disabled customers are deleted from the replica, so revocation reaches the terminal by the same cycle. Failure is non-fatal: the replica keeps its previous contents and the cursor does not advance. | **Extend the existing `inbox`/`SyncEnvelope` path to carry customers** — `inbox` stores no payload; making it store one turns a 2-column idempotency ledger into a general replication log, changing a proven sale-critical table for a read-only feature. **A push channel (cloud notifies the terminal)** — requires an inbound connection to a notebook behind consumer NAT; nothing in the repo does this. **Replicate the full `Customer` row** — every terminal would hold every customer's internal notes and commercial terms offline. |
| **`customers` table shape and RLS** | Mirrors `branches` exactly: own denormalized `organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE`, symmetric `USING`/`WITH CHECK` tenant policy with the `NULLIF(…, '')::uuid` pooler hardening, `ENABLE` + `FORCE ROW LEVEL SECURITY`, `REVOKE ALL FROM PUBLIC`, `GRANT SELECT, INSERT, UPDATE` to `app_runtime` (**no `DELETE`** — this change has no delete flow, so deletion is structurally unavailable, the `platform_admins` precedent). `updated_at_utc` is added beyond the proposal's field table because the sync cursor in (b) above needs it. Enums persist as `text` with a `CHECK` on the allowed values, not as Postgres enum types — `users.roles` already stores catalog names as text, and a Postgres enum needs its own migration to extend. | A `DELETE` grant "for later" — grants an unused destructive capability. Postgres `ENUM` types — `ALTER TYPE … ADD VALUE` cannot run inside a transaction block on older servers and gives nothing over a `CHECK`. |
| **Web form shape (create vs. edit)** | **One `CustomerForm` component, two modes.** `CustomerKind` is a required select at create and **read-only at edit** — it drives which price list applies in Phase C, so silently flipping Retail↔Wholesale on an existing customer would retroactively change commercial meaning; changing it is a deliberate future operation, not a field edit. `DisplayName` is the only other required field (the spec's "Retail customer with only `DisplayName` and `Phone`" scenario forbids requiring fiscal or address data). Fiscal fields (`LegalName`, `TaxIdType`/`TaxId`, `TaxCondition`) render in a collapsible group that is expanded by default when kind is `Wholesale`. `TaxId` is captured as free text with no AFIP validation (explicitly out of scope). `IsEnabled` is a toggle on edit only; a created customer is always enabled. | Separate `CreateCustomerScreen`/`EditCustomerScreen` — two copies of a 20-field form drift, which is the proposal's own "two UIs drift" risk applied within one client. A required full field set — contradicts the spec's minimal-Retail scenario. |
| **Issuing ordering access** | A separate `POST /customers/{customerId}/ordering-access` (issue, returns the plaintext credential exactly once, the `/device/pair` precedent) and `DELETE` (revoke). **Not** part of customer creation, per the proposal's stated assumption (2). The web screen surfaces it as an explicit per-customer action with a one-time credential display, which is also the mitigation for the "silent dead end" risk: the customer list shows an explicit "no ordering access" state. | Auto-issuing on create — every counter-sale Retail customer would get a live ordering credential nobody asked for, and the proposal's assumption (2) says they are separate operations. |

## Data Flow

```text
Create a customer (web or desktop, identical server path)
  POST /customers {customerKind, displayName, …}
    -> RequireAuthorization (default cookie) + TenantScopeEndpointFilter -> scope
    -> caller = userStore.LoadActorAsync(scope, NameIdentifier)
       -> null | IsRevoked | !HasFlag(ManageUsers) -> 403
    -> validation: displayName non-blank; customerKind/taxIdType/taxCondition
       in their catalogs -> 400            [pure, before any I/O]
    -> ONE tx: set_config(scope)
               -> INSERT customers (organization_id pinned by WITH CHECK)
               -> AuditLogWriter.InsertAsync(customer.created)
               -> COMMIT                   <- row and audit row are atomic
    -> 201 {customerId}
  Cross-org is unrepresentable: organization_id is never a request field.

Submit an order (the security fix)
  POST /orders {orderId, customerId, accessCredential, …}   <- no accessEnabled
    -> RequireAuthorization + TenantScopeEndpointFilter -> scope
    -> submissionService.SubmitAsync(scope, customerId, accessCredential, …)
       -> accessService.AuthorizeAsync(scope.OrganizationId, credential, corrId)
          -> resolver.ResolveAsync(orgId, credential)
             sha256(credential) -> customer_ordering_access row | null
          -> null                                   -> deny "not-found"
          -> row.OrganizationId != requested        -> deny "not-found"   (same reason)
          -> !row.IsEnabled                         -> deny "credential-revoked"
          -> audit every decision via IAuditSink (allow AND deny)
       -> row.CustomerId != request.CustomerId      -> deny "not-found"
       -> customerStore.FindAsync(scope, customerId)
          -> null (incl. cross-org, invisible under RLS) -> deny "not-found"
          -> !IsEnabled                             -> deny "customer-disabled"
    -> only now: orderStore.Submit(...)             <- no dangling CustomerId
    -> 200 Accepted | 403 Denied
  A caller claiming enabled=true has nowhere to put the claim: the field is gone
  from the DTO and the service signature cannot receive an access object.

Desktop customer management (Commerce.Pos.Windows)
  MainWindow: "Manage customers" button rendered only if
              CurrentOperator.Value?.Permissions has ManageUsers   [UX only]
    -> CustomersWindow (modal, own HttpClient + CookieContainer)
       -> POST /account/sign-in {adminEmail, adminPassword}   <- typed in-window
          -> 401 -> "Sign in failed"; no local state changes
       -> GET/POST/PUT /customers with that cookie
          -> server re-checks ManageUsers on EVERY call        [the real gate]
       -> window closed -> CookieContainer discarded
  Offline -> HttpRequestException -> "Customer management requires connectivity."
             (the pairing/bootstrap precedent; no local write, no queue)

Offline customer availability for a sale
  MainWindow.SyncButton_Click
    -> await ReconcileOperatorsAsync()                    [existing]
    -> await PullCustomersAsync()                         [new, same position]
       GET /customers/sync?since={cursor}   Authorization: Bearer <DeviceToken>
         org/branch from the STORED device_credentials row, never the body
       -> 200 {customers[], disabledIds[], serverTimeUtc}
          -> ONE SQLite tx: UPSERT customers_replica
                            DELETE disabled ids
                            UPDATE sync_cursors SET last_synced_utc = serverTimeUtc
       -> unreachable/401 -> replica unchanged, cursor unchanged, sale path
                             never blocked (the SyncButton failure precedent)
    -> existing pending-outbox push, unchanged
  Sale-time selection reads customers_replica only. Zero HTTP, zero token read.
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0008_customer_registry.sql` | Create | `customers`, `customer_ordering_access`, `users.customer_id` + FK + `CHECK`, RLS/grants/policies for both new tables. **No `orders` statements** (no such table). |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim (the hand-kept parity convention `MigrationRlsTests` asserts). |
| `deploy/README.md` | Modify | `0008` apply section and its inverse block. |
| `src/Commerce.Domain/Customers/Customer.cs` | Create | Aggregate, full field set, `IsEnabled`, `Enable()`/`Disable()`. No `UserId` (ADR-008). |
| `src/Commerce.Domain/Customers/CustomerKind.cs`, `TaxIdType.cs`, `TaxCondition.cs` | Create | The three closed vocabularies, as enums with a canonical string map. |
| `src/Commerce.Domain/Identity/UserAccount.cs` | Modify | `Guid? CustomerId`; `EffectivePermissions` short-circuits to `None` when it is set. |
| `src/Commerce.Domain/Ordering/Order.cs` | Modify | Constructor guard: `customerId == Guid.Empty` throws. |
| `src/Commerce.Domain/Ordering/CustomerOrderingAccess.cs` | Modify | Unchanged shape; XML doc records that instances now originate only from the store. |
| `src/Commerce.Application/Ordering/ICustomerOrderingAccessResolver.cs` | Create | `ResolveAsync(orgId, credential, ct)`. |
| `src/Commerce.Application/Ordering/CustomerCatalogAccessService.cs` | **Modify (security)** | `AuthorizeAsync`/`GetPermittedCatalogueAsync` take `(orgId, credential, corrId)`; the `CustomerOrderingAccess`-taking overloads are deleted. |
| `src/Commerce.Cloud.Api/Persistence/PostgresCustomerStore.cs` | Create | `CreateAsync` (tx + audit), `UpdateAsync` (tx + audit, old/new values), `FindAsync`, `ListAsync`, `ListChangedSinceAsync`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresCustomerOrderingAccessStore.cs` | Create | `ICustomerOrderingAccessResolver` impl, `IssueAsync`, `RevokeAsync`. |
| `src/Commerce.Cloud.Api/Persistence/CustomerRecords.cs` | Create | `NewCustomer`, `CustomerRecord`, `CustomerSummary`, `CustomerReplicaRow` DTOs. |
| `src/Commerce.Cloud.Api/Endpoints/Customers.cs` | Create | `GET /customers`, `GET /customers/{id}`, `POST`, `PUT /{id}`, `POST /{id}/ordering-access`, `DELETE /{id}/ordering-access` — `adminGroup`'s exact authorization shape. |
| `src/Commerce.Cloud.Api/Endpoints/Ordering.cs` | **Modify (security)** | `AccessEnabled` removed from `SubmitOrderRequest`; handler passes `customerId` + `accessCredential`, builds no access object. |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderSubmissionService.cs` | **Modify (security)** | `SubmitAsync`; resolves access, asserts customer binding + existence + enabled before `CloudOrderStore.Submit`. |
| `src/Commerce.Cloud.Api/Endpoints/Device.cs` | Modify | `OperatorVerifyResponse` gains `Permissions`; new `GET /device/customers/sync`. |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modify | `SignedInResponse`/`/account/me` gain `Permissions` (store-derived). |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Register `PostgresCustomerStore`, `PostgresCustomerOrderingAccessStore` (as `ICustomerOrderingAccessResolver`), `MapCustomerEndpoints`. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Add `customers` and `customer_ordering_access` (+ `relforcerowsecurity` + policy names) to the readiness query and both messages. |
| `src/Commerce.Web/src/api/customers.ts` | Create | `listCustomers`, `getCustomer`, `createCustomer`, `updateCustomer`, `issueOrderingAccess`, `revokeOrderingAccess`. |
| `src/Commerce.Web/src/api/types.ts` | Modify | Customer DTOs; `permissions` on `SignedInResponse`; `accessEnabled` removed from `SubmitOrderRequest`. |
| `src/Commerce.Web/src/auth/AuthContext.tsx` | Modify | Carry `permissions`; export a `hasPermission` helper. |
| `src/Commerce.Web/src/routes/RequireAdmin.tsx` | Create | `RequireAuth`'s exact shape, additionally requiring the `ManageUsers` bit. |
| `src/Commerce.Web/src/App.tsx`, `routes/AppLayout.tsx` | Modify | `/app/customers` under `RequireAdmin`; nav tab hidden without the bit. |
| `src/Commerce.Web/src/screens/CustomersScreen.tsx`, `CustomerForm.tsx` | Create | List + create/edit, one form in two modes. |
| `src/Commerce.Web/src/screens/OrderScreen.tsx`, `e2e/ordering.spec.ts` | Modify | Stop sending `accessEnabled`. |
| `src/Commerce.Pos.Windows/CustomersWindow.xaml(.cs)` | Create | Modal admin sign-in + list/create/edit; connectivity-required messaging. |
| `src/Commerce.Pos.Windows/CustomerAdminClient.cs` | Create | Cookie-bearing typed client for `/account/sign-in` + `/customers`. |
| `src/Commerce.Pos.Windows/CustomerReplicaClient.cs` | Create | Device-bearer typed client for `GET /device/customers/sync`. |
| `src/Commerce.Pos.Windows/CachedOperator.cs`, `LocalOperatorStore.cs`, `OperatorProvisioningClient.cs` | Modify | Carry/persist the server-derived `Permissions` int. |
| `src/Commerce.Pos.Windows/MainWindow.xaml(.cs)` | Modify | Permission-gated "Manage customers" button; `PullCustomersAsync()` in `SyncButton_Click`. |
| `src/Commerce.Pos.Windows/PosHostBuilder.cs` | Modify | Register the two new typed clients. |
| `src/Commerce.BranchNode/BranchSyncStore.cs` | Modify | `customers_replica` + `sync_cursors` tables; `UpsertCustomers`, `RemoveCustomers`, `ListCustomers`, `GetCursor`/`SetCursor` in one transaction. |
| `tests/Commerce.Cloud/CustomerTests.cs`, `CustomerCatalogAccessServiceTests.cs`, `UserAccountCustomerGuardTests.cs` | Create/Modify | Unit coverage. |
| `tests/Commerce.Integration/CustomerRegistryTests.cs`, `CustomerOrderingAccessTests.cs`, `MigrationRlsTests.cs` | Create/Modify | Endpoint, RLS, and migration coverage. |
| `tests/Commerce.BranchNode/CustomerReplicaTests.cs` | Create | Replica upsert/remove/cursor coverage. |

## Interfaces / Contracts

```sql
-- 0008_customer_registry.sql (additive; no `orders` table exists to alter)
CREATE TABLE IF NOT EXISTS customers (
    id                  uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    customer_kind       text NOT NULL CHECK (customer_kind IN ('Retail','Wholesale')),
    display_name        text NOT NULL,
    legal_name          text NULL,
    tax_id_type         text NOT NULL DEFAULT 'None'
                             CHECK (tax_id_type IN ('None','Cuit','Cuil')),
    tax_id              text NULL,
    tax_condition       text NOT NULL DEFAULT 'NoAplica'
                             CHECK (tax_condition IN ('ConsumidorFinal','ResponsableInscripto',
                                                      'Monotributo','Exento','NoAplica')),
    phone               text NULL,
    email               text NULL,
    address_street      text NULL,
    address_number      text NULL,
    neighborhood        text NULL,          -- optional: real Argentine addressing
    locality            text NULL,
    province            text NULL,
    postal_code         text NULL,
    delivery_notes      text NULL,
    discount_percentage numeric(5,2) NULL,  -- ADR-010 seed; unused until Phase C
    payment_terms       text NULL,          -- free text until Phase C/E
    notes               text NULL,          -- staff-only
    is_enabled          boolean NOT NULL DEFAULT true,
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    created_by_user_id  uuid NOT NULL,
    updated_at_utc      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customers_tax_id_requires_type
        CHECK ((tax_id_type = 'None' AND tax_id IS NULL) OR
               (tax_id_type <> 'None' AND tax_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS customers_org_idx     ON customers (organization_id);
CREATE INDEX IF NOT EXISTS customers_org_updated ON customers (organization_id, updated_at_utc);

CREATE TABLE IF NOT EXISTS customer_ordering_access (
    credential_hash text PRIMARY KEY,       -- sha256(credential.ToString()) hex, the 0004 precedent
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    customer_id     uuid NOT NULL REFERENCES customers (id) ON DELETE CASCADE,
    is_enabled      boolean NOT NULL DEFAULT true,
    issued_at_utc   timestamptz NOT NULL DEFAULT now(),
    issued_by_user_id uuid NOT NULL,
    revoked_at_utc  timestamptz NULL
);
CREATE INDEX IF NOT EXISTS customer_ordering_access_customer_idx
    ON customer_ordering_access (customer_id) WHERE is_enabled;

ALTER TABLE users ADD COLUMN IF NOT EXISTS customer_id uuid NULL
    REFERENCES customers (id) ON DELETE RESTRICT;
-- A customer login can never hold a staff role, enforced by the DATABASE as
-- well as by UserAccount.EffectivePermissions.
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_customer_has_no_roles;
ALTER TABLE users ADD CONSTRAINT users_customer_has_no_roles
    CHECK (customer_id IS NULL OR roles = '[]'::jsonb);
CREATE INDEX IF NOT EXISTS users_customer_idx ON users (customer_id) WHERE customer_id IS NOT NULL;

ALTER TABLE customers                ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers                FORCE  ROW LEVEL SECURITY;
ALTER TABLE customer_ordering_access ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_ordering_access FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON customers, customer_ordering_access FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON customers                TO app_runtime;  -- no DELETE
GRANT SELECT, INSERT, UPDATE ON customer_ordering_access TO app_runtime;  -- no DELETE

-- Symmetric, identical in shape to branches_tenant_isolation, including the
-- NULLIF(..., '')::uuid pooler-safety hardening from 0001. NEVER regress it.
DROP POLICY IF EXISTS customers_tenant_isolation ON customers;
CREATE POLICY customers_tenant_isolation ON customers
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Asymmetric, the device_credentials precedent: lookup is by credential hash;
-- the organization comparison is made against the ROW's own organization_id in
-- CustomerCatalogAccessService.Evaluate, so a cross-org credential denies with
-- the same "not-found" reason as an unknown one.
DROP POLICY IF EXISTS customer_ordering_access_lookup ON customer_ordering_access;
CREATE POLICY customer_ordering_access_lookup ON customer_ordering_access
    FOR SELECT USING (true);
DROP POLICY IF EXISTS customer_ordering_access_issue ON customer_ordering_access;
CREATE POLICY customer_ordering_access_issue ON customer_ordering_access
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
-- Unscoped UPDATE permitted ONLY when the result is revoked: an unscoped
-- un-revoke is unrepresentable, not merely untested.
DROP POLICY IF EXISTS customer_ordering_access_revoke ON customer_ordering_access;
CREATE POLICY customer_ordering_access_revoke ON customer_ordering_access
    FOR UPDATE USING (true) WITH CHECK (NOT is_enabled);

-- Inverse (rollback), shipped as comments in the file:
--   ALTER TABLE users DROP CONSTRAINT users_customer_has_no_roles;
--   ALTER TABLE users DROP COLUMN customer_id;
--   DROP TABLE customer_ordering_access;  DROP TABLE customers;
-- NOTE: rolling back re-opens the authorization defect. Prefer forward-fix.
```

```csharp
// Commerce.Domain/Identity/UserAccount.cs  — the guard, by construction
public Guid? CustomerId { get; }
public Permission EffectivePermissions =>
    CustomerId is null
        ? Roles.Aggregate(Permission.None, (acc, role) => acc | role.Permissions)
        : Permission.None;   // a customer login holds NO staff permission, ever

// Commerce.Application/Ordering/ICustomerOrderingAccessResolver.cs
public interface ICustomerOrderingAccessResolver
{
    Task<CustomerOrderingAccess?> ResolveAsync(
        Guid organizationId, Guid credential, CancellationToken ct);
}

// Commerce.Application/Ordering/CustomerCatalogAccessService.cs — new surface.
// There is NO overload taking a CustomerOrderingAccess: building one from a
// request body and passing it in does not compile.
public Task<CustomerAccessResult> AuthorizeAsync(
    Guid requestedOrganizationId, Guid credential, Guid correlationId, CancellationToken ct);
public Task<CatalogueAccessResult> GetPermittedCatalogueAsync(
    Guid requestedOrganizationId, Guid credential,
    IReadOnlyList<Product> organizationCatalogue, Guid correlationId, CancellationToken ct);
// Deny reasons unchanged plus deliberate collision:
//   unknown credential -> "not-found"       (same as cross-org: no probe signal)
//   row.IsEnabled=false -> "credential-revoked"
//   customer missing/disabled -> "not-found" / "customer-disabled"

// Commerce.Cloud.Api/Endpoints/Ordering.cs — AccessEnabled is GONE
public sealed record SubmitOrderRequest(
    Guid OrderId, Guid CustomerId, Guid AccessCredential,
    Guid DestinationBranchId, Guid ActorId,
    IReadOnlyList<OrderLineSnapshot> Lines, Guid CorrelationId);

// Commerce.Cloud.Api/Endpoints/Customers.cs
public sealed record CreateCustomerRequest(
    string CustomerKind, string DisplayName, string? LegalName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood,
    string? Locality, string? Province, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes);
// No OrganizationId, no IsEnabled, no CreatedByUserId: org comes from the
// tenant scope, enabled is true at birth, actor comes from the cookie claim.
public sealed record UpdateCustomerRequest(/* CreateCustomerRequest minus CustomerKind */ bool IsEnabled);
public sealed record CreateCustomerResponse(Guid CustomerId);
public sealed record IssueOrderingAccessResponse(Guid Credential);  // shown exactly once

// Commerce.Cloud.Api/Endpoints/Device.cs
public sealed record OperatorVerifyResponse(
    string Status, Guid? UserId, string? Email, Guid? OrganizationId, int Permissions);
public sealed record CustomerSyncResponse(
    IReadOnlyList<CustomerReplicaRow> Customers,
    IReadOnlyList<Guid> DisabledIds,
    DateTimeOffset ServerTimeUtc);
public sealed record CustomerReplicaRow(
    Guid CustomerId, string DisplayName, string CustomerKind,
    string? TaxId, string? Phone, string? Locality, DateTimeOffset UpdatedAtUtc);
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `UserAccount`: a `CustomerId`-bearing account with `business-admin` in `Roles` reports `Permission.None`; the same account without `CustomerId` reports the full set | xUnit |
| Unit | `CustomerCatalogAccessService`: unknown credential ⇒ `not-found`; disabled row ⇒ `credential-revoked`; row from another org ⇒ `not-found` (**string-equal** to the unknown case); allow and deny both audited | xUnit with a stub resolver, no I/O |
| Unit | `Order` constructor rejects `Guid.Empty` customer id | xUnit |
| Unit | `Customer`: `tax_id` invariant, `Enable`/`Disable` | xUnit |
| Integration | `POST /customers`: `ManageUsers` holder creates Retail-with-two-fields and Wholesale-with-full-fiscal; a `seller` ⇒ 403; exactly one audit row per create/edit with old/new values; a rolled-back create writes no audit row | `WebApplicationFactory` + live Postgres |
| Integration | `PUT /customers/{id}`: cross-org target ⇒ 404 identical to a nonexistent id (RLS, no explicit org comparison) | two seeded orgs |
| Integration | **Security**: a submission naming a credential the caller does not own is denied; revoking access denies the *next* submission and the denial is audited; a credential valid in org A is denied in org B; an order for a `customerId` with no row (or a disabled row) is denied; `SubmitOrderRequest` has no `accessEnabled` member (compile-time + a body carrying it is ignored, not honoured) | same |
| Integration (RLS) | Org B cannot read, insert, or update org A's `customers`; `app_runtime` has no `DELETE` on either new table; an unscoped `UPDATE customer_ordering_access SET is_enabled = true` is refused by the `WITH CHECK`; `users_customer_has_no_roles` rejects a customer-linked user with a non-empty `roles` array | `MigrationRlsTests`, direct connections |
| Integration (migration) | `0008` applies twice cleanly; `/health/ready` fails before it and passes after | same |
| Integration | `GET /device/customers/sync`: device bearer required; org comes from the stored credential row, not the query; `since` filtering returns only changed rows; a customer disabled after the cursor appears in `disabledIds` | same |
| Unit (BranchNode) | `customers_replica` upsert is idempotent; `RemoveCustomers` deletes; the cursor advances only on a successful transaction; a failed pull leaves both untouched | xUnit + a temp SQLite file (the existing `BranchSyncStore` test pattern) |
| Web (Vitest) | `CustomersScreen` lists, creates, and edits against a mocked client; `CustomerForm` disables `CustomerKind` in edit mode; `RequireAdmin` redirects a `seller`; `AppLayout` hides the tab without the bit; `OrderScreen` no longer sends `accessEnabled` | Vitest + Testing Library, mirroring `RequireAuth.test.tsx` |
| Web (E2E) | `ordering.spec.ts` updated to the new request shape and still passes against the real backend | Playwright |
| Manual | Desktop: an admin creates a customer from `CustomersWindow` and it appears in the web list; a `seller` sees no button; offline shows the connectivity message; after one Sync press the new customer is selectable with the network disconnected | Runbook step, no WPF UI harness exists |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary. |
| Git repository selection | N/A — no product code runs Git. |
| Commit state | N/A — no commit automation added. |
| Push state | N/A — `railway.json` untouched. |
| PR commands | N/A — no PR automation added. |
| **Routing** | **Applicable** — six new authenticated org routes under `/customers` and one new device-bearer route under `/device/customers/sync`, plus one modified security-critical route (`POST /orders`). Safe behavior: `/customers` reuses `Account.cs`'s `adminGroup` shape verbatim (`RequireAuthorization` default cookie + `TenantScopeEndpointFilter` + store-loaded caller + `ManageUsers`), so a `seller` cookie, a platform cookie (`Path=/platform`, not sent), and a device bearer token all authenticate nothing there. `/device/customers/sync` requires the `DeviceBearer` policy and derives org/branch from the stored `device_credentials` row. `POST /orders` no longer accepts any caller-asserted authorization state; every deny path returns the same 403 outcome shape and unknown-vs-cross-org share one reason string. RED tests: `seller` ⇒ 403 on every `/customers` verb; cookie-less and device-token calls on `/customers` ⇒ 401; cross-org target ⇒ 404; the four order-denial cases; `disabledIds` propagation. |
| **Process integration** | **Applicable** — a new outbound HTTP dependency from `Commerce.Pos.Windows` to `/account/sign-in` and `/customers` (admin cookie) and to `/device/customers/sync` (device bearer). Safe behavior: the admin cookie lives in a window-scoped `CookieContainer`, is never persisted to `operators.json` or `installation.json`, and is discarded on close; the device token is read in exactly one new expression (`CustomerReplicaClient.PullAsync`), preserving `MainWindow`'s documented "sync-blocked, not sales-blocked" structural property — the sale path still makes zero HTTP calls and zero token reads; every network failure is caught and degrades to "requires connectivity" or "replica unchanged", never to a local write or a queued mutation. RED tests: an unreachable host leaves `customers_replica` and the cursor byte-identical; a 401 does not advance the cursor. |
| Shell / subprocess | N/A — none introduced. |
| **Executable-file classification** | N/A — no file is classified or executed. |

## Migration / Rollout

Forward-only. Per environment, **before** deploying the new image: apply
`0008_customer_registry.sql` via `psql` against the direct (non-pooled)
connection. It carries no password placeholder, so no substitution step is
needed. `/health/ready` fails closed until `customers` and
`customer_ordering_access` exist with `FORCE ROW LEVEL SECURITY` and their
policies, so ordering is enforced by the gate rather than by discipline.

`ALTER TABLE users ADD CONSTRAINT users_customer_has_no_roles` validates
existing rows; every current row has `customer_id IS NULL`, so it passes
trivially. No `NOT VALID`/`VALIDATE` split is needed.

**No data migration and no row deletion**: the proposal's "delete pre-change
order rows" step is dropped because no `orders` table exists (verified). No
existing row in any table is read or rewritten by `0008`.

**Session impact**: none for existing cookies (no claim change — `permissions`
is derived per request from the store, not baked into the cookie). POS
terminals keep working unchanged; `operators.json` gains a field that older
entries lack, read as `0` (no permissions ⇒ button hidden) until the next
`/device/operators/verify`, which is a safe default.

**Order-of-deploy hazard**: `POST /orders` becomes stricter the moment the new
image is live — any client still sending `accessEnabled` is not broken (the
field is simply ignored by the deserializer), but a submission whose credential
has no persisted row is now denied. Since no `customer_ordering_access` row can
exist before `0008`, **every** order submission is denied until an admin issues
at least one access credential. That is the correct fail-closed behavior and it
is precisely the defect being fixed, but it must be stated in the deploy note:
issue ordering access to the intended customers immediately after deploy.

Rollback: revert the commit, then run `0008`'s inverse block (shipped as
comments in the file). Rolling back re-opens the authorization defect; prefer
forward-fix. If only the `users` constraint is a problem, drop it alone.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | `0008` + dev `init-rls.sql` parity + readiness check + `MigrationRlsTests` (RLS, grants, the `CHECK`, re-apply idempotency) + README | ~340 | Both tables exist with forced RLS and every privilege assertion green; **zero behavior change** — no C# reads them yet | Revert; run the inverse block |
| 2 | `Customer` aggregate + the three enums + `UserAccount.CustomerId` and the `EffectivePermissions` short-circuit + `Order` ctor guard + `CustomerRecords` + `PostgresCustomerStore` + unit tests | ~420 | Guard unit tests green; store integration tests green; no endpoint yet | Revert; the tables become unused, not broken |
| 3 | **Security fix**: `ICustomerOrderingAccessResolver` + `PostgresCustomerOrderingAccessStore` + `CustomerCatalogAccessService` rewrite + `CloudOrderSubmissionService.SubmitAsync` + `Ordering.cs` DTO change + `Program.cs` wiring + `OrderScreen.tsx`/`ordering.spec.ts` + unit and integration tests | ~480 | All four denial cases; revocation denies the next submission and is audited; cross-org denied with the identical reason string | Revert; the defect returns (documented, prefer forward-fix) |
| 4 | `Customers.cs` (six routes) + `/account/me` and sign-in `permissions` + audit wiring + integration tests | ~450 | `ManageUsers` gate, cross-org 404, atomic audit on create/edit, one-time credential display | Revert; the aggregate stays, unreachable over HTTP |
| 5 | Web: `api/customers.ts`, types, `AuthContext` permissions, `RequireAdmin`, `App.tsx`/`AppLayout` routing, `CustomersScreen` + `CustomerForm` + Vitest specs | ~540 | Admin sees and uses the screen; a `seller` is redirected and sees no tab; create/edit round-trip | Revert the web slice only; the API is untouched |
| 6 | POS: `CustomersWindow` + `CustomerAdminClient` + `CustomerReplicaClient` + `CachedOperator`/`LocalOperatorStore`/`OperatorProvisioningClient` permissions + `MainWindow` button and `PullCustomersAsync` + `GET /device/customers/sync` + `BranchSyncStore` replica/cursor tables + BranchNode tests | ~720 | Replica upsert/remove/cursor unit tests; sync endpoint integration tests; manual desktop walkthrough | Revert the POS slice only; cloud and web unaffected |

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

**Why chained**: ~2 950 authored lines, roughly 7.4× the 400-line budget, and
the slices are genuinely independent and ordered by dependency. Unit 1 is inert
schema. Unit 2 is domain plus a store nobody calls. Unit 3 is the mandatory
security fix on its own, which is the single most important reviewable in the
change and must not be buried under two UIs — it also lands **before** Unit 4,
so the endpoints that create ordering access cannot ship before the path that
correctly evaluates it. Units 5 and 6 are client-only and touch no shared
server code, so they can be reviewed independently and rolled back
independently. No slice leaves a security hole open across a merge. Feature
Branch Chain — PR #1 targets the feature branch, #2 targets #1, and so on
through #6.

If a single-PR exception is chosen instead, Unit 3 must still be reviewed as a
separately-labelled commit range.

## Open Questions

- [ ] **Blocking-ish, needs an explicit acknowledgement rather than a new
      decision**: the proposal locks "`orders.customer_id` becomes `NOT NULL`,
      strictly FK-enforced; pre-change order rows get deleted in the
      migration". Verified: **no `orders` table exists** — orders are an
      in-memory dictionary. This design preserves the *invariant* in the
      submission path and ships `0008` with no `orders` statements. Persisting
      orders (and therefore getting a literal FK) is proposed as the immediate
      follow-up change. Confirm this substitution before apply.
- [ ] **Acknowledgement, not a fork**: the proposal states the POS offline
      customer read "reuses the existing `Commerce.BranchNode` cloud→local sync
      channel (the same one catalog/pricing already use)". Verified: **no such
      channel exists** — `BranchSyncStore` has only a local→cloud outbox and a
      payload-less idempotency inbox. Unit 6 builds the minimum viable pull
      described above. It is new surface, not reuse, and that is the honest
      reason Unit 6 is the largest slice.
- [ ] Non-blocking: `GET /customers` returns the full list unpaginated. Fine at
      the current scale (one butchery, hundreds of customers); pagination is a
      trivially additive follow-up and is deliberately not built now.
