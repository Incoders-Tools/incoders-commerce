# Employee Lifecycle and Payroll Extras

## Objective

Make the whole life of an employee manageable from the web and the POS and make
the payroll add up everything that was owed and taken in the period: the
salary for its pay frequency, plus overtime, minus the goods taken and the
advances (items 3, 6, 7 and 9 of the owner's 2026-10-11 request).

## Where it stands (2026-10-11)

- `employees` already has `file_number` (unique per organization),
  `hire_date`, `termination_date` and `is_active`; "Dar de baja" /
  "Reincorporar" exist (`SetEmployeeActiveRequest`).
- The payslip already sums the base salary, the pending advances
  (`PayslipLineSource.Advances`), lines added by hand (`Manual`, where overtime
  goes today) and the goods taken on the employee's linked customer account,
  with a discount decided at pay day (`PayslipPurchases`).
- Goods: the employee gets a linked customer "Apellido, Nombre (personal)";
  the POS sells to it on account. The web can already add manual movements to
  a customer's current account, but the form and its hint say "se le vende en
  el POS" only.

## Owner decisions (2026-10-11)

- Legajo: one number per organization (unchanged, so it survives a branch
  change), shown zero-padded ("0007"); creating an employee shows which number
  it will get.
- Overtime: hours x the employee's overtime hourly rate x a multiplier per
  record. Sundays are paid double, so a Sunday record proposes x2 (editable);
  any other day x1. Recorded when it happens, against the employee; the
  payroll picks up the pending ones by itself, like the advances.
- Termination: its own section in the employee ("Gestionar baja"): date and
  reason, then it proposes to settle the last salary through the same payroll
  flow.
- Goods: not tied to the POS. The web can register goods taken by an
  employee too (on the same linked customer account, so the payroll deducts
  them the same way).

## Tasks

- [ ] E1 Legajo: zero-padded display everywhere (web + POS), next number shown on create (server endpoint).
- [ ] E2 Overtime: employee hourly rate; overtime records (date, hours, multiplier, note) with Sunday = x2 proposed; pending ones become a payslip line by themselves (domain + migration + API + web + POS where staff is managed).
- [ ] E3 Termination section: date + reason, then "Liquidar último sueldo" opening the payroll for that employee; web + POS.
- [ ] E4 Goods from the web: "Registrar mercadería" on the employee (a debit on its linked customer account) and the wording no longer says POS only.

## Constraints

Same as `owner-requests-quick-adjustments.md` (server rules, parity, idempotent
migrations, strict TDD, healthy Postgres for DB suites, commits to `dev`).

## Progress

- Feature document created 2026-10-11.
