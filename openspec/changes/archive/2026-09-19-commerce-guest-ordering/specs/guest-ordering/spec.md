# Guest Ordering Specification

## Purpose

Define guest order intake with no backing `Customer` row: order origin
classification, the paired construction invariant this forces on `Order`,
list-price resolution, non-priority dispatch ranking, and non-staff actor
representation. ADR-009's "non-priority" is a ranking attribute only — a
guest order is never blocked from fulfillment and never requires staff
acceptance beyond the verification gate defined in `public-order-surface`.

## Requirements

### Requirement: Order Origin Classification

Every `Order` MUST carry a required `OrderOrigin` of `Guest` or
`RegisteredCustomer`. `OrderOrigin` MUST NOT be nullable, defaulted
implicitly, or inferable only from the presence/absence of `CustomerId` —
it MUST be an explicit, first-class field set at construction.

#### Scenario: Guest order is stamped with Guest origin

- GIVEN a guest submits an order with no `Customer` behind it
- WHEN the order is constructed
- THEN its `OrderOrigin` is `Guest`

#### Scenario: Registered customer order is stamped with RegisteredCustomer origin

- GIVEN a registered customer submits an order through an enabled credential
- WHEN the order is constructed
- THEN its `OrderOrigin` is `RegisteredCustomer` and it carries a resolved
  `CustomerId`

### Requirement: Order Construction Invariant

`Order` MUST enforce the paired invariant `RegisteredCustomer ⇒ CustomerId
present` and `Guest ⇒ CustomerId null` at construction. `Order.CustomerId`
MUST be `Guid?`. The constructor MUST reject any combination that violates
the pairing; no call site MAY produce an inconsistent instance.
(Previously: `Order`'s constructor required a non-empty `CustomerId`
unconditionally, making a guest order impossible to represent.)

#### Scenario: Registered order without a CustomerId is rejected

- GIVEN a caller attempts to construct an `Order` with `OrderOrigin.RegisteredCustomer`
- WHEN no `CustomerId` is supplied
- THEN construction fails and no `Order` instance is produced

#### Scenario: Guest order with a CustomerId is rejected

- GIVEN a caller attempts to construct an `Order` with `OrderOrigin.Guest`
- WHEN a non-null `CustomerId` is supplied
- THEN construction fails and no `Order` instance is produced

### Requirement: Guest Identity Capture on the Order

Because a guest has no `Customer` row to read identity from, the guest's
DNI/identificación and verified contact channel (confirmed per
`public-order-surface`'s verification gate) MUST be captured directly on
the `Order` at submission time.

#### Scenario: Guest order carries identity fields

- GIVEN a guest order is admitted after verification
- WHEN the order is read back
- THEN it exposes the guest's DNI/identificación and verified contact
  channel without dereferencing any `Customer` row

### Requirement: Guest Price Resolution

Guest order submission MUST call `PricingResolutionService.ResolveAsync`
with `discountPercentage: null`, resolving strictly to official list price.
No caller-supplied or inferred discount MAY be applied to a guest order.

#### Scenario: Guest receives list price

- GIVEN a catalogue item with a configured registered-customer discount
- WHEN a guest submits an order for that item
- THEN the resolved unit price is the undiscounted list price

#### Scenario: Registered customer with a discount pays strictly less than a guest

- GIVEN the same catalogue item and a registered customer with an active
  discount
- WHEN a guest and that registered customer each submit an order for the
  item through the full submission path
- THEN the registered customer's resolved price is strictly lower than the
  guest's resolved price

### Requirement: Guest Order Non-Priority Ranking

A guest order MUST be fully dispatchable and MUST NOT be blocked from
fulfillment or gated behind explicit staff acceptance beyond the
verification step. `OrderOrigin.Guest` MUST be usable as a ranking
attribute wherever a branch prioritizes pending work, and MUST NOT cause
any read path to present guest and registered orders as equal-weight
without distinguishing origin.

#### Scenario: Verified guest order is dispatched normally

- GIVEN a guest order has passed the verification gate
- WHEN a branch processes its pending work queue
- THEN the guest order is eligible for fulfillment with no additional
  staff-acceptance step

#### Scenario: Registered orders rank above guest orders when prioritizing

- GIVEN a branch's pending queue contains both a guest order and a
  registered-customer order submitted around the same time
- WHEN the branch requests a priority-ordered view of pending work
- THEN the registered-customer order ranks above the guest order

#### Scenario: Order listing distinguishes origin

- GIVEN a branch's order list contains both guest and registered orders
- WHEN the list is read
- THEN each order's `OrderOrigin` is present and no query merges guest and
  registered orders into an undifferentiated set

### Requirement: Non-Staff Actor Representation

A guest order has no staff actor. The audit/actor shape used for order
submission MUST represent a non-staff-originated order explicitly and MUST
NOT use `Guid.Empty` or another sentinel that reads as "unknown staff
actor."

#### Scenario: Guest order records a non-staff actor shape

- GIVEN a guest submits an order with no staff operator present
- WHEN the order's actor/audit information is read
- THEN it explicitly identifies the order as non-staff-originated rather
  than presenting an empty or default staff actor id
