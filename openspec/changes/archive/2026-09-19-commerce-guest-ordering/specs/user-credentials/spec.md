# Delta for User Credentials

## ADDED Requirements

### Requirement: Customer-Scoped Session Distinct From Staff Scheme

A `UserAccount` carrying a non-null `CustomerId` MUST authenticate into a
customer-scoped session issued under its own authentication scheme and
cookie, separate and distinct from the staff scheme issued by
`/account/sign-in`. This is enforced at the authentication layer, in
addition to and independent of the existing `EffectivePermissions → None`
denial for customer-linked accounts.

#### Scenario: Registered customer sign-in issues a customer-scoped session

- GIVEN a `UserAccount` with a non-null `CustomerId` and valid credentials
- WHEN that account signs in through the customer-scoped sign-in endpoint
- THEN the issued session uses the customer-scoped scheme, not the staff
  scheme

#### Scenario: Customer-scoped session is rejected by the staff scheme

- GIVEN a session was issued under the customer-scoped scheme
- WHEN it is presented to an endpoint that requires the staff scheme
- THEN authentication for that scheme fails before any authorization check
  runs

### Requirement: Admin-Provisioned Customer Login Only

Creation of a `CustomerId`-linked `UserAccount` MUST remain a
`ManageUsers`-gated administrative operation. No endpoint, form, or route
MAY allow a caller to create or activate their own registered-customer
login.

#### Scenario: No public self-registration endpoint exists

- GIVEN the complete set of authentication-related endpoints
- WHEN that set is enumerated
- THEN none accepts an unauthenticated or guest request to create a
  `CustomerId`-linked login
