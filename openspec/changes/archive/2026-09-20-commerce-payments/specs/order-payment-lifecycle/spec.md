# Order Payment Lifecycle Specification

## Purpose

Define payment as a lifecycle separate from order fulfilment (ADR-011):
attempts, partial payments, refunds/reversals, and settlement state per
order and per customer, with fail-closed approval semantics. This
capability models the lifecycle only; it selects no payment provider and
implements no gateway integration (planning-only, `approval_scope:
planning-only`).

## Requirements

### Requirement: Payment as a Separate Aggregate from Order

A `Payment` (or payment attempt) MUST be a first-class aggregate that
references an `Order` (or a POS sale) by id and MUST NOT be embedded in
`Order`. `OrderDeliveryStatus` and `OrderPendingReason` MUST remain
verbatim unchanged by this capability; no payment value MAY be added to
either enum. Multiple payment attempts, partial payments, and
refunds/reversals for the same order MUST be representable without
mutating the order's existing fields or history.

#### Scenario: Recording a payment does not change order fulfilment state

- GIVEN an `Order` with a given `OrderDeliveryStatus` and `OrderPendingReason`
- WHEN a payment is recorded against that order
- THEN `OrderDeliveryStatus` and `OrderPendingReason` are read back unchanged
  and neither enum exposes any payment-related value

#### Scenario: Order delivered and unpaid is simultaneously representable

- GIVEN an order has been delivered with no payment recorded against it
- WHEN the order's fulfilment state and settlement state are each queried
- THEN the order reads as delivered and, independently, as unpaid, with
  neither state blocking or implying the other

#### Scenario: Order paid and undelivered is simultaneously representable

- GIVEN a payment has been recorded in full against an order that has not
  yet been delivered
- WHEN the order's fulfilment state and settlement state are each queried
- THEN the order reads as paid and, independently, as undelivered, with
  neither state blocking or implying the other

### Requirement: Payment Method Taxonomy

Every recorded payment MUST carry an explicit payment method drawn from a
fixed taxonomy: cash, account credit (referencing `Customer.PaymentTerms`
as the commercial-terms context, not as the payment record itself), bank
transfer, card, and Mercado Pago. Mercado Pago MUST be represented as its
own distinct method value and MUST NOT be folded into a generic
"card/gateway" bucket. This capability defines the taxonomy only; no
provider integration for card, transfer, or Mercado Pago ships as part of
this change (ADR-011 requires a further ADR before an integration is
built).

#### Scenario: Payment records a distinct method

- GIVEN a payment is recorded against an order
- WHEN the method is set to one of cash, account credit, bank transfer,
  card, or Mercado Pago
- THEN the recorded payment carries exactly that method value

#### Scenario: Mercado Pago is not merged into a generic card method

- GIVEN a payment recorded with method Mercado Pago and a separate payment
  recorded with method card
- WHEN both payments are read back
- THEN their method values are distinct and neither is presented as the
  other

### Requirement: Partial Payments and Reversals Without History Mutation

The system MUST support recording more than one payment attempt against
the same order and MUST support reversing a previously recorded payment.
A reversal MUST NOT delete or overwrite the reversed payment's history; it
MUST instead produce a new record that restores the prior outstanding
balance. Two partial payments against one order MUST produce a correct
outstanding balance, and reversing one of them MUST restore the balance
that existed before that payment was recorded.

#### Scenario: Two partial payments reduce the outstanding balance correctly

- GIVEN an order with a known total and no prior payments
- WHEN two partial payments are recorded against it, each smaller than the
  total
- THEN the outstanding balance equals the total minus the sum of both
  partial payments

#### Scenario: Reversing a partial payment restores the prior balance

- GIVEN an order with two partial payments recorded, each still present in
  history
- WHEN one of the two payments is reversed
- THEN the outstanding balance returns to what it was before that payment
  was recorded, and both the original payment and the reversal remain
  visible in history

### Requirement: Settlement Query Surface Is Reporting-Only, Never a Gate

The system MUST expose a server-side query answering what is owed and
what is settled, per order and per customer, accounting for partial
payments. An unpaid, delivered order MUST be visible through this
surface for reporting purposes only. Settlement state MUST NOT block a
new order, MUST NOT block dispatch, and MUST NOT block any other
operational path for that customer or order.

#### Scenario: Unpaid delivered order is visible but blocks nothing

- GIVEN a customer has one order that is delivered and fully unpaid
- WHEN that customer submits a new order
- THEN the new order is accepted normally and the unpaid delivered order's
  existence has no bearing on its acceptance

#### Scenario: Settlement query reports owed and settled amounts

- GIVEN an order with a known total and a partial payment recorded against
  it
- WHEN the settlement query is run for that order
- THEN it reports the settled amount as the partial payment and the owed
  amount as the remainder of the total

### Requirement: Fail-Closed Approval Outcome

Absent or unreachable payment-approval configuration MUST yield an
explicit unapproved/unavailable outcome. It MUST NOT be treated as an
approval and MUST NOT be treated as a silent zero-amount success. This
applies regardless of payment method; an approval path that cannot be
evaluated MUST fail closed, never open.

#### Scenario: Unreachable approval configuration yields an explicit unavailable outcome

- GIVEN payment-approval configuration is absent or unreachable
- WHEN a payment approval is attempted
- THEN the outcome is an explicit unapproved/unavailable result, and no
  payment is recorded as approved or settled as a result of that attempt

#### Scenario: Fail-closed outcome is distinguishable from a valid zero-amount record

- GIVEN a fail-closed unapproved/unavailable outcome has occurred
- WHEN that outcome is compared against a legitimately recorded
  zero-effect payment record
- THEN the two are distinguishable and the fail-closed outcome is never
  presented as a successful record

### Requirement: Recording and Reversing Authorization Matches Order Management

Recording or reversing a payment MUST be permitted to the same role that
already manages the order (seller/business-admin per the existing role
taxonomy). No second-approver or dual-control workflow is required for a
reversal.

#### Scenario: Order-managing role records and reverses a payment

- GIVEN an actor holds the role that manages the order (seller or
  business-admin)
- WHEN that actor records a payment and later reverses it
- THEN both actions succeed without requiring approval from a second actor

#### Scenario: An actor without order-management rights cannot record a payment

- GIVEN an actor does not hold the role that manages orders
- WHEN that actor attempts to record or reverse a payment
- THEN the attempt is denied

### Requirement: Offline Branch Sale Never Blocks on Payment Approval

A local branch sale MUST commit with no dependency on cloud or gateway
payment-approval confirmation (ADR-002). Any payment-approval flow
introduced by this capability MUST degrade to "recorded locally, settled
later" rather than gate the sale path.

#### Scenario: Branch sale commits with no cloud or gateway reachability

- GIVEN a branch has no connectivity to the cloud or any payment gateway
- WHEN an authorized cashier completes a sale with a cash or locally
  recorded payment
- THEN the sale commits locally and is not blocked by the absence of
  cloud or gateway payment confirmation
