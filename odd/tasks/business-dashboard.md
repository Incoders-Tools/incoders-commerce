# Business Dashboard (v1, mock data)

## Objective

Give business admins a modern, responsive home dashboard in the web console
that shows the state of the business at a glance: POS orders, web orders,
grand total, money on hand, receivables, top-selling products, stock-risk
products, customer current accounts, and delivery (reparto) vs counter
(carniceria / POS) sales.

## Why

There is no dashboard today: `/app` redirects to the catalog. The owner wants
the dashboard first and then to build the modules it needs around it. Several
metrics have no backing data yet (inventory, current-account balances, cash
balance, delivery channel), so v1 runs entirely on mock data behind one data
seam.

## Scope

- Frontend only (`src/Commerce.Web`). No backend endpoint, no migration.
- One data seam (`DashboardSource` port) with a mock implementation. The screen
  depends only on the port.
- KPI tiles, sales-by-channel trend, top products, stock-risk list, current
  accounts list, period selector, clear "sample data" notice.
- Route `/app/dashboard`, nav item, `/app` landing for business admins.
- es/en i18n.

## Constraints

- **MOCK DATA IS TEMPORARY.** The owner will say when to switch to real data.
  Until then the mock stays; when told, remove the mock source and the
  sample-data notice, and wire the port to real Cloud.Api endpoints (see
  "Real-data cutover" below).
- Container-presentational: the screen container fetches through the port;
  presentational widgets get props only.
- Responsive from phone width up; use the full viewport width on desktop.
- Keep the navigation contract in sync: unit routing tests, `e2e/` specs,
  both locale files.
- TDD mode: **Strict (RED -> GREEN -> REFACTOR)**, source: global Claude
  config "Strict TDD Mode: enabled". Runner: `npm test` (Vitest) in
  `src/Commerce.Web`; plus `npm run lint`, `npm run build`.
- Delivery: commit straight to `dev` (repo workflow), Conventional Commits.

## Tasks

- [x] T1 Dashboard data model, `DashboardSource` port, mock source (route: delegated writer, 2+ non-trivial files) — commit 3bb861a; RED: suite failed to import missing modules, GREEN: 8/8 mock-source tests
- [x] T2 Dashboard screen and widgets, responsive, es/en (route: delegated writer) — commit 8f9a15f; RED: DashboardScreen import unresolved, GREEN: 9 screen tests + i18n parity; adds `recharts`
- [x] T3 Route, nav item, `/app` landing for business admins, routing tests (route: delegated writer) — commit 38f1408; RED: 5 new routing/nav tests failed, GREEN: 50 routing+i18n tests; e2e sign-in/catalog specs updated by hand (CI-only, untypechecked)

## Acceptance criteria

- A business admin landing on `/app` sees the dashboard.
- Every metric listed in the objective is visible and labelled.
- A visible notice says the figures are sample data.
- Layout has no horizontal scroll at 360px and uses the full width at 1440px.
- `npm test`, `npm run lint`, `npm run build` pass.

## Real-data cutover (pending, owner-triggered)

| Metric | Real source today | Gap |
|---|---|---|
| POS sales count/total | `pos_sales` | aggregate endpoint |
| Web orders count/total | `orders` + `order_lines` | aggregate endpoint |
| Payments by method | `payment_entries` | aggregate endpoint |
| Top products | web `order_lines`; POS lines only in `sync_inbox` jsonb | `pos_sale_lines` projection |
| Money on hand | none | cash sessions in cloud |
| Receivables / current accounts | AccountCredit payments only | balance per customer |
| Stock risk | none | inventory module |
| Delivery vs counter | none | sales channel + delivery module |

## Progress

- Feature document created 2026-10-01.
- T1-T3 done on `dev`. Final checks in src/Commerce.Web: `npm test` 55 files / 369 tests passed; `npm run lint` exit 0 (only pre-existing warnings); `npm run build` ok (chunk-size warning, main bundle 869 kB after adding recharts).
- Not verified: visual rendering in a real browser (screen is behind sign-in) and e2e (CI only).
- Decisions: channels are disjoint (counter/delivery/web), POS = counter + delivery; port lives in `dashboard/port.ts` (`DashboardSource.ts` would collide with `dashboardSource.ts` on case-insensitive filesystems); default period 7 days; `SAMPLE_DATA` flag in `dashboard/dashboardSource.ts` drives the notice.

## Next step

Owner review of the dashboard; consider lazy-loading the dashboard route to keep recharts out of the main bundle; real-data cutover when the owner says so.
