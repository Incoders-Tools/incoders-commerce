# Proposal: Commerce Guest Ordering

## Intent

**This platform has no public surface at all.** Verified this session: every endpoint group in `src/Commerce.Cloud.Api/Program.cs` — `/orders`, `/customers`, `/catalog`, `/pricing` — calls `.RequireAuthorization()` against the staff cookie scheme. There is no anonymous group and no customer-scoped scheme. The `private-customer-ordering` channel is not actually reachable by a customer today: `CustomerCatalogAccessService.AuthorizeAsync` runs *inside* `CloudOrderSubmissionService.SubmitAsync`, which itself sits behind the staff cookie. In practice a staff operator types a customer's link credential on their behalf in `OrderScreen.tsx`.

So Phase D is not "add a branch to a screen". It is **standing up the platform's first public-reachable HTTP surface**, and it is the phase that finally lets someone order without a staff member present.

Two more things are blocked on it. `Order`'s constructor throws when `CustomerId == Guid.Empty` — the domain literally cannot represent a guest order, which is ADR-009's own stated blocker. And `PricingResolutionService` (Phase C, shipped) already accepts `discountPercentage: null` and documents it as "GUEST: official list price, no discount", but **no caller ever exercises that branch** — `SubmitAsync` always requires a resolved, enabled `Customer`. Guest pricing exists as a type signature nobody has ever run.

**Why now.** Phases A–C are done on this branch. Price resolution exists; the guest path is the last missing consumer of it.

## Scope

### In Scope

- **Order origin/classification in the domain.** A first-class `OrderOrigin` (`Guest` | `RegisteredCustomer`) on `Order`, plus the rework of the `CustomerId` non-empty guard it forces. Guest orders carry a distinguishing, non-priority classification per ADR-009 — never commingled as equal-weight with registered orders.
- **Guest submission path** in `CloudOrderSubmissionService`: skips `CustomerOrderingAccess`/`Customer` resolution, calls `PricingResolutionService.ResolveAsync` with `discountPercentage: null`, stamps `Guest`, and captures the guest's contact/delivery data on the order itself (a guest has no `Customer` row to read it from).
- **First public HTTP surface**: a public catalogue read + guest order submit reachable without a staff session, gated by lightweight verification (DNI/identificación + a verified contact channel — SMS/email/WhatsApp) before an order is admitted, plus rate limiting and abuse controls. No payment gate (see Decision 2).
- **Single hardcoded organization/branch target** for guest orders (Decision 3): every guest order resolves to the one organization (Vaca Verde) and its principal/default branch. The domain and endpoint shape stay structurally extensible to multi-org later, but resolving org/branch dynamically is out of scope now.
- **Customer-scoped session distinct from the staff cookie**, so a `UserAccount` with `CustomerId` set authenticates into the customer surface and is structurally incapable of reaching staff endpoints. The existing `EffectivePermissions → None` short-circuit is the substrate; it is not enough on its own, because today a customer-linked login would still receive the *same cookie type* staff use.
- **ONE order screen with a guest/registered branch** (ADR-009, locked). `OrderScreen.tsx` becomes that screen — guest and registered presented as peers, no login-pressure UX, no second screen.
- **Admin-provisioned registered logins only.** No public self-registration route, form, or endpoint ships. Ever.
- **Non-staff actor representation**: `SubmitOrderRequest.ActorId` is a staff operator Guid today. A guest order has no staff actor; the audit trail needs an explicit shape for non-staff-originated orders rather than a `Guid.Empty` that reads as "unknown staff".
- **Guest verification step**: capture DNI/identificación and send/confirm a verification code over a contact channel (SMS/email/WhatsApp — `sdd-design` picks the fastest to ship) before the order is admitted. This is the sole gate against malicious/spam guest orders; no payment gate.
- **Dispatch ranking, not blocking**: guest orders are fully dispatchable once verified; ADR-009's "non-priority" is expressed as a lower rank relative to registered-customer orders when a branch prioritizes pending work, never as a fulfillment block or a manual staff-acceptance gate.
- **ADR-010's mandated guest-vs-registered price divergence test**, end-to-end through the submission path (not only the isolated `PricingResolutionService` unit test).

