# POS Operator Session Specification

## Purpose

Define a local, per-terminal operator identity layered on top of device
pairing in `Commerce.Pos.Windows`, so desktop-originated sales can be
attributed to the actual staff member present, without weakening the
"cloud cannot block local sales" invariant. This also defines the
lifecycle and authorization contract for operators on POS terminals,
including which screens their identified role may reach.

## Requirements

### Requirement: Operator Login Layered on Device Pairing

The POS MUST require operator login only after a valid device credential
already exists (pairing unchanged). The operator layer MUST NOT alter,
bypass, or replace the pairing flow.

#### Scenario: Operator login requires a paired terminal

- GIVEN a POS terminal has no valid device credential
- WHEN the application starts
- THEN pairing is required first and no operator login is offered

### Requirement: One-Time Online Provisioning Per Terminal-Operator Pair

The POS MUST let any staff member with valid server credentials
provision their own local PIN credential on a paired terminal by
verifying once online, with no separate authorization step. The server
verification MUST use the existing credential-verification path (as
used by `/account/sign-in`). On success, the POS MUST persist a locally
cached PIN credential, DPAPI-encrypted at rest, following
`LocalInstallationStore`'s exact tolerant-decrypt pattern: a corrupted or
undecryptable file MUST be treated as "no credential" and MUST NOT
throw.

#### Scenario: Self-provisioning succeeds online

- GIVEN a staff member has valid server credentials and physical access
  to a paired terminal with no cached PIN for them
- WHEN they provision a PIN while online
- THEN server verification succeeds, no separate admin authorization is
  required, and a DPAPI-encrypted PIN credential is cached locally for
  that operator on that terminal

#### Scenario: Provisioning fails clearly when offline

- GIVEN a staff member attempts first-time provisioning on a terminal
  with no connectivity
- WHEN provisioning is attempted
- THEN the online verification cannot complete, provisioning fails
  clearly, and no local PIN credential is cached

#### Scenario: Corrupted local operator file is treated as no credential

- GIVEN a terminal's local operator credential file is corrupted or
  undecryptable
- WHEN the POS reads it
- THEN it is treated as "no credential" and the read MUST NOT throw

### Requirement: Offline Operator Switching After Provisioning

Once an operator has a cached PIN credential on a terminal, subsequent
logins or switches to that operator on that terminal MUST be verified
locally against the cached credential, with no network call.

#### Scenario: Provisioned operator logs in offline

- GIVEN an operator has previously provisioned a PIN on a terminal
- WHEN they log in again with the network disconnected
- THEN the PIN is verified locally against the cached credential and no
  network call is made

### Requirement: Multiple Cached Operators Per Terminal

A terminal MUST support more than one operator holding a cached PIN
credential simultaneously. Each operator provisions independently, once,
online; provisioning by one operator MUST NOT affect another operator's
cached credential.

#### Scenario: Second operator provisions without disturbing the first

- GIVEN Operator A already has a cached PIN on a terminal
- WHEN Operator B provisions their own PIN on the same terminal while
  online
- THEN Operator B's credential is cached alongside Operator A's, and
  Operator A's cached credential and ability to log in remain unchanged

### Requirement: Offline Credential Staleness

A cached PIN credential MUST expire after a design-defined number of
days without a successful server reconnection/reconciliation for that
operator on that terminal. An expired credential MUST NOT authenticate
locally and MUST require re-provisioning online.

#### Scenario: Stale cached credential requires re-provisioning

- GIVEN an operator's cached PIN credential has not successfully
  reconnected/reconciled with the server within the configured
  staleness window
- WHEN that operator attempts to log in with their PIN
- THEN local authentication is refused and the operator must
  re-provision online before logging in again

#### Scenario: Reconnection resets the staleness window

- GIVEN an operator's cached PIN credential successfully
  reconnects/reconciles with the server
- WHEN the reconnection completes
- THEN the staleness window for that operator's credential is reset from
  that point

### Requirement: Lock Screen Gates The Sale UI

