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

### Requirement: No Permission Gating Introduced

This capability MUST NOT introduce any permission check on
`CommitSaleButton_Click`. `Permission.Seller` remains `ViewSales`-only;
no `RecordSales` permission is introduced by this capability.

#### Scenario: Sale button behavior is unchanged apart from attribution

- GIVEN an operator session capability is present on a terminal
- WHEN a sale is completed regardless of whether an operator is
  identified
- THEN no permission check runs beyond what existed before this change,
  and the only observable difference is the `actorId` attributed

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
