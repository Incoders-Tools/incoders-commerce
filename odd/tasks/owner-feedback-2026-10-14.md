# Owner feedback 2026-10-14 (advances history, advance voids, treasury labels, recurring movements)

## Objective

See, correct and void the advances handed out to an employee, with both sides of an advance (the cash it left and the
employee's account) always moving together; name every treasury movement kind in Spanish; and register recurring
movements from the same place as any other movement.

## Findings (verified 2026-10-14 against the local data and the code)

- The advance page (`EmployeesScreen.tsx`, `AdvanceForm`) only registers a new advance: there is no list of the
  advances handed out, nor a way to correct or void one.
- An advance writes two movements keyed by its id (`PostgresEmployeeStore.GiveAdvanceAsync`): a treasury movement
  (kind `EmployeeAdvance`, Out) and a Payment (Debit) on the employee's account. They can be undone separately: reversing
  the debit on the employee's account leaves the cash movement counting. Ariel Bacca's $ 15.000 of 2026-10-13 is that
  case: reversed on his account on 2026-10-14 ("Mal cargado"), still Out in "Caja efectivo · Ruta 51". His account
  ($ 12.000 owed) is right; the drawer is $ 15.000 short of what it really holds.
- The treasury cannot void an `EmployeeAdvance` movement itself (`EditableKinds`), which is right: it belongs to the
  employee's advance.
- `EmployeeAdvance` and `SalaryPayment` have no label in `treasury.json` `movementKinds`, so the web shows the code. They
  are movement kinds (fixed in code because each one has its own behavior), not account types: they do not belong in
  "Tipos de cuenta".
- Recurring movements ("Gastos e ingresos recurrentes", `TreasuryRecurrences.tsx`) live in their own section under the
  accounts, with their own "Nuevo" button, apart from "Registrar movimiento".

## Decisions (2026-10-14; the owner left the approach to us)

- An advance is voided or corrected whole, from the employee: its cash movement is voided (it stays visible, struck
  through with the reason, and no balance counts it, as any voided treasury movement) and its debit is reversed on the
  employee's account. Reversing the debit from the employee's account does the same. A correction voids the advance and
  hands out the corrected one in the same transaction.
- Refused while the employee is in a Draft payroll (its advances line was read when the draft was prepared): delete the
  draft or leave the employee out, then void. Allowed after a paid payroll: the reversal leaves the amount in the
  employee's favor ("Le debemos"), which the next payroll pays, and the drawer gets the money back. Both books agree.
- The advance history reads the last month by default (from the same day of the previous month to today), with a date
  range.
- Advances already left half-voided (debit reversed, cash still counting) are repaired by a migration with the same rule.

## Tasks

- [x] A1 Treasury labels: "Adelanto a empleado" (`EmployeeAdvance`) and "Pago de sueldo" (`SalaryPayment`).
  RED: the movement read "EmployeeAdvance"; GREEN: treasury screen 13.
- [ ] A2 Server: an employee's advances (`GET /employees/{id}/advances?from&to`), void
  (`POST /employees/{id}/advances/{advanceId}/void`) and correction (`PUT /employees/{id}/advances/{advanceId}`), one path
  for both sides; reversing an advance's debit on the employee's account voids its cash movement too. 409
  `advance-in-payroll-draft`, `advance-voided`.
  `PostgresEmployeeStore.Advances.cs`: `ListAdvancesAsync` (debit and cash joined by the advance id; voided when either
  side was undone, the reason the cash void's or the reversal's concept; a correction linked through the void's
  `replacement_movement_id`), `VoidAdvanceAsync` and `CorrectAdvanceAsync` (the employee's row locked first: the ledgers
  are append-only and `app_runtime` cannot lock their rows), `InsertAdvanceAsync` shared with `GiveAdvanceAsync`;
  `PostgresTreasuryStore.VoidSourceMovementAsync` (ON CONFLICT DO NOTHING); `PostgresCurrentAccountStore.ReverseAsync`
  voids an advance's cash after reversing its debit, `ReverseMovementOutcome.InPayrollDraft`. Reason required, up to
  150 characters. RED: did not compile; then `permission denied for table current_account_movements` (FOR UPDATE on the
  ledger), fixed by locking the employee. GREEN: payroll 46, HTTP 1; payroll + treasury + accounts + employees + device
  operator + migrations 256.
- [x] A3 Migration 0059: void the cash movement of every advance whose debit was already reversed.
  `0059_advance_cash_voids.sql`: voided by whoever reversed the debit, when they did, reason "Anulado en la cuenta del
  empleado: <concept>"; idempotent; mirrored in `init-rls.sql`. RED: no file; GREEN: 7. Applied to `commerce_dev`:
  Ariel's $ 15.000 of 2026-10-13 voided ("Anulado en la cuenta del empleado: Mal cargado"), the $ 12.000 untouched.
- [x] A4 Web advance page: history with date range (last month by default), status, "Corregir" and "Anular".
  `EmployeeAdvancePage.tsx` replaces `AdvanceForm` (row action now "Adelantos"): "Nuevo adelanto" (stays on the page and
  reloads the history) or "Corregir el adelanto del …" (loaded with it, reason required); "Adelantos entregados" with
  Desde/Hasta (`monthBeforeIso`, as the server's `AddMonths(-1)`), a voided one struck through with why, the standing
  total; "Anular" asks the reason (`ConfirmDialog`). The employee account's reversal explains
  `advance-in-payroll-draft`. Texts in es and en. The page tests were written before the page but not run red (the module
  was missing); a mutation control (reason check off, strike-through off) failed them. GREEN: web 873.
- [x] A5 Treasury: "Movimiento recurrente" in the header buttons, opening in the same panel as "Registrar movimiento"
  (one open at a time); the separate section goes away.
  The panel opens with the form for a new one, the ones already set up below ("Los que ya se registran solos": edit,
  pause); "Cancelar" closes it as any other form. RED: no button; GREEN: treasury screen 14.
- [x] A6 Cross-layer parity map: the POS hands out advances; their history, voids and corrections are web only
  (accounting, as the account reversals). Treasury row: recurring movements and kind labels. POS employees 53.

## Follow-ups (reviews of b7fb667..7404e77, pushed 2026-10-14)

- [x] F1 Advance void vs. a reversal from the employee's account (`review-ce90a17fc8a5ba57` R3-undo-race-throws,
  R4-advance-void-account-reverse-race): refuted. The reversal inserts through `current_account_movements_employee_fk`,
  whose KEY SHARE lock on the employee waits for the void's `FOR UPDATE` (and one already inserted makes the void wait
  and read the debit reversed). `PayrollTests ...WaitsForAVoidHoldingTheEmployee` keeps it (green before any change, so
  no code change; the comment in `UndoAdvanceAsync` says why).
