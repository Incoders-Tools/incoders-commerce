# Document numbering

## Purpose

People never see GUIDs. Branches, terminals and documents carry short, readable numbers that can be read aloud, written on a receipt and found in a report. GUIDs stay inside the system and in technical views for the system administrator.

## Format

| Document | Number | Reads as |
| --- | --- | --- |
| POS sale | `V01-C2-125` | Sale (`V`), branch `01`, register 2, sale 125 of that register |
| Web order | `P01-W-37` | Order (`P`), branch `01`, web origin, order 37 of that branch |
| Goods reception | `R01-W-12` | Reception (`R`), branch `01`, web console origin, reception 12 of that branch |
| Notes (future) | `N...` | Reserved for later document types |

```
V  01  -  C2  -  125
|  |      |      +-- sequence: counts the documents of this origin, starting at 1
|  |      +--------- origin: C{register} for a POS terminal, W for the web
|  +---------------- branch code: at least two digits
+------------------- document type letter
```

The letter goes first so a number is never read as a date. The origin keeps POS and web documents separable in reports. The sequence is not zero-padded.

## Who assigns each part

| Part | Assigned by | When | Rules |
| --- | --- | --- | --- |
| Type letter | The system | Fixed per document type | `V` sale, `P` order, `R` goods reception, `N` reserved |
| Branch code | The server | When the branch is created | Numeric 1 to 999, unique per organization, never editable. The database trigger `branches_code_allocate` takes the next free number under a per-organization lock |
| Register | The server | When the terminal is paired | Numeric 1 to 999, unique per branch, kept for that installation, never reused for another one |
| Sequence (POS) | The terminal | At the moment of the sale, offline | Local counter per (branch, register), taken inside the same SQLite transaction that saves the sale |
| Sequence (web) | The server | When the order is stored (`orders`, migration `0025`) | Counter per branch, taken in the same transaction that inserts the order, under a per-branch advisory lock. Resubmitting the same order id returns the stored order and its number without advancing the counter |

Numbers are never reused. A branch that has handed out register 7 will never hand out 7 to a different installation, even after the first one is gone, because sales are numbered offline from (branch, register, sequence) and two machines must never produce the same number. Reinstalling the POS creates a new installation, so it gets a new register.

## Offline rules

- A paired terminal numbers every sale without contacting the server.
- A terminal that does not know its register yet (paired before this feature, or never online since) still completes the sale. The sale shows "número pendiente", is stored without a number and is never numbered later. The terminal fetches its identity in the background, so the next sale is numbered.
- Retrying a sale returns the number it already had and does not advance the counter.
- Sales made before this feature keep no number. History is never renumbered.

## Conflicts and audit

The server verifies the number a terminal claims when the sale arrives. It accepts the number only if the calling installation held that register in that branch, the branch code matches, and nobody used the number before. Otherwise the sale is still stored, without a number, and an audit entry explains why. Numbering never blocks a sale from syncing.

| Audit action | Meaning |
| --- | --- |
| `terminal.register.assigned` | A new register number was allocated (pairing or identity refresh) |
| `sale.number_conflict` | A claimed sale number was rejected; the reason is in the entry |
| `sale.projection_failed` | The sale was stored but could not be projected into `pos_sales` |

## Where numbers are explained to the user

- POS sale message: "Venta V01-C2-125 registrada". Its tooltip reads "V = Venta · 01 = Sucursal · C2 = Caja 2 · 125 = número de venta de esta caja".
- Web order confirmation: "Pedido P01-W-37 recibido", with the tooltip "P = Pedido · 01 = Sucursal · W = Web · 37 = número de pedido de la sucursal".
- POS lock screen terminal label "Sucursal 01 · Ruta 51 · Caja 2". Its tooltip reads "Sucursal 01 = código de sucursal · Caja 2 = número de esta caja".
- Web Branches screen: the "Código" column header explains that the code is automatic, never changes and appears in sale numbers.

## Reports

Every numbered sale is stored in `pos_sales`, and every web order in `orders` (with its lines in `order_lines`), with branch and sequence as separate columns (a sale adds its register), so reports filter by:

- type: sales (`V`) and orders (`P`);
- branch: the branch code;
- origin: register for a POS terminal, or web.

## Examples

- `V01-C1-1`: the first sale of register 1 in branch 01.
- `V03-C2-410`: sale 410 of register 2 in branch 03. Register 2 of branch 03 is unrelated to register 2 of branch 01.
- `P01-W-37`: web order 37 of branch 01. `P01-W-1` and `P02-W-1` are unrelated: the sequence counts per branch.

## GUIDs

