import { apiFetch } from './client'
import type { CustomerSignedInResponse, CustomerSignInRequest, OrderSubmissionOutcome, SubmitOrderLine } from './types'

/**
 * The `/customer` group (commerce-guest-ordering design.md "Customer
 * session"; Endpoints/CustomerSession.cs) — a session distinct from the
 * staff cookie, `Cookie.Path = "/customer"`, never sent to a staff route.
 */

export function customerSignIn(request: CustomerSignInRequest): Promise<CustomerSignedInResponse> {
  return apiFetch<CustomerSignedInResponse>('/customer/sign-in', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function customerSignOut(): Promise<void> {
  return apiFetch<void>('/customer/sign-out', { method: 'POST' })
}

export function fetchCustomerSession(): Promise<CustomerSignedInResponse> {
  return apiFetch<CustomerSignedInResponse>('/customer/me')
}

export interface SubmitCustomerOrderRequest {
  orderId: string
  lines: SubmitOrderLine[]
  correlationId: string
}

/**
 * KNOWN GAP (documented in apply-progress, not silently assumed done):
 * `POST /customer/orders` is design.md's documented target contract
 * (design.md "Interfaces / Contracts", Data Flow's "Registered customer
 * order") but was never implemented by Units 1-5 — no Phase 1-5 task in
 * tasks.md created it, and `CloudOrderSubmissionService` has no
 * `SubmitForCustomerSessionAsync` method. This client function is wired to
 * the documented contract so the frontend is ready the moment that endpoint
 * ships; until then, calling it will 404. `OrderScreen`'s registered branch
 * calls it and treats any resulting `ApiError` like any other server-side
 * denial, not a distinguishable UI collapse.
 */
export function submitCustomerOrder(request: SubmitCustomerOrderRequest): Promise<OrderSubmissionOutcome> {
  return apiFetch<OrderSubmissionOutcome>('/customer/orders', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}