While no operator is signed in, the POS main window MUST show only a full-window
lock screen, covering the navigation and the content: the sale, the sections and
the cash prompt MUST be neither visible nor interactive (keyboard and scanner
input included). Owner-approved behavior change (2026-09-30): sales now REQUIRE a
signed-in operator in the UI. The previous rule that identification never blocks
a sale is retired for the UI; the domain and sync layers keep their fallback (an
absent operator resolves the actor to the installation id), so replicated and
legacy sales stay valid.

The lock screen MUST offer two ways in:

- Operator tiles plus PIN: one large tile per operator cached on the terminal whose
  credential is not stale; tapping a tile asks for that operator's 6-digit PIN,
  verified locally against the cached credential with no network call. A wrong PIN
  shows an inline error and stays on the PIN entry. The operator is always chosen
  first, so two operators sharing a PIN is harmless; there is no PIN-only login.
- "Ingresar con usuario y contraseña": email and password verified online. An
  operator not yet cached on the terminal, or one who ticked "Olvidé mi PIN", MUST
  then create a PIN (new and confirmation, valid per the PIN policy) that is cached
  locally, replacing any previous one, before entering. An operator already cached
  and not expired enters directly and keeps their PIN; one whose credential expired
  chooses a new PIN. Online failures MUST be shown inline in
  Spanish: invalid credentials, operator not permitted, branch not in scope, no
  branches assigned, terminal not recognized (telling the operator to configure the
  terminal again) and server unreachable.

When the terminal has no usable cached operator (first run after pairing, or every
cached credential stale) the lock screen MUST open on email and password, with no
tiles. When the server is unreachable the tile and PIN path MUST keep working. The
lock screen MUST show the terminal's branch, scroll when its content exceeds the
window, and work in every theme. The sign-in MUST NOT open any modal window.

#### Scenario: Nobody signed in

- GIVEN the application starts on a paired terminal
- WHEN the main window appears
- THEN only the lock screen is shown, and the sale, the navigation and the cash
  prompt are not visible and cannot receive input

#### Scenario: Tile and PIN, offline

- GIVEN an operator is cached and the network is down
- WHEN they tap their tile and enter the right PIN
- THEN they get in and no network call is made

#### Scenario: Wrong PIN

- GIVEN an operator tapped their tile
- WHEN they enter a wrong PIN
- THEN "PIN incorrecto." is shown inline and the lock screen stays

#### Scenario: New staff member creates a PIN

- GIVEN an administrator created a staff user and handed over the email and password
- WHEN the staff member signs in with "Ingresar con usuario y contraseña" on a terminal
  where they are not cached
- THEN after the online verification they choose a PIN, which is cached, and they
  get in; next time they can use their tile and PIN

#### Scenario: Forgotten PIN

- GIVEN an operator is cached and forgot their PIN
- WHEN they sign in with email and password and tick "Olvidé mi PIN"
- THEN they choose a new PIN that replaces the old one

#### Scenario: First run

- GIVEN a paired terminal has no cached operator
- WHEN the application starts
- THEN the lock screen shows email and password directly, with no tiles

#### Scenario: Terminal not recognized

- GIVEN the server does not know the terminal's device credential
- WHEN an operator signs in with email and password
- THEN the lock screen tells them to configure the terminal again

#### Scenario: Sale is attributed to the signed-in operator

- GIVEN an operator signed in through the lock screen
- WHEN a sale is completed
- THEN `CompleteOfflineSale`'s `actorId` is the operator's user id rather than the
  installation id

### Requirement: Operating The POS Requires OperatePos

Signing an operator into a terminal MUST require the `OperatePos`
permission, held by the `cashier` and `business-admin` roles. A `seller` (a
field salesperson who takes orders on the web) MUST NOT be able to pair a
terminal or be provisioned as an operator. The server MUST verify the
permission only after the credentials are proven (a wrong password is still
the generic 401), and MUST answer a permitted-credentials user lacking
`OperatePos` with HTTP 403 and the typed status `operator-not-permitted` on
both `POST /device/pair` and `POST /device/operators/verify`; no device
credential is issued and no operator is provisioned. `GET
/device/operators/{userId}/status` MUST report `inactive` for a user
without `OperatePos`, so a previously provisioned operator whose role was
changed is deprovisioned on the next reconciliation. The terminal MUST show
a friendly Spanish message asking for a `cashier` role assignment.