### Out of Scope (non-goals)

- **`CloudOrderStore` persistence.** It is an in-memory `Dictionary<Guid, Order>`; there is no `orders` table. This is a **pre-existing Phase B gap**, already flagged in that change's `design.md`, not something guest ordering introduces. Stated plainly as a scope boundary: **guest-order classification will not survive a process restart — exactly like every other order today.** Shipping guest ordering does not make this worse; it does make it more visible. Recommended as the immediate follow-up change.
- **Public self-registration**, in any form (ADR-009, locked).
- **Guest→registered account conversion / upgrade flow.** A guest who later becomes a customer is admin-provisioned like everyone else.
- **Guest order history, guest login, guest saved carts.** A guest order is a one-shot submission with no durable guest identity.
- **Promotions, quantity-break pricing, manual override** — deferred by ADR-010 to its own ADR; unchanged here.
- **Changing how price is computed.** Phase C's engine is the sole authority and is not modified; this change only adds its first guest-branch caller.
- **Payment, checkout, invoicing, AFIP.** An order is submitted, not paid (ADR-003 boundary unchanged).

## Decisions (confirmed by the user — resolved, not open)

### 1. `CustomerOrderingAccess` disposition — RESOLVED: (a) Coexist

ADR-009 left this explicitly OPEN and deferred it to "Phase B design". **Phase B (`commerce-customer-identity`) shipped without resolving it**, so it lands here. Verified: `CustomerOrderingAccess` (`(OrganizationId, CustomerId, Credential, IsEnabled)`) and `UserAccount.CustomerId` are two parallel customer-identity mechanisms with **zero linking code between them** — they merely both point at `Customer`.

**User-confirmed: Coexist.** Credential and admin-provisioned login both bind to the same `Customer`; either grants the same commercial conditions. `private-customer-ordering/spec.md` stays in force unamended; existing issued credentials keep working with no customer communication. Accepted cost: two identity mechanisms permanently, with the risk that a third appears organically later — accepted knowingly, not a silent default.

Rejected alternatives: (b) Supersede — would require amending the approved spec and migrating issued credentials; (c) Invite/activation — cleanest end state but effectively a fourth work unit, out of scope for this change.

### 2. Public auth boundary and abuse control — RESOLVED: lightweight verification, no payment gate

None of ADR-008/009/010 covers this, because all three were written assuming a public surface partly existed. It does not.

**User-confirmed shape**:
- Guest submit is **not anonymous and not payment-gated**. It requires a lightweight verification challenge at finalize: the guest's DNI/identificación plus a verified contact channel (SMS / email / WhatsApp — pick whichever is fastest to ship) confirmed before the order is admitted into the system. This closes the same abuse hole a payment gate would, without pulling Phase E (payments — no provider selected, ADR-011) into this change.
- **Explicitly rejected**: requiring pre-payment before a guest order is admitted. That would expand this change into Phase E scope (provider selection, PCI, payment failure handling) and contradicts ADR-003's "a pending order never implies settlement" boundary and this proposal's own non-goal of touching payment/checkout.
- Registered customers authenticate through a **distinct customer-scoped cookie scheme**, separate from staff — confirmed, not reused from `/account/sign-in`'s staff scheme.
- Rate-limit/throttling thresholds for the guest-submit endpoint remain an implementation detail for `sdd-design` to size, informed by realistic order volume for a two-branch butcher shop, not by an assumption of internet-scale traffic.

### 3. Organization and branch scoping on a guest request — RESOLVED: single hardcoded org, extensible shape

**User-confirmed**: for this implementation there is exactly one organization (Vaca Verde) and a guest order always targets the principal/default branch. This is hardcoded for now, not resolved dynamically (no subdomain/path routing needed yet). The domain and endpoint shape must stay structurally capable of resolving org/branch per-request later (e.g. when a second organization onboards), but building that resolution mechanism now is **out of scope** — a single-tenant default is correct for the current deployment.

### 4. Guest operational identity — RESOLVED: DNI/identificación + verified contact

