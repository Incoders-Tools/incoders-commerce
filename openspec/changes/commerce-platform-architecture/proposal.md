# Proposal: Commerce Platform Architecture (Phase A — boundaries and ADRs)

## Intent

An external architecture document ("vaca-verde-arquitectura-pedidos-identidad-pagos") proposes evolving the platform toward public/ordering/admin web surfaces, a Customer entity independent of Identity, a server-side pricing engine, and Payment as a lifecycle separate from Order. None of that is decided in this repo today, and the ground truth is thinner than the document assumes:

| Topic | Reality in repo today |
|---|---|
| Domain / subdomains | **Zero** domain configuration anywhere; `PUBLIC_BASE_URL` is a `<railway-domain>` placeholder (`deploy/staging-runbook.md:68`) |
| Customer | **No `Customer` entity.** `Order.CustomerId` is a bare `Guid`; only `CustomerOrderingAccess` exists |
| Pricing | No pricing/commercial-conditions engine; `OrderLineSnapshot` carries submitted context only |
| Payment | None. ADR-003 explicitly defers settlement |
| Customer-facing UI | None. `OrderScreen.tsx` is a **staff-facing** console keying in an order using the customer's link-credential |

Formalizing now — before provider invoicing, a growing customer base, and payments arrive — prevents each future phase from re-litigating the same boundaries. This change registers decisions; it writes no code.

## Scope

### In Scope

- New ADRs under the repo's existing convention `docs/architecture/decisions/` (ADR-006+), continuing the ADR-001..005 series and updating `decisions/README.md`.
- Adopt, adapted to this repo's real structure, the document's ADR-01..07: single domain with `admin.` / `pedidos.` / `api.` subdomains; monorepo retained; independent per-app deployments; Customer separate from Identity; centralized server-side pricing; Payment lifecycle separate from Order; POS stays offline-authoritative (already locked by ADR-002/ADR-003 — reaffirmed, not replaced).
- Record the domain/subdomain decision as the first concrete configuration boundary (replacing the `PUBLIC_BASE_URL` placeholder is later work).
- **Surface the customer-access-model contradiction** (below) as a blocking open decision.

### Out of Scope (each a separate future SDD change)

- Phase B Customer + Identity separation · Phase C pricing engine · Phase D guest/authenticated ordering · Phase E payments · Phase F Local↔Cloud outbox/inbox sync ownership · Phase G per-app path-filtered CI/CD.
- Spec deltas for Customer, pricing, orders, payments. **None are written here.**
- Any code, migration, web routing, or payment-provider integration.
- The `apps/` + `packages/` reorganization the document assumes: this repo uses `src/Commerce.*`. Noted as a *candidate future* decision, **not mandated now**.
- The separate public-home/`/login` routing change (follows this one, once the access model is decided).

## Customer Access Model — Decided (confirmed by the user)

`openspec/specs/private-customer-ordering/spec.md` (approved) requires an unpredictable, revocable, org-bound credential — no password, no login, no session. The user confirmed **coexistence, not replacement**, with these specifics locked:

