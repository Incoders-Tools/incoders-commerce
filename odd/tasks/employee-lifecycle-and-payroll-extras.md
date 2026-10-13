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
  Owner decision 2026-10-12: the web registers products with quantity, like a sale on the counter (Mostrador) list,
  moving the stock; before paying a payroll the administrator decides a discount on the goods taken, per item and on
  the whole. So every item is kept (web and POS) and the payslip discounts them.
  - [x] E4a Server goods ledger. Migration 0056 (`employee_goods_items`: one row per item, source `Web` or `PosSale`,
    `discount_percent`, `payslip_id` ON DELETE SET NULL, RLS). `PostgresEmployeeGoodsStore`: quote and register on the
    organization's DEFAULT list (Mostrador; never the linked customer's own list, Reparto in Vaca Verde), in the selected
    branch (catalog, prices and stock are the branch's): stock out (`Sale`, source `EmployeeGoods`), an Invoice on the
    linked customer account, the items pending, audit `employee.goods_registered`. A POS sale on current account to
    that customer copies its lines (a sale discount spread with `EmployeeGoodsRules.Spread` so they add up to the
    charge; keyed by sale and line), and a void takes them back unless a paid payroll settled them. `POST
    /employees/{id}/goods/quote`, `POST /employees/{id}/goods`, `GET /employees/{id}/goods` (web only; 409
    `employee-no-goods-account`, `no-effective-price`). RED: did not compile; a first GREEN attempt exposed that the
    catalog read ignored the branch scope. GREEN: 21 payroll + 39 HTTP; regression 1569. Applied locally.
  - [x] E4b Payroll: the payslip takes the pending items; a discount per item and on the total, before paying.
    Preparing a payslip takes every goods item still pending (whatever its date: the goods debt is taken whole, as
    before); taken out of the payslip or with the draft discarded they are pending again (`payslip_id` ON DELETE SET
    NULL; an item keeps the discount it was given). `PayslipPurchases(Amount, DiscountPercent, ItemsDiscount)`: the item
    discounts (`EmployeeGoodsRules.ItemDiscount`, to the cent) come off first, never more than is owed, then the percent
    applies to the rest; a debt without items (from before the ledger, or entered by hand) only gets the percent.
    `PUT /payroll/runs/{id}/payslips/{payslipId}` takes optional `goodsItems: [{ itemId, discountPercent }]` (omitted:
    each item keeps its own; one the payslip did not take: 409 `goods-item-not-in-payslip`, nothing changes). The run
    detail gives each payslip its `goodsItems` and `purchasesItemsDiscount`; the credit note reads "Bonificación al
    personal" when items were discounted. RED: did not compile; GREEN: 35 (payroll + treasury), regression 220; control:
    without taking the items, 3 failures.
  - [x] E4c Web: "Registrar mercadería" on the employee (products, quantities, Mostrador prices, total).
    Row action "Mercadería" opens `EmployeeGoodsPage`: date (up to today), the staff order's `ProductSearch` (same
    search, `/orders/staff/presentations`: a business admin holds TakeOrders), quantities read with the organization's
    number format (whole for unit products), the server's quote debounced (a stale answer never shows) with each line's
    price and total, then "Registrar mercadería" with a key generated once per draft and kept across retries. Below,
    what was taken (date, product, quantity, price, total, origin Web / Venta POS, status Pendiente / En la liquidación N
    / Descontada en la liquidación N). Without a goods account it says how to turn it on. Refusals under `goods.codes`
    (not `errors.codes`, which the POS compares). RED: the page did not exist and the row action was missing; GREEN: 5;
    web suite 819, POS employees 52.
  - [ ] E4d Web payslip editor (items with their discount) and the wording that says POS only (web + POS).
    - [x] Wording: "Lleva mercadería" and its hint say the goods are registered in the web («Mercadería») or sold at
      the POS on account, and deducted with the discount decided at payroll, per product and on the total; the same
      texts in the web (es/en) and the POS form (`TakesGoods_ReadsTheWebsTexts_WhichNoLongerSayThePosOnly`). RED: the
      POS hint differed and neither said "web"; GREEN: POS employees 53, web 820.

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

- [x] G10 E4a review (`review-c26b35192eb5ffab`, approved, 4 lenses), fixed right after `671dd52`: a retried web
  registration (answer lost) moved stock and debt twice; the request now carries `goodsId` as an idempotency key
  (advisory lock + recheck, the retry gets the first registration back). The POS goods copy ran inside the sale money
  savepoint, so a bad item (a returned line: negative quantity) rolled the charge back; it now has its own savepoint,
  copies only the lines taken (positive) and spreads the charge over them. RED: did not compile / the charge was
  missing; GREEN: 97. Left: readability notes (locations only), per-line connections in the quote, HTTP refusals and the
  void-after-paid guard untested.

- [x] G11 E4a follow-up review (`review-37ccd441d0006ad9`, approved), fixed right after `619c686`: the idempotency
  key was not bound to the employee (a key reused for another employee returned the first one's registration); now
  it is refused (409 `goods-key-in-use`), and an empty key is a 400. RED: did not compile; GREEN: 32. Left: the
  goods-copy failure path is proved through the returned line only (no injected failure), log-only on failure.
- [ ] G12 E4b review (`review-048e036ee1501797`, approved, 4 lenses), fixed right after `848ab17`: an item named twice
  in `goodsItems` is a 400 (the store keeps the last); the item discounts are set in one `UPDATE … FROM unnest`;
  `UpdatePayslipAsync` takes them before the cancellation token; `ItemsDiscounted` reads `AppliedItemsDiscount`; the
  credit note says where the discount came from ("Bonificación al personal (por ítem y 10 % del resto)",
  `PayslipPurchases.DiscountConcept`), proved on the paid run; an item taken out keeps its discount into the next
  payroll (proved). RED: did not compile; GREEN: 35; control: without the 400, the HTTP test fails. Open:
  - Owner decision: when the goods owed drop between preparing and paying (the employee paid part directly), the
    percent applies to what is still owed but the item discounts apply whole (capped at what is owed), not in proportion.
  - The run detail reads lines and items per payslip (one query each); fine for a branch's staff, batch if it grows.
- [ ] G13 E4c review (`review-bdf6f8c8de313b33`, approved), fixed right after `488a4d7`: editing the draft renewed the
  registration key, so after a lost answer (registered on the server) an edited retry registered the goods twice; the
  key now stays until a registration succeeds, as the staff order's (the server hands the first one back). RED: the
  keys differed; GREEN: 15 (goods page + employees). Open (locations only): a failed quote is not retried until the
  draft changes (`EmployeeGoodsPage.tsx:102-114`); test coverage suggestion (`EmployeeGoodsPage.test.tsx:12-145`); the
  status cell (`EmployeeGoodsPage.tsx:284`).

## Constraints

Same as `owner-requests-quick-adjustments.md` (server rules, parity, idempotent
migrations, strict TDD, healthy Postgres for DB suites, commits to `dev`).

## Progress

- Feature document created 2026-10-11.
