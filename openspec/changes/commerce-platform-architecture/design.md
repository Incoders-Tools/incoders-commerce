# Design: Commerce Platform Architecture (Phase A — boundaries and ADRs)

## Technical Approach

This design is unusual: the change's deliverable **is documentation**, so the
design phase authors the ADR *content* and apply merely places the files. No
code, no spec delta, no config.

Verified ground truth before writing (each claim checked, not inherited):

| Claim | Verification |
|---|---|
| Last ADR is 005 | `docs/architecture/decisions/` lists ADR-001..005 only. New series starts at **ADR-006**. |
| ADR format | `# ADR-00N: Title` → `## Status` (Accepted) → `## Context` → `## Decision` (bullets, bolded lead-ins) → `## Consequences` (bullets, naming concrete paths/tests). Uniform across all five. |
| ADR-003 defers settlement | Confirmed verbatim: *"Any future settlement or payment-provider integration requires a new ADR; it is explicitly out of scope here."* ADR-011 cites it; does not restate. |
| POS offline authority | Confirmed already decided by **ADR-002** (local sale authority, cloud owns online-order origin and revocation, freshness policy). **No new ADR** — see Decision 3. |
| `Order.CustomerId` is a bare `Guid` | Confirmed at `src/Commerce.Domain/Ordering/Order.cs:15`. No `Customer` aggregate anywhere; `Order` has `OrderDeliveryStatus`/`OrderPendingReason` only, no payment state. |
| `PUBLIC_BASE_URL` is a placeholder | `deploy/staging-runbook.md:68` → `https://<railway-domain>`. |
| Spec cross-reference | `openspec/specs/private-customer-ordering/spec.md:11` cites "the approved offline-identity ADR" (= ADR-002) and requires an unpredictable, revocable, org-bound credential. Nothing below amends it. |

**Correction to the change's premise** (found while verifying): the claim that
Commerce.Web, Commerce.Cloud.Api and Commerce.Pos.Windows *already* deploy
independently is **false for Commerce.Web**. `deploy/staging-runbook.md:84` and
`deploy/README.md:314` show the SPA is embedded in Cloud.Api's `wwwroot` and
ships inside the same container image; `openspec/specs/safe-release-upgrades/spec.md:11`
treats them as one cloud-facing deploy step. Only Pos.Windows (MSIX/MSI) is a
separate artifact. ADR-007 below is written against that reality — independent
deployability is a **goal**, not a present-tense fact.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **1. Numbering and granularity** | Six new ADRs, **006–011**, one per boundary, continuing the flat `ADR-00N-kebab-title.md` convention. One-decision-per-file matches ADR-001..005 and lets each phase (B–E) cite exactly one document. | **One combined "platform architecture" ADR** — would force Phases B–E to cite a document 80% of which is irrelevant to them, and could not be superseded piecewise. |
| **2. Status vocabulary** | All six carry `## Status: Accepted`, because the *decision* is accepted even where *implementation* is deferred; the deferral lives in `## Decision`/`## Consequences` as an explicit phase pointer. This honours the index's rule that an ADR records a decision only after naming problem, alternatives, implications, evidence. | **A new `Proposed`/`Planned` status** — introduces a status value the repo has never used, and would leave the decisions re-litigable, which is precisely what this change exists to prevent. |
| **3. No duplicate POS ADR** | The "POS remains offline-authoritative" topic gets **no new ADR**. ADR-002 already decides local sale authority, cloud-owned revocation, and the offline freshness policy. ADR-006..011 *cite* ADR-002 where relevant. | **ADR-012 reaffirming ADR-002** — a duplicate decision record is a future contradiction surface; the repo's own precedent (ADR-005 → "does not change ADR-004") is to cite, not restate. |
| **4. Grounding rule** | No ADR names a path, entity, table, or app layout absent from the repo without the label *goal / future work*. Where the external document assumed `apps/`+`packages/`, ADR-007 records the **outcome** (independent deployability) rather than the folder shape. | **Adopting the document's structure verbatim** — would make ADR-007 describe a repository that does not exist and mandate a rename this change explicitly excludes. |
| **5. Open items stay open in writing** | `CustomerOrderingAccess`'s disposition is written into ADR-009 as an explicitly **OPEN, Phase-B-owned** question, and the same topic stays as a row in the index's *Planned ADR topics* table. | **Silently resolving it** — would supersede an approved spec (`private-customer-ordering`) by omission, the top risk in the proposal. |

