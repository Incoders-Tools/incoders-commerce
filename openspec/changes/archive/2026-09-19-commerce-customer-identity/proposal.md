# Proposal: Commerce Customer Identity

## Intent

Two problems, one foundation.

**A live authorization defect.** `POST /orders` (`Endpoints/Ordering.cs:36-41`) builds `CustomerOrderingAccess` entirely from the request body — `CustomerId`, `AccessCredential`, and `AccessEnabled` (a caller-supplied boolean). `CustomerCatalogAccessService.Evaluate` checks only that self-asserted flag plus the server-derived org scope; no persisted store is consulted anywhere in this path, because none exists. Any authenticated caller can submit `accessEnabled: true` for an arbitrary customer/credential pair and pass "authorization". This violates the already-approved `private-customer-ordering` requirement "Bound and Revocable Customer Access" (revocation cannot deny what is never read) and `tenant-access-foundation`'s rule that actor state comes from the persisted store, not the request. Fixing it is mandatory here, not optional.

**The missing party.** There is no `Customer` aggregate (ADR-008). `Order.CustomerId` is an unconstrained `Guid` pointing at nothing. Commercial conditions (ADR-010, Phase C), guest-vs-registered branching (ADR-009, Phase D), and any customer-facing surface all need this entity to exist first. Phase B builds it and carries the data; it implements neither pricing nor guest checkout.

## Scope

### In Scope

- **`Customer` aggregate + persistence**: new `customers` table, organization-bound, RLS per convention (`FORCE ROW LEVEL SECURITY`, `REVOKE ALL FROM PUBLIC` + explicit `GRANT`, symmetric `NULLIF(current_setting(...))` pooler idiom), `PostgresCustomerStore` following `PostgresOrganizationStore` conventions. Migration `0008`. **No `UserId` field** (ADR-008).

