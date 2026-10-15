# Owner feedback 2026-10-15, round 2 (POS PIN echo, settings by role, money input everywhere)

Branch `feat/owner-feedback-2026-10-15-round-2`, merged into `dev` after the promotion PR to `main` lands (owner
decision). Shared by both layers for S3: `skills/cross-layer-parity`, `docs/architecture/cross-layer-parity.md`.

## Specs

- S1 POS PIN: "Cuando ingreso un codigo (pin) desde el POS para acceder a la terminal, el ultimo caracter que tipeo no
  se llega a visualizar demora 1 segundo y entra, se supone como usuario debo poder ver todo lo ingresado, es decir
  cantidad de caracteres exactas."
- S2 POS settings by role: "Cuando el usuario logueado al pos es un cajero u otro rol que no sea admin o sysadmin no
  deberia ver cuestiones tecnicas de configuracion como reconfigurar terminal. Quiza si la sincro porque puede querer
  sincronizar algo."
  Decision: a non-administrator sees Apariencia, Sincronización and Sucursal y estado; "Identidad técnica / Reconfigurar
  terminal" and "Versión y actualizaciones / Buscar actualizaciones" are hidden.
- S3 Money input: "En abrir caja pongamos un placeholder para que el user sepa como ingresar los valores. En Argentina
  tambien se estila para los miles el punt "." ahi podemos llegar a tener problemas porque usuarios podrian hacer
  quincemil asi: 15.000 y la caja se abriria con 15. Como podemos solucionar este tipo de inputs en todo el sistema?
  Inclusive tambien en la web."
  Decision (one rule, one reader per layer, recorded in the parity map; quantities keep the organization's format):
  - a `.` followed by exactly 3 digits groups thousands: `15.000` = 15000, `1.234.567,50` = 1234567.50;
  - a `,`, or a `.` followed by 1 or 2 digits, is the decimal: `15,5` and `15.50` = 15.50;
  - anything else is refused with a clear message (`1,234.56`, `15.0000`);
  - below the field, the amount as read: "Se abrirá con $ 15.000,00";
  - placeholder `Ej.: 15.000 o 15.000,50`.

## Tasks

- [x] T1 S1 POS lock screen draws the sixth PIN dot before verifying, with "Verificando…" while it checks (route: inline).
  `LockScreenView.QueuePinSubmit`: busy "Verificando…", then `SubmitPin` at `DispatcherPriority.Background` (after
  Render), once (Enter or the sixth digit). RED: `PosLockScreenMarkupTests.LockScreenView_DrawsTheSixthPinDot_BeforeTheCheckRuns`
  (`SubmitPin()` inside `PasswordChanged`). GREEN: lock screen, settings, theme, operator menu, shell, busy 129/129.
- [x] T2 S2 POS settings window hides the technical sections from a non-administrator (route: inline).
  `TerminalSettingsAccess.ShowsTechnicalSections` (administrator = `ManageUsers`, the operator menu's "Administrador");
  `SettingsWindow(..., showTechnicalSections)` collapses `TechnicalIdentitySection` and `UpdatesSection`; `MainWindow`
  passes the signed-in operator. RED: `PosTerminalSettingsAccessTests` (no `TerminalSettingsAccess`). GREEN: 129/129 with T1's set.
  Commit T1: `54337c8`. Commit T4: `5923b0b`.
- [x] T3 S3 POS shared money reader (`MoneyInput`) used by open cash, close cash, tender, customer payment, cash
  movements and every other POS money field; preview and placeholder on open cash (route: inline).
  `MoneyInput` (`Read`/`TryRead`/`Format`, `Placeholder` "Ej.: 15.000 o 15.000,50") behind `CashSessionInput` (open and
  close cash), `TenderInput` (cash received), `CustomerPaymentInput.TryReadAmount` (customer payment, cash movements) and
  `EmployeeFormRules.ParseAmount` (salary, overtime rate, advance). Open cash: no default "0", the example while empty
  (`OpeningFloatPlaceholder`), "Se abrirá con $ 15.000,00" under it (`CashSessionInput.OpeningReadBack`). Messages
  and the employee tooltips use the same example. Sale-line quantities, discount and percent fields are not money and
  keep their readers. RED: `PosMoneyInputTests` (no `MoneyInput`). GREEN: money, cash session, tender, employees, cash
  movement, customer payment, close cash, theme, components 177/177; POS, parity, tender, cash, payments, employees
  1093/1093. Commit T2: `f8a1006`.
- [x] T4 S3 Web shared money reader (`lib/money.ts`) replacing both `parseAmount` and the movement form's own check;
  every web money field uses it, with the placeholder and the preview (route: inline).
  `readMoney`/`parseMoney`/`formatMoney` (max decimals per field: 4 for a reception unit cost, the server's
  `MaxUnitCostDecimals`); `components/form/MoneyField.tsx`: `MoneyField` (labelled), `MoneyInput` (bare; `compact` in a
  row: short placeholder `15.000,50`, no read-back line), `MoneyReadBack` ("Se toma como $ 15.000,00"). Wired: supplier
  account movement (was `type="number"`), treasury movement/edit/recurrence, advance, employee salary and overtime rate,
  payroll payment parts and payslip lines (compact), price editor cell (compact; it already reads back old -> new),
  reception unit cost. `treasuryInput.parseAmount` keeps its positive-only contract over `parseMoney`;
  `lib/quantity.parseAmount` removed. Percent fields stay as they are (not money). RED: `money.test.ts` (no module),
  supplier account `15.000` and `1.234,50` posted nothing, treasury `parseAmount('15.000')` null. GREEN: money 8,
  MoneyField 4, focused screens 275 passed. `10.123` in the supplier "more than two decimals" case is now 10123 by the
  rule, so the case types `10,123`. `tsc -b` and oxlint clean.
- [x] T5 S3 Parity map and docs record the money rule (route: inline).
  `docs/architecture/cross-layer-parity.md`: "Money input" convention; the staff row no longer says the POS mirrors
  `parseAmount`.

## Log

- L1 (owner, 2026-10-15) "1. Cuando ingreso un codigo (pin) desde el POS para acceder a la terminal, el ultimo caracter
  que tipeo no se llega a visualizar demora 1 segundo y entra, se supone como usuario debo poder ver todo lo ingresado,
  es decir cantidad de caracteres exactas. 2. Cuando el usuario logueado al pos es un cajero u otro rol que no sea admin
  o sysadmin no deberia ver cuestiones tecnicas de configuracion como reconfigurar terminal. Quiza si la sincro porque
  puede querer sincronizar algo. 3. En abrir caja pongamos un placeholder para que el user sepa como ingresar los
  valores. En Argentina tambien se estila para los miles el punt "." ahi podemos llegar a tener problemas porque
  usuarios podrian hacer quincemil asi: 15.000 y la caja se abriria con 15. Como podemos solucionar este tipo de inputs
  en todo el sistema? Inclusive tambien en la web."
- L2 Findings: S1 `LockScreenView.xaml.cs:189` verifies the PIN inside `PasswordChanged` on the sixth digit, so the
  ~1 s hash runs before WPF renders the sixth dot. S2 `SettingsWindow.xaml` shows every section to any operator. S3 the
  POS has five readers (`CashSessionInput`, `TenderInput`, `CustomerPaymentInput`, `EmployeeList`, `SaleLinesTable`)
  that read `15.000` as 15; the web has two `parseAmount` (`lib/quantity.ts` reads `15.000` right,
  `treasury/treasuryInput.ts` refuses it, observed in its RED) and `MovementForm.tsx` refuses `15.000` and `15,50`.
- L3 (owner, 2026-10-15) Decisions: S3 rule as proposed; S2 non-administrators keep Apariencia, Sincronización and
  estado; this work goes to the next promotion, the current PR opens without it.
- L4 POS tests build with `--artifacts-path artifacts/agent-tests` (gitignored): the owner's local stack (Cloud.Api and
  the POS, started 10:14) locks the default `bin` outputs. Existing POS tests pinned the old rule ("no thousands
  separator is ever accepted": `1.000,50`, `650.000,00` refused, `10.005` as three decimals); by S3 they are now
  1000.50, 650000 and 10005.