- **Two customer-facing paths on the same order screen** (not two separate screens): **guest** and **registered**. The screen carries branching logic — a registered customer's phone, address, and commercial discount are pre-filled and never re-typed; a guest fills the minimal form. This is a UI/UX decision for Phase D, recorded here so it isn't re-litigated later.
- **Registered customer accounts are admin-provisioned only — no public self-registration.** A `business-admin` (or a user holding `ManageUsers`) creates the customer's account (email + password) and hands the credentials to the customer directly. The customer then self-manages via the **existing password-recovery/renewal infrastructure** (`commerce-password-recovery`, already shipped) — the business is never asked to re-issue a password after the initial handoff.
- **Guest orders are explicitly non-priority and must be clearly classified in the system** — distinguishable from registered-customer orders at a glance (origin/tag), not commingled as equally-weighted records. Registered-customer orders are the priority, **regardless of the channel that created them**: public web (self-service), admin desktop (POS), or admin web console. All four origins are "order creation," differing only in actor and channel — not in kind.
- **The existing link-credential mechanism (`CustomerOrderingAccess`) is not retired by this decision.** Its exact relationship to the new admin-provisioned login (same Customer? a superseded predecessor? an activation step per the document's "invite → set password" flow?) is **explicitly deferred to Phase B design** — this proposal only locks the guest/registered/pricing/provisioning behavior above; it does not resolve what happens to the pre-existing credential mechanism's code path.

## Pricing (locked)

- **Guest** sees the official list price, **no discount**.
- **Registered customer** sees the discount/commercial conditions defined on their entity (the centralized pricing engine, Phase C, is what resolves this — not the frontend).
- No incentive-to-login pressure in copy/UX — messaging should read as "clientes habituales con acceso" vs. "pedido como invitado," not a hard login wall.

## Remaining open items (non-blocking — deferred to their respective phases)

1. **Legacy `CustomerOrderingAccess` disposition** (Phase B design must resolve: same Customer entity as the new login, a superseded predecessor, or literally the activation mechanism).
2. **`vacaverde.com.ar` is confirmed not yet purchased** (per the user, earlier this session) — ADR-006 records the *intended* domain/subdomain topology as a decision to apply once acquired, not a live configuration change.
3. **Payments sequencing** relative to guest vs. registered checkout — left to Phase E.

## Capabilities

### New Capabilities

- None. This change produces ADRs, not spec files. New capabilities (`customer-identity`, `commercial-pricing`, `order-payment-lifecycle`, `web-surface-topology`) are introduced by Phases B–G.

### Modified Capabilities

- None **yet**. `private-customer-ordering` will require a delta **only if** option (a) or (c) is chosen — that delta belongs to Phase B/D, not here.

## Approach

Verify-then-record. Each adopted ADR is checked against what this repo actually has before it is written, so no ADR claims a structure (`apps/`, a `Customer` table, a payment provider) that does not exist. ADRs that merely reaffirm ADR-002/ADR-003 cite them rather than restating. The contradiction is registered in the ADR text as an open decision, per `decisions/README.md`'s rule: *"An ADR records a decision only after it names the problem, alternatives, operational implications, and evidence."*

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `docs/architecture/decisions/ADR-006..0NN` | New | Domain topology, Customer↔Identity separation, pricing authority, Payment/Order separation |
| `docs/architecture/decisions/README.md` | Modified | Index rows; move resolved topics out of "Planned" |
| `docs/architecture/README.md` | Modified | Link the new decisions |
| `openspec/specs/private-customer-ordering/spec.md` | **Unchanged here** | Amended only in a later phase, after the access decision |
| `src/**`, `deploy/**` | **Unchanged** | No code, no migration, no config in Phase A |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Silently superseding an approved spec | Avoided | User confirmed coexistence — `private-customer-ordering` is not amended by this change |
| ADRs describe an aspirational structure (`apps/`, `packages/`) not in this repo | High | Every ADR grounded in verified paths; reorganization named as future, not mandated |
| Recording a domain the project does not control | Medium | ADR-006 records intent, not a live config; confirmed not yet purchased |
| Phase A expands into Phase B design | Medium | Spec deltas explicitly out of scope; legacy-credential disposition explicitly deferred |
| Guest + registered + legacy-credential coexisting long-term becomes permanent complexity | Medium | Guest orders must be classified/tagged distinctly per the locked decision, not commingled |

## Rollback Plan

Delete the new `ADR-00N-*.md` files and revert `decisions/README.md` and `docs/architecture/README.md`. No code, schema, config, or spec is touched, so revert is complete and has no runtime effect.

## Dependencies

- **Blocking**: user answer to Q1 (customer access model).
- The external architecture document (outside this repo) as source material.
- Existing ADR-002 (offline authority/identity) and ADR-003 (pending orders, idempotency) — reaffirmed, not superseded.

## Success Criteria

- [ ] Customer access model decision (guest + registered coexisting, admin-provisioned accounts, legacy credential mechanism deferred) recorded verbatim in the relevant ADR.
- [ ] Each adopted decision (ADR-01..07 equivalents) exists as an accepted ADR or is explicitly marked pending with its blocker.
- [ ] `decisions/README.md` indexes every new ADR.
- [ ] No ADR references a path, entity, or app structure absent from this repo without labelling it future work.
- [ ] Phases B–G are named as out-of-scope future changes, with no spec delta written here.
- [ ] Zero changes under `src/` and `deploy/`; `dotnet build Commerce.sln` is unaffected.
