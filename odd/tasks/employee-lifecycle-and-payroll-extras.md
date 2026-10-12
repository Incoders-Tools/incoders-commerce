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

- [x] E1 Legajo: zero-padded display everywhere (web + POS), next number shown on create (server endpoint).
  `EmployeeFileNumber.Format` (domain, used by the POS and the linked customer's note) and `lib/fileNumber.ts`;
  `GET /employees/next-file-number` (device operator too; a preview, the create still takes the number under its
  lock, so the field stays empty and the placeholder/hint says which one). Web: list, form, save notice, payroll
  run, payslips print, employee account. POS: list (search finds "10" and "0010"), form ("0007" parses), hint,
  save notice. RED: server didn't compile, POS didn't compile, web 3 failures; GREEN: 80 server, 49 POS, 792 web;
  web control without the screens' code: 2 failures.
- [x] E2 Overtime: employee hourly rate; overtime records (date, hours, multiplier, note) with Sunday = x2 proposed; pending ones become a payslip line by themselves (domain + migration + API + web + POS where staff is managed).
  - [x] E2a Server. `OvertimeRules` (domain: Sunday proposes x2, else x1; hours 0-24, multiplier 1-3, two decimals;
    amount = hours x rate x multiplier, frozen when recorded); migration 0054 (`employees.overtime_hourly_rate`,
    `employee_overtime` with RLS, `payslip_lines.source` widened to `Overtime`); `GET/POST /employees/{id}/overtime`,
    `DELETE /employees/{id}/overtime/{overtimeId}` (device operator too; 409 `overtime-rate-missing` / `overtime-settled`);
    `EmployeeRequest.overtimeHourlyRate` (omitted keeps it, 0 clears it). The payroll takes the pending records worked
    up to the end of the period as one "Horas extra (7,5 h)" earning; taking the payslip or the draft out releases them
    (`ON DELETE SET NULL` on `payslips.id`, proved under RLS as `app_runtime`), and so does saving the payslip without
    that line; a paid run settles them. RED: the tests did not compile; GREEN: 87, regression 321. Applied locally.
  - [x] E2b Web: rate in the employee form ("Valor de la hora extra"; empty sends 0 on an edit, nothing on a create);
    row action "Horas extra" opens `EmployeeOvertimePage` (date up to today, hours, multiplier proposed by the day via
    `lib/overtime.ts` until the operator changes it, note, amount preview; list with Pendiente / En la liquidación N /
    Liquidada; delete only a pending one, after confirming; without a rate it says where to set it). The payslip
    editor shows the overtime line read-only ("Se calcula con las horas extra cargadas."), removable. RED: 4 failures
    + 2 files missing; GREEN: 15, full web suite 804; control without the screens: failures again.
  - [x] E2c POS: "Valor de la hora extra" in the staff file (always sent on an edit, 0 when emptied); row action
    "Horas extra" opens `OvertimePanel` (date dd/mm/aaaa up to today, hours, the multiplier the day proposes until
    typed, note, amount preview, records with status; "Borrar" only on a pending one, confirmed in a bar with the web's
    question); `EmployeeAdminClient` list/record/delete; conflict texts and form errors equal to `employees.json`.
    Slip disclosed: `7af3707` (E2b) added the two overtime conflict codes to the web while the POS had none, so
    `PosEmployeesTests.TheConflictTexts_AreTheWebsOnes` was red on `dev` until this commit (I ran only the web suite
    after E2b). RED: the POS tests did not compile; GREEN: 68, regression 960 (POS, device operator, payroll).
    The WPF panel itself is proved through source and rules, not rendered: check it by hand.
- [x] E3 Termination section: date + reason, then "Liquidar último sueldo" opening the payroll for that employee; web + POS.
  Owner decision 2026-10-12: the final settlement proposes the agreed salary in proportion to the days worked
  (monthly: days of the termination month; biweekly 15; weekly 7), editable as any draft.
  - [x] E3a Server. Migration 0055 (`employees.termination_reason`); `SetEmployeeActiveRequest.terminationReason`
    (optional for older terminals; a date after today refused; reinstating clears date and reason);
    `FinalSettlementRules` (period from the day after the last paid period that included the employee, else its hire
    date or the 1st of the termination month, to the termination date; proportional salary; concept "Sueldo básico
    proporcional (10 de 31 días)"; note "Liquidación final de X (baja dd/mm/aaaa: motivo)"); `POST
    /payroll/runs/final-settlement` (web only; 409 `employee-active`, `employee-in-draft`, `employee-other-branch`):
    a Draft run of that employee alone with the pending overtime, advances and goods (shared `AddPayslipAsync`). A
    regular run already skipped inactive employees, so the last salary cannot be paid twice. RED: did not compile;
    GREEN: 104, regression 1143. The HTTP tests were written after the endpoint (no separate control). Applied locally.
  - [x] E3b Web: the row action "Gestionar baja" replaces the bare Dar de baja / Reincorporar toggle and opens
    `EmployeeLeavePage`: an active employee gets the leave with its date (up to today) and reason (required); one given
    the leave shows "Dado de baja el dd/mm/aaaa. Motivo: …", "Liquidar último sueldo" (creates the final settlement and
    opens `/app/payroll/{runId}`) and "Reincorporar"; each refusal explains itself (`leave.codes`). RED: the page did not
    exist and the row action was missing; GREEN: 14, full web suite 812, POS employees 52; control: failures again.
  - [x] E3c POS: the row action "Gestionar baja" replaces the Dar de baja / Reincorporar toggle and opens `LeavePanel`:
    an active employee gets the leave with its date (dd/mm/aaaa, up to today) and reason (required); one given the leave
    reads "Dado de baja el … Motivo: …", is told the last salary is settled in the web, and can be reinstated. The
    client sends date and reason; `employee-in-payroll-draft` reads as the web's. RED: did not compile; GREEN: 96 (POS
    employees, list markup, device operator). The WPF panel is proved through source and rules, not rendered.
- [ ] E4 Goods from the web: "Registrar mercadería" on the employee (a debit on its linked customer account) and the wording no longer says POS only.

## Follow-ups

- [ ] G1 E1 review (`review-51bbdbd50ea9eaa7`, approved). Fixed right after `e1fe9a5`: web tests for editing a
  padded legajo ("0001" shown, 1 sent; no next-number call) and for an unreadable next number (plain
  placeholder, no alert); control: unpadded edit value and an unhandled rejection each fail. Still open: the POS
  hint is checked through source strings in `PosEmployeesTests`, not by running the view.

- [ ] G2 E2a review (`review-cb2947519801970d`, approved). Fixed right after `0c95f57`: a payslip's overtime line
  always pays exactly the hours it took (an edited amount or concept is replaced; to pay other hours, delete a record
  or add a line by hand), and one on a payslip that took none is saved as a hand line (both were unproved branches);
  the employee audit logs the rate that stays (null kept, 0 cleared), not the raw input; a work date after today is
  refused (400); HTTP proves `overtime-rate-missing`. RED: 3 failures; GREEN: 39. Still open: listing the overtime of
  an unknown employee answers an empty list (not 404), and `overtime-settled` is proved in the store, not over HTTP.

- [x] G3 E2b review (`review-3f6a59e0ab8539ea`, approved), fixed right after `7af3707`: a refused delete showed its
  reason inside the form, which is not there without a rate (RED: the alert never appeared; the alert now sits outside
  the form); the delete test could pass during the reload (now it waits for the second read and counts the rows left;
  control: without the reload it fails); zero hours and a non-numeric overtime rate are proved refused.

- [ ] G4 E2b follow-up review (`review-218354bf4a47c4cb`, approved): two test-precision notes in
  `EmployeeOvertimePage.test.tsx`: the row count after a delete could be read before the list re-renders, and the
  zero-hours check could see the alert left by the 25-hours attempt.

- [x] G5 E2c review (`review-df92bb160603a6e8`, approved; findings came with locations only, read by hand). Fixed
  right after `1cfb867`: opening "Horas extra" on another employee the same day kept the previous multiplier (the
  date text did not change, so TextChanged did not propose one) and, while loading or after a failed load, the
  previous list; now both are reset explicitly (proved on the source; RED first). Judged not a defect: an edit from a
  stale list can overwrite a rate set meanwhile in the web, like every other field of the form (no concurrency check
  on employees).

- [ ] G6 E3a review (`review-3ab2aa829dd3024d`, approved). Fixed right after `045a76f`: reinstating an employee
  who is in a Draft run (its final settlement) is refused (409 `employee-in-payroll-draft`: it would be paid that and
  the regular runs too), and a second final settlement once a paid run covers the termination date is refused (409
  `employee-already-settled`). RED: did not compile; GREEN: 52. Open, for the owner: a monthly employee whose unpaid
  period spans more than one month is prorated over the days of the termination month (e.g. 41 days / 31); the draft
  is editable. Also open: boundary tests of the leave date and reason over HTTP.

- [ ] G7 E3a follow-up review (`review-1902594162b9336b`, approved). Fixed right after it: reinstating now locks
  the employee row first, as the final settlement does, so the draft check and a concurrent settlement never
  interleave (not provable by a deterministic test; reasoned on both `FOR UPDATE`s). Accepted: the guard also refuses
  while the employee sits in a regular draft taken before the leave (discard or pay it first); telling the two apart
  would need a final-settlement marker on runs. Open: no test of `employee-already-settled` over HTTP.

- [x] G8 E3b review (`review-63cef5ba17d63955`, approved), fixed right after `f6a1b64`: a leave saved while re-reading
  the employee fails showed an error and the form again (a retry would record it twice); it now shows the leave it
  sent. A leave without date or reason (an older terminal) showed an empty box; it now says "Dado de baja." RED: 2
  failures; GREEN: 16. Left as suggestions: the reinstate test reads the first call's body; one busy label for both
  buttons.

- [x] G9 E3c review (`review-c10cd291119dcc14`, approved), fixed right after `130c617`: the leave form's checks moved
  out of the WPF code-behind into `EmployeeFormRules.LeaveInputError` and are tested (reason required, dd/mm/aaaa up
  to today); a reason ending in "." no longer reads "Motivo: Renuncia..". RED: did not compile; GREEN. Judged not a
  defect: the POS maps only `employee-in-payroll-draft` of the leave codes, the others belong to the final settlement,
  which the POS does not offer.

## Constraints

Same as `owner-requests-quick-adjustments.md` (server rules, parity, idempotent
migrations, strict TDD, healthy Postgres for DB suites, commits to `dev`).

## Progress

- Feature document created 2026-10-11.
