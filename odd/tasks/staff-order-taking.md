# Staff Order Taking

## Objective

Give staff a real, responsive screen to take an order for a customer: a
seller on the road from a phone or tablet, and an administrator or other web
staff entering an order a customer dictates by phone. The order is priced from
the customer's price list and enters the same order flow as web orders.

## Why

`/app/orders` (`StaffOrderScreen`) is a development form: it asks for raw
GUIDs (customer, the customer's ordering-access credential, destination
branch, actor, product, presentation) inside a small card, and `POST /orders`
authorizes with the CUSTOMER's ordering credential, which staff do not have.
There is no staff-authorized order path and no price preview anywhere.

## Decisions (owner, 2026-10-03)

- Build the screen now; it replaces `StaffOrderScreen`.
- One responsive screen serves both uses: mobile/tablet first (seller in the
  field) and desktop browser (phone orders taken at the office).
- Staff who may take orders: sellers, business admins, and a system
  administrator acting on a selected organization.

## Proposed defaults (to confirm while implementing)

- New permission `TakeOrders`, granted to `seller` and `business-admin` in
  `RoleCatalog`; stored role rows are migrated so existing users gain it. The
  "Orders" menu entry shows only with `TakeOrders` (or sysadmin acting on an
  organization).
- The order's destination branch is the selected branch; the actor is the
  signed-in staff member (never a request field).
- Lines are priced server-side with the same rule as web orders (buyer's
  list, fallback to the organization default list, then customer discount).
  A quote endpoint returns the same per-line prices before submitting so the
  staff member can tell the customer the total.
- Out of scope now: editing or cancelling a submitted order, delivery
  scheduling, offline capture on the phone.

## Tasks

- [ ] T1 Permission: `Permission.TakeOrders`, catalog grants, migration of
  stored roles, web `hasPermission` and navigation gating (route: delegated
  backend writer).
- [ ] T2 Server: staff order submission authorized by `TakeOrders` for a
  chosen customer of the organization (customer exists, visible, enabled;
  same checks and pricing as the self-service path, without the customer
  credential), destination = selected branch (`branch-selection-required`
  otherwise), actor = caller, idempotent by `orderId`; quote endpoint with
  per-line resolved price, list used and fallback note, and order total;
  audit record (route: delegated backend writer).
- [ ] T3 Web: responsive "Take order" screen replacing `StaffOrderScreen`:
  customer search (name, code, phone) showing city and price list; product
  search by name/code with the customer's prices; lines with quantity
  following the organization number format; running total; optional note;
  submit; confirmation with the human order number and a "new order" action.
  Shares `OrderLinesEditor` where it fits. Layout follows the other admin
  screens (page header, no embedded card) (route: delegated web writer).
- [ ] T4 E2E: Playwright journey at a phone viewport and at desktop width:
  pick a customer, add two products, see the customer's prices and total,
  submit, see the order number; the order appears in `/orders/pending`
  (route: delegated web writer).

## Acceptance criteria

- A seller signed in on a phone takes an order for a Reparto customer and
  sees Reparto prices before submitting; the stored order carries the same
  prices.
- An administrator on a desktop browser takes the same order for a customer
  calling by phone.
- A cashier (no `TakeOrders`) does not see Orders and the API refuses them.
- Submitting twice with the same `orderId` creates one order.
- No screen asks for a GUID.

## Constraints

- Price channel independence of `PricingResolutionService` is preserved: the
  price depends on the buyer, never on the channel.
- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test`; `npm test`, `npm run lint`, `npm run build`; Playwright via
  `npm run test:e2e`.
- Run one test-running writer at a time (shared `commerce_test` deadlocks).
- Migrations forward-only, idempotent, appended to `deploy/dev/db/init-rls.sql`.
