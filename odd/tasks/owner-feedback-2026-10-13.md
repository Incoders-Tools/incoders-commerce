# Owner feedback 2026-10-13 (staff, payroll, dates)

## Objective

Act on the owner's first hands-on review of the employee features: make the goods quantity, the employee's position
and the payroll's goods readable; show every date as dd/MM/yyyy; let a payroll be prepared for chosen employees and
paid from several accounts.

## Findings (verified 2026-10-13 against the local data)

- The employee account read "all 0": the data is right (`/employees/{id}/account/summary` gives -15.000, the advance),
  but the screen is the customers'/suppliers' one: "Vencido", "A vencer" and the aging (all 0 for an advance, which has
  no due date) and the balance in one card with a small "Nos debe". Pending overtime and the goods (on the linked
  customer's account) are not there.
- The payroll deducts the goods already, in their own block ("Mercadería que llevó"), not as a deduction line.
- Dates are shown dd/MM/yyyy (`formatIsoDate`), but the 35 `<input type="date">` of the web (23 files) are drawn in the
  browser's language (mm/dd/yyyy in an English browser). The POS types dates as dd/mm/aaaa already.

## Decisions (owner, 2026-10-13)

- Overtime and goods do not post to the employee's account until the payroll (as the salary): they show as pending.
- Dates are dd/MM/yyyy everywhere, fixed (no setting for now).
- A payslip's net can be paid from several accounts ("pago mixto"); the parts never exceed the net, and what is left
  stays owed to the employee ("Le debemos"), said before confirming.
- A payroll is prepared for the chosen employees (all checked by default).

## Tasks

- [x] P1a Goods quantity says its unit: "Kilos (ej.: 1,5)" for weighed products, "Unidades (ej.: 2)" by the unit.
  The box's placeholder reads "Kilos (Ej: 1,5)" (the organization's number format example) with "kg" beside it for a
  weighed or bulk product, "Unidades (Ej: 2)" with "u." (and a numeric keyboard) for one sold by the unit. RED: no
  placeholder; GREEN: goods page 7.
- [x] P1b The payroll shows the goods as a read-only deduction line too ("Mercadería (con bonificación)").
  In the payslip editor, among "Haberes y descuentos", with what is deducted after the discounts (it follows them as
  they are typed) and "Se calcula con la mercadería que llevó y la bonificación de abajo."; no "Quitar", never sent
  as a line (the server keeps the goods apart). RED: no such line; GREEN: web 835, POS employees 53.
- [x] P2 The employee's own account view (no due dates or aging): account balance, goods to deduct, pending overtime and
  the position to date (without the month's salary); "Horas extra pendientes" in the staff list.
  Server: `EmployeeRecord.OvertimeToPay` / `OvertimeHoursToPay`, the overtime no paid payroll settled (pending, or in a
  run still in draft). Web: the employee's account shows four cards instead of the due dates and the aging: "Saldo de la
  cuenta" (advances and balances), "Mercadería a descontar" (or "No lleva mercadería"), "Horas extra a pagar" ("Le
  debemos (4,5 h sin liquidar)") and "Posición a hoy" (balance + overtime - goods, "Sin contar el sueldo del mes, que se
  suma al liquidar."); the staff list (web and POS) shows "Horas extra a pagar" ("$ 40.500,00 (4,5 h)"). The goods unit
  suffix is now proved exactly (P1a review). RED: did not compile (server, POS), no cards or column (web); GREEN: payroll
  + treasury + POS + device operator 118, web 838 (99 files), POS employees 53.
- [x] P3 A date field of our own that shows and takes dd/MM/yyyy, in place of every `<input type="date">`.
  `DateInput`: `Input type="date"` draws it, so every one of the 35 date fields (21 screens) changed with no screen
  touched. A text field "dd/mm/aaaa" that puts the slashes while typing, takes a pasted ISO date, gives the screen the
  ISO date once it is a real day ('' while incomplete or for 31/02), follows a date the screen sets in the same render,
  carries min/max, and a "Elegir fecha" button opens the browser's calendar. Moments shown with the browser's language or
  without leading zeros now read `formatInstantDate(Time)` ("05/01/2026 09:07"): catalog, organizations, price lists,
  stock movements, discount PIN. POS: `PosCulture` (es-AR) for the sales history and the sync status (no "yyyy-MM-dd",
  no "5/1", no Windows' language for the day's name or the money). Tests that typed or read ISO dates in date fields now
  type and read dd/mm/aaaa. RED: no `DateInput`, `formatInstant*`, `PosCulture`; GREEN: web 843 (100 files), POS 927.
  Open: 41 other `CultureInfo.CurrentCulture` uses in the POS (some parse typed amounts; to review one by one before
  forcing es-AR).
- [ ] P4 Preparing a payroll for chosen employees (multiple selection).
- [ ] P5 Paying a payslip from several accounts.

## Constraints

Same as `owner-requests-quick-adjustments.md` (server rules, parity, idempotent migrations, strict TDD, healthy
Postgres for DB suites, commits to `dev`). Apply only the new migration file to `commerce_dev` (0001/0007 carry
password placeholders).

## Progress

- Feature document created 2026-10-13.
