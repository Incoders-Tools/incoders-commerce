# ADR-009: Guest and registered customer ordering coexist

## Status

Accepted (behaviour locked — a future change implements the surface)

## Context

`openspec/specs/private-customer-ordering/spec.md` (approved) requires an unpredictable, revocable, organization-bound credential with no password, no login and no session, and the only customer-facing surface today is `OrderScreen.tsx` — a staff-facing console where an operator keys in an order using the customer's link credential. A public ordering surface plus registered customer logins appears to contradict that spec. It does not: the decision is coexistence, with the legacy mechanism's fate deliberately left open.

## Decision

- **Guest and registered are two branches of ONE order screen, not two screens.** The screen branches: a registered customer's contact data, address, and commercial discount are pre-filled and never re-typed; a guest completes a minimal form.
- **Registered customer accounts are admin-provisioned only. There is no public self-registration.** A `business-admin`, or any user holding `ManageUsers`, creates the account (email + password) and hands the credentials over directly.
- **The customer self-manages thereafter** via the already-shipped password-recovery/renewal infrastructure (`commerce-password-recovery`). The business is never the channel for a password after the initial handoff. No new credential-management mechanism is introduced for customers.
- **Guest orders are explicitly non-priority and MUST be classified as such.** Guest-originated orders carry a distinguishing classification so they are identifiable at a glance and never commingled as equally-weighted records with registered-customer orders.
- **All order-origin channels are equivalent in kind.** Public web (self-service), admin desktop/POS, and admin web console are all order creation, differing only in actor and channel. Registered-customer orders are the priority regardless of which channel created them — priority is a property of the customer relationship, not of the channel.
- **No login-pressure UX.** Copy presents the two paths as peers ("clientes habituales con acceso" vs. "pedido como invitado"); a hard login wall or nagging upsell is rejected.
- **OPEN — deferred to a future design**: the disposition of the existing `CustomerOrderingAccess` link-credential mechanism. It is not retired by this decision. Whether it (a) binds to the same `Customer` as the new login, (b) is a superseded predecessor kept for compatibility, or (c) becomes the activation step of an invite → set-password flow is unresolved. Until that decision, `private-customer-ordering`'s requirements remain in force unchanged.

## Consequences

- `openspec/specs/private-customer-ordering/spec.md` is not amended by this change. Any delta belongs to a future change, after the open item above is closed.
- The order model needs an origin/classification attribute; until it exists, guest ordering must not ship, because unclassified guest orders are exactly what this decision forbids.
- No public registration endpoint may be added. A future request for self-registration must amend this ADR first.
- Provisioning a customer account is a `ManageUsers` operation and inherits that permission's existing audit and tenancy behaviour.