Sale completion itself (`CommitSaleButton_Click`) is unchanged: it adds no
permission check beyond the existing operator session (an operator can only
exist after the sign-in above), and no `RecordSales` permission is
introduced.

#### Scenario: Seller cannot pair a terminal

- GIVEN a `seller` with the correct password and a branch in scope
- WHEN they call `POST /device/pair`
- THEN the response is 403 with status `operator-not-permitted` and no
  device credential is issued

#### Scenario: Seller cannot be provisioned as an operator

- GIVEN a paired terminal and a `seller` with the correct password
- WHEN the terminal calls `POST /device/operators/verify`
- THEN the response is 403 with status `operator-not-permitted`

#### Scenario: Cashier and business-admin can operate

- GIVEN a `cashier` (or `business-admin`) with the correct password and the
  terminal's branch in scope
- WHEN the terminal calls `POST /device/operators/verify`
- THEN the response is `verified`

#### Scenario: Wrong password is not disclosed as a role verdict

- GIVEN a `seller` and an incorrect password
- WHEN they call `POST /device/pair` or `POST /device/operators/verify`
- THEN the response is the generic 401, not `operator-not-permitted`

#### Scenario: Status is inactive without OperatePos

- GIVEN a provisioned operator whose user no longer holds `OperatePos`
- WHEN the terminal calls `GET /device/operators/{userId}/status`
- THEN the status is `inactive`

#### Scenario: Sale completion adds no permission check

- GIVEN an operator session capability is present on a terminal
- WHEN a sale is completed regardless of whether an operator is identified
- THEN no permission check runs beyond what existed before, and the only
  observable difference is the `actorId` attributed

### Requirement: Admin-Only Customer Management Screen Gated by Current Operator Role

The desktop customer-management screen (list/create/edit) MUST be gated by
the role of the terminal's current operator (`CurrentOperator`), using the
same operator-identification mechanism this capability already establishes
— no new authorization concept is introduced. Only an operator holding
`business-admin`/`ManageUsers` MAY reach the screen; a `seller` operator MUST
NOT see or reach it. Unlike sale completion, reaching this screen is an
administrative operation and MAY require the terminal to resolve the current
operator's persisted role, consistent with the connectivity precedent
already established for device pairing and bootstrap.

#### Scenario: Business-admin operator reaches the customer-management screen

- GIVEN the terminal's current operator holds `business-admin`/`ManageUsers`
- WHEN that operator navigates to the customer-management screen
- THEN the screen is shown and customer create/edit/list actions are
  available

#### Scenario: Seller operator cannot reach the customer-management screen

- GIVEN the terminal's current operator holds only the `seller` role
- WHEN that operator attempts to navigate to the customer-management screen
- THEN access is denied and the screen is not shown

#### Scenario: No operator identified denies access to the screen

- GIVEN the terminal has no currently identified operator
- WHEN an attempt is made to open the customer-management screen
- THEN access is denied, consistent with treating an unidentified operator
  as having no elevated role

### Requirement: Operator Switching Keeps the Cash Session

Switching the signed-in operator MUST NOT close or change the open cash
session (`pos-cash-session`); each sale keeps the operator who made it.

#### Scenario: Switch inside an open session

- GIVEN a cash session is open
- WHEN another operator signs in
- THEN the same session stays open

### Requirement: Operator Menu

The operator button in the POS navigation bar MUST open a menu instead of any
provisioning screen. The menu MUST show the active operator's email and access
level (administrator, cashier, or no access to the point of sale) and MUST offer
"Cambiar operador" (only when another non-stale operator is cached on the terminal)
and "Cerrar sesión". Both return to the lock screen. The menu MUST NOT offer to add
an operator: a new operator signs in on the lock screen with email and password.

#### Scenario: Menu of the active operator

- GIVEN a cashier is signed in and a second non-stale operator is cached
- WHEN the operator button is clicked
- THEN the menu shows the cashier's email and "Cajero", "Cambiar operador" and
  "Cerrar sesión", and no provisioning entry

#### Scenario: Switching goes through the lock screen

- GIVEN an operator is signed in
- WHEN they choose "Cambiar operador"
- THEN the lock screen is shown

