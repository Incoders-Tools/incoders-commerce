# Delta for Tenant Access Foundation

## MODIFIED Requirements

### Requirement: Installation Identity and Audit

Each branch installation MUST have a distinct identity, established only
through server-verified operator sign-in and never self-minted or
caller-asserted by the installation itself. The installation's identity
MUST be backed by a server-issued, independently verifiable device
credential bound to a specific organization, branch, and installation.
Sensitive authentication, authorization, scope, revocation, and
synchronization decisions MUST be auditable with actor or credential,
organization, branch, time, action, outcome, and correlation
information.

(Previously: installation identity had no requirement that it be
server-verified or credential-backed — an installation could carry a
distinct identity value without the server ever confirming it belonged
to a real organization or branch.)

#### Scenario: Auditable sensitive action

- GIVEN an authorized user performs a sensitive management action
- WHEN the action completes or is denied
- THEN an immutable audit entry records the required context and outcome

#### Scenario: Installation replacement identity

- GIVEN a replacement installation is prepared
- WHEN it is enabled
- THEN it uses a new installation identity and the prior identity remains
  revocable and traceable

#### Scenario: Device bearer credential must be server-verified, not caller-asserted

- GIVEN a POS installation presents a `Bearer` device credential to
  `Cloud.Api`
- WHEN the credential is not a server-issued credential recognized by
  server-side lookup or signature verification
- THEN authentication fails regardless of whether the presented value is
  a well-formed organization/installation id pair

#### Scenario: Server-verified device credential resolves branch scope

- GIVEN a POS installation presents a device credential that the server
  issued and can verify
- WHEN authentication succeeds
- THEN the resulting principal carries the organization and branch scope
  the server bound to that credential at issuance time
