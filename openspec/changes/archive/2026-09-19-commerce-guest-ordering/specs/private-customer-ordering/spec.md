# Delta for Private Customer Ordering

## ADDED Requirements

### Requirement: Registered Order Origin Stamping

Order submissions completed through the credential-based private customer
ordering channel MUST be stamped with `OrderOrigin.RegisteredCustomer` and
MUST carry the resolved `CustomerId`. This addition does not alter the
credential requirements of "Bound and Revocable Customer Access", which
remain unchanged and in force verbatim under Decision 1 (coexist):
`CustomerOrderingAccess` continues to operate alongside the
admin-provisioned login introduced by `user-credentials`, both binding to
the same `Customer`.

#### Scenario: Credential-based order carries RegisteredCustomer origin

- GIVEN an enabled customer submits an order using a valid
  `CustomerOrderingAccess` credential
- WHEN the order is constructed
- THEN its `OrderOrigin` is `RegisteredCustomer` and its `CustomerId` is the
  credential's resolved customer

#### Scenario: Admin-provisioned login also stamps RegisteredCustomer origin

- GIVEN a customer submits an order while signed in through the
  admin-provisioned, `CustomerId`-linked login rather than a
  `CustomerOrderingAccess` credential
- WHEN the order is constructed
- THEN its `OrderOrigin` is `RegisteredCustomer` and its `CustomerId` is the
  linked customer, consistent with the credential-based path