- **Full field set** (owned by this proposal — the user asked for typical fields plus judgment on what else a real business needs; extend later if something's missing rather than guessing everything perfectly now):

  | Field | Notes |
  |---|---|
  | `Id`, `OrganizationId` | Identity, tenant scope |
  | `CustomerKind` | `Retail` (B2C — final consumer, online/counter orders) or `Wholesale` (B2B — other butcheries/resellers, delivery/distribution) — drives which fields below are expected to be filled and, later, which price list/discount tier applies (Phase C) |
  | `DisplayName` | Person's name (Retail) or trade name (Wholesale) |
  | `LegalName` | Nullable — razón social, populated for Wholesale/fiscally-registered customers |
  | `TaxIdType` / `TaxId` | `None \| Cuit \| Cuil`, plus the number — Argentine fiscal identifiers, kept as one typed pair rather than two always-nullable columns |
  | `TaxCondition` | AFIP-style VAT condition: `ConsumidorFinal \| ResponsableInscripto \| Monotributo \| Exento \| NoAplica` — needed for correct invoicing later, cheap to capture now |
  | `Phone`, `Email` | Both nullable — a Retail customer may give only one |
  | `AddressStreet`, `AddressNumber`, `Neighborhood`, `Locality`, `Province`, `PostalCode` | `Neighborhood` (barrio) is optional/free-text, not required, exists because it's real Argentine addressing — the user's own example of a field to include without being asked |
  | `DeliveryNotes` | Free text — gate codes, reference points, delivery-window preferences; matters most for Wholesale/reparto |
  | `DiscountPercentage` | Nullable numeric — the ADR-010 commercial-conditions seed field (Q3: include now, unused until Phase C resolves it) |
  | `PaymentTerms` | Free text now (e.g. "Contado", "Cuenta corriente 30 días") — a real enum/structured model is Phase C/E's job, not guessed here |
  | `Notes` | Free text, staff-only internal notes |
  | `IsEnabled` | Enabled/disabled lifecycle, mirrors `UserAccount.IsRevoked`'s shape |
  | `CreatedAtUtc`, `CreatedByUserId` | Audit trail, consistent with `commerce-role-taxonomy`'s audit conventions |

- **Admin provisioning, no self-registration** (ADR-009): a `ManageUsers` holder creates/edits customers. Every create/edit is audited per `commerce-role-taxonomy`'s audit path.
- **Nullable `CustomerId` on the existing `users` table / `UserAccount`** — same identity plane as staff, not a second one. A customer login is tenant-bound exactly as staff is, so `platform-admin`'s separate-table rationale (non-tenant data) does not apply. A user with a `CustomerId` is a customer login; without, staff. Linking is optional: a customer may have zero logins.
- **MANDATORY security fix**: `CustomerOrderingAccess` evaluation reads enabled/binding state from a persisted store, never from the request. `AccessEnabled` is removed from `SubmitOrderRequest`. Revocation becomes observable. `CustomerOrderingAccess` is kept as a superseded-but-functional predecessor (ADR-009 option "b"), not retired.
- **`Order.CustomerId` becomes a real, `NOT NULL` FK** to `customers` — see Decisions below; there is no pre-launch order data to preserve, so this ships strict from day one, no placeholder-row backfill machinery.
- **Full customer management UI, on BOTH web and desktop, kept in sync** (locked decision, see below):
  - `Commerce.Web`: a new admin-only screen (list, create, edit) under the existing `ManageUsers`-gated area, calling the same Cloud API endpoints this change adds.
  - `Commerce.Pos.Windows`: a new admin-only screen (same capability, `business-admin`/`ManageUsers`-only — a `seller` never sees it), calling the SAME Cloud API endpoints when online. Creating/editing a customer is an administrative operation and follows the same connectivity precedent as device pairing and bootstrap: it requires connectivity. It does **not** need a new offline-write/conflict-resolution mechanism.
  - **"Kept in sync" is read-side, not write-side**: both admin UIs write straight to the Cloud API (single source of truth, no dual-write). The POS also needs customers available **offline for order-taking** (selecting an existing customer while ringing up a sale without connectivity) — that read path reuses the existing cloud→local sync channel (`Commerce.BranchNode`, the same mechanism already used for catalog/pricing), not a new one. Design owns the exact sync message shape.

### Out of Scope

- Guest checkout / public ordering surface and the order origin-classification field (Phase D — ADR-009 forbids shipping guest orders before classification exists, but classification is not built here either).
- The pricing/commercial-conditions **engine** (Phase C — ADR-010). This change carries `DiscountPercentage`/`PaymentTerms` as data; nothing resolves or applies them yet.
- Payments, settlement, invoicing (AFIP electronic invoicing, CUIT/CUIL validation against AFIP's own web services) — `TaxId`/`TaxCondition` are captured as plain data now, not validated against any external authority.
- Reframing `CustomerOrderingAccess` as an invite → set-password activation flow (ADR-009 option "c") — a later refinement.
- Customer self-registration (ADR-009 forbids; would require amending the ADR).
- Customer-facing password/profile self-service beyond the already-shipped `commerce-password-recovery` infrastructure.
- Customer delete/merge, credit limits, customer segmentation/tagging beyond `CustomerKind`.
- Offline customer *creation* on the POS (only offline customer *selection* for a sale is in scope, per the sync note above).

## Decisions (confirmed by the user)

| Decision | Answer |
|---|---|
| Pre-change `orders.customer_id` values | No real orders exist yet — nothing has shipped to a real customer. Delete any pre-change order rows (and their cascades) during migration rather than building placeholder-row/`NOT VALID` machinery for data that shouldn't be there. `orders.customer_id` becomes `NOT NULL` and strictly FK-enforced from the first row. |
| Provisioning UI | Full customer management screens on **both** `Commerce.Web` and `Commerce.Pos.Windows`, admin-gated, in this same change (not split into a follow-up). |
| Commercial-conditions field | Included now (`DiscountPercentage`, `PaymentTerms`) — unused until Phase C, per the field table above. |
| Field ownership | The user gave typical fields (localidad, teléfono, nombre, dirección, CUIT, CUIL, descuentos) and delegated the rest to this proposal — extend later if something's missing rather than over-designing now. |

## Capabilities

### New Capabilities
- `customer-registry`: the `Customer` commercial party — organization-bound aggregate, persistence with RLS, full field set (identity, fiscal, address, commercial), admin-only provisioning and editing on web and desktop, enabled/disabled lifecycle, optional link from an identity.

### Modified Capabilities
- `private-customer-ordering`: access evaluation MUST resolve the credential and enabled state from the persisted store; a request-supplied enabled flag MUST NOT be accepted. `Order.CustomerId` MUST reference a real `Customer` in the same organization.
- `user-credentials`: `UserAccount` carries an optional `CustomerId`; provisioning a customer login is a `ManageUsers` operation; a login without `CustomerId` remains a staff user.
- `tenant-access-foundation`: reaffirm and enforce "actor state from the persisted store" on the ordering path; cross-organization customer resolution MUST be denied and auditable.
- `pos-operator-session` (from `commerce-pos-user-login`): the desktop customer-management screen is gated the same way `MainWindow`'s existing admin-only affordances are — by the current operator's role, not a new mechanism.

## Approach

Additive aggregate, a subtractive security fix, and two admin UIs against one backend.

The `Customer` aggregate lands in `src/Commerce.Domain/Customers/` with a Postgres store mirroring `PostgresOrganizationStore`'s shape exactly — no new persistence idiom is invented. Migration `0008` creates `customers` (full field set), deletes pre-change order rows, adds a nullable `customer_id` column + FK on `users`, persists customer ordering access, and converts `orders.customer_id` to a `NOT NULL` FK.

The security fix is deliberately *narrowing*: `CustomerCatalogAccessService.Evaluate` gains a store dependency and rejects unknown/disabled/cross-org credentials; `SubmitOrderRequest` loses `AccessEnabled` entirely so the trusted field cannot be re-introduced by accident. The only client in the repo is the staff `OrderScreen.tsx`, which hardcodes `accessEnabled: true` — its call and the `ordering.spec.ts` e2e fixture are updated in the same change.

Identity stays on one plane: nullable FK on `users`, reusing sign-in, RLS, and password recovery rather than forking a second identity table for no security reason ADR-008 identifies.

Both admin UIs (web, desktop) are thin CRUD screens over the same new `Customers.cs` Cloud API endpoints — single source of truth, no dual-write. The desktop screen requires connectivity to create/edit, exactly like pairing and bootstrap already do; it is gated to `business-admin`/`ManageUsers` the same way `commerce-pos-user-login`'s operator-session already distinguishes roles at the terminal. Offline **selection** of an existing customer during a sale reuses the existing `Commerce.BranchNode` cloud→local sync channel (the same one catalog/pricing already use) — design owns the exact sync message/table shape; this is read replication of an already-created customer, not a new offline-write path.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Domain/Customers/Customer.cs` | New | Commercial-party aggregate, full field set, org-bound, enabled state |
| `src/Commerce.Cloud.Api/Persistence/PostgresCustomerStore.cs` | New | Connection-per-call store, org scope set first, mirrors `PostgresOrganizationStore` |
| `src/Commerce.Domain/Identity/UserAccount.cs` | Modified | Nullable `CustomerId` |
| `src/Commerce.Domain/Ordering/Order.cs` | Modified | `CustomerId` becomes a real, `NOT NULL` reference |
| `src/Commerce.Domain/Ordering/CustomerOrderingAccess.cs` | Modified | Now persisted; enabled state is store-owned |
| `src/Commerce.Application/Ordering/CustomerCatalogAccessService.cs` | **Modified (security)** | Evaluates against the persisted store, not the DTO |
| `src/Commerce.Cloud.Api/Endpoints/Ordering.cs` | **Modified (security)** | `AccessEnabled` removed from `SubmitOrderRequest` |
| `src/Commerce.Cloud.Api/Endpoints/Customers.cs` | New | `ManageUsers`-gated customer create/edit/list |
| `src/Commerce.Web/src/screens/OrderScreen.tsx`, `e2e/ordering.spec.ts` | Modified | Stop sending `accessEnabled` |
| `src/Commerce.Web/src/screens/CustomersScreen.tsx` (+ routes) | New | Admin-only list/create/edit, full field set |
| `src/Commerce.Pos.Windows/CustomersWindow.xaml(.cs)` (or equivalent) | New | Same capability, admin-gated, desktop |
| `src/Commerce.BranchNode/*` | Modified | Cloud→local read replication of enabled customers, for offline sale-time selection only |
| `deploy/db/migrations/0008_*.sql` | New | `customers`, persisted access, `users.customer_id`, `orders.customer_id` FK, pre-change order cleanup |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| The security fix breaks a caller that relied on trust-the-body | Low | Verified: only `OrderScreen.tsx` (hardcoded `true`) and the e2e fixture; both updated in-change. No POS/external caller found |
| Customer provisioned with no ordering access can't order; operators see a silent dead end | Medium | Provisioning must state whether access issuance is part of creation or a separate step; success criteria cover it |
| One identity plane blurs staff/customer authorization | Medium | A `CustomerId`-bearing user must be denied staff permissions by construction, not by convention; design owns the exact guard |
| Persisting `CustomerOrderingAccess` cements a mechanism ADR-009 may later replace | Low | Explicitly a superseded-but-functional predecessor; option "c" stays out of scope |
| Two admin UIs (web + desktop) drift in field validation/behavior over time | Medium | Both call the identical Cloud API endpoints/contract; no client-side business rule exists in only one of them |
| Introducing a new BranchNode sync message for customers grows the sync surface | Medium | Design must mirror the existing catalog/pricing cloud→local pattern exactly, not invent a new one |
| Change is large (aggregate + security fix + two UIs + sync) | High | Design/tasks phase forecasts size explicitly; delivery-strategy decision (single PR vs. chained) made after that estimate, per this session's established practice |

## Rollback Plan

Revert the commit: the aggregate, store, and endpoints disappear; `OrderScreen` and the e2e fixture return to sending `accessEnabled`. Migration `0008` ships inverse statements in comments — drop the `orders.customer_id` FK, drop `users.customer_id`, `DROP TABLE` customer ordering access and `customers`. **Note**: rolling back re-opens the authorization defect; prefer forward-fix. If the FK is the only problem, drop the constraint alone and keep the aggregate.

## Dependencies

- ADR-008, ADR-009, ADR-010 (accepted, merged — not re-litigated here).
- `commerce-role-taxonomy`'s `ManageUsers` gate and audit-log path (merged).
- `commerce-password-recovery`'s `/account/users` authenticated group (merged).

## Success Criteria

- [ ] A submission with `accessEnabled`-style self-assertion is rejected: an unknown or disabled credential is denied even when the caller claims it is enabled.
- [ ] Revoking a customer's access denies the next order submission, and the denial is auditable.
- [ ] A credential valid in Organization A is denied against Organization B.
- [ ] `SubmitOrderRequest` has no caller-supplied enabled flag.
- [ ] A `ManageUsers` holder creates a customer (Retail or Wholesale) in their own organization, with the full field set persisted; a customer cannot be created cross-organization.
- [ ] A customer exists and is usable with zero linked logins.
- [ ] A user with a `CustomerId` cannot exercise staff permissions.
- [ ] An order cannot be accepted for a `CustomerId` with no matching customer row in the caller's organization.
- [ ] RLS: a second-organization fixture cannot read or write another org's customers.
- [ ] A `business-admin` creates/edits a customer from `Commerce.Web` and, independently, from `Commerce.Pos.Windows` — both produce the identical persisted record via the same API.
- [ ] A `seller` (non-admin) cannot reach the customer-management screen on either client.
- [ ] A customer created online is selectable for a sale on the POS after the next sync, with no connectivity at selection time.
- [ ] `dotnet test Commerce.sln` and the web test suite pass.

## Assumptions pending correction

(1) customer creation and login creation are two separate operations — creating a customer never implicitly creates a login; (2) issuing ordering access is likewise a separate step from creating the customer; (3) customer logins reuse the existing sign-in endpoint and session scheme with no new auth surface.