## ADR Draft Content

### ADR-006 — `ADR-006-domain-and-subdomain-topology.md`

**Status**: Accepted (applies on domain acquisition — not a current configuration)

**Context**: The platform has **zero** domain configuration. `PUBLIC_BASE_URL` is
the literal placeholder `https://<railway-domain>` (`deploy/staging-runbook.md:68`),
and it is already load-bearing — password-reset links are built as
`{PUBLIC_BASE_URL}/reset-password?token=...`. Upcoming public ordering, an admin
console, and POS clients will each need a stable origin. `vacaverde.com.ar` is
**confirmed not yet purchased**; deciding the topology now prevents ad-hoc
origins from being minted one at a time under deadline.

**Decision**:
- **One apex domain, `vacaverde.com.ar`**, is the platform's public identity. All
  first-party origins are subdomains of it; no second apex domain is introduced
  for a surface that is part of this platform.
- **Reserved subdomains**: `vacaverde.com.ar` (public/customer-facing ordering
  entry), `admin.` (staff/administrative web console), `pedidos.` (reserved for a
  dedicated ordering surface if it is ever split from the apex), `api.`
  (reserved for the HTTP API **only if** it is ever split from the SPA host).
- **`api.` and `pedidos.` are reserved, not required.** Today Cloud.Api serves
  the SPA from its own `wwwroot` in one image; a single origin is therefore the
  correct current shape and splitting hosts is a later, separately-justified
  change. Reserving the names prevents them being taken by something else.
- **This ADR changes no configuration.** Railway-generated domains remain the
  live origins until the apex is purchased and mapped. When it is, the work is:
  register the domain, add custom domains on the Railway service(s), and replace
  `PUBLIC_BASE_URL` with the real origin — per environment.
- **Per-environment origins** follow the same rule (e.g. a staging subdomain or a
  separate staging apex); staging and production never share one origin, because
  `PUBLIC_BASE_URL` targets emailed reset links at real users.

**Consequences**:
- `deploy/staging-runbook.md` step 4's `PUBLIC_BASE_URL` row and step 5's
  `https://<railway-domain>` checks become domain-mapping steps once acquired;
  until then the placeholder is correct and must not be "fixed" to an aspirational value.
- Anything that hardcodes an origin (CORS, POS `Commerce:CloudApiBaseUrl`,
  email link construction) must read it from configuration, so the cutover is a
  variable change and not a code change.
- Until the domain is purchased this ADR is **inert**; it constrains naming, not
  runtime. Purchasing a different domain requires amending this ADR, not silently
  diverging.

### ADR-007 — `ADR-007-monorepo-and-independent-deployability.md`

**Status**: Accepted

**Context**: An external architecture document proposes an `apps/` + `packages/`
monorepo layout. This repository does not use it: first-party projects live under
`src/Commerce.*` with a single `Commerce.sln` and one `dotnet test Commerce.sln`
harness (ADR-001). Separately, the deployment shape is **not** what "monorepo of
independent apps" implies: `Commerce.Web` is built into `Commerce.Cloud.Api`'s
`wwwroot` and shipped in the same container image (`deploy/staging-runbook.md:84`,
`deploy/README.md:314`), and `openspec/specs/safe-release-upgrades/spec.md:11`
treats them as one cloud deploy. Only `Commerce.Pos.Windows` is an independent
artifact (signed MSIX/MSI, ADR-004/ADR-005).

**Decision**:
- **The monorepo is retained.** One repository, one solution, one test harness.
  Shared domain code (`Commerce.Domain`, `Commerce.Application`) is consumed by
  project reference, not by publishing internal packages.
- **The `apps/` + `packages/` reorganization is rejected for now.** `src/Commerce.*`
  is the layout; a rename would churn every project reference, CI path, and
  document for no behavioural gain. It remains a candidate future ADR if and when
  a concrete need (e.g. multiple non-.NET apps) appears.
- **Independent deployability is a goal, not a current property.** Target state:
  each deployable unit can be built, versioned, and released without forcing a
  release of the others. Present state: **POS is independent; Cloud.Api and Web
  are one artifact.** This ADR does not mandate splitting them.
