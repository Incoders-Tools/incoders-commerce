# ADR-010: Centralized server-side pricing

## Status

Accepted (target behaviour — a future change implements it)

## Context

No pricing or commercial-conditions engine exists. `OrderLineSnapshot` carries the commercial context submitted with an order (ADR-003), which records what was shown but decides nothing about what should be shown. With registered customers gaining entity-level discounts (ADR-008/ADR-009), the question of who computes a price becomes answerable in three places — client, API, or database — and answering it inconsistently produces prices that disagree per channel.

## Decision

- **Price is resolved server-side, in one place.** A single pricing component resolves the effective price for a (customer context, product/presentation, quantity) tuple. No client — SPA, POS, or admin console — ever computes, derives, or adjusts a price.
- **Guest: official list price, no discount.** There is no guest discount tier.
- **Registered customer: the commercial conditions defined on their `Customer` entity** (ADR-008), resolved by the engine, never by the frontend and never by a value typed into a form.
- **All channels resolve identically.** Public web, POS, and admin console are the same order creation (ADR-009) and MUST receive the same price for the same inputs. Channel is not a pricing input.
- **Snapshot-on-submit is retained** (ADR-003): the resolved price and its commercial context are frozen onto the order, so later condition changes never rewrite a submitted order.
- **Offline**: the POS resolves against its locally-cached conditions and remains authoritative for the local sale per ADR-002; cache freshness is governed by ADR-002's freshness policy, not invented here.

## Consequences

- A client-side discount calculation is a defect against this ADR, not a shortcut.
- A future change owns the engine, its cache/propagation to branches, and its tests, including a guest-vs-registered price divergence test for the same catalogue item.
- Manual price override, promotions, and quantity-break pricing are not decided here and require their own ADR.
