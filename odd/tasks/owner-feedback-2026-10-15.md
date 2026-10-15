# Owner feedback 2026-10-15 (POS sale totals, website copy, advances range, employee goods price list)

## Objective

Read every POS sale total as it was sold, present Vaca Verde as the distributor it is, open WhatsApp from the website,
sign the website, default the advances history to each employee's pay period, and price the goods of the staff on the
counter list (Mostrador), saying so.

## Findings (verified 2026-10-15 against the local data and the code)

- B1 The POS day's sales showed $ 2.636.100,00 for sale V01-C1-5 of $ 26.361,00 (on account). The server holds 26361.00
  everywhere (pos_sales, the customer's account). The branch database wrote the total with the Windows culture
  (`effect.TotalAmount.ToString()`, `BranchSyncStore.cs:548`: "26361,00" under es-AR) and reads it as invariant text,
  where the comma is a group separator. The same wrong total feeds the cash count's expected cash, the customer's
  pending balance at the POS and a sale void's payload. Sale lines were already written invariant.
- B2 The website says "carnicería"; the owner: Vaca Verde is a distributor, with a butcher shop on Ruta 51 as a part of
  the business apart.
- B3 The phone opens a call (`tel:`); it should open WhatsApp.
- B4 The website footer lacks "Engineered by Incoders — All rights reserved." linking to www.incoders.com.ar.
- B5 The advances history reads the last month for everyone; it should follow the employee's pay period.
- B6 Goods registered in the web are priced on the default list (Mostrador), but the page does not say so; and the
  customer linked to an employee for the goods it takes was created with the organization's default customer list
  (Reparto for Vaca Verde: "Bacca, Ariel (personal)"), so a POS sale on account to it is priced on Reparto.

## Decisions (owner, 2026-10-15)

- B5: monthly staff, from the first day of the current month; weekly, the last 7 days; fortnightly, the last 15 days;
  always up to today (both days included).
- B6: the staff buying goods are priced on Mostrador, in the web and at the POS.

## Tasks

- [x] B1 Branch database: sale totals written invariant; totals already stored with a comma repaired when it opens.
  `e1b0411`. `PosSaleTotalCultureTests` (es-AR): the day's sales and the cash count read 26.361,00; a total stored
  "26361,00" reads back right after reopening. RED: 2636100 (with the repair switched off too); GREEN: POS 931.
- [x] B2 Website copy: a distributor with a butcher shop on Ruta 51 (hero, title, description, footer, structured data).
- [x] B3 Website: the phone opens WhatsApp (wa.me/5491121611045: an Argentine mobile on WhatsApp carries the 9).
- [x] B4 Website footer: "© {year} Engineered by Incoders — All rights reserved." linking to https://www.incoders.com.ar.
  `d948f62`. RED: 4 site tests; GREEN: site 11; checked at 390 px (no horizontal overflow).
- [x] B5 Advances history: default range by pay frequency. `lib/payPeriod.ts` (`payPeriodStartIso`): the web's
  opening range only; the API without dates still reads the last month. RED: the page opened on the last month;
  GREEN: advance page 7, pay period 2.
- [ ] B6 Employee goods: the quote says its price list; the linked customer gets Mostrador (new links and, by migration,
  the existing ones).

## Constraints

- Strict TDD; business rules on the server; cross-layer parity (`skills/cross-layer-parity`).
- Migrations forward-only, idempotent, appended verbatim to `deploy/dev/db/init-rls.sql`.
- Commits straight to `dev`, reviewed before pushing.