- [x] F2 Launcher (`review-aacc510d121a0747` R3-001): a failed npm ci or build of the website warns, opens `/login` and
  starts the rest (the POS included) instead of stopping the launcher. Checked by running the block against a site whose
  build exits 3: two warnings, `/login`, `VITE_APP_URL` cleared, location restored.
- [x] F3 Recurring movements (`review-1c439f78aeaec7ea` R3-edit-cancel-closes-panel, R3-form-reset-unasserted):
  "Cancelar" on an edit goes back to the new form (on the new form it still closes the panel); a saved one leaves an
  empty form. RED: the panel closed; GREEN: treasury screen 15.
- [x] F4 Advances history (`review-ff03d626e87f1b71` R3-history-load-race, R3-invalid-range-stale-list): only the latest
  read counts; a range the other way round hides the list it no longer matches. RED: a late answer replaced the list;
  GREEN: advance page 6.
- Open, accepted as they are: F4 side effect (while Desde or Hasta is being typed the list hides until the date is
  complete), launcher R3-002..005 (suggestions), advances server readability R2 (`UndoAdvanceAsync` signature, nested switch guards, reason
  limit literals), R3-correction-skips-employee-eligibility (a correction for an inactive employee: the web offers
  "Adelantos" only to active ones, as giving one), R4-correction-retry-not-idempotent (a correction retried after a lost
  answer finds the advance voided: 409 `advance-voided`, nothing doubled).

## Constraints

- Strict TDD; business rules on the server; cross-layer parity (`skills/cross-layer-parity`).
- Migrations forward-only, idempotent, appended verbatim to `deploy/dev/db/init-rls.sql`.
- Commits straight to `dev`, reviewed before each one.
