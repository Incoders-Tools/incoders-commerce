# Suppliers and Current Accounts

## Objective

Let a business admin register and manage suppliers in the web console (meat,
technology services, cleaning, and so on) and keep each supplier's current
account: what was invoiced, what was paid, the running balance and what is
overdue.

## Why

Suppliers do not exist yet (only a supplier price-list import parser). They
come before purchases, receptions and delivery. PRD 9.6 (suppliers and
purchases) and 9.13 (current accounts) require commercial, tax and contact
data, terms, and a current account built from movements with partial
payments, due dates, auditable reversals instead of deletion, and debt
total / overdue / aging.

## Scope

- Supplier master data, modelled on Customer: display and legal name, tax id
  type (Cuit/Cuil/Dni/None) and tax condition, phone, email, address, core
  city (`cities`), supplier category, payment terms in days, bank details
  (CBU/CVU and alias), notes, enabled flag, audit dates, and a contacts
  sub-table (first name, last name, phone, email, role, primary).
- Supplier categories ("rubros"): organization-scoped ABM like business
  types. Seeded per organization with Carne, Tecnología, Limpieza only when
  the owner asks; the web lets the admin create them.
- Current account ledger, generic by design (PRD 9.13 covers customers,
  suppliers and employees) but wired to suppliers only in this feature:
  append-only movements (opening balance, invoice, debit note, credit note,
  payment, adjustment) with date, due date, document reference, concept,
  amount, and reversal by a compensating movement. Balance is always derived
  from movements. Statement by date range with opening and closing balance;
  summary with total, overdue and aging buckets (0-30, 31-60, 61-90, 90+).
- Web: suppliers list (search, category and city filters), full-page form
  with contacts editor and city picker, categories ABM, supplier current
  account screen.

## Out of scope (later features)

- Purchase orders, receptions, document capture/OCR, cost history.
- Linking payments to financial accounts (cash, bank).
- Customer and employee current accounts on the same ledger.
- Dashboard wiring (dashboard stays on mock data until the owner says).

## Constraints

- Clean domain: no infrastructure in `src/Commerce.Domain`; Npgsql stores in
  `src/Commerce.Cloud.Api/Persistence`; minimal API endpoints.
- Every new table org-scoped with forced RLS, `app_runtime` grants, the
  `NULLIF(current_setting(...), '')::uuid` idiom. Ledger movements are
  append-only (`SELECT, INSERT` only). Migrations forward-only, idempotent,
  appended verbatim to `deploy/dev/db/init-rls.sql`.
- Money as `numeric(18,2)`, ARS. Amounts positive; direction explicit.
- Permissions: reuse the existing admin permission used by customers unless
  a supplier-specific one is clearly needed.
- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test`, `npm test` (Vitest) in `src/Commerce.Web`, plus
  `npm run lint` and `npm run build`.
- Commit straight to `dev`, Conventional Commits, no AI attribution.

## Tasks

- [x] T1 Migration: supplier_categories, suppliers, supplier_contacts, current-account movements + migration tests (route: delegated backend writer) -- 0428a38. RED: 14 migration tests failed (tables missing); GREEN: 14/14 pass. Migrations 0030_suppliers.sql, 0031_current_account_movements.sql applied to commerce_dev and mirrored in init-rls.sql.
- [x] T2 API: suppliers CRUD with contacts, categories ABM, search and filters, optimistic concurrency like customers (route: delegated backend writer) -- ba69618. RED: compile error (no Commerce.Domain.Suppliers), then GREEN: 57 supplier tests (domain + migration + endpoints).
- [x] T3 API: supplier current account (register movement, reverse, statement by range, summary with aging, balances in the supplier list) (route: delegated backend writer) -- 6b5ecc9. RED: 21 account endpoint tests failed (no routes) and domain rules uncompilable; GREEN: 29 domain rule tests + 36 supplier endpoint tests (incl. acceptance scenario). Balances endpoint is GET /suppliers/account/balances.
- [ ] T4 Web: suppliers list and full-page form, contacts editor, city picker, categories ABM (route: delegated web writer)
- [ ] T5 Web: supplier current account screen (statement, add movement, reverse, aging) (route: delegated web writer)

## Acceptance criteria

- A business admin creates a supplier with category, city, tax id and two
  contacts, finds it by name, contact or tax id, and edits it.
- Registering an invoice of 100.000 due in 30 days and a payment of 40.000
  shows a balance of 60.000 owed; reversing the payment shows 100.000, and
  the reversed payment stays visible as reversed.
- An invoice past its due date counts as overdue and lands in the right aging
  bucket.
- No movement can be updated or deleted at the database level.
- All checks pass.

## Progress

- Feature document created 2026-10-02.
- 2026-10-02: T1-T3 (backend) done by the backend writer; local DB migrated. Sign convention: supplier balance = sum(Credit) - sum(Debit) = what the business owes. Aging rule: reversed documents excluded, Debit movements applied FIFO by due date, buckets by days past due (1-30, 31-60, 61-90, 90+), not-yet-due or undated debt is Current. Routes: /suppliers, /suppliers/categories, /suppliers/{id}/account/{movements,movements/{mid}/reverse,statement,summary}, /suppliers/account/balances.

## Next step

T4-T5 (web writer).
