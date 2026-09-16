import { apiFetch } from './client'
import type { OrderSubmissionOutcome, SubmitOrderRequest } from './types'

export function submitOrder(request: SubmitOrderRequest): Promise<OrderSubmissionOutcome> {
  return apiFetch<OrderSubmissionOutcome>('/orders/', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}
