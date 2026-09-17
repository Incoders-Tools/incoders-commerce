# ADR-011: Payment lifecycle separate from Order

## Status

Accepted (target shape — a future change implements it)

## Context

ADR-003 states plainly that pending-order handling defers settlement and that any future settlement or payment-provider integration requires a new ADR. This is that ADR's scope-setting successor. Today `Order` carries `OrderDeliveryStatus` and `OrderPendingReason` only (`src/Commerce.Domain/Ordering/Order.cs`); there is no payment state, no payment entity, and no provider integration anywhere in the repository.

## Decision

- **Payment is a separate lifecycle from Order.** Fulfilment state and money state are distinct dimensions and MUST NOT be merged into one status enum. An order can be delivered and unpaid, or paid and undelivered.
- **Order keeps a delivery/fulfilment status only.** The existing `OrderDeliveryStatus`/`OrderPendingReason` semantics from ADR-003 are unchanged and are not extended with payment values.
- **Payment state is its own concept**, referencing the order rather than being embedded in it, so that partial payments, multiple attempts, refunds, and reversals are representable without mutating order history.
- **ADR-003's guarantees carry over unchanged**: a pending order never implies settlement, and every payment-affecting synchronization effect is idempotent under a stable `operationId` with inbox de-duplication.
- **No provider is selected here.** Provider choice, PCI scope, reconciliation, and fiscal/invoicing integration are future decisions requiring their own ADR or an amendment to this one.
- **Sequencing relative to guest vs. registered checkout is open** and belongs to that future change.

## Consequences

- Any change adding a `Paid` value to an order status enum contradicts this ADR.
- A future change must define what a payment references under ADR-008's `Customer`, and how offline POS payments reconcile under ADR-002's local sale authority.
- This ADR satisfies ADR-003's "requires a new ADR" precondition for modelling only; building a payment integration still requires a further decision.
