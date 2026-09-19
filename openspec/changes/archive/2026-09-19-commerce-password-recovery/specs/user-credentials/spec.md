# Delta for User Credentials

## ADDED Requirements

### Requirement: Forgot-Password Reset Request

The system MUST expose an anonymous reset-request endpoint that accepts an
email address and, regardless of whether a matching non-revoked user exists,
responds with an empty-body 202. When the email matches a persisted
non-revoked user, the system MUST create a single-use, per-user reset token,
hashed at rest, expiring 1 hour after issuance, and MUST send an email
containing the reset link/token to that user via the transactional email
provider. When no match exists, the system MUST perform a dummy hash
verification to preserve timing parity with the matching path and MUST NOT
create a token or send an email. The endpoint MUST enforce a cooldown per
submitted email AND per source IP, rejecting requests that arrive before the
cooldown elapses.

#### Scenario: Known email receives a reset token and email

- GIVEN a persisted, non-revoked user with a known email
- WHEN a reset request is submitted for that email
- THEN a single-use token is created, hashed at rest, expiring in 1 hour,
  and an email containing the reset link is sent to that user
- AND the response is an empty-body 202

#### Scenario: Unknown email looks identical to a known one

- GIVEN no user exists with a submitted email
- WHEN a reset request is submitted for that email
- THEN no token is created and no email is sent
- AND the response is an empty-body 202, indistinguishable in status, body,
  and timing from the known-email case

#### Scenario: Repeated requests are throttled

- GIVEN a reset request was already submitted for an email or from an IP
  within the cooldown window
- WHEN another reset request arrives for that same email or from that same IP
  before the cooldown elapses
- THEN the request is rejected without creating a new token or sending
  another email

### Requirement: Forgot-Password Reset Confirm

The system MUST expose an anonymous confirm endpoint that accepts a reset
token and a new password. The system MUST reject the request generically
when the token is invalid, already used, or expired (more than 1 hour past
issuance), without revealing which condition applied. On success, the system
MUST hash the new password using the existing `PasswordHasher<UserAccount>`
convention, persist it, mark the token as used so it cannot be reused, and
invalidate the user's existing session(s).

#### Scenario: Valid token sets a new password

- GIVEN a persisted, unused reset token issued less than 1 hour ago
- WHEN the confirm endpoint is called with that token and a new password
- THEN the user's password hash is updated, the token becomes unusable, and
  the user's prior session(s) no longer authenticate

#### Scenario: Token cannot be reused

- GIVEN a reset token was already used to set a new password
- WHEN the same token is submitted again
- THEN the request is rejected and the password is not changed

#### Scenario: Expired token is rejected

- GIVEN a reset token was issued more than 1 hour ago
- WHEN the confirm endpoint is called with that token
- THEN the request is rejected and the password is not changed

### Requirement: Authenticated Password Renewal

The system MUST expose an authenticated endpoint that lets a signed-in user
change their own password by supplying their current password and a new
password. The system MUST verify the current password against the stored
hash before accepting the new one, rejecting the request generically when
the current password does not verify. On success, the system MUST hash the
new password using the existing `PasswordHasher<UserAccount>` convention,
persist it, and invalidate the user's existing session(s).

#### Scenario: Correct current password renews the password

- GIVEN an authenticated, non-revoked user
- WHEN they submit their correct current password and a new password to the
  renewal endpoint
- THEN their password hash is updated and their prior session(s) no longer
  authenticate

#### Scenario: Wrong current password is rejected

- GIVEN an authenticated user
- WHEN they submit an incorrect current password to the renewal endpoint
- THEN the request is rejected, the password is not changed, and no other
  detail is revealed

### Requirement: Admin-Forced Password Reset

The system MUST expose an authenticated endpoint that lets a user holding
`Permission.ManageUsers` force-set a new password for another user, scoped
to users within the same organization as the acting admin. The system MUST
reject the request when the target user belongs to a different organization
than the acting admin, without revealing whether the target user id exists
elsewhere. On success, the system MUST hash the new password using the
existing `PasswordHasher<UserAccount>` convention, persist it, and
invalidate the target user's existing session(s).

#### Scenario: Admin resets a same-organization user's password

- GIVEN an authenticated user holding `Permission.ManageUsers` and a target
  user in the same organization
- WHEN the admin submits a new password for the target user
- THEN the target user's password hash is updated and the target user's
  prior session(s) no longer authenticate

#### Scenario: Cross-organization target is rejected

- GIVEN an authenticated user holding `Permission.ManageUsers` and a target
  user in a different organization
- WHEN the admin submits a new password for that target user
- THEN the request is rejected and the target user's password is not changed

### Requirement: Session Invalidation on Password Change

The system MUST ensure that any successful password change — self-service
reset confirm, authenticated renewal, or admin-forced reset — invalidates
all previously issued cookie sessions for the affected user, such that a
cookie issued before the change no longer authenticates requests after it.

#### Scenario: Prior session cookie stops authenticating after a change

- GIVEN a user has an active, previously issued session cookie
- WHEN that user's password is changed by any of reset confirm, renewal, or
  admin-forced reset
- THEN a request authenticated with the prior cookie is rejected
