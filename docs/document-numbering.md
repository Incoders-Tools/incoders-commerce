# Document numbering

## Purpose

People never see GUIDs. Branches, terminals and documents carry short, readable numbers that can be read aloud, written on a receipt and found in a report. GUIDs stay inside the system and in technical views for the system administrator.

## Format

| Document | Number | Reads as |
| --- | --- | --- |
| POS sale | `V01-C2-125` | Sale (`V`), branch `01`, register 2, sale 125 of that register |
| Web order (planned) | `P01-W-37` | Order (`P`), branch `01`, web origin, order 37 of that branch |
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
| Type letter | The system | Fixed per document type | `V` sale, `P` order, `N` reserved |
| Branch code | The server | When the branch is created | Numeric 1 to 999, unique per organization, never editable. The database trigger `branches_code_allocate` takes the next free number under a per-organization lock |
| Register | The server | When the terminal is paired | Numeric 1 to 999, unique per branch, kept for that installation, never reused for another one |
| Sequence (POS) | The terminal | At the moment of the sale, offline | Local counter per (branch, register), taken inside the same SQLite transaction that saves the sale |
| Sequence (web) | The server | When the order is stored | Counter per branch. Pending: web orders are still in memory, see `odd/tasks/persist-web-orders.md` |

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
- POS lock screen terminal label "Sucursal 01 · Ruta 51 · Caja 2". Its tooltip reads "Sucursal 01 = código de sucursal · Caja 2 = número de esta caja".
- Web Branches screen: the "Código" column header explains that the code is automatic, never changes and appears in sale numbers.

## Reports

Every numbered sale is stored in `pos_sales` with branch, register and sequence as separate columns, so reports filter by:

- type: sales (`V`) today, orders (`P`) once they are stored;
- branch: the branch code;
- origin: register for a POS terminal, or web.

## Examples

- `V01-C1-1`: the first sale of register 1 in branch 01.
- `V03-C2-410`: sale 410 of register 2 in branch 03. Register 2 of branch 03 is unrelated to register 2 of branch 01.
- `P01-W-37` (planned): web order 37 of branch 01.

## GUIDs

GUIDs remain the internal identity of organizations, branches, installations, sales and operations. They appear only in sysadmin technical views (for example the branch id column of the Branches screen) and in log files. The POS log keeps the operation and installation ids for support. Staff order entry by raw branch GUID (`StaffOrderScreen`) is a known leftover for the orders feature.

## Migrations

| Migration | Adds |
| --- | --- |
| `0021_branch_codes.sql` | `branches.code`, allocation trigger, backfill, immutability |
| `0022_terminal_registers.sql` | `terminal_registers` and the allocation function `terminal_registers_assign`, backfill from live credentials |
| `0023_pos_sales.sql` | `pos_sales` projection with the partial unique index on the sale number |
| `0024_terminal_registers_assign_result.sql` | `terminal_registers_assign` reports whether it allocated a new number |

Apply them in order, by hand, to every existing environment before deploying the API. Details are in `deploy/README.md`.

`0024` is not backward compatible: it changes the result type of `terminal_registers_assign`, so an API built before it breaks at pairing once it is applied. Apply `0024` and deploy the matching API together. The new API also fails `/health/ready` against a database without `0024`, so neither order can serve traffic half-migrated. Nothing is in production yet.

## Related

- Specs: `organization-persistence` (Branch Short Code), `pos-installation-identity` (Register Number), `pos-scan-sale` (Sale Number), `branch-offline-sync` (Sale Number Projection).
- Feature record: `odd/tasks/human-document-numbers.md`.