**User-confirmed**: a guest is identified by their DNI/identificación (not just name+phone or email alone), combined with the verified-contact-channel step from Decision 2. This is what a branch can act on when the order arrives with no `Customer` behind it, and it doubles as the anti-abuse check — no separate mechanism is needed for both concerns.

### 5. Guest dispatch priority — RESOLVED: normal fulfillment, lower ranking only

**User-confirmed**: a guest order is **not blocked from fulfillment** and requires no explicit staff acceptance gate beyond the verification in Decision 2 — it is dispatched like any other order. ADR-009's "non-priority" classification is purely an **ordering/ranking concern**: registered, logged-in customers rank above guests when a branch is prioritizing pending work, but a guest order is a first-class, dispatchable order once verified.

### 6. Domain shape for guest classification (technical recommendation, unchanged, correctable)

Proposed: `OrderOrigin` as a required field, `Order.CustomerId` becomes `Guid?`, with the paired invariant `RegisteredCustomer ⇒ CustomerId present` / `Guest ⇒ CustomerId null`. The current non-empty guard is replaced by that invariant.

**Rejected alternative**: a synthetic per-guest `Customer` row. It keeps `CustomerId` non-null at the cost of polluting the customer registry with non-customers, contradicting "admin-provisioned only", and making guest orders indistinguishable from registered ones in every existing query and report — the precise commingling ADR-009 forbids.

## Capabilities

### New Capabilities

- `guest-ordering`: guest order intake without a `Customer`; `OrderOrigin` classification; non-priority handling; list-price resolution (`discountPercentage: null`); guest contact/delivery capture; non-staff actor semantics.
- `public-order-surface`: the platform's first public-reachable boundary — anonymous guest submit, public catalogue read, customer-scoped session distinct from the staff scheme, rate limiting and abuse controls, no self-registration.

### Modified Capabilities

- `private-customer-ordering`: `Order` gains a required origin; `CustomerId` becomes optional under a paired invariant; submission is reachable by a non-staff caller for the first time. Not amended beyond that — under confirmed Decision 1 (coexist), the credential requirements stay in force verbatim.
- `user-credentials`: a customer-scoped session type distinct from the staff cookie; admin-provisioned customer logins; explicitly no self-registration path.
- `tenant-access-foundation`: organization scoping for requests that arrive with no authenticated principal at all (a guest order still belongs to exactly one organization — how the org is determined on an anonymous request is a design obligation).

## Approach

Domain first, surface last. `OrderOrigin` and the `CustomerId` invariant land in `src/Commerce.Domain/Ordering/Order.cs` with the guard rework. `CloudOrderSubmissionService` grows a guest branch that bypasses customer resolution and calls the **unmodified** Phase C pricing engine with a null discount — the engine is the sole price authority and this change adds a caller, not a rule. The public surface is a new endpoint group in `Program.cs` with its own scheme and rate-limit policy, deliberately separated from the staff group rather than relaxed out of it. `OrderScreen.tsx` becomes the single branching screen, guest and registered as peers.

