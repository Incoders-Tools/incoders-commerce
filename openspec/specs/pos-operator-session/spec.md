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

### Requirement: Operator Identification Never Blocks a Sale

Identifying the current operator MUST be strictly additive. If no
operator can be identified — no cached PIN present, or no connectivity
to provision one — the sale MUST proceed, falling back to attributing it
to the installation. Operator identification MUST NOT gate
`CommitSaleButton_Click`.

#### Scenario: Sale proceeds with no operator identified

- GIVEN a terminal has no operator with a valid cached PIN currently
  logged in and no connectivity to provision one
- WHEN a sale is completed
- THEN the sale succeeds and is attributed to the installation, exactly
  as before this change

#### Scenario: Sale is attributed to the current operator when identified

- GIVEN a terminal has a currently logged-in operator with a valid
  cached PIN
- WHEN a sale is completed
- THEN `CompleteOfflineSale`'s `actorId` is the current operator's user
  id rather than the installation id

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
level (administrator, cashier, or no access to the point of sale) or "Sin
operador activo" when nobody is signed in, and MUST offer: "Cambiar operador"
(only when another non-stale operator is cached on the terminal), "Cerrar
sesión" (only while an operator is active), and "Iniciar sesión" (only while
nobody is active). "Cambiar operador" MUST open the PIN picker for operators
already cached on the terminal and MUST NOT offer to provision anyone. The menu
MUST NOT offer to add an operator.

#### Scenario: Menu of the active operator

- GIVEN a cashier is signed in and a second non-stale operator is cached
- WHEN the operator button is clicked
- THEN the menu shows the cashier's email and "Cajero", "Cambiar operador" and
  "Cerrar sesión", and no provisioning entry

#### Scenario: Menu with nobody signed in

- GIVEN no operator is active
- WHEN the operator button is clicked
- THEN the menu shows "Sin operador activo" and only "Iniciar sesión"

#### Scenario: Switching never provisions

- GIVEN an operator is signed in
- WHEN they choose "Cambiar operador"
- THEN only the PIN picker of cached operators is shown, with no email,
  password or new-PIN fields

### Requirement: Operator Sign-Out Keeps the Cash Session

"Cerrar sesión" MUST clear the terminal's current operator and then offer the
PIN picker so another cached operator can sign in. It MUST NOT close or change
an open cash session (`pos-cash-session`) and MUST NOT block sales: until
somebody signs in, sales keep being attributed to the installation, per
"Operator Identification Never Blocks a Sale". Cancelling the picker leaves the
terminal with no active operator.

#### Scenario: Sign out inside an open session

- GIVEN a cash session is open and an operator is signed in
- WHEN the operator chooses "Cerrar sesión"
- THEN the current operator is cleared, the nav label reads "Sin operador
  activo", the PIN picker is offered, and the cash session is still open

#### Scenario: Signing out and in as another operator

- GIVEN two operators are cached and the first is signed in
- WHEN the first signs out and the second enters their PIN
- THEN the second is the current operator and the same cash session stays open

### Requirement: Operator Provisioning Lives in Personal

Adding an operator to a terminal (email, password and a new PIN, verified online
once) MUST be done from the "Personal" area, in "Operadores de esta terminal",
which also lists the operators cached on the terminal and MUST let an
administrator remove one from the terminal. Removing an operator forgets only
the local PIN credential, never the cloud account, and signs the operator out
when they were active. The provisioning form MUST be shown at startup, and from
"Iniciar sesión", only when the terminal has no non-stale cached operator
(first run), so a new terminal can always be set up.

#### Scenario: Adding an operator from Personal

- GIVEN an administrator opens Personal > "Operadores de esta terminal"
- WHEN they add an operator with valid credentials and a PIN
- THEN the operator is cached on the terminal and can sign in with the PIN

#### Scenario: Removing the active operator

- GIVEN an operator is signed in and is removed from the terminal in Personal
- WHEN the window closes
- THEN that operator is signed out and no longer appears in the PIN picker

#### Scenario: First run still provisions at startup

- GIVEN a paired terminal has no cached operator
- WHEN the application starts
- THEN the provisioning form is shown, with "Continuar sin operador" available
