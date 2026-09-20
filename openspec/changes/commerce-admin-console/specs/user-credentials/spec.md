# Delta for User Credentials

## ADDED Requirements

### Requirement: List Staff In Organization

The system MUST expose `GET /account/users` on the existing
`ManageUsers`-gated `/account/users` group, returning the staff users
persisted in the caller's own organization. The endpoint MUST NOT return a
user belonging to another organization, and MUST NOT return a
`CustomerId`-linked (customer) account.

#### Scenario: Business-admin lists staff in their own organization

- GIVEN Organization A has three persisted staff users
- WHEN an authenticated `business-admin` in Organization A calls
  `GET /account/users`
- THEN all three staff users are returned

#### Scenario: Listing does not leak another organization's staff

- GIVEN Organization A and Organization B each have persisted staff users
- WHEN an authenticated caller in Organization A calls
  `GET /account/users`
- THEN no user belonging to Organization B is returned

#### Scenario: Listing excludes customer-linked accounts

- GIVEN Organization A has both staff users and a `CustomerId`-linked user
  account
- WHEN an authenticated caller in Organization A calls
  `GET /account/users`
- THEN the `CustomerId`-linked account is not included in the response

#### Scenario: Caller without ManageUsers is denied

- GIVEN an authenticated caller lacking `Permission.ManageUsers`
- WHEN they call `GET /account/users`
- THEN the request is rejected
