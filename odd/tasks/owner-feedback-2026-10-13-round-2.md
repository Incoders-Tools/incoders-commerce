# Owner feedback 2026-10-13, round 2 (goods voids, staff position, treasury)

## Objective

Let a goods registration be undone or corrected, show each employee's position in the staff list, make "Ver anulación"
visibly go somewhere, and read every treasury account's movements in one place.

## Findings (verified 2026-10-13 in the code)

- Goods taken by an employee (`employee_goods_items`, web source) can be neither voided nor corrected. Worse: reversing
  their charge on the employee's customer account (`POST /customers/{id}/account/movements/{m}/reverse`) leaves the stock
  out and the items pending, so the next payroll deducts goods already cancelled.
- The staff list's account column is the employee account's balance: in practice the advances. The position (salary,
  overtime, goods) is only on the employee's account screen, and computed there by the web.
- "Ver anulación" links to `#movement-{id}`: the row exists, but it is usually right beside the reversed one (same date)
  and nothing is highlighted, so it reads as a dead link. The reversal has no link back.
- Treasury lists one account's movements at a time (click on its card); there is no account selector nor an
  all-accounts view.
- The dashboard is sample data as a whole (`dashboardSource.ts`, `SAMPLE_DATA`): its "Por cobrar" is not real.

## Decisions (owner, 2026-10-13)

- A goods registration is voided whole ("Anular") or corrected ("Corregir": voided, and the form opens again with the
  same products). Reversing its charge on the customer account voids it the same way.
- Staff list: an "Adelantos" column (the account balance) and the pay to date: agreed salary + overtime to pay − what the
  employee owes (advances, goods). It is monthly: a paid payroll brings it back to the salary; whatever is not settled
  carries over.
- Salary deductions (absences, suspensions, late arrivals) wait; the payslip's manual deduction lines cover them today.
- Treasury: movements of every account, with an account filter.

## Tasks

- [x] R1 Void a goods registration (server): `POST /employees/{id}/goods/{goodsId}/void` and the customer account
  reversal of its charge, one path: charge reversed, stock back in the goods' branch, items released (a draft payslip's
  goods amount lowered by them); 409 `goods-settled` once a paid payroll took them.
  `PostgresEmployeeGoodsStore.VoidAsync` (advisory lock on the key; a retry of a done void is 204 and moves nothing)
  and `ReleaseAsync`, shared with `PostgresCurrentAccountStore.ReverseAsync`: reversing a customer charge whose source
  is `EmployeeGoods` (web) or `PosSale` releases its items (the web's stock comes back in its own branch, the branch GUC
  set to it; a POS sale keeps its stock, as any customer's), `ReverseMovementOutcome.SettledByPayroll` (409
  `goods-settled`) once paid. Items are deleted as a voided POS sale's are; the audit keeps `employee.goods_voided` and
  the account keeps the charge and its reversal. A goods item now carries its product and quantity behavior (selected
  branch) for "Corregir". RED: did not compile, then the missing item fields; GREEN: payroll + treasury 48; accounts,
  POS sales, stock, suppliers, customers 715.
- [x] R2 Goods page: "Anular" (with confirmation) and "Corregir" per registration.
  One pair of actions per web registration not yet deducted by a paid payroll (none for a POS sale's items): "Anular la
  carga del 12/10/2026 (Asado, Chorizo)" asks first with the amount and what happens; "Corregir" voids it and loads its
  products, quantities (unit or kilos) and date back into the form with a new key. `goods-settled` is explained here and
  in the account screens' reversal. RED: no actions; GREEN: web 856, POS employees 53.
  Review `review-4791ef0b37fd2dc0` (correction): a charge reversed before this change (items pending, stock out) made
  "Anular" answer 204 and release nothing; `VoidAsync` now releases on an already reversed charge too (a no-op once
  done). RED: the items stayed; GREEN: payroll + treasury 49.
- [x] R3 Staff list: "Adelantos" and "A cobrar" (salary + overtime − advances − goods), computed by the server; POS too.
  `EmployeePayRules.PayToDate` (Domain): salary while active + account balance (advances negative, a salary left owed
  positive) + overtime to pay − goods owed; `EmployeeRecord.PayToDate`. Web and POS: "Cuenta" is now "Adelantos y
  saldo", and "A cobrar a hoy" reads it ("Nos debe $ X" in red when the employee owes more than its salary), with a hint
  saying a paid payroll brings it back to the salary and what was not settled carries over. One salary only: periods
  left unliquidated are not added up (the payroll proposes them when prepared). RED: did not compile (server, POS),
  no column (web); GREEN: payroll + treasury + employees + device operator 139, POS employees 55, web 857.
- [ ] R4 "Ver anulación" highlights the reversal and the reversal links back to the movement it cancels.
- [ ] R5 Treasury: movements of all accounts with an account filter.

## Constraints

- Strict TDD; business rules on the server; cross-layer parity (`skills/cross-layer-parity`).
- Commits straight to `dev`, reviewed before each one.
