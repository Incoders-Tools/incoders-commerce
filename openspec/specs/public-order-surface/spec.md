# Public Order Surface Specification

## Purpose

Define the platform's first public-reachable HTTP boundary: anonymous
guest order submission gated by lightweight verification, public catalogue
read, a customer-scoped session distinct from the staff cookie scheme,
rate limiting, and the explicit absence of any self-registration route.
Today's organization/branch resolution is hardcoded to the single Vaca
Verde organization and its principal branch; the endpoint shape MUST stay
structurally capable of resolving org/branch per-request later without
implying that resolution exists today.

## Requirements

### Requirement: Guest Verification Gate Before Admission

A guest order MUST NOT be admitted into the system until the submitter's
DNI/identificación and a verification code sent to a declared contact
channel (SMS/email/WhatsApp) have both been confirmed. No payment gate is
required or permitted as a substitute for this verification.

#### Scenario: Verified guest order is admitted

- GIVEN a guest submits DNI/identificación and a contact channel, then
  confirms the verification code sent to that channel
- WHEN the guest submits the order
- THEN the order is admitted with `OrderOrigin.Guest` and the confirmed
  identity captured on the order

#### Scenario: Unverified guest submission is rejected

- GIVEN a guest has not confirmed a verification code for the declared
  contact channel
- WHEN the guest attempts to submit an order
- THEN the submission is rejected and no order is admitted

#### Scenario: Expired or incorrect verification code is rejected

- GIVEN a verification code was issued for a guest's contact channel
- WHEN the guest submits an expired code or a code that does not match the
  one issued
- THEN confirmation fails, no order is admitted, and the guest may request
  a new code

#### Scenario: A failed submission does not burn the verification

- GIVEN a guest holds a confirmed verification
- WHEN the order cannot be stored (the destination branch does not exist,
  a price cannot be resolved, or the insert fails)
- THEN no order is admitted and the verification stays unconsumed, so the
  guest can submit again with the same confirmation

#### Scenario: A verification admits exactly one order

- GIVEN a guest order was admitted with a confirmed verification
- WHEN the same verification is presented for a different order
- THEN the submission is rejected as an invalid verification
- AND presenting it again for the SAME order id returns the stored order
  and its number without consuming anything

### Requirement: Public Catalogue Read

The catalogue MUST be readable without an authenticated session, scoped to
the resolved organization and branch, so a guest can build an order before
verification. Today's resolution targets the single configured
organization and its principal branch; the request/response shape MUST NOT
assume this is the only organization that will ever exist.

#### Scenario: Anonymous catalogue read

- GIVEN no authenticated session is presented
- WHEN a client requests the public catalogue
- THEN the catalogue for the resolved default organization and branch is
  returned

### Requirement: Customer-Scoped Session Distinct From Staff Scheme

A registered customer's session MUST be issued under an authentication
scheme distinct from the staff cookie scheme used by `/account/sign-in`.
An endpoint group serving staff-only operations MUST reject a
customer-scoped session at the endpoint-group/scheme level, independent of
and in addition to the `EffectivePermissions → None` authorization
short-circuit.

#### Scenario: Customer session cannot reach a staff endpoint

- GIVEN a registered customer holds a valid customer-scoped session and no
  staff session
- WHEN that session is presented to a staff-only endpoint
- THEN the request is rejected at the endpoint-group/scheme boundary
  without evaluating staff-specific business logic

#### Scenario: Staff session cannot be substituted for the customer scheme

- GIVEN a staff user holds a valid staff-scheme session
- WHEN that session is presented to a customer-scoped endpoint expecting
  the customer scheme
- THEN the request is rejected

### Requirement: No Self-Registration Route

The shipped public surface MUST NOT expose any endpoint, form, or route
through which a caller can create their own registered-customer login.
Registered logins remain admin-provisioned only.

#### Scenario: No self-registration endpoint exists

- GIVEN the complete set of routes exposed by the public and customer
  endpoint groups
- WHEN that route set is enumerated
- THEN no route accepts an anonymous or guest request to create a
  registered-customer login

### Requirement: Rate Limiting on Guest Submission

The guest-submit endpoint MUST enforce a rate-limit policy sized for
realistic order volume for a two-branch operation. An abusive burst of
requests against the guest-submit endpoint MUST be rejected once the limit
is exceeded, without degrading staff or registered-customer traffic on
other endpoint groups.

#### Scenario: Abusive burst is throttled

- GIVEN the guest-submit endpoint's rate limit has already been reached for
  the current window
- WHEN an additional guest submission arrives within that window
- THEN the request is rejected with a rate-limit response and no order is
  admitted

#### Scenario: Rate limiting is isolated to the public group

- GIVEN the guest-submit endpoint's rate limit has been exceeded
- WHEN a staff or registered-customer request is made against its own
  endpoint group
- THEN that request is unaffected by the guest endpoint's throttled state

### Requirement: Order Confirmation Shows the Order Number

After an order is accepted, the web order screen (guest and registered) MUST
show the human order number, for example "Pedido P01-W-37 recibido", with a
tooltip that explains each part ("P = Pedido · 01 = Sucursal · W = Web · 37 =
número de pedido de la sucursal"), in the active language. It MUST NOT show
the order's GUID. If the response carries no number, the plain accepted
message is shown.

#### Scenario: A guest sees the number and its explanation

- GIVEN a guest order was accepted as `P01-W-37`
- WHEN the confirmation is shown
- THEN it reads "Pedido P01-W-37 recibido", the number carries the
  composition tooltip and no GUID is shown

#### Scenario: A response without a number still confirms

- GIVEN an accepted order whose response has no order number
- WHEN the confirmation is shown
- THEN it reads "Pedido aceptado." with no tooltip
