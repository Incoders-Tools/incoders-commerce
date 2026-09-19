# Delta for User Credentials

## ADDED Requirements

### Requirement: Optional Customer Link on User Account

`UserAccount` MUST carry an optional (nullable) `CustomerId` referencing a
`Customer` row in the same organization. A `UserAccount` with no `CustomerId`
remains a staff user, unaffected by this capability. Provisioning a
`CustomerId`-bearing login (creating or linking a `UserAccount` to a
`Customer`) MUST be a `ManageUsers`-gated operation, separate from customer
creation itself. Customer login provisioning and customer creation MUST NOT
be coupled: creating a customer MUST NOT implicitly create a login, and
linking a login MUST NOT implicitly create a customer.

#### Scenario: Login without CustomerId remains staff

- GIVEN a `UserAccount` row has no `CustomerId`
- WHEN its role and permissions are evaluated
- THEN it is treated as a staff user exactly as before this change

#### Scenario: Provisioning a customer login requires ManageUsers

- GIVEN an actor lacks the `ManageUsers` permission
- WHEN they attempt to link a `UserAccount` to a `Customer` via `CustomerId`
- THEN the request is denied and no link is persisted

#### Scenario: Customer creation does not implicitly create a login

- GIVEN a `ManageUsers` holder creates a new `Customer`
- WHEN the customer row is persisted
- THEN no `UserAccount` row is created as a side effect

### Requirement: Customer-Linked User Is Denied Staff Permissions

A `UserAccount` carrying a non-null `CustomerId` MUST be denied all staff
permissions, regardless of any `Role` rows or role assignments recorded
against that account. This denial MUST be enforced by construction in
permission evaluation, not by convention or by omitting role assignment.

#### Scenario: Customer-linked user's effective permissions are always none

- GIVEN a `UserAccount` has a non-null `CustomerId` and also has one or more
  staff `Role` rows assigned to it (e.g. through misconfiguration or a prior
  state)
- WHEN that user's effective staff permissions are evaluated
- THEN the result grants no staff permission, regardless of the assigned
  roles

#### Scenario: Granting a staff role to a customer-linked user is rejected

- GIVEN a `UserAccount` has a non-null `CustomerId`
- WHEN an admin attempts to grant that account a staff role or staff
  permission
- THEN the grant is rejected and the account's effective staff permissions
  remain none
