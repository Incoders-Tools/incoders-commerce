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

#### Scenario: Operator re-pairs to the same branch

- GIVEN a POS terminal already holds a valid device credential for
  Branch 1
- WHEN an operator authorized for Branch 1 initiates re-pairing and signs
  in again
- THEN the terminal receives a freshly issued credential for Branch 1
  and continues operating normally