### Requirement: Sign-Out Keeps The Cash Session And The Cart

"Cerrar sesión" and "Cambiar operador" MUST both clear the terminal's current
operator and return to the lock screen. Neither MUST close or change an open cash
session (`pos-cash-session`) or clear a sale in progress: the cash session and the
cart stay as they are, hidden behind the lock screen, and the next operator who
signs in resumes them. Removing the active operator from the terminal in Personal,
and the status check dropping a revoked or inactive active operator, MUST also
return to the lock screen.

#### Scenario: Sign out inside an open session

- GIVEN a cash session is open, a sale is in progress and an operator is signed in
- WHEN the operator chooses "Cerrar sesión"
- THEN the lock screen is shown, the sale is not visible, the cash session is still
  open and the cart is unchanged

#### Scenario: Signing out and in as another operator

- GIVEN two operators are cached and the first is signed in
- WHEN the first signs out and the second enters their PIN
- THEN the second is the current operator and the same cash session stays open with
  the same cart

#### Scenario: The active operator is deactivated

- GIVEN an operator is signed in and an administrator deactivated their account
- WHEN the next status check reports them inactive
- THEN their cached credential is dropped and the lock screen is shown

### Requirement: Personal Is Admin Staff Management, Not Operator Sign-In

The "Personal" section of the POS MUST be reachable only by an operator holding
`ManageUsers` and MUST be administrator staff management: create a staff user
(email, initial password, role among Cajero, Vendedor and Administrador, and the
terminal's branch), list staff with role, branch membership and status, deactivate
and reactivate staff, reset a password, and manage the operators cached on this
terminal. It MUST NOT provide a way to add an operator to the terminal or to sign
an operator in: a new staff member signs in through the sign-in flow after the
current operator signs out. Removing an operator from the terminal forgets only
the local PIN credential, never the cloud account, and signs the operator out when
they were active.

#### Scenario: Personal offers no operator provisioning

- GIVEN an administrator opens Personal
- WHEN the section renders
- THEN "Operadores de esta terminal" lists the cached operators with "Quitar de
  esta terminal", and there is no action to add or provision an operator

#### Scenario: Removing the active operator

- GIVEN an operator is signed in and removes themselves from the terminal in
  Personal
- WHEN the removal is confirmed
- THEN that operator is signed out, the lock screen is shown, and they no
  longer appear as a tile on the lock screen

#### Scenario: A new staff member is added through the lock screen

- GIVEN an administrator created a staff user in Personal
- WHEN the current operator signs out and the new staff member signs in on the
  lock screen with email and password
- THEN they choose a PIN and are provisioned on the terminal through the lock
  screen, not through Personal

### Requirement: Clientes And Personal Are Sections Of The Main Window

The POS main window MUST be a shell whose content area shows one section at a
time: Venta (the sale), Clientes and Personal. Clientes and Personal MUST open
inside the main window, never as modal windows, and the navigation MUST show the
active section and offer a "Venta" entry that returns to the sale. Switching
sections MUST NOT lose the current sale: the cart, the scan box and the cash
session survive a visit to another section. Clientes and Personal MUST be offered
only to an operator holding `ManageUsers`; when the operator changes and loses
that permission while one of them is open, the shell MUST return to the sale.
Each section MUST scroll when its content exceeds the window, MUST show statuses
and errors inline above the scroll area (no message boxes for expected errors),
and MUST lock its inputs and show progress while a network action runs.

The administrator server authorization (a cookie sign-in as the signed-in
administrator) MUST be asked inline in the section ("Confirmá tu contraseña"), kept
only while the section is open, and the password MUST never be stored.

#### Scenario: Visiting Clientes keeps the sale

- GIVEN a sale with lines is in progress
- WHEN the administrator opens Clientes and then returns with "Venta"
- THEN the same lines are still in the cart

#### Scenario: Losing ManageUsers closes the section

- GIVEN an administrator has Personal open
- WHEN they sign out
- THEN the shell shows the sale

#### Scenario: An error stays visible

- GIVEN a section form is scrolled to its last field
- WHEN a request fails
- THEN the error is shown above the scroll area and is visible without scrolling
