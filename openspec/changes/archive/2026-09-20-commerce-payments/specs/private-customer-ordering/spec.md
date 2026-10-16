# Delta for Private Customer Ordering

## ADDED Requirements

### Requirement: Order Settlement View Referencing Payments

An `Order` MUST expose a settlement view derived from the payments
recorded against it (per `order-payment-lifecycle`), reporting what is
owed and what is settled. `OrderDeliveryStatus` and `OrderPendingReason`
MUST remain unchanged by this addition; the settlement view MUST be
additive and MUST NOT alter, replace, or gate either existing field. The
settlement view MUST NOT block order submission, dispatch, or any other
operational path already defined for private customer ordering.

#### Scenario: Order exposes a settlement view alongside unchanged fulfilment fields

- GIVEN an order has one or more payments recorded against it
- WHEN the order is read back
- THEN it exposes a settlement view reporting owed and settled amounts,
  while `OrderDeliveryStatus` and `OrderPendingReason` remain exactly as
  they were before payments existed

#### Scenario: Unsettled order does not block a new order submission

- GIVEN a customer has a prior order with an unsettled balance
- WHEN that customer submits a new order through the existing
  credential-based or admin-provisioned login path
- THEN the new order is accepted following the existing "Customer
  Reference Integrity" and "Bound and Revocable Customer Access"
  requirements, with the prior order's settlement state having no bearing
  on acceptance
