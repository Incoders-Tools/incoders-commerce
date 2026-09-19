## Exploration: Commerce foundation

### Current State

The repository is a documentation-only product definition: no application stack, executable test harness, installer, CI workflow, tag, or release exists. Confirmed boundaries are stronger than the draft technology choices:

- Each of the first two branches has one Windows x64 notebook that co-locates POS, local administration, and a logical local node. Local sales, cash, stock, and peripherals remain operational without Internet.
- Cloud owns the origin of online orders; synchronization exchanges durable, globally identified, idempotent operations rather than copying databases or tables. Authority for shared masters, users, permissions, and conflicts remains unresolved.
- Every record is organization-scoped; users may be branch-scoped and authorization is enforced by services. The physical tenant-isolation model is still open.
- Customer ordering uses an unpredictable, revocable customer-and-organization-bound link. It is treated here as a private enabled-customer channel, not a public marketplace.
- Local and web administration retain authorized business-management parity; POS and direct peripherals remain local-only. The product domain must preserve Product, Presentation, Category, contextual units, and per-presentation sale/inventory behavior.
- `PRODUCT_VARIANTS_AND_RELEASE_STRATEGY.md` is proposed, not accepted or implemented. The current Git flow remains `dev` → `staging` → `main`.

SQLite is now a planning constraint for the branch node, but not permission to share a database file across machines. SQLite documents that WAL requires all processes to be on the same host, reinforcing the existing rule that future terminals communicate with the branch node rather than mounting its database ([SQLite WAL](https://www.sqlite.org/wal.html)).

### Affected Areas

- `PRD.md` — source for scope, roles, tenancy, offline authority, parity, ordering, and staged delivery.
- `docs/architecture/synchronization.md` — confirmed sync invariants and unresolved authority matrix.
- `docs/architecture/deployment-profiles.md` — local-node boundary and separation of upgrade from device replacement.
- `docs/domain/product-domain.md` — reusable catalogue invariants needed by the ordering slice.
- `PRODUCT_VARIANTS_AND_RELEASE_STRATEGY.md` — proposed packaging and release direction requiring ADR validation.
- `openspec/config.yaml` — planning-only, hybrid persistence, Strict TDD enabled with no command yet.

### Approaches

1. **Foundation-first subsystems** — build identity, sync, ordering, and updating as separate horizontal platforms before a usable flow.
   - Pros: Each concern can be specified deeply; boundaries appear explicit.
   - Cons: Delays feedback, encourages speculative infrastructure, and can leave integration risks untested.
   - Effort: High

2. **Modular walking skeleton with one thin end-to-end order path** — establish a modular monolith boundary, one cloud application/API, and one explicit branch-node boundary; prove the cross-cutting foundations through representative flows.
   - Pros: Tests architecture through real behavior; preserves later module growth; limits the first increment.
   - Cons: Requires strict slice boundaries and several early ADRs; does not deliver the full PRD.
   - Effort: Medium

### Recommendation

Use approach 2. Plan one bounded foundation increment with these slices:

1. **Executable baseline:** select desktop/web/API/runtime and packaging through ADRs, create the first real test command, and enforce RED → GREEN → REFACTOR. Prefer a modular monolith initially; do not infer a framework from installed tools.
2. **Tenant and identity kernel:** organization/branch context, administrative users, roles/permissions, installation identity, audit fields, and automated cross-tenant/branch authorization tests. Decide physical cloud isolation and offline credential/revocation policy explicitly.
3. **Branch persistence and sync seam:** local SQLite owned by the branch node; transactional outbox/inbox or equivalent durable operation log; idempotent replay, acknowledgements, freshness, and an explicit per-data-type authority/conflict table. Keep local sales outside the network critical path.
4. **Private ordering walking slice:** enable one customer, issue/rotate/revoke a bound link, expose an authorized catalogue using Product/Presentation invariants, submit one order to cloud, and retry delivery to the destination branch until acknowledged, ensuring duplicate deliveries cannot create duplicate order acceptance or downstream business effects. Reversible assumption: while a branch is offline, accept the order as pending branch confirmation and show stale availability rather than promising stock.
5. **Safe upgrade proof:** define application, sync-contract, and schema compatibility; produce a signed immutable Windows package and channel manifest; prove N→N+1 backup, migration, health check, interruption recovery, and rollback. GitHub Releases can hold tagged binary assets ([GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)); MSIX App Installer can provide non-Store update checks, but MSIX versus a custom updater remains an ADR tied to the desktop stack and operational control needs ([Microsoft App Installer](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview)). Client initiation means an authorized user starts a compatible upgrade during a safe operational window; it must not interrupt a sale.

Functional acceptance is bounded to one primary organization and two branches; security acceptance additionally uses a second isolated organization fixture solely to prove cross-tenant access is denied. Acceptance also covers representative admin authorization shared by local/web application contracts, one enabled customer, one catalogue/order path, offline local-sale continuity, retryable delivery with idempotent business processing, and a recoverable upgrade rehearsal. This establishes seams, not complete management UI parity.

Explicit non-goals are the remaining business modules, public marketplace discovery, production payment/fiscal/notification providers, hardware protocols, complete administration screens, multi-station deployment, production release publication, and implementation under this planning authorization.

### Risks

- Offline revocation and shared-master authority can create either unsafe access or unusable branches if left implicit.
- Variable-weight ordering and payment adjustment can expand the slice; the foundation should preserve the model but defer provider settlement rules.
- A "latest" download without compatibility metadata, signing, backup, and rollback is not a safe updater.
- Building horizontal infrastructure without the walking-skeleton acceptance path would hide integration failures.

### Ready for Proposal

Yes. No product question blocks proposal creation. The proposal should preserve the reversible offline-order assumption and schedule explicit ADR/decision tasks for stack, tenant storage, offline identity, authority/conflict rules, and Windows packaging before implementation.
