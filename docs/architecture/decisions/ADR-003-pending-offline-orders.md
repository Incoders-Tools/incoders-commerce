# ADR-003: Pending offline orders and idempotent synchronization

## Status

Accepted

## Context

`openspec/changes/commerce-foundation/proposal.md` and `design.md` require proving one enabled-customer catalogue-to-order path without implementing full inventory or payments. The open question is what a branch must promise (or must not promise) when an order targets a branch that is offline at submission time, and how retried synchronization avoids duplicate business effects.

## Decision

- **Offline destination orders stay pending**: when an order's destination branch is offline, the order is accepted by the cloud and recorded as pending. It never confirms stock and never implies settlement. Confirmation happens only once the destination branch is reachable and has applied the order against real branch-owned stock/state.
- **No silent expansion into payments or full inventory**: pending-order handling defers settlement and detailed inventory reservation; this ADR does not authorize building payment processing or full inventory reservation as part of the walking skeleton.
- **Idempotent effects everywhere sync happens**: retries reuse a stable `operationId`. Unique inbox keys at the receiving side (branch or cloud) prevent a retried operation from producing a duplicate business effect (e.g., a duplicate sale, duplicate ACK, or duplicate order acceptance). Acknowledgements are only ever sent after a durable commit, never before.
- **Envelope shape**: synchronization envelopes carry contract/tenant/branch/aggregate versions, actor, correlation ID, and payload, enabling idempotency checks and conflict detection without relying on wall-clock ordering.
- **Order snapshots**: orders snapshot Product, Presentation, quantity behavior, units, and commercial context at submission time, so that later catalogue changes do not retroactively alter an already-submitted order.

## Consequences

- Test suites (`tests/Commerce.Integration/OrderingTests.cs`, `SyncTests.cs`) must include duplicate-submission and lost-ACK replay scenarios and assert exactly one committed business effect.
- Any future settlement or payment-provider integration requires a new ADR; it is explicitly out of scope here.
- API contracts for order submission must expose "pending" as a first-class, honest state to the customer, rather than a false "confirmed" status.
