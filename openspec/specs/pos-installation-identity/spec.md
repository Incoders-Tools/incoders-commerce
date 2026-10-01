# POS Installation Identity Specification

## Purpose

Define real operator sign-in, branch selection, and server-issued
verifiable device credentials for `Commerce.Pos.Windows`, replacing
self-minted installation identity with a durable, server-confirmed
organization + branch + installation binding.

## Requirements

### Requirement: Sign-In Required When No Valid Device Credential Exists

The POS terminal MUST require real operator sign-in (email/password,
verified through the same credential-verification path used by
`/account/sign-in`) whenever no valid local device credential exists. The
POS MUST NOT fabricate or self-assign an organization, branch, or
installation identity.

#### Scenario: Fresh install has no local credential

- GIVEN a fresh POS installation with no `installation.json` (or no valid
  device credential within it)
- WHEN the application starts
- THEN the sign-in screen is shown and no organization, branch, or
  installation identity is created before sign-in succeeds

#### Scenario: Invalid sign-in credentials are rejected

- GIVEN an operator enters an email/password that fails the same
  credential-verification path used by `/account/sign-in`
- WHEN sign-in is submitted
- THEN sign-in fails, no device credential is issued, and no local
  installation identity is persisted or altered

### Requirement: Branch Selection Scoped to the Signed-In Operator

After successful sign-in, the POS MUST list only branches present in the
signed-in user's `UserAccount.BranchScope`. When exactly one branch is
authorized, the POS MUST auto-select it without showing a picker. When
more than one branch is authorized, the POS MUST show a picker and
require explicit selection before proceeding.

#### Scenario: Single authorized branch is auto-selected

- GIVEN a signed-in operator's `BranchScope` contains exactly one branch
- WHEN sign-in succeeds
- THEN that branch is selected automatically and no picker is shown

#### Scenario: Multiple authorized branches require explicit selection

- GIVEN a signed-in operator's `BranchScope` contains more than one branch
- WHEN sign-in succeeds
- THEN a picker lists exactly those branches and pairing does not proceed
  until the operator selects one

#### Scenario: Zero authorized branches shows an operational message

- GIVEN a signed-in operator's `BranchScope` contains no branches
- WHEN sign-in succeeds
- THEN the POS shows a message stating no branches are assigned and to
  contact an administrator, distinct from a generic authentication
  failure, and no device credential is issued

### Requirement: Server-Issued, Verifiable Device Credential

On successful sign-in and branch selection, the server MUST issue a
device credential bound to the specific organization, branch, and
installation. This credential MUST be independently verifiable by
`Cloud.Api` (via server-side lookup or signature validation), not a
caller-asserted claim. `LocalInstallationStore` MUST persist the
server-confirmed organization id, branch id, installation id, and the
credential material returned by the server.

#### Scenario: Credential issued after successful pairing

- GIVEN an operator signs in and selects (or is auto-assigned) a branch
- WHEN pairing completes
- THEN the server issues a device credential bound to that organization,
  branch, and installation, and the POS persists it locally alongside the
  confirmed identity

#### Scenario: Persisted identity reused on restart

- GIVEN a POS installation has a persisted, valid device credential
- WHEN the application restarts
- THEN the POS uses the persisted identity and credential without
  requiring sign-in again

### Requirement: Offline Sales Continue Despite Credential Problems

Loss, expiry, or server-side revocation of the device credential MUST
NOT block local offline sales. Only cloud synchronization MUST be
blocked until the operator re-signs-in successfully.

#### Scenario: Revoked credential does not block local sales

- GIVEN a POS installation's device credential has been revoked
  server-side
- WHEN an authorized cashier completes a valid sale offline
- THEN the sale succeeds locally and is queued for synchronization

#### Scenario: Revoked credential blocks sync until re-sign-in

- GIVEN a POS installation's device credential has been revoked
  server-side
- WHEN the POS attempts to synchronize
- THEN synchronization is rejected and the POS prompts the operator to
  re-sign-in before sync can resume

### Requirement: Re-Pairing Without Manual File Deletion

The POS MUST support a re-pairing flow allowing an operator to sign in
again on an already-paired terminal and select a different (or the same)
branch, without requiring manual deletion of the local installation
file.

#### Scenario: Operator re-pairs to a different branch

- GIVEN a POS terminal already holds a valid device credential for
  Branch 1
- WHEN an operator authorized for Branch 2 initiates re-pairing and signs
  in
- THEN the terminal is re-paired to Branch 2, the new server-issued
  credential replaces the prior one, and no manual file deletion is
  required
