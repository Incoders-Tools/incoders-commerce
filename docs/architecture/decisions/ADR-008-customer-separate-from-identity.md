# ADR-008: Customer separate from Identity

## Status

Accepted (target shape — a future change implements it)

## Context

There is no `Customer` aggregate in this repository. `src/Commerce.Domain/Ordering/Order.cs` declares `public Guid CustomerId { get; }` — a bare identifier with no entity behind it — and the only customer-adjacent type is `CustomerOrderingAccess`, a credential, not a party. Commercial conditions, contact data, and addresses therefore have nowhere to live. Meanwhile ADR-002 makes ASP.NET Core Identity the identity provider for users. If a customer is modelled as a user, every customer requires a login, which contradicts both offline POS walk-in sales and the guest ordering decided in ADR-009.

## Decision

- **Customer is a commercial party, not an identity.** A `Customer` aggregate owns the business facts: organization binding, display/legal name, contact data, addresses, enabled state, and the commercial conditions ADR-010 resolves against. It can exist with no login at all.
- **Identity optionally points at Customer, never the reverse.** The link is a nullable `CustomerId` on the user/identity side. `Customer` has no `UserId` field and no knowledge of authentication. A customer may have zero or one linked login; a login without a `CustomerId` is a staff user.
- **`Order.CustomerId` becomes a real reference** to that aggregate. Today it is an unconstrained `Guid`; a future change is responsible for the aggregate, persistence, and referential meaning. No change is made here.
- **ADR-003's snapshot rule is unaffected**: orders keep snapshotting commercial context at submission, so later edits to a `Customer` never rewrite history.
- **Tenancy is unchanged**: `Customer` is organization-bound and subject to ADR-002's RLS and cross-tenant non-disclosure rules, including a second-organization fixture.

## Consequences

- A future change must decide the migration for existing `Order.CustomerId` values, which currently reference no row.
- Authorization code must not assume "has a login ⇒ is a customer" or "is a customer ⇒ has a login"; both are false under this shape.
- Whether `CustomerOrderingAccess` attaches to this aggregate is open — see ADR-009.