GUIDs remain the internal identity of organizations, branches, installations, sales and operations. They appear only in sysadmin technical views (for example the branch id column of the Branches screen) and in log files. The POS log keeps the operation and installation ids for support. Staff order entry by raw branch GUID (`StaffOrderScreen`) is a known leftover: it is a staff and testing tool, not the customer flow.

## Migrations

| Migration | Adds |
| --- | --- |
| `0021_branch_codes.sql` | `branches.code`, allocation trigger, backfill, immutability |
| `0022_terminal_registers.sql` | `terminal_registers` and the allocation function `terminal_registers_assign`, backfill from live credentials |
| `0023_pos_sales.sql` | `pos_sales` projection with the partial unique index on the sale number |
| `0024_terminal_registers_assign_result.sql` | `terminal_registers_assign` reports whether it allocated a new number |
| `0025_orders.sql` | `orders` and `order_lines`: web orders stored in Postgres with their `(branch_code, sequence)` number |
| `0026_orders_guest_check.sql` | Guest origin check of `orders` rejects a NULL document id, contact address or display name (0025 let NULL pass) |
| `0032_purchase_receptions.sql` | `purchase_receptions` and lines: goods receptions with their `(branch_code, sequence)` number, assigned when the reception is confirmed |
| `0033_stock.sql` | `stock_movements` (append-only stock ledger), `stock_minimums`, `presentation_costs` |

Apply them in order, by hand, to every existing environment before deploying the API. Details are in `deploy/README.md`.

`0024` is not backward compatible: it changes the result type of `terminal_registers_assign`, so an API built before it breaks at pairing once it is applied. Apply `0024` and deploy the matching API together. The new API fails `/health/ready` against a database without `0024` (or `0025`), so it never takes traffic before its schema is there. The reverse is not guarded: an API built before `0024` has no such check, so it stays ready in front of an already migrated database and fails every pairing. Nothing is in production yet.

`0025` is additive: apply it before the API version that stores orders. `0026` is not purely additive: it replaces the guest check constraint of `orders` (drops and recreates it, rejecting NULL guest parts); apply it right after `0025`; no API version depends on it. Orders held only in the memory of an older API process are lost at that restart (nothing is in production yet), and an order sent to a branch that does not exist in the organization is denied (`destination-branch-not-found`) instead of being stored.

## Goods receptions

- A goods reception (`purchase_receptions`, migration `0032`) is a DRAFT without a number until it is confirmed. Confirming it assigns `R{branch code}-W-{sequence}` in the same transaction that moves the stock and posts the invoice to the supplier account: the sequence is the next one of the branch, taken under a per-branch advisory lock, and is unique per branch (`purchase_receptions_number_uk`).
- `W` is the origin: receptions are entered in the web console. The sequence is not zero-padded and a number is never reused: a voided reception keeps its number.
- The supplier's own document is a separate field (`document_reference`) and is never part of the number. The same supplier document (type and reference) cannot be confirmed twice.

## Web orders

- A web order (registered customer or guest) is stored in `orders` when it is accepted. Its number is `P{branch code}-W-{sequence}`; the branch code is the destination branch's own `branches.code`.
- The order id the client sends is idempotent per organization. Sending it again returns the same order and number, never a second one.
- A guest order spends its email verification in the same database transaction that stores the order, so a failed submission (unknown branch, failed insert) never burns the verification and one confirmation admits one order. Resubmitting an order id that was already admitted returns the stored order only when the request presents the very ticket that admitted it; the ticket is not spent again. Any other ticket is rejected as `verification-invalid`.
- The order response carries the number (`order.orderNumber`) and the web order screen (guest and registered) shows it: "Pedido P01-W-37 recibido". Its tooltip reads "P = Pedido · 01 = Sucursal · W = Web · 37 = número de pedido de la sucursal", built from the web translations (the domain keeps no display text). An order without a readable number shows "Pedido aceptado." and no GUID is ever shown.
- The order sent to a branch (`OrderPayloadV1`) carries the number as the optional trailing field `OrderNumber`, so a branch can show it too; older payloads without it still read.
- Delivery to the branch happens after the order's transaction commits, so a rolled-back submission never leaves the branch holding an order the cloud does not have. The order is stored pending first and a failed delivery step leaves it pending, never lost. The pending list returns only orders still pending, in deterministic order, at most 200 by default (500 maximum).
- The access decisions of a customer ordering credential (allowed and denied, with the reason) are appended to `audit_log` with actor kind `customer`, so denials survive a restart.

## Related

- Specs: `organization-persistence` (Branch Short Code), `pos-installation-identity` (Register Number), `pos-scan-sale` (Sale Number), `branch-offline-sync` (Sale Number Projection).
- Feature records: `odd/tasks/human-document-numbers.md`, `odd/tasks/persist-web-orders.md`.