- AND the terminal receives a register number of Branch 2 (see "Register
  Number")

#### Scenario: Operator re-pairs to the same branch

- GIVEN a POS terminal already holds a valid device credential for
  Branch 1
- WHEN an operator authorized for Branch 1 initiates re-pairing and signs
  in again
- THEN the terminal receives a freshly issued credential for Branch 1
  and continues operating normally
- AND it keeps the register number it already had in Branch 1

### Requirement: Issued-To User Distinct From Current Operator

The device credential's `issued_to_user_id` MUST continue to record only
the operator who performed pairing, unaffected by any subsequent
operator-session activity on that terminal. Current-operator identity
(who is currently identified as operating the terminal, per
`pos-operator-session`) MUST be tracked separately from
`issued_to_user_id` and MUST NOT overwrite or be reconciled with it.

#### Scenario: Pairing operator remains issued_to_user_id after operator switches

- GIVEN a terminal was paired by Operator A, recording
  `issued_to_user_id = Operator A`
- WHEN Operator B later provisions and logs in as the current operator
  on that same terminal
- THEN `issued_to_user_id` still identifies Operator A, unchanged, and
  Operator B's identity is tracked only as the current operator, not as
  a new value of `issued_to_user_id`

#### Scenario: Current operator identity does not require re-pairing

- GIVEN a terminal has a valid device credential paired by Operator A
- WHEN a different staff member provisions and becomes the current
  operator
- THEN no re-pairing occurs and the existing device credential and
  `issued_to_user_id` are left untouched

### Requirement: Register Number

Every paired POS terminal MUST carry a register number (1 to 999) that is
unique within its branch, assigned by the server in the same transaction that
issues the device credential, and shown to people as `Caja {n}` (for example
`Sucursal 01 · Ruta 51 · Caja 2`; no GUID is shown). Together with the branch
code it is the `C{register}` part of POS document numbers such as
`V01-C2-125`, which terminals generate offline, so a (branch, register) pair
MUST identify exactly one installation for ever: a register number MUST NEVER
be reused for a different installation, even after its terminal was moved or
retired. The next number of a branch is the highest ever assigned there plus
one; a branch that used all 999 numbers answers pairing with a typed
`register-numbers-exhausted` conflict and the pairing changes nothing.
Allocation MUST be race-free per branch.

The pairing response (`DevicePairResponse`) carries the branch code and the
register number, and the terminal persists them in `installation.json` (old
files without them still load, with the identity unknown). A terminal that
does not know its identity (paired before this requirement, or offline when it
paired) fetches it from the authenticated `GET /device/identity` endpoint,
which answers from the STORED device credential (never from the request),
returns `{ organizationId, branchId, branchName, branchCode, registerNumber }`
and allocates a register number when the live credential has none. Offline,
the identity simply stays unknown: the terminal keeps working and the screens
show only what is known. Terminals paired before this requirement are
backfilled per branch in credential issue order.

#### Scenario: First pairing gets the next number of its branch

- GIVEN Branch 1 already has terminals holding registers 1 and 2
- WHEN a new installation pairs to Branch 1
- THEN it is assigned register 3 and the pairing response carries branch
  code and register number

#### Scenario: Re-pairing to the same branch keeps the number

- GIVEN an installation holds register 2 of Branch 1
- WHEN it is paired to Branch 1 again (a fresh credential)
- THEN it keeps register 2 and no other number is consumed

#### Scenario: Re-pairing to another branch gives a new number there

- GIVEN an installation holds register 1 of Branch A
- WHEN it is paired to Branch B
- THEN it receives the next unused register of Branch B
- AND its register in Branch A is released
- AND if it is later paired to Branch A again it gets register 1 back

#### Scenario: Numbers are never reused

- GIVEN an installation moved away from Branch A and released register 1
- WHEN a different installation pairs to Branch A
- THEN it receives a register higher than every number ever assigned in
  Branch A, never the released register 1

#### Scenario: A terminal paired before registers existed fetches its identity

- GIVEN a terminal holds a valid device credential but no register number
- WHEN it calls `GET /device/identity` with its device bearer
- THEN it receives its branch code, branch name and a newly allocated
  register number
- AND later calls return the same register number
- AND a call without a valid device bearer is rejected with 401

#### Scenario: Concurrent pairings never share a number

- GIVEN several installations pair to the same branch at the same time
- THEN each receives a different register number

#### Scenario: A branch without numbers left rejects the pairing atomically

- GIVEN a branch that already assigned register 999
- WHEN another installation pairs to it
- THEN pairing answers `409` with status `register-numbers-exhausted`
- AND no credential is issued and the installation's previous credential
  stays valid