- **Constraint that follows from the goal**: no deployable unit may depend on
  another's *in-process* internals. Cross-unit communication is over the API
  contract (ADR-001's branch-node rule, generalized). Co-locating Web inside
  Cloud.Api's image is a **packaging** choice and must remain reversible — the
  SPA must keep talking to the API over HTTP with a configurable base origin, never
  via server-rendered coupling.
- **Per-app path-filtered CI/CD is deferred** to its own change (Phase G).

**Consequences**:
- A change that makes the SPA structurally inseparable from Cloud.Api (shared
  server-side rendering state, non-configurable origin) contradicts this ADR.
- Release tooling may keep shipping Cloud.Api + Web as one image; that is
  explicitly allowed and is not a violation.
- `Commerce.sln` remains the single build/test entry point; nothing here changes
  ADR-001's harness decision.

### ADR-008 — `ADR-008-customer-separate-from-identity.md`

**Status**: Accepted (target shape — Phase B implements)

**Context**: There is **no `Customer` aggregate in this repository**.
`src/Commerce.Domain/Ordering/Order.cs` declares `public Guid CustomerId { get; }`
— a bare identifier with no entity behind it — and the only customer-adjacent
type is `CustomerOrderingAccess`, a credential, not a party. Commercial
conditions, contact data, and addresses therefore have nowhere to live. Meanwhile
ADR-002 makes ASP.NET Core Identity the identity provider for *users*. If a
customer is modelled as a user, every customer requires a login, which
contradicts both offline POS walk-in sales and the guest ordering decided in
ADR-009.

**Decision**:
- **Customer is a commercial party, not an identity.** A `Customer` aggregate
  owns the business facts: organization binding, display/legal name, contact
  data, addresses, enabled state, and the commercial conditions ADR-010 resolves
  against. It **can exist with no login at all**.
- **Identity optionally points at Customer, never the reverse.** The link is a
  nullable `CustomerId` on the user/identity side. `Customer` has no `UserId`
  field and no knowledge of authentication. A customer may have zero or one
  linked login; a login without a `CustomerId` is a staff user.
- **`Order.CustomerId` becomes a real reference** to that aggregate. Today it is
  an unconstrained `Guid`; Phase B is responsible for the aggregate, persistence,
  and referential meaning. **No change is made here.**
- **ADR-003's snapshot rule is unaffected**: orders keep snapshotting commercial
  context at submission, so later edits to a `Customer` never rewrite history.
- **Tenancy is unchanged**: `Customer` is organization-bound and subject to
  ADR-002's RLS and cross-tenant non-disclosure rules, including a
  second-organization fixture.

**Consequences**:
- Phase B must decide the migration for existing `Order.CustomerId` values, which
  currently reference no row.
- Authorization code must not assume "has a login ⇒ is a customer" or
  "is a customer ⇒ has a login"; both are false under this shape.
- Whether `CustomerOrderingAccess` attaches to this aggregate is **open** — see ADR-009.

### ADR-009 — `ADR-009-guest-and-registered-ordering.md`

**Status**: Accepted (behaviour locked — Phase D implements the surface)

**Context**: `openspec/specs/private-customer-ordering/spec.md` (approved)
requires an unpredictable, revocable, organization-bound credential with no
password, no login and no session, and the only customer-facing surface today is
`OrderScreen.tsx` — a **staff-facing** console where an operator keys in an order
using the customer's link credential. A public ordering surface plus registered
customer logins appears to contradict that spec. It does not: the decision is
**coexistence**, with the legacy mechanism's fate deliberately left open.

**Decision**:
- **Guest and registered are two branches of ONE order screen, not two screens.**
  The screen branches: a registered customer's contact data, address, and
  commercial discount are pre-filled and never re-typed; a guest completes a
  minimal form.
- **Registered customer accounts are admin-provisioned only. There is no public
  self-registration.** A `business-admin`, or any user holding `ManageUsers`,
  creates the account (email + password) and hands the credentials over directly.
- **The customer self-manages thereafter** via the already-shipped
  password-recovery/renewal infrastructure (`commerce-password-recovery`). The
  business is never the channel for a password after the initial handoff. No new
  credential-management mechanism is introduced for customers.
- **Guest orders are explicitly non-priority and MUST be classified as such.**
  Guest-originated orders carry a distinguishing classification so they are
  identifiable at a glance and never commingled as equally-weighted records with
  registered-customer orders.
- **All order-origin channels are equivalent in kind.** Public web (self-service),
  admin desktop/POS, and admin web console are all *order creation*, differing
  only in actor and channel. Registered-customer orders are the priority
  **regardless of which channel created them** — priority is a property of the
  customer relationship, not of the channel.
- **No login-pressure UX.** Copy presents the two paths as peers ("clientes
  habituales con acceso" vs. "pedido como invitado"); a hard login wall or
  nagging upsell is rejected.
- **OPEN — deferred to Phase B design**: the disposition of the existing
  `CustomerOrderingAccess` link-credential mechanism. It is **not retired by this
  decision**. Whether it (a) binds to the same `Customer` as the new login,
  (b) is a superseded predecessor kept for compatibility, or (c) becomes the
  activation step of an invite → set-password flow is **unresolved**. Until Phase
  B decides, `private-customer-ordering`'s requirements remain in force unchanged.

**Consequences**:
- `openspec/specs/private-customer-ordering/spec.md` is **not amended by this
  change**. Any delta belongs to Phase B/D, after the open item above is closed.
- The order model needs an origin/classification attribute (Phase D); until it
  exists, guest ordering must not ship, because unclassified guest orders are
  exactly what this decision forbids.
- No public registration endpoint may be added. A future request for
  self-registration must amend this ADR first.
- Provisioning a customer account is a `ManageUsers` operation and inherits that
  permission's existing audit and tenancy behaviour.

### ADR-010 — `ADR-010-centralized-server-side-pricing.md`

**Status**: Accepted (target behaviour — Phase C implements)

**Context**: No pricing or commercial-conditions engine exists. `OrderLineSnapshot`
carries the commercial context submitted with an order (ADR-003), which records
what was shown but decides nothing about what *should* be shown. With registered
customers gaining entity-level discounts (ADR-008/ADR-009), the question of who
computes a price becomes answerable in three places — client, API, or database —
and answering it inconsistently produces prices that disagree per channel.

**Decision**:
- **Price is resolved server-side, in one place.** A single pricing component
  resolves the effective price for a (customer context, product/presentation,
  quantity) tuple. No client — SPA, POS, or admin console — ever computes,
  derives, or adjusts a price.
- **Guest: official list price, no discount.** There is no guest discount tier.
- **Registered customer: the commercial conditions defined on their `Customer`
  entity** (ADR-008), resolved by the engine, never by the frontend and never by
  a value typed into a form.
- **All channels resolve identically.** Public web, POS, and admin console are the
  same order creation (ADR-009) and MUST receive the same price for the same
  inputs. Channel is not a pricing input.
- **Snapshot-on-submit is retained** (ADR-003): the resolved price and its
  commercial context are frozen onto the order, so later condition changes never
  rewrite a submitted order.
- **Offline**: the POS resolves against its locally-cached conditions and remains
  authoritative for the local sale per ADR-002; cache freshness is governed by
  ADR-002's freshness policy, not invented here.

**Consequences**:
- A client-side discount calculation is a defect against this ADR, not a shortcut.
- Phase C owns the engine, its cache/propagation to branches, and its tests,
  including a guest-vs-registered price divergence test for the same catalogue item.
- Manual price override, promotions, and quantity-break pricing are **not decided
  here** and require their own ADR.

### ADR-011 — `ADR-011-payment-lifecycle-separate-from-order.md`

**Status**: Accepted (target shape — Phase E implements)

**Context**: ADR-003 states plainly that pending-order handling *"defers
settlement"* and that *"any future settlement or payment-provider integration
requires a new ADR."* This is that ADR's scope-setting predecessor. Today
`Order` carries `OrderDeliveryStatus` and `OrderPendingReason` only
(`src/Commerce.Domain/Ordering/Order.cs`); there is no payment state, no payment
entity, and no provider integration anywhere in the repository.

**Decision**:
- **Payment is a separate lifecycle from Order.** Fulfilment state and money state
  are distinct dimensions and MUST NOT be merged into one status enum. An order
  can be delivered and unpaid, or paid and undelivered.
- **Order keeps a delivery/fulfilment status only.** The existing
  `OrderDeliveryStatus`/`OrderPendingReason` semantics from ADR-003 are unchanged
  and are not extended with payment values.
- **Payment state is its own concept**, referencing the order rather than being
  embedded in it, so that partial payments, multiple attempts, refunds, and
  reversals are representable without mutating order history.
- **ADR-003's guarantees carry over unchanged**: a pending order never implies
  settlement, and every payment-affecting synchronization effect is idempotent
  under a stable `operationId` with inbox de-duplication.
- **No provider is selected here.** Provider choice, PCI scope, reconciliation,
  and fiscal/invoicing integration are Phase E decisions requiring their own ADR
  or an amendment to this one.
- **Sequencing relative to guest vs. registered checkout is open** and belongs to
  Phase E.

**Consequences**:
- Any change adding a `Paid` value to an order status enum contradicts this ADR.
- Phase E must define what a payment references under ADR-008's `Customer`, and
  how offline POS payments reconcile under ADR-002's local sale authority.
- This ADR satisfies ADR-003's "requires a new ADR" precondition for *modelling*
  only; building a payment integration still requires the Phase E decision.

## File Changes

| Path | Action | Description |
|---|---|---|
| `docs/architecture/decisions/ADR-006-domain-and-subdomain-topology.md` | Create | Apex + reserved subdomains; explicitly inert until the domain is purchased. |
| `docs/architecture/decisions/ADR-007-monorepo-and-independent-deployability.md` | Create | Monorepo retained; `apps/`/`packages/` rejected for now; independent deployability as a goal, with the Cloud.Api+Web single-artifact reality stated. |
| `docs/architecture/decisions/ADR-008-customer-separate-from-identity.md` | Create | `Customer` as a commercial party; identity optionally links via a nullable `CustomerId`. |
| `docs/architecture/decisions/ADR-009-guest-and-registered-ordering.md` | Create | Locked access model; `CustomerOrderingAccess` disposition flagged OPEN for Phase B. |
| `docs/architecture/decisions/ADR-010-centralized-server-side-pricing.md` | Create | Guest = list price; registered = entity conditions; server-side only. |
| `docs/architecture/decisions/ADR-011-payment-lifecycle-separate-from-order.md` | Create | Payment lifecycle separate from order fulfilment; cites ADR-003. |
| `docs/architecture/decisions/README.md` | Modify | Six new rows in *Accepted ADRs*; one new row in *Planned ADR topics*. |
| `docs/architecture/README.md` | Modify | Two-line edit (below). |
| `src/**`, `deploy/**`, `openspec/specs/**` | **Untouched** | No code, migration, config, or spec delta. |

### `docs/architecture/decisions/README.md` — exact diff shape

Append to the **Accepted ADRs** table, after the ADR-005 row:

```markdown
| [ADR-006](./ADR-006-domain-and-subdomain-topology.md) | Single apex domain (`vacaverde.com.ar`) with reserved `admin.` / `pedidos.` / `api.` subdomains; applies on acquisition, not a current configuration. |
| [ADR-007](./ADR-007-monorepo-and-independent-deployability.md) | Monorepo retained under `src/Commerce.*`; independent per-unit deployability as a goal, not a present property. |
| [ADR-008](./ADR-008-customer-separate-from-identity.md) | Customer modelled as a commercial party independent of Identity; identity optionally links via `CustomerId`. |
| [ADR-009](./ADR-009-guest-and-registered-ordering.md) | Guest and registered ordering coexist on one screen; accounts are admin-provisioned only; guest orders are classified non-priority. |
| [ADR-010](./ADR-010-centralized-server-side-pricing.md) | Centralized server-side price resolution; guest sees list price, registered sees entity commercial conditions. |
| [ADR-011](./ADR-011-payment-lifecycle-separate-from-order.md) | Payment modelled as a lifecycle separate from order fulfilment; no provider selected. |
```

Append to the **Planned ADR topics** table, after the *Hardware integrations* row:

```markdown
| Legacy `CustomerOrderingAccess` disposition | ADR-009 leaves open whether the existing link credential binds to the same Customer, is a superseded predecessor, or becomes an account-activation step. Phase B design must resolve it. |
| Payment provider and settlement integration | ADR-011 models the lifecycle but selects no provider; PCI scope, reconciliation, and fiscal integration need field and commercial evidence. |
```

No existing row is edited or removed. The *ADR rule* section is unchanged — each
new ADR is written to satisfy it.

### `docs/architecture/README.md` — exact diff shape

Two minimal edits, no restructuring:

1. In **Read first**, item 4 currently reads *"lists decisions that still require
   ADRs"*. Change to *"lists accepted decisions (ADR-001..011) and the topics that
   still require ADRs."*
2. In the **Architecture status** table, move `identity continuity` out of the
   `Pending` cell (ADR-002 decided it) and add to `Confirmed`: *"single apex
   domain with reserved subdomains (pending acquisition); monorepo retained;
   Customer independent of Identity; centralized server-side pricing; payment
   lifecycle separate from order — all per ADR-006..011."* Leave `Provisional`
   untouched.

## Data Flow

Not applicable — no runtime data moves. The document dependency graph:

```text
ADR-001 (stack)            ADR-002 (offline authority, identity, tenancy, RLS)
    │                          │            └── reaffirmed by ADR-009/010/011; NOT duplicated
    │                      ADR-003 (pending orders, idempotency, snapshots, "settlement needs a new ADR")
    │                          │
ADR-007 ──┐               ADR-011 (payment lifecycle)  ← cites ADR-003
ADR-006 ──┤                   ▲
          └── ADR-008 (Customer) ──→ ADR-009 (guest/registered) ──→ ADR-010 (pricing)
                   │                        │
                   └── Phase B ─────────────┴── OPEN: CustomerOrderingAccess
```

## Interfaces / Contracts

None. No type, endpoint, schema, or config key is introduced by this change.
ADR-008's `CustomerId` link and ADR-011's payment concept are described in prose
deliberately — committing to a signature here would pre-empt Phases B and E.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Build | Nothing regresses | `dotnet build Commerce.sln` — expected unaffected; zero changes under `src/`. |
| Docs review | Every ADR names problem, alternatives, operational implications, and evidence (the index's own rule) | Manual read against `decisions/README.md`'s ADR rule. |
| Link integrity | Every new index row resolves to a real file; every intra-ADR cross-reference (ADR-002/003/004/005) names an existing ADR | Manual/`git grep` check at apply. |
| Contradiction check | No new ADR asserts a path, entity, or deployment property absent from the repo without a *goal/future* label | Cross-read against `src/Commerce.Domain/Ordering/Order.cs`, `deploy/staging-runbook.md`, `openspec/specs/private-customer-ordering/spec.md`. |

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file
classification, or process-integration boundary. This change adds Markdown files
under `docs/` and edits two existing Markdown files. ADR-006 *describes* a future
routing/origin topology but changes no route, host binding, CORS policy, or
configuration value.

## Migration / Rollout

No migration. No schema, config, feature flag, or deploy step. Rollback is
deleting the six new files and reverting the two Markdown edits; there is no
runtime effect to undo.

**Delivery forecast** (authored Markdown lines, additions + deletions):
six ADRs at roughly 35–55 lines each (~270) plus ~10 lines of index/README edits
≈ **280 lines**, all prose, single-purpose, in one directory.

```text
Decision needed before apply: No
Chained PRs recommended: No
400-line budget risk: Low
```

One PR. Splitting six related ADRs across chained PRs would cost more reviewer
context-switching than it saves, and an index row must land with its ADR file.

## Open Questions

- [ ] **Not blocking, recorded in ADR-009**: `CustomerOrderingAccess` disposition —
      Phase B design owns it. Written into the ADR as OPEN and into the index's
      *Planned ADR topics*.
- [ ] **Premise correction, needs acknowledgement at apply**: the change brief
      asserted Commerce.Web already deploys independently. It does not — it is
      embedded in Cloud.Api's `wwwroot` and shipped as one image. ADR-007 is
      written against the verified reality (goal, not fact). If independent SPA
      hosting is actually wanted now, that is a separate change, not an ADR edit.
- [ ] **Not blocking**: `vacaverde.com.ar` is unpurchased. ADR-006 is inert until
      it is acquired; acquiring a *different* domain requires amending ADR-006.
- [ ] **Not blocking**: payments-vs-checkout sequencing (guest vs. registered)
      stays with Phase E per ADR-011.
