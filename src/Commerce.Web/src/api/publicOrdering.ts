import { apiFetch, apiFetchOutcome } from './client'
import type {
  GuestVerificationConfirmRequest,
  GuestVerificationRequest,
  GuestVerificationRequestedResponse,
  OrderSubmissionOutcome,
  PresentationRecord,
  SubmitGuestOrderRequest,
} from './types'

/**
 * The platform's first anonymous HTTP boundary (commerce-guest-ordering
 * design.md "Public surface"; Endpoints/PublicOrdering.cs). Every route here
 * requires no session — `apiFetch`'s `credentials: 'include'` is harmless
 * (no cookie is required to be honored) and kept for consistency with every
 * other client in this module.
 */

export function listPublicPresentations(): Promise<PresentationRecord[]> {
  return apiFetch<PresentationRecord[]>('/public/catalog/presentations')
}

export function requestGuestVerification(
  request: GuestVerificationRequest,
): Promise<GuestVerificationRequestedResponse> {
  return apiFetch<GuestVerificationRequestedResponse>('/public/guest-orders/verification', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function confirmGuestVerification(request: GuestVerificationConfirmRequest): Promise<void> {
  return apiFetch<void>('/public/guest-orders/verification/confirm', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

/**
 * `POST /public/guest-orders` returns the outcome DTO on both the 200
 * accepted path and the 403 denied path (Endpoints/PublicOrdering.cs) —
 * `apiFetchOutcome` is required here, not plain `apiFetch` (see
 * api/client.ts's remark on `Endpoints/Ordering.cs`'s identical shape).
 */
export function submitGuestOrder(request: SubmitGuestOrderRequest): Promise<OrderSubmissionOutcome> {
  return apiFetchOutcome<OrderSubmissionOutcome>('/public/guest-orders', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}