The divergence test is written against the full submission path so "guest gets list price" is proven by an executed code path, not by a type signature.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Domain/Ordering/Order.cs` | Modified | `OrderOrigin`; `CustomerId` → `Guid?`; non-empty guard replaced by paired invariant; guest contact fields |
| `src/Commerce.Domain/Ordering/CustomerOrderingAccess.cs` | Modified/Unchanged | Depends entirely on Open Decision 1 |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderSubmissionService.cs` | Modified | Guest branch bypassing `Customer` resolution; `discountPercentage: null`; origin stamping |
| `src/Commerce.Cloud.Api/Program.cs` | Modified | Second auth scheme + public/anonymous endpoint group + rate limiting |
| `src/Commerce.Cloud.Api/Endpoints/Ordering.cs`, `Catalog.cs` | Modified | Public-reachable variants alongside the staff-only ones |
| `src/Commerce.Application/Pricing/PricingResolutionService.cs` | **Unchanged** | Already guest-capable; this change is its first guest caller |
| `src/Commerce.Web/src/screens/OrderScreen.tsx` | Modified (rework) | ONE screen, guest/registered branch, no raw ID text fields for the public path |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderStore.cs` | **Unchanged (deferred)** | Stays in-memory; classification is non-durable across restart |
| `openspec/specs/private-customer-ordering/spec.md` | Amended | Origin + optional `CustomerId`; credential requirements only if Decision 1 = (b)/(c) |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| **First public HTTP surface** — abuse, spam orders, enumeration, DoS; no prior ADR coverage | High | Resolved: DNI/identificación + verified-contact-channel gate before admission (Decision 2); rate limiting and abuse controls are success criteria, not optional hardening |
| **Guest orders commingle with registered** in queries, lists, and reports that predate `OrderOrigin` | High | Origin is required (not nullable-with-default); every existing order read path audited; ADR-009's non-priority rule enforced in the domain as a ranking field, not in UI |
| `Order.CustomerId` becoming nullable ripples into pricing lookup, snapshot fields, and order filtering | Medium | Paired invariant enforced in the aggregate constructor so no call site can construct an inconsistent order |
| Guest classification lost on restart (in-memory store) | Medium | Explicitly accepted and stated as a scope boundary; persistence recommended as the immediate follow-up change |
| Customer session accidentally reaching staff endpoints | Medium | Distinct scheme at the endpoint-group level, plus the existing `EffectivePermissions → None` short-circuit as defence in depth; negative test required |
| Login-pressure UX creeping into the branch screen | Low | ADR-009 is locked; guest and registered presented as peers is a spec requirement with a scenario |

## Rollback Plan

Revert the commit: the public endpoint group and second auth scheme disappear, `Order` returns to a required non-empty `CustomerId`, `OrderScreen.tsx` returns to the staff-only single form, and the pricing engine is untouched throughout (it was never modified). Because `CloudOrderStore` is in-memory, **no order data survives a rollback to migrate** — there is no schema change and no lossy data conversion, which makes this change unusually cheap to revert *today* and materially harder to revert once order persistence lands. Narrower rollback: disable the public endpoint group alone while keeping the domain origin field, leaving guest ordering staff-only.

## Dependencies

- ADR-008 (identity), ADR-009 (ordering origin), ADR-010 (pricing) — accepted, not re-litigated.
- `commerce-pricing-engine` (Phase C, shipped on this branch) — `PricingResolutionService`'s null-discount branch is the guest pricing mechanism.
- `commerce-customer-identity` (Phase B, shipped) — `UserAccount.CustomerId` is the substrate for registered customer logins.

## Success Criteria

- [ ] A guest completes DNI/identificación + verified-contact-channel confirmation and submits an order with no payment gate, receiving a server-resolved list price.
- [ ] The same catalogue item resolves to a strictly lower price for a registered customer with a discount than for a guest — proven end-to-end through the submission path (ADR-010's mandated divergence test).
- [ ] A guest order is persisted with `OrderOrigin.Guest` and is distinguishable as non-priority in every order read path; no query returns guest and registered orders as equal-weight.
- [ ] `Order` cannot be constructed in an inconsistent state: registered without a `CustomerId`, or guest with one.
- [ ] A customer-scoped session cannot reach any staff endpoint (negative test at the endpoint-group level, not only via `EffectivePermissions`).
- [ ] No self-registration route, form, or endpoint exists anywhere in the shipped surface.
- [ ] The public guest-submit endpoint enforces rate limiting; an abusive burst is rejected without affecting staff or registered traffic.
- [ ] One order screen serves both paths, with guest and registered presented as peers and no login-pressure affordance.
- [ ] `CustomerOrderingAccess` coexists with the admin-provisioned login (Decision 1), both reflected consistently in code and in `private-customer-ordering/spec.md`.
- [ ] `dotnet test Commerce.sln` and `dotnet build Commerce.sln` pass.

## Resolved Assumptions

(1) guest orders are one-shot with no durable guest identity beyond DNI/identificación + verified contact captured on the order; (2) order persistence stays deferred and classification is non-durable across restart; (3) a registered customer authenticates through a *new*, distinct cookie scheme, not the staff one; (4) the pricing engine is not modified by this change; (5) organization/branch resolution is hardcoded to the single Vaca Verde organization and its principal branch, structurally extensible but not dynamically resolved.
